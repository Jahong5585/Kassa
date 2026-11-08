BEGIN;

CREATE TABLE stock_valuation_snapshots (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    snapshot_date date NOT NULL,
    id uuid NOT NULL,
    captured_at timestamptz NOT NULL,
    time_zone text NOT NULL,
    captured_by uuid NOT NULL,
    PRIMARY KEY (org_id, store_id, snapshot_date),
    UNIQUE (org_id, store_id, id),
    FOREIGN KEY (org_id, captured_by, store_id) REFERENCES staff_stores (org_id, staff_id, store_id)
);

CREATE TABLE stock_valuation_snapshot_lines (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    snapshot_date date NOT NULL,
    product_id uuid NOT NULL,
    lot_id uuid NOT NULL,
    product_name text NOT NULL,
    unit text NOT NULL,
    stock_quantity numeric(14,3) NOT NULL CHECK (stock_quantity >= 0),
    quarantine_quantity numeric(14,3) NOT NULL CHECK (quarantine_quantity >= 0),
    inventory_value numeric(22,2) CHECK (inventory_value >= 0),
    PRIMARY KEY (org_id, store_id, snapshot_date, product_id, lot_id),
    FOREIGN KEY (org_id, store_id, snapshot_date) REFERENCES stock_valuation_snapshots,
    CHECK (stock_quantity + quarantine_quantity > 0)
);

GRANT SELECT, INSERT ON stock_valuation_snapshots, stock_valuation_snapshot_lines TO kasa_api;

COMMIT;
