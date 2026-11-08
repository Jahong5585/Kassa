-- Run as the owner after fixtures.sql on an isolated database.
BEGIN;
DO $$
BEGIN
    IF replenishment_next_refresh_at('2026-11-07 06:00Z', 'Asia/Tashkent', '15:30')
            IS DISTINCT FROM '2026-11-07 10:30Z'::timestamptz
        OR replenishment_next_refresh_at('2026-11-07 10:30Z', 'Asia/Tashkent', '15:30')
            IS DISTINCT FROM '2026-11-07 19:00Z'::timestamptz
        OR replenishment_next_refresh_at('2026-11-07 19:00Z', 'Asia/Tashkent', NULL)
            IS DISTINCT FROM '2026-11-08 19:00Z'::timestamptz THEN
        RAISE EXCEPTION 'Unexpected replenishment refresh schedule';
    END IF;
END
$$;

INSERT INTO suppliers (org_id, store_id, code, name, tax_id, phone, email, payment_terms, lead_time_days, active, version)
SELECT org_id, id, 'TIMER-01', 'Фоновый пересчёт', '', '', '', '', 1, false, 1
FROM stores WHERE id = '00000000-0000-0000-0000-000000000011';

INSERT INTO replenishment_policies (org_id, store_id, supplier_code, supplier_sku, product_id, version,
    settings, updated_by, updated_at)
SELECT org_id, store_id, code, 'TIMER-COLA', '00000000-0000-0000-0000-000000000144', 1,
    '{"OrderWeekdays":[1,2,3,4,5,6,7]}'::jsonb, '00000000-0000-0000-0000-000000000036', now() - interval '2 days'
FROM suppliers WHERE code = 'TIMER-01';

-- No policy change or queued product event can account for this refresh.
DELETE FROM replenishment_refresh_events WHERE product_id = '00000000-0000-0000-0000-000000000144';
INSERT INTO replenishment_snapshots (org_id, store_id, supplier_code, supplier_sku, policy_version,
    calculated_at, refresh_at, error_code)
SELECT org_id, store_id, supplier_code, supplier_sku, version,
    now() - interval '2 days', now() - interval '1 day', 'fixture_expired_snapshot'
FROM replenishment_policies WHERE supplier_code = 'TIMER-01';

SAVEPOINT rollback_trial;
UPDATE stock_lots SET quantity = quantity - 1
WHERE product_id = '00000000-0000-0000-0000-000000000144'
  AND id = '00000000-0000-0000-0000-000000000154';
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM replenishment_refresh_events
        WHERE product_id = '00000000-0000-0000-0000-000000000144') THEN
        RAISE EXCEPTION 'Stock update did not queue a replenishment refresh';
    END IF;
END
$$;
ROLLBACK TO SAVEPOINT rollback_trial;
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM replenishment_refresh_events
        WHERE product_id = '00000000-0000-0000-0000-000000000144') THEN
        RAISE EXCEPTION 'Rolled-back stock update left a refresh event';
    END IF;
END
$$;

INSERT INTO replenishment_policies (org_id, store_id, supplier_code, supplier_sku, product_id, version,
    settings, updated_by, updated_at)
SELECT org_id, store_id, code, 'TIMER-QUEUED', '00000000-0000-0000-0000-000000000042', 1,
    '{"OrderWeekdays":[1,2,3,4,5,6,7]}'::jsonb, '00000000-0000-0000-0000-000000000036', now()
FROM suppliers WHERE code = 'TIMER-01';

-- Simulate work committed before startup, with a snapshot whose next scheduled time is still in the future.
INSERT INTO replenishment_snapshots (org_id, store_id, supplier_code, supplier_sku, policy_version,
    calculated_at, refresh_at, error_code)
SELECT org_id, store_id, supplier_code, supplier_sku, version, now(), now() + interval '1 day', 'fixture_queued_snapshot'
FROM replenishment_policies WHERE supplier_code = 'TIMER-01' AND supplier_sku = 'TIMER-QUEUED';
INSERT INTO replenishment_refresh_events (org_id, store_id, product_id)
SELECT org_id, store_id, product_id FROM replenishment_policies WHERE supplier_code = 'TIMER-01' AND supplier_sku = 'TIMER-QUEUED';
COMMIT;
