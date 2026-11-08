BEGIN;

CREATE INDEX refunds_by_store_period ON refunds (org_id, store_id, created_at);
CREATE INDEX cash_movements_by_store_period ON cash_movements (org_id, store_id, created_at);

COMMIT;
