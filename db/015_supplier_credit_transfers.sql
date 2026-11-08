BEGIN;

CREATE TABLE supplier_credit_transfers (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    supplier_code text NOT NULL,
    id uuid NOT NULL,
    source_receipt_id uuid NOT NULL,
    target_receipt_id uuid NOT NULL,
    amount numeric(16,2) NOT NULL CHECK (amount > 0),
    reason text NOT NULL CHECK (char_length(reason) BETWEEN 1 AND 500),
    recorded_by uuid NOT NULL,
    recorded_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    PRIMARY KEY (org_id, store_id, supplier_code, id),
    CHECK (source_receipt_id <> target_receipt_id),
    FOREIGN KEY (org_id, store_id, supplier_code) REFERENCES suppliers,
    FOREIGN KEY (org_id, store_id, source_receipt_id) REFERENCES receiving_documents,
    FOREIGN KEY (org_id, store_id, target_receipt_id) REFERENCES receiving_documents,
    FOREIGN KEY (org_id, recorded_by, store_id) REFERENCES staff_stores (org_id, staff_id, store_id)
);
CREATE INDEX supplier_credit_transfers_by_source
    ON supplier_credit_transfers (org_id, store_id, supplier_code, source_receipt_id);
CREATE INDEX supplier_credit_transfers_by_target
    ON supplier_credit_transfers (org_id, store_id, supplier_code, target_receipt_id);

CREATE TABLE supplier_credit_transfer_reversals (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    supplier_code text NOT NULL,
    id uuid NOT NULL,
    transfer_id uuid NOT NULL,
    reason text NOT NULL CHECK (char_length(reason) BETWEEN 1 AND 500),
    reversed_by uuid NOT NULL,
    reversed_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    PRIMARY KEY (org_id, store_id, supplier_code, id),
    UNIQUE (org_id, store_id, supplier_code, transfer_id),
    FOREIGN KEY (org_id, store_id, supplier_code, transfer_id) REFERENCES supplier_credit_transfers,
    FOREIGN KEY (org_id, reversed_by, store_id) REFERENCES staff_stores (org_id, staff_id, store_id)
);

GRANT SELECT, INSERT ON supplier_credit_transfers, supplier_credit_transfer_reversals TO kasa_api;

COMMIT;
