-- Execute as the owner of a disposable development database only.
\getenv register_key POS_TEST_REGISTER_KEY
BEGIN;
INSERT INTO organizations (id, name) VALUES
 ('00000000-0000-0000-0000-000000000001', 'Учебный магазин А'),
 ('00000000-0000-0000-0000-000000000002', 'Учебный магазин Б');
INSERT INTO stores (org_id, id, name) VALUES
 ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000011', 'А'),
 ('00000000-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000012', 'Б');
INSERT INTO registers (org_id, store_id, id, name, key_digest) VALUES
 ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000011',
  '00000000-0000-0000-0000-000000000021', 'А1', sha256(convert_to(:'register_key', 'UTF8'))),
 ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000011',
  '00000000-0000-0000-0000-000000000022', 'А2', sha256(convert_to(:'register_key', 'UTF8'))),
 ('00000000-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000012',
  '00000000-0000-0000-0000-000000000023', 'Б1', sha256(convert_to(:'register_key', 'UTF8')));
INSERT INTO staff (org_id, id, subject, role) VALUES
 ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000031', 'cashier-a', 'cashier'),
 ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000032', 'cashier-b', 'cashier'),
 ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000033', 'receiver', 'receiver'),
 ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000034', 'admin-a', 'admin'),
 ('00000000-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000035', 'admin-foreign', 'admin'),
 ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000036', 'admin-replenishment', 'admin'),
 ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000037', 'admin-settlements', 'admin'),
 ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000038', 'admin-inventory', 'admin'),
 ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000039', 'receiver-exceptions', 'receiver'),
 ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000040', 'cashier-diagnostics', 'cashier'),
 ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000003044', 'cashier-diagnostics-peer', 'cashier');
INSERT INTO staff_stores (org_id, staff_id, store_id)
 SELECT s.org_id, s.id, t.id FROM staff s JOIN stores t ON t.org_id = s.org_id;
INSERT INTO cashier_registers (org_id, staff_id, store_id, register_id)
 SELECT s.org_id, s.id, r.store_id, r.id
 FROM staff s JOIN registers r ON r.org_id = s.org_id
 WHERE (s.subject IN ('cashier-a', 'cashier-diagnostics', 'cashier-diagnostics-peer') AND r.name = 'А1')
    OR (s.subject = 'cashier-b' AND r.name = 'А2');
INSERT INTO products (org_id, id, name, barcode, unit, requires_expiry) VALUES
 ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000041', 'Учебная кола', '001', 'piece', true),
 ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000042', 'Учебные яблоки', '002', 'kg', false),
 ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000043', 'Учебный весовой товар', '003', 'kg', false);
INSERT INTO store_prices (org_id, store_id, product_id, unit_price, version)
 SELECT p.org_id, s.id, p.id, CASE p.barcode WHEN '001' THEN 17500 WHEN '003' THEN 12.50 ELSE 18000 END, 1
 FROM products p JOIN stores s ON s.org_id = p.org_id;
INSERT INTO stock_lots (org_id, store_id, product_id, id, expiry_date, quantity)
 SELECT p.org_id, s.id, p.id, '00000000-0000-0000-0000-000000000051',
   CASE p.barcode WHEN '001' THEN current_date + 30 ELSE NULL END,
   CASE p.barcode WHEN '001' THEN 3 ELSE 10 END
 FROM products p JOIN stores s ON s.org_id = p.org_id;
INSERT INTO stock_lots (org_id, store_id, product_id, id, expiry_date, quantity)
 SELECT p.org_id, s.id, p.id, '00000000-0000-0000-0000-000000000052', current_date - 2, 5
 FROM products p JOIN stores s ON s.org_id = p.org_id WHERE p.barcode = '001';
INSERT INTO stock_lots (org_id, store_id, product_id, id, expiry_date, quantity)
 SELECT p.org_id, s.id, p.id, '00000000-0000-0000-0000-000000000053', NULL, 2
 FROM products p JOIN stores s ON s.org_id = p.org_id WHERE p.barcode = '001';
-- Sales at local noon on eight complete days, with one documented stockout day.
INSERT INTO products (org_id, id, name, barcode, unit, requires_expiry)
VALUES ('00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000144',
    'Учебная кола для закупок','00999','piece',true);
