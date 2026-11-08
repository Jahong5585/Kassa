BEGIN;

CREATE TABLE supplier_payment_allocations (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    supplier_code text NOT NULL,
    id uuid NOT NULL,
    payment_id uuid NOT NULL,
    allocated_by uuid NOT NULL,
    allocated_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    PRIMARY KEY (org_id, store_id, supplier_code, id),
    FOREIGN KEY (org_id, store_id, supplier_code, payment_id) REFERENCES supplier_payments,
    FOREIGN KEY (org_id, allocated_by, store_id) REFERENCES staff_stores (org_id, staff_id, store_id)
);
CREATE INDEX supplier_payment_allocations_by_payment
    ON supplier_payment_allocations (org_id, store_id, supplier_code, payment_id);

CREATE TABLE supplier_payment_allocation_lines (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    supplier_code text NOT NULL,
    allocation_id uuid NOT NULL,
    receipt_id uuid NOT NULL,
    amount numeric(16,2) NOT NULL CHECK (amount > 0),
    PRIMARY KEY (org_id, store_id, supplier_code, allocation_id, receipt_id),
    FOREIGN KEY (org_id, store_id, supplier_code, allocation_id) REFERENCES supplier_payment_allocations,
    FOREIGN KEY (org_id, store_id, receipt_id) REFERENCES receiving_documents
);
CREATE INDEX supplier_payment_allocation_lines_by_receipt
    ON supplier_payment_allocation_lines (org_id, store_id, supplier_code, receipt_id);

CREATE TABLE supplier_payment_allocation_reversals (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    supplier_code text NOT NULL,
    id uuid NOT NULL,
    allocation_id uuid NOT NULL,
    reason text NOT NULL CHECK (char_length(reason) BETWEEN 1 AND 500),
    reversed_by uuid NOT NULL,
    reversed_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    PRIMARY KEY (org_id, store_id, supplier_code, id),
    UNIQUE (org_id, store_id, supplier_code, allocation_id),
    FOREIGN KEY (org_id, store_id, supplier_code, allocation_id) REFERENCES supplier_payment_allocations,
    FOREIGN KEY (org_id, reversed_by, store_id) REFERENCES staff_stores (org_id, staff_id, store_id)
);

CREATE TABLE supplier_invoice_terms (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    receipt_id uuid NOT NULL,
    due_date date,
    version integer NOT NULL CHECK (version > 0),
    PRIMARY KEY (org_id, store_id, receipt_id),
    FOREIGN KEY (org_id, store_id, receipt_id) REFERENCES receiving_documents
);

CREATE INDEX supplier_receipts_by_invoice_date
    ON receiving_documents (org_id, store_id, upper(supplier_ref), invoice_date DESC, id) WHERE status = 'posted';

GRANT SELECT, INSERT ON supplier_payment_allocations, supplier_payment_allocation_lines,
    supplier_payment_allocation_reversals, supplier_invoice_terms TO kasa_api;
GRANT UPDATE (due_date, version) ON supplier_invoice_terms TO kasa_api;

COMMIT;
