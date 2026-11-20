BEGIN;

ALTER TABLE products ADD COLUMN shelf_life_days integer
    CHECK (shelf_life_days IS NULL OR (shelf_life_days BETWEEN 1 AND 3650 AND requires_expiry));
GRANT UPDATE (shelf_life_days) ON products TO kasa_api;
CREATE INDEX product_barcodes_shared_lookup ON product_barcodes (code, org_id, product_id);

-- A durable response makes retries safe even after subsequent catalogue/price edits.
CREATE TABLE receiving_product_creations (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    request_id uuid NOT NULL,
    body jsonb NOT NULL,
    result jsonb NOT NULL,
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    PRIMARY KEY (org_id, store_id, request_id),
    FOREIGN KEY (org_id, store_id) REFERENCES stores (org_id, id),
    FOREIGN KEY (org_id, created_by) REFERENCES staff (org_id, id)
);
GRANT SELECT, INSERT ON receiving_product_creations TO kasa_api;

COMMIT;
