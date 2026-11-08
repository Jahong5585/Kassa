-- Run as the owner after fixtures.sql on an isolated database.
DO $$
DECLARE
    org uuid := '00000000-0000-0000-0000-000000000002';
    store uuid := '00000000-0000-0000-0000-000000000012';
    admin uuid := '00000000-0000-0000-0000-000000000035';
    product uuid := '00000000-0000-0000-0000-000000000242';
    lot uuid := '00000000-0000-0000-0000-000000000252';
    movement_time timestamptz :=
        (((now() AT TIME ZONE 'Asia/Tashkent')::date - 35) + time '12:00') AT TIME ZONE 'Asia/Tashkent';
BEGIN
    INSERT INTO products (org_id, id, name, unit, requires_expiry)
    VALUES (org, product, 'Партия для проверки лимита', 'piece', false);
    INSERT INTO store_prices (org_id, store_id, product_id, unit_price, version)
    VALUES (org, store, product, 100, 1);
    INSERT INTO stock_lots (org_id, store_id, product_id, id, quantity)
    VALUES (org, store, product, lot, 0);
    INSERT INTO stock_lots (org_id, store_id, product_id, id, quantity, inventory_value, expiry_date)
    SELECT org, store, product, ('00000000-0000-0000-0000-' || lpad(n::text, 12, '0'))::uuid, 1, 1.23, current_date + 20
    FROM generate_series(5000, 5999) AS g(n);
    INSERT INTO products (org_id, id, name, unit, requires_expiry)
    VALUES (org, '00000000-0000-0000-0000-000000000243', 'Ещё один товар', 'piece', false);
    INSERT INTO store_prices (org_id, store_id, product_id, unit_price, version)
    VALUES (org, store, '00000000-0000-0000-0000-000000000243', 100, 1);
    INSERT INTO stock_lots (org_id, store_id, product_id, id, quantity, expiry_date)
    VALUES (org, store, '00000000-0000-0000-0000-000000000243', '00000000-0000-0000-0000-000000000253', 1, current_date + 20);
    WITH operations AS (
        INSERT INTO stock_operations (org_id, store_id, id, kind, reason, actor_id, created_at)
        SELECT org, store, ('00000000-0000-0000-0000-' || lpad(n::text, 12, '0'))::uuid,
               'writeOffStock', 'Проверка лимита экспорта', admin, movement_time
        FROM generate_series(1, 1001) AS g(n)
        RETURNING org_id, store_id, id
    )
    INSERT INTO stock_operation_lines (org_id, store_id, operation_id, product_id, lot_id,
        stock_before, stock_after, quarantine_before, quarantine_after)
    SELECT org_id, store_id, id, product, lot, 1, 0, 0, 0 FROM operations;
    WITH operations AS (
        INSERT INTO stock_operations (org_id, store_id, id, kind, reason, actor_id, created_at)
        SELECT org, store, ('00000000-0000-0000-0000-' || lpad(n::text, 12, '0'))::uuid,
               'writeOffStock', 'Проверка фонового лимита', admin, movement_time - interval '1 day'
        FROM generate_series(10001, 19000) AS g(n)
        RETURNING org_id, store_id, id
    )
    INSERT INTO stock_operation_lines (org_id, store_id, operation_id, product_id, lot_id,
        stock_before, stock_after, quarantine_before, quarantine_after)
    SELECT org_id, store_id, id, product, lot, 1, 0, 0, 0 FROM operations;
    INSERT INTO stock_movement_exports
        (org_id, store_id, actor_id, id, from_date, to_date, product_id, lot_id, kind, time_zone, request_id)
    VALUES (org, store, admin, '00000000-0000-0000-0000-000000003197',
        (movement_time AT TIME ZONE 'Asia/Tashkent')::date, (movement_time AT TIME ZONE 'Asia/Tashkent')::date,
        product, lot, 'writeOff', 'Asia/Tashkent', 'fixture-export-before-start');
    INSERT INTO stock_movement_exports
        (org_id, store_id, actor_id, id, from_date, to_date, time_zone, request_id, status, finished_at, expires_at, row_count, contents)
    VALUES (org, store, admin, '00000000-0000-0000-0000-000000003198', current_date, current_date,
        'Asia/Tashkent', 'fixture-export-expired', 'ready', now() - interval '25 hours', now() - interval '1 hour',
        0, convert_to('expired', 'UTF8'));
END
$$;

ANALYZE stock_operations;
ANALYZE stock_operation_lines;
ANALYZE stock_lots;
