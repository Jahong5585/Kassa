BEGIN;

CREATE TABLE catalog_change_feed (
    cursor_id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    product_id uuid NOT NULL,
    change_kind text NOT NULL CHECK (change_kind IN ('upsert', 'delete')),
    changed_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE INDEX catalog_change_feed_store_cursor
    ON catalog_change_feed (org_id, store_id, cursor_id);

CREATE OR REPLACE FUNCTION record_store_catalog_change()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $function$
DECLARE
    row_org uuid;
    row_store uuid;
    row_product uuid;
    row_kind text;
BEGIN
    IF TG_OP = 'DELETE' THEN
        row_org := OLD.org_id;
        row_store := OLD.store_id;
        row_product := OLD.product_id;
    ELSE
        row_org := NEW.org_id;
        row_store := NEW.store_id;
        row_product := NEW.product_id;
    END IF;

    row_kind := 'upsert';
    IF TG_TABLE_NAME = 'store_prices' THEN
        IF TG_OP = 'DELETE' THEN
            row_kind := 'delete';
        ELSIF NOT NEW.active THEN
            row_kind := 'delete';
        END IF;
    END IF;

    INSERT INTO catalog_change_feed (org_id, store_id, product_id, change_kind)
    VALUES (row_org, row_store, row_product, row_kind);
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END;
$function$;

CREATE OR REPLACE FUNCTION record_shared_catalog_change()
RETURNS trigger
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, public
AS $function$
DECLARE
    row_org uuid;
    row_product uuid;
    row_kind text;
BEGIN
    IF TG_TABLE_NAME = 'products' THEN
        IF TG_OP = 'DELETE' THEN
            row_org := OLD.org_id;
            row_product := OLD.id;
        ELSE
            row_org := NEW.org_id;
            row_product := NEW.id;
        END IF;
    ELSIF TG_OP = 'DELETE' THEN
        row_org := OLD.org_id;
        row_product := OLD.product_id;
    ELSE
        row_org := NEW.org_id;
        row_product := NEW.product_id;
    END IF;

    row_kind := CASE WHEN TG_OP = 'DELETE' AND TG_TABLE_NAME = 'products' THEN 'delete' ELSE 'upsert' END;
    INSERT INTO catalog_change_feed (org_id, store_id, product_id, change_kind)
    SELECT row_org, sp.store_id, row_product, row_kind
    FROM store_prices sp
    WHERE (sp.org_id, sp.product_id) = (row_org, row_product);
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END;
$function$;

CREATE TRIGGER catalog_change_store_prices
AFTER INSERT OR UPDATE OR DELETE ON store_prices
FOR EACH ROW EXECUTE FUNCTION record_store_catalog_change();

CREATE TRIGGER catalog_change_stock_lots
AFTER INSERT OR UPDATE OR DELETE ON stock_lots
FOR EACH ROW EXECUTE FUNCTION record_store_catalog_change();

CREATE TRIGGER catalog_change_lot_markdowns
AFTER INSERT OR UPDATE OR DELETE ON lot_retail_settings
FOR EACH ROW EXECUTE FUNCTION record_store_catalog_change();

CREATE TRIGGER catalog_change_quantity_promotions
AFTER INSERT OR UPDATE OR DELETE ON store_quantity_promotions
FOR EACH ROW EXECUTE FUNCTION record_store_catalog_change();

CREATE TRIGGER catalog_change_products
AFTER INSERT OR UPDATE OR DELETE ON products
FOR EACH ROW EXECUTE FUNCTION record_shared_catalog_change();

CREATE TRIGGER catalog_change_product_barcodes
AFTER INSERT OR UPDATE OR DELETE ON product_barcodes
FOR EACH ROW EXECUTE FUNCTION record_shared_catalog_change();

INSERT INTO catalog_change_feed (org_id, store_id, product_id, change_kind)
SELECT org_id, store_id, product_id, CASE WHEN active THEN 'upsert' ELSE 'delete' END
FROM store_prices
ORDER BY org_id, store_id, product_id;

GRANT SELECT ON catalog_change_feed TO kasa_api;

COMMIT;