INSERT INTO store_prices (org_id, store_id, product_id, unit_price, version)
VALUES ('00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000011',
    '00000000-0000-0000-0000-000000000144',10000,1);
INSERT INTO stock_lots (org_id, store_id, product_id, id, expiry_date, quantity)
SELECT sp.org_id, sp.store_id, sp.product_id, ('00000000-0000-0000-0000-000000000' || lot)::uuid,
    (now() AT TIME ZONE 'Asia/Tashkent')::date + days, units
FROM store_prices sp CROSS JOIN (VALUES (154,30,15),(155,0,10),(156,-1,5),(157,NULL,2)) AS lots(lot,days,units)
WHERE sp.product_id = '00000000-0000-0000-0000-000000000144';

WITH historical AS (
    SELECT ('00000000-0000-0000-0000-000000000' || (810 + d))::uuid AS shift_id,
        ('00000000-0000-0000-0000-000000000' || (820 + d))::uuid AS sale_id,
        (((now() AT TIME ZONE 'Asia/Tashkent')::date - d) + time '12:00') AT TIME ZONE 'Asia/Tashkent' AS sold_at,
        CASE WHEN d = 3 THEN 5 ELSE 20 END AS quantity
    FROM generate_series(1, 8) d
), open_close AS (
    INSERT INTO shifts (org_id, store_id, register_id, id, cashier_id, opened_at, closed_at, opening_cash, counted_cash)
    SELECT '00000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000011',
        '00000000-0000-0000-0000-000000000021',shift_id,'00000000-0000-0000-0000-000000000031',
        sold_at - interval '1 hour', sold_at + interval '1 hour', 0, 0
    FROM historical RETURNING org_id, store_id, register_id, id, cashier_id
), documents AS (
    INSERT INTO sales (org_id, store_id, register_id, id, shift_id, cashier_id, method, total, request_digest, created_at)
    SELECT sh.org_id, sh.store_id, sh.register_id, h.sale_id, sh.id, sh.cashier_id, 'manualCashless',
        h.quantity * 10000, sha256(convert_to(h.sale_id::text, 'UTF8')), h.sold_at
    FROM open_close sh JOIN historical h ON h.shift_id = sh.id RETURNING org_id, store_id, register_id, id
)
INSERT INTO sale_lines (org_id, store_id, register_id, sale_id, product_id, lot_id, name, unit, quantity,
    unit_price, line_total)
SELECT d.org_id, d.store_id, d.register_id, d.id, '00000000-0000-0000-0000-000000000144',
    '00000000-0000-0000-0000-000000000154','Учебная кола для закупок','piece',h.quantity,10000,h.quantity * 10000
FROM documents d JOIN historical h ON h.sale_id = d.id;
INSERT INTO stores (org_id, id, name) VALUES
 ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000013', 'Дополнительная точка');
INSERT INTO staff_stores (org_id, staff_id, store_id) VALUES
 ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000038', '00000000-0000-0000-0000-000000000013');
INSERT INTO audit_events (org_id, store_id, staff_id, action, object_id, request_id, occurred_at, object_code)
SELECT '00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000011',
       '00000000-0000-0000-0000-000000000034', 'fixture.audit_tie',
       '00000000-0000-0000-0000-000000000021', 'test-audit-' || n,
       (((now() AT TIME ZONE 'Asia/Tashkent')::date - 2) + time '12:00') AT TIME ZONE 'Asia/Tashkent',
       'private-internal-code'
FROM generate_series(1, 3) AS fixture(n);
INSERT INTO shifts (org_id, store_id, register_id, id, cashier_id, opened_at, closed_at, opening_cash, counted_cash)
VALUES ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000011',
        '00000000-0000-0000-0000-000000000021', '00000000-0000-0000-0000-000000002980',
        '00000000-0000-0000-0000-000000000031', now() - interval '2 days', now() - interval '2 days' + interval '1 hour',
        20, 15);
COMMIT;

-- Test data loaded after migration 006 on a fresh Compose instance.
INSERT INTO product_barcodes (org_id, code, product_id, quantity_factor)
    SELECT org_id, barcode, id, 1 FROM products WHERE barcode IS NOT NULL;
INSERT INTO store_price_history (org_id, store_id, product_id, version, unit_price, active, reason)
    SELECT org_id, store_id, product_id, version, unit_price, active, 'fixture_baseline' FROM store_prices;
