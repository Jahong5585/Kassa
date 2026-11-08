-- Run as the owner after fixtures.sql on an isolated database.
DO $$
DECLARE
    org uuid := '00000000-0000-0000-0000-000000000001';
    store uuid := '00000000-0000-0000-0000-000000000011';
    register_a uuid := '00000000-0000-0000-0000-000000000021';
    register_b uuid := '00000000-0000-0000-0000-000000000022';
    cashier_a uuid := '00000000-0000-0000-0000-000000000031';
    cashier_b uuid := '00000000-0000-0000-0000-000000000032';
    shift_a uuid := '00000000-0000-0000-0000-000000000940';
    shift_b uuid := '00000000-0000-0000-0000-000000000941';
    sale_a uuid := '00000000-0000-0000-0000-000000000942';
    sale_b uuid := '00000000-0000-0000-0000-000000000943';
    refund uuid := '00000000-0000-0000-0000-000000000944';
    product uuid := '00000000-0000-0000-0000-000000000042';
    lot uuid := '00000000-0000-0000-0000-000000000051';
    midnight timestamptz := ((now() AT TIME ZONE 'Asia/Tashkent')::date - 14)::timestamp AT TIME ZONE 'Asia/Tashkent';
    digest bytea := sha256(convert_to('sales_period_fixture', 'UTF8'));
BEGIN
    INSERT INTO shifts (org_id, store_id, register_id, id, cashier_id, opened_at, closed_at, opening_cash, counted_cash)
    VALUES (org, store, register_a, shift_a, cashier_a, midnight - interval '1 hour', midnight, 1000, 1120),
           (org, store, register_b, shift_b, cashier_b, midnight, midnight + interval '1 hour', 2000, 2305);
    INSERT INTO sales (org_id, store_id, register_id, id, shift_id, cashier_id, method, total,
        tendered, change_due, request_digest, created_at, discount_reason)
    VALUES (org, store, register_a, sale_a, shift_a, cashier_a, 'cash', 120, 200, 80, digest,
            midnight - interval '1 second', 'Стендовая скидка'),
           (org, store, register_b, sale_b, shift_b, cashier_b, 'manualCashless', 50, NULL, NULL, digest, midnight, NULL);
    INSERT INTO sale_lines (org_id, store_id, register_id, sale_id, product_id, lot_id,
        name, unit, quantity, unit_price, line_total, discount_amount, price_version)
    VALUES (org, store, register_a, sale_a, product, lot, 'Учебные яблоки', 'kg', 0.6, 150, 75, 15, 1),
           (org, store, register_a, sale_a, '00000000-0000-0000-0000-000000000043', lot,
            'Учебный весовой товар', 'kg', 0.3, 200, 45, 15, 1),
           (org, store, register_b, sale_b, product, lot, 'Учебные яблоки', 'kg', 1, 50, 50, 0, 1);
    INSERT INTO refunds (org_id, store_id, id, register_id, shift_id, cashier_id, source_register_id,
        sale_id, method, total, reason, request_digest, created_at)
    VALUES (org, store, refund, register_b, shift_b, cashier_b, register_a, sale_a, 'cash', 120,
            'Учебный возврат на другой кассе', digest, midnight);
    INSERT INTO refund_lines (org_id, store_id, refund_id, source_register_id, sale_id, product_id, lot_id,
        quantity, amount, destination)
    VALUES (org, store, refund, register_a, sale_a, product, lot, 0.6, 75, 'stock'),
           (org, store, refund, register_a, sale_a, '00000000-0000-0000-0000-000000000043', lot, 0.3, 45, 'stock');
    INSERT INTO cash_movements (org_id, store_id, register_id, shift_id, id, cashier_id, direction, amount,
        reason, created_at)
    VALUES (org, store, register_b, shift_b, '00000000-0000-0000-0000-000000000945', cashier_b,
            'in', 500, 'Размен', midnight + interval '1 second'),
           (org, store, register_b, shift_b, '00000000-0000-0000-0000-000000000946', cashier_b,
            'out', 75, 'Инкассация', midnight + interval '2 seconds');
END
$$;

