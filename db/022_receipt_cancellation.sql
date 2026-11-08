BEGIN;

ALTER TABLE receiving_documents DROP CONSTRAINT receiving_documents_status_check;
ALTER TABLE receiving_documents ADD CONSTRAINT receiving_documents_status_check
    CHECK (status IN ('draft', 'submitted', 'posted', 'cancelled'));
ALTER TABLE receiving_documents
    ADD COLUMN cancelled_by uuid,
    ADD COLUMN cancelled_at timestamptz,
    ADD COLUMN cancellation_reason text CHECK (char_length(cancellation_reason) BETWEEN 1 AND 500),
    ADD FOREIGN KEY (org_id, cancelled_by, store_id) REFERENCES staff_stores (org_id, staff_id, store_id),
    ADD CHECK (
        (status = 'cancelled' AND cancelled_by IS NOT NULL AND cancelled_at IS NOT NULL AND cancellation_reason IS NOT NULL)
        OR (status <> 'cancelled' AND cancelled_by IS NULL AND cancelled_at IS NULL AND cancellation_reason IS NULL)
    );

DROP INDEX receiving_unique_invoice;
CREATE UNIQUE INDEX receiving_unique_invoice
    ON receiving_documents (org_id, store_id, lower(supplier_ref), lower(invoice_number), invoice_date)
    WHERE status <> 'cancelled';

GRANT UPDATE (cancelled_by, cancelled_at, cancellation_reason) ON receiving_documents TO kasa_api;

COMMIT;
