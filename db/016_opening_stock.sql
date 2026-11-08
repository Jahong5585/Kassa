BEGIN;

ALTER TABLE stock_operations DROP CONSTRAINT stock_operations_kind_check;
ALTER TABLE stock_operations ADD CHECK (kind IN
    ('writeOffStock', 'writeOffQuarantine', 'quarantine', 'inventory', 'supplierReturn', 'opening'));

CREATE TABLE opening_stock_batches (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    id uuid NOT NULL,
    opening_date date NOT NULL,
    source_reference text NOT NULL CHECK (char_length(source_reference) BETWEEN 1 AND 300),
    PRIMARY KEY (org_id, store_id, id),
    FOREIGN KEY (org_id, store_id, id) REFERENCES stock_operations
);

CREATE TABLE opening_stock_lines (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    operation_id uuid NOT NULL,
    product_id uuid NOT NULL,
    lot_id uuid NOT NULL,
    expiry_date date,
    total_cost numeric(16,2) CHECK (total_cost >= 0),
    PRIMARY KEY (org_id, store_id, operation_id, product_id, lot_id),
    FOREIGN KEY (org_id, store_id, operation_id) REFERENCES opening_stock_batches,
    FOREIGN KEY (org_id, store_id, operation_id, product_id, lot_id) REFERENCES stock_operation_lines
);

GRANT SELECT, INSERT ON opening_stock_batches, opening_stock_lines TO kasa_api;

COMMIT;
