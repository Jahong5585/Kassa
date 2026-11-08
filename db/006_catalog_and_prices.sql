BEGIN;

ALTER TABLE products ALTER COLUMN barcode DROP NOT NULL;
ALTER TABLE products DROP CONSTRAINT products_org_id_barcode_key;
ALTER TABLE products ADD COLUMN revision integer NOT NULL DEFAULT 1 CHECK (revision > 0),
    ADD COLUMN sku text NOT NULL DEFAULT '',
    ADD COLUMN category text NOT NULL DEFAULT '',
    ADD COLUMN brand text NOT NULL DEFAULT '',
    ADD COLUMN supplier_ref text NOT NULL DEFAULT '',
    ADD COLUMN tax_profile text NOT NULL DEFAULT '';
CREATE UNIQUE INDEX products_unique_sku ON products (org_id, lower(sku)) WHERE sku <> '';

CREATE TABLE product_barcodes (
    org_id uuid NOT NULL,
    code text NOT NULL CHECK (code ~ '^[0-9]{1,48}$'),
    product_id uuid NOT NULL,
    quantity_factor numeric(14,3) NOT NULL CHECK (quantity_factor > 0),
    PRIMARY KEY (org_id, code),
    FOREIGN KEY (org_id, product_id) REFERENCES products
);
CREATE INDEX product_barcodes_by_product ON product_barcodes (org_id, product_id, code);
INSERT INTO product_barcodes (org_id, code, product_id, quantity_factor)
    SELECT org_id, barcode, id, 1 FROM products WHERE barcode ~ '^[0-9]{1,48}$';

ALTER TABLE store_prices ALTER COLUMN unit_price DROP NOT NULL;
CREATE TABLE store_price_history (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    product_id uuid NOT NULL,
    version integer NOT NULL CHECK (version > 0),
    unit_price numeric(14,2) CHECK (unit_price > 0),
    active boolean NOT NULL,
    reason text NOT NULL,
    changed_by uuid,
    recorded_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    PRIMARY KEY (org_id, store_id, product_id, version),
    FOREIGN KEY (org_id, store_id, product_id) REFERENCES store_prices,
    FOREIGN KEY (org_id, changed_by, store_id) REFERENCES staff_stores (org_id, staff_id, store_id)
);
INSERT INTO store_price_history (org_id, store_id, product_id, version, unit_price, active, reason)
    SELECT org_id, store_id, product_id, version, unit_price, active, 'migration_baseline' FROM store_prices;

CREATE TABLE product_proposals (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    id uuid NOT NULL,
    proposed_by uuid NOT NULL,
    body jsonb NOT NULL,
    status text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'approved', 'rejected')),
    review_reason text,
    reviewed_by uuid,
    reviewed_at timestamptz,
    published_product_id uuid,
    created_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    PRIMARY KEY (org_id, store_id, id),
    FOREIGN KEY (org_id, proposed_by, store_id) REFERENCES staff_stores (org_id, staff_id, store_id),
    FOREIGN KEY (org_id, reviewed_by, store_id) REFERENCES staff_stores (org_id, staff_id, store_id),
    FOREIGN KEY (org_id, store_id, published_product_id) REFERENCES store_prices (org_id, store_id, product_id),
    CHECK ((status = 'pending') = (reviewed_by IS NULL AND reviewed_at IS NULL AND review_reason IS NULL)),
    CHECK ((status = 'approved') = (published_product_id IS NOT NULL))
);

GRANT SELECT, INSERT ON product_proposals TO kasa_api;
GRANT UPDATE (status, review_reason, reviewed_by, reviewed_at, published_product_id) ON product_proposals TO kasa_api;
GRANT INSERT ON products, store_prices TO kasa_api;
GRANT UPDATE (name, barcode, requires_expiry, revision, sku, category, brand, supplier_ref, tax_profile)
    ON products TO kasa_api;
GRANT UPDATE (unit_price, active, version) ON store_prices TO kasa_api;
GRANT SELECT, INSERT, DELETE ON product_barcodes TO kasa_api;
GRANT SELECT, INSERT ON store_price_history TO kasa_api;
COMMIT;