DO $$
DECLARE
    org uuid := '00000000-0000-0000-0000-000000000001';
    store uuid := '00000000-0000-0000-0000-000000000011';
    register_a uuid := '00000000-0000-0000-0000-000000000021';
    cashier uuid := '00000000-0000-0000-0000-000000000031';
    admin uuid := '00000000-0000-0000-0000-000000000036';
    shift uuid := '00000000-0000-0000-0000-000000000980';
    sale_a uuid := '00000000-0000-0000-0000-000000000981';
    sale_b uuid := '00000000-0000-0000-0000-000000000982';
    refund uuid := '00000000-0000-0000-0000-000000000983';
    writeoff uuid := '00000000-0000-0000-0000-000000000984';
    shortage uuid := '00000000-0000-0000-0000-000000000985';
    unvalued uuid := '00000000-0000-0000-0000-000000000986';
    product uuid := '00000000-0000-0000-0000-000000000041';
    other_product uuid := '00000000-0000-0000-0000-000000000042';
    lot uuid := '00000000-0000-0000-0000-000000000051';
    midnight timestamptz := ((now() AT TIME ZONE 'Asia/Tashkent')::date - 64)::timestamp AT TIME ZONE 'Asia/Tashkent';
    digest bytea := sha256(convert_to('profit_fixture', 'UTF8'));
BEGIN
    INSERT INTO shifts (org_id, store_id, register_id, id, cashier_id, opened_at, closed_at, opening_cash, counted_cash)
    VALUES (org, store, register_a, shift, cashier, midnight - interval '1 hour', midnight + interval '1 hour', 200, 275);
    INSERT INTO sales (org_id, store_id, register_id, id, shift_id, cashier_id, method, total,
        tendered, change_due, request_digest, created_at, discount_reason)
    VALUES (org, store, register_a, sale_a, shift, cashier, 'cash', 200, 200, 0, digest,
            midnight - interval '1 second', 'Учебная скидка'),
           (org, store, register_a, sale_b, shift, cashier, 'manualCashless', 40, NULL, NULL, digest, midnight, NULL);
    INSERT INTO sale_lines (org_id, store_id, register_id, sale_id, product_id, lot_id,
        name, unit, quantity, unit_price, line_total, discount_amount, price_version, inventory_cost)
    VALUES (org, store, register_a, sale_a, product, lot, 'Кола', 'piece', 2, 55, 100, 10, 1, 50),
           (org, store, register_a, sale_a, other_product, lot, 'Яблоки', 'kg', 1, 100, 100, 0, 1, 50),
           (org, store, register_a, sale_b, other_product, lot, 'Яблоки', 'kg', 1, 40, 40, 0, 1, 12);
    INSERT INTO refunds (org_id, store_id, id, register_id, shift_id, cashier_id, source_register_id,
        sale_id, method, total, reason, request_digest, created_at)
    VALUES (org, store, refund, register_a, shift, cashier, register_a, sale_a, 'cash', 50,
            'Частичный возврат', digest, midnight);
    INSERT INTO refund_lines (org_id, store_id, refund_id, source_register_id, sale_id, product_id, lot_id,
        quantity, amount, destination, inventory_cost)
    VALUES (org, store, refund, register_a, sale_a, product, lot, 1, 50, 'quarantine', 25);
    INSERT INTO cash_movements (org_id, store_id, register_id, shift_id, id, cashier_id, direction, amount, reason, created_at)
    VALUES (org, store, register_a, shift, '00000000-0000-0000-0000-000000000987', cashier,
            'out', 75, 'Инкассация', midnight);
    INSERT INTO stock_operations (org_id, store_id, id, kind, reason, actor_id, created_at)
    VALUES (org, store, writeoff, 'writeOffQuarantine', 'Порча', admin, midnight - interval '1 second'),
           (org, store, shortage, 'inventory', 'Недостача', admin, midnight),
           (org, store, unvalued, 'writeOffStock', 'Порча без оценки', admin, midnight + interval '1 day');
    INSERT INTO stock_operation_lines (org_id, store_id, operation_id, product_id, lot_id, stock_before,
        stock_after, quarantine_before, quarantine_after, inventory_cost_delta)
    VALUES (org, store, writeoff, product, lot, 2, 2, 1, 0, -9.50),
           (org, store, shortage, other_product, lot, 2, 1.9, 0, 0, -2.50),
           (org, store, shortage, product, lot, 2, 1, 0, 1, 0),
           (org, store, unvalued, product, lot, 1, 0, 0, 0, NULL);
END
$$;
