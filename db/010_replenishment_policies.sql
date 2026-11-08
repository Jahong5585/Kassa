BEGIN;

CREATE TABLE replenishment_policies (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    supplier_code text NOT NULL,
    supplier_sku text NOT NULL CHECK (supplier_sku ~ '^[A-Z0-9][A-Z0-9_-]{0,31}$'),
    product_id uuid NOT NULL,
    version integer NOT NULL CHECK (version > 0),
    settings jsonb NOT NULL CHECK (jsonb_typeof(settings) = 'object'),
    updated_by uuid NOT NULL,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (org_id, store_id, supplier_code, supplier_sku),
    FOREIGN KEY (org_id, store_id, supplier_code) REFERENCES suppliers,
    FOREIGN KEY (org_id, store_id, product_id) REFERENCES store_prices,
    FOREIGN KEY (org_id, updated_by, store_id) REFERENCES staff_stores (org_id, staff_id, store_id)
);

ALTER TABLE purchase_order_revisions ADD COLUMN replenishment jsonb
    CHECK (replenishment IS NULL OR jsonb_typeof(replenishment) = 'object');

GRANT SELECT, INSERT ON replenishment_policies TO kasa_api;
GRANT UPDATE (version, settings, updated_by, updated_at) ON replenishment_policies TO kasa_api;

COMMIT;
