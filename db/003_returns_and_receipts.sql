BEGIN;

ALTER TABLE stock_lots ADD COLUMN quarantine_quantity numeric(14,3) NOT NULL DEFAULT 0
    CHECK (quarantine_quantity >= 0);

ALTER TABLE audit_events ALTER COLUMN register_id DROP NOT NULL;
ALTER TABLE audit_events ADD FOREIGN KEY (org_id, store_id) REFERENCES stores;

CREATE TABLE refunds (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    id uuid NOT NULL,
    register_id uuid NOT NULL,
    shift_id uuid NOT NULL,
    cashier_id uuid NOT NULL,
    source_register_id uuid NOT NULL,
    sale_id uuid NOT NULL,
    method text NOT NULL CHECK (method IN ('cash', 'manualCashless')),
    total numeric(16,2) NOT NULL CHECK (total >= 0),
    reason text NOT NULL,
    request_digest bytea NOT NULL CHECK (octet_length(request_digest) = 32),
    approved_by uuid,
    created_at timestamptz NOT NULL,
    PRIMARY KEY (org_id, store_id, id),
    UNIQUE (org_id, store_id, id, source_register_id, sale_id),
    FOREIGN KEY (org_id, store_id, register_id, shift_id, cashier_id)
        REFERENCES shifts (org_id, store_id, register_id, id, cashier_id),
    FOREIGN KEY (org_id, store_id, source_register_id, sale_id) REFERENCES sales,
    FOREIGN KEY (org_id, approved_by, store_id) REFERENCES staff_stores (org_id, staff_id, store_id)
);
CREATE INDEX refunds_by_shift ON refunds (org_id, store_id, register_id, shift_id);
CREATE INDEX refunds_by_sale ON refunds (org_id, store_id, source_register_id, sale_id);

CREATE TABLE refund_lines (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    refund_id uuid NOT NULL,
    source_register_id uuid NOT NULL,
    sale_id uuid NOT NULL,
    product_id uuid NOT NULL,
    lot_id uuid NOT NULL,
    quantity numeric(14,3) NOT NULL CHECK (quantity > 0),
    amount numeric(16,2) NOT NULL CHECK (amount >= 0),
    destination text NOT NULL CHECK (destination IN ('stock', 'quarantine')),
    PRIMARY KEY (org_id, store_id, refund_id, product_id, lot_id),
    FOREIGN KEY (org_id, store_id, refund_id, source_register_id, sale_id)
        REFERENCES refunds (org_id, store_id, id, source_register_id, sale_id),
    FOREIGN KEY (org_id, store_id, source_register_id, sale_id, product_id, lot_id)
        REFERENCES sale_lines (org_id, store_id, register_id, sale_id, product_id, lot_id)
);
CREATE INDEX refund_lines_by_sale_line
    ON refund_lines (org_id, store_id, source_register_id, sale_id, product_id, lot_id);

CREATE TABLE refund_approvals (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    id uuid NOT NULL,
    register_id uuid NOT NULL,
    approved_by uuid NOT NULL,
    amount numeric(16,2) NOT NULL CHECK (amount >= 0),
    request_digest bytea NOT NULL CHECK (octet_length(request_digest) = 32),
    PRIMARY KEY (org_id, store_id, id),
    FOREIGN KEY (org_id, store_id, register_id) REFERENCES registers,
    FOREIGN KEY (org_id, approved_by, store_id) REFERENCES staff_stores (org_id, staff_id, store_id)
);

CREATE TABLE purchase_orders (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    id uuid NOT NULL,
    supplier_ref text NOT NULL,
    created_by uuid NOT NULL,
    PRIMARY KEY (org_id, store_id, id),
    FOREIGN KEY (org_id, created_by, store_id) REFERENCES staff_stores (org_id, staff_id, store_id)
);

CREATE TABLE purchase_order_lines (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    order_id uuid NOT NULL,
    product_id uuid NOT NULL,
    ordered numeric(14,3) NOT NULL CHECK (ordered > 0),
    received numeric(14,3) NOT NULL DEFAULT 0 CHECK (received >= 0 AND received <= ordered),
    PRIMARY KEY (org_id, store_id, order_id, product_id),
    FOREIGN KEY (org_id, store_id, order_id) REFERENCES purchase_orders,
    FOREIGN KEY (org_id, store_id, product_id) REFERENCES store_prices
);

CREATE TABLE receiving_documents (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    id uuid NOT NULL,
    created_by uuid NOT NULL,
    supplier_ref text NOT NULL,
    invoice_number text NOT NULL,
    invoice_date date NOT NULL,
    draft_body jsonb NOT NULL,
    creation_body jsonb NOT NULL,
    purchase_order_id uuid,
    status text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'submitted', 'posted')),
    version integer NOT NULL DEFAULT 1 CHECK (version > 0),
    posted_by uuid,
    posted_at timestamptz,
    review_note text,
    PRIMARY KEY (org_id, store_id, id),
    FOREIGN KEY (org_id, created_by, store_id) REFERENCES staff_stores (org_id, staff_id, store_id),
    FOREIGN KEY (org_id, posted_by, store_id) REFERENCES staff_stores (org_id, staff_id, store_id),
    FOREIGN KEY (org_id, store_id, purchase_order_id) REFERENCES purchase_orders,
    CHECK ((status = 'posted') = (posted_at IS NOT NULL AND posted_by IS NOT NULL))
);
CREATE UNIQUE INDEX receiving_unique_invoice
    ON receiving_documents (org_id, store_id, lower(supplier_ref), lower(invoice_number), invoice_date);

CREATE TABLE receiving_movements (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    receipt_id uuid NOT NULL,
    product_id uuid NOT NULL,
    lot_id uuid NOT NULL,
    quantity numeric(14,3) NOT NULL CHECK (quantity > 0),
    PRIMARY KEY (org_id, store_id, receipt_id, product_id, lot_id),
    FOREIGN KEY (org_id, store_id, receipt_id) REFERENCES receiving_documents,
    FOREIGN KEY (org_id, store_id, product_id, lot_id) REFERENCES stock_lots
);

GRANT SELECT, INSERT ON refunds, refund_lines, refund_approvals, receiving_documents TO kasa_api;
GRANT INSERT ON receiving_movements, stock_lots TO kasa_api;
GRANT SELECT, INSERT ON purchase_orders, purchase_order_lines TO kasa_api;
GRANT UPDATE (received) ON purchase_order_lines TO kasa_api;
GRANT UPDATE (quarantine_quantity) ON stock_lots TO kasa_api;
GRANT UPDATE (draft_body, status, version, posted_by, posted_at, review_note) ON receiving_documents TO kasa_api;
COMMIT;
