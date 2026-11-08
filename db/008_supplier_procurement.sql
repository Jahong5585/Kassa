BEGIN;

CREATE TABLE supplier_prices (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    supplier_code text NOT NULL,
    supplier_sku text NOT NULL,
    version integer NOT NULL CHECK (version > 0),
    product_id uuid NOT NULL,
    package_unit text NOT NULL,
    units_per_package numeric(14,3) NOT NULL CHECK (units_per_package > 0),
    package_price numeric(14,2) NOT NULL CHECK (package_price > 0),
    valid_from date NOT NULL,
    valid_through date CHECK (valid_through >= valid_from),
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    PRIMARY KEY (org_id, store_id, supplier_code, supplier_sku, version),
    FOREIGN KEY (org_id, store_id, supplier_code) REFERENCES suppliers,
    FOREIGN KEY (org_id, store_id, product_id) REFERENCES store_prices,
    FOREIGN KEY (org_id, created_by, store_id) REFERENCES staff_stores (org_id, staff_id, store_id)
);

ALTER TABLE purchase_orders ADD COLUMN stage text NOT NULL DEFAULT 'registered'
    CHECK (stage IN ('registered', 'draft', 'approved', 'sent')),
    ADD COLUMN revision integer NOT NULL DEFAULT 1 CHECK (revision > 0),
    ADD COLUMN approved_revision integer,
    ADD COLUMN approved_by uuid,
    ADD COLUMN approved_at timestamptz,
    ADD COLUMN sent_by uuid,
    ADD COLUMN sent_at timestamptz,
    ADD COLUMN dispatch_reference text,
    ADD COLUMN expected_delivery_date date,
    ADD FOREIGN KEY (org_id, approved_by, store_id) REFERENCES staff_stores (org_id, staff_id, store_id),
    ADD FOREIGN KEY (org_id, sent_by, store_id) REFERENCES staff_stores (org_id, staff_id, store_id),
    ADD CHECK ((stage IN ('approved', 'sent')) =
        (approved_revision IS NOT NULL AND approved_by IS NOT NULL AND approved_at IS NOT NULL)),
    ADD CHECK (approved_revision IS NULL OR approved_revision = revision),
    ADD CHECK ((stage = 'sent') = (sent_by IS NOT NULL AND sent_at IS NOT NULL AND dispatch_reference IS NOT NULL)),
    ADD CHECK ((stage = 'registered') = (expected_delivery_date IS NULL));

CREATE TABLE purchase_order_revisions (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    order_id uuid NOT NULL,
    version integer NOT NULL CHECK (version > 0),
    expected_delivery_date date NOT NULL,
    body jsonb NOT NULL,
    total numeric(16,2) NOT NULL CHECK (total > 0),
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    PRIMARY KEY (org_id, store_id, order_id, version),
    FOREIGN KEY (org_id, store_id, order_id) REFERENCES purchase_orders,
    FOREIGN KEY (org_id, created_by, store_id) REFERENCES staff_stores (org_id, staff_id, store_id)
);

ALTER TABLE stock_operations DROP CONSTRAINT stock_operations_kind_check;
ALTER TABLE stock_operations ADD CHECK (kind IN ('writeOffStock', 'writeOffQuarantine', 'quarantine', 'inventory', 'supplierReturn'));

CREATE TABLE supplier_returns (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    id uuid NOT NULL,
    receipt_id uuid NOT NULL,
    credit_amount numeric(16,2) CHECK (credit_amount >= 0),
    PRIMARY KEY (org_id, store_id, id),
    UNIQUE (org_id, store_id, id, receipt_id),
    FOREIGN KEY (org_id, store_id, id) REFERENCES stock_operations,
    FOREIGN KEY (org_id, store_id, receipt_id) REFERENCES receiving_documents
);

CREATE TABLE supplier_return_lines (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    return_id uuid NOT NULL,
    receipt_id uuid NOT NULL,
    product_id uuid NOT NULL,
    lot_id uuid NOT NULL,
    quantity numeric(14,3) NOT NULL CHECK (quantity > 0),
    bucket text NOT NULL CHECK (bucket IN ('stock', 'quarantine')),
    credit_amount numeric(16,2) CHECK (credit_amount >= 0),
    PRIMARY KEY (org_id, store_id, return_id, product_id, lot_id),
    FOREIGN KEY (org_id, store_id, return_id, receipt_id) REFERENCES supplier_returns (org_id, store_id, id, receipt_id),
    FOREIGN KEY (org_id, store_id, receipt_id, product_id, lot_id) REFERENCES receiving_movements
);
CREATE INDEX supplier_return_lines_by_source
    ON supplier_return_lines (org_id, store_id, receipt_id, product_id, lot_id);

CREATE TABLE supplier_payments (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    supplier_code text NOT NULL,
    id uuid NOT NULL,
    amount numeric(16,2) NOT NULL CHECK (amount > 0),
    payment_date date NOT NULL,
    reference text NOT NULL,
    recorded_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    PRIMARY KEY (org_id, store_id, supplier_code, id),
    UNIQUE (org_id, store_id, supplier_code, payment_date, reference),
    FOREIGN KEY (org_id, store_id, supplier_code) REFERENCES suppliers,
    FOREIGN KEY (org_id, recorded_by, store_id) REFERENCES staff_stores (org_id, staff_id, store_id)
);

GRANT SELECT, INSERT ON supplier_prices, purchase_order_revisions, supplier_returns, supplier_return_lines, supplier_payments TO kasa_api;
GRANT UPDATE (stage, revision, approved_revision, approved_by, approved_at, sent_by, sent_at, dispatch_reference,
    expected_delivery_date) ON purchase_orders TO kasa_api;
GRANT SELECT ON receiving_movements, receiving_movement_costs TO kasa_api;
COMMIT;
