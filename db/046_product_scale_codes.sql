BEGIN;

CREATE TABLE product_scale_codes (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    product_id uuid NOT NULL,
    code text NOT NULL CHECK (code ~ '^[0-9]{3}$'),
    created_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    updated_by uuid NOT NULL,
    PRIMARY KEY (org_id, store_id, product_id),
    CONSTRAINT product_scale_codes_unique_code UNIQUE (org_id, store_id, code),
    FOREIGN KEY (org_id, store_id, product_id) REFERENCES store_prices (org_id, store_id, product_id),
    FOREIGN KEY (org_id, updated_by, store_id) REFERENCES staff_stores (org_id, staff_id, store_id)
);

CREATE INDEX product_scale_codes_product
    ON product_scale_codes (org_id, product_id, store_id);

CREATE TRIGGER catalog_change_product_scale_codes
AFTER INSERT OR UPDATE OR DELETE ON product_scale_codes
FOR EACH ROW EXECUTE FUNCTION record_store_catalog_change();

GRANT SELECT, INSERT, DELETE ON product_scale_codes TO kasa_api;
GRANT UPDATE (code, updated_at, updated_by) ON product_scale_codes TO kasa_api;

COMMIT;
