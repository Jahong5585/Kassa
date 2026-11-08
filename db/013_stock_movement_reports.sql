BEGIN;

CREATE INDEX receiving_posted_by_store_period ON receiving_documents (org_id, store_id, posted_at)
    WHERE status = 'posted';

COMMIT;
