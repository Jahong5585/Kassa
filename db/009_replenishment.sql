BEGIN;

CREATE TABLE confirmed_stockout_days (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    product_id uuid NOT NULL,
    day date NOT NULL,
    confirmed boolean NOT NULL,
    reason text NOT NULL CHECK (char_length(reason) BETWEEN 1 AND 500),
    version integer NOT NULL CHECK (version > 0),
    recorded_by uuid NOT NULL,
    recorded_at timestamptz NOT NULL,
    PRIMARY KEY (org_id, store_id, product_id, day),
    FOREIGN KEY (org_id, store_id, product_id) REFERENCES store_prices,
    FOREIGN KEY (org_id, recorded_by, store_id) REFERENCES staff_stores (org_id, staff_id, store_id)
);
GRANT SELECT, INSERT ON confirmed_stockout_days TO kasa_api;
GRANT UPDATE (confirmed, reason, version, recorded_by, recorded_at) ON confirmed_stockout_days TO kasa_api;

CREATE INDEX purchase_order_lines_open_by_product
    ON purchase_order_lines (org_id, store_id, product_id, order_id) WHERE received < ordered;

COMMIT;
