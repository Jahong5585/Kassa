BEGIN;

CREATE TABLE replenishment_refresh_events (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    product_id uuid NOT NULL,
    FOREIGN KEY (org_id, store_id) REFERENCES stores
);
CREATE INDEX replenishment_refresh_events_by_product
    ON replenishment_refresh_events (org_id, store_id, product_id);

CREATE INDEX replenishment_policies_by_product
    ON replenishment_policies (org_id, store_id, product_id);

CREATE TABLE replenishment_snapshots (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    supplier_code text NOT NULL,
    supplier_sku text NOT NULL,
    policy_version integer NOT NULL CHECK (policy_version > 0),
    calculated_at timestamptz NOT NULL,
    refresh_at timestamptz NOT NULL,
    recommendation jsonb CHECK (jsonb_typeof(recommendation) = 'object'),
    error_code text,
    PRIMARY KEY (org_id, store_id, supplier_code, supplier_sku),
    FOREIGN KEY (org_id, store_id, supplier_code, supplier_sku) REFERENCES replenishment_policies,
    CHECK ((recommendation IS NULL) <> (error_code IS NULL)),
    CHECK (refresh_at > calculated_at)
);

GRANT SELECT, INSERT, DELETE ON replenishment_refresh_events TO kasa_api;
GRANT USAGE ON SEQUENCE replenishment_refresh_events_id_seq TO kasa_api;
GRANT SELECT, INSERT ON replenishment_snapshots TO kasa_api;
GRANT UPDATE (policy_version, calculated_at, refresh_at, recommendation, error_code) ON replenishment_snapshots TO kasa_api;

CREATE FUNCTION replenishment_next_refresh_at(at_time timestamptz, zone text, cutoff time)
RETURNS timestamptz LANGUAGE sql STABLE SET search_path = pg_catalog AS $$
    SELECT CASE
        WHEN (local_time::date + cutoff) AT TIME ZONE zone > at_time
            THEN (local_time::date + cutoff) AT TIME ZONE zone
        ELSE ((local_time::date + 1)::timestamp) AT TIME ZONE zone
    END
    FROM (SELECT at_time AT TIME ZONE zone AS local_time) AS clock
$$;

CREATE FUNCTION queue_replenishment_product() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog, public AS $$
BEGIN
    INSERT INTO public.replenishment_refresh_events (org_id, store_id, product_id)
    VALUES (NEW.org_id, NEW.store_id, NEW.product_id);
    RETURN NULL;
END
$$;

CREATE TRIGGER replenishment_new_stock_lot AFTER INSERT ON stock_lots
FOR EACH ROW EXECUTE FUNCTION queue_replenishment_product();
CREATE TRIGGER replenishment_changed_stock_lot AFTER UPDATE ON stock_lots
FOR EACH ROW WHEN ((OLD.quantity, OLD.quarantine_quantity, OLD.quarantined, OLD.expiry_date)
    IS DISTINCT FROM (NEW.quantity, NEW.quarantine_quantity, NEW.quarantined, NEW.expiry_date))
EXECUTE FUNCTION queue_replenishment_product();

CREATE TRIGGER replenishment_new_incoming_line AFTER INSERT ON purchase_order_lines
FOR EACH ROW EXECUTE FUNCTION queue_replenishment_product();
CREATE TRIGGER replenishment_changed_incoming_line AFTER UPDATE ON purchase_order_lines
FOR EACH ROW WHEN ((OLD.ordered, OLD.received) IS DISTINCT FROM (NEW.ordered, NEW.received))
EXECUTE FUNCTION queue_replenishment_product();

CREATE TRIGGER replenishment_new_stockout AFTER INSERT ON confirmed_stockout_days
FOR EACH ROW WHEN (NEW.confirmed) EXECUTE FUNCTION queue_replenishment_product();
CREATE TRIGGER replenishment_changed_stockout AFTER UPDATE ON confirmed_stockout_days
FOR EACH ROW WHEN (OLD.confirmed IS DISTINCT FROM NEW.confirmed)
EXECUTE FUNCTION queue_replenishment_product();

CREATE TRIGGER replenishment_new_supplier_price AFTER INSERT ON supplier_prices
FOR EACH ROW EXECUTE FUNCTION queue_replenishment_product();

CREATE TRIGGER replenishment_changed_store_product AFTER UPDATE ON store_prices
FOR EACH ROW WHEN (OLD.active IS DISTINCT FROM NEW.active)
EXECUTE FUNCTION queue_replenishment_product();

CREATE FUNCTION queue_replenishment_catalog_product() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog, public AS $$
BEGIN
    INSERT INTO public.replenishment_refresh_events (org_id, store_id, product_id)
    SELECT org_id, store_id, product_id FROM public.store_prices
    WHERE (org_id, product_id) = (NEW.org_id, NEW.id);
    RETURN NULL;
END
$$;
CREATE TRIGGER replenishment_changed_catalog_product AFTER UPDATE ON products
FOR EACH ROW WHEN ((OLD.name, OLD.unit, OLD.requires_expiry) IS DISTINCT FROM (NEW.name, NEW.unit, NEW.requires_expiry))
EXECUTE FUNCTION queue_replenishment_catalog_product();

CREATE FUNCTION queue_replenishment_supplier() RETURNS trigger
LANGUAGE plpgsql SET search_path = pg_catalog, public AS $$
BEGIN
    INSERT INTO public.replenishment_refresh_events (org_id, store_id, product_id)
    SELECT DISTINCT org_id, store_id, product_id FROM public.replenishment_policies
    WHERE (org_id, store_id, supplier_code) = (NEW.org_id, NEW.store_id, NEW.code);
    RETURN NULL;
END
$$;
CREATE TRIGGER replenishment_changed_supplier AFTER UPDATE ON suppliers
FOR EACH ROW WHEN ((OLD.active, OLD.lead_time_days) IS DISTINCT FROM (NEW.active, NEW.lead_time_days))
EXECUTE FUNCTION queue_replenishment_supplier();

COMMIT;
