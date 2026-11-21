BEGIN;

INSERT INTO products (org_id, id, name, barcode, unit, requires_expiry, category)
VALUES ('00000000-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000241',
        'Контрольный финансовый товар', '9800000000241', 'piece', false, 'Контроль');
INSERT INTO product_barcodes (org_id, code, product_id, quantity_factor)
VALUES ('00000000-0000-0000-0000-000000000002', '9800000000241',
        '00000000-0000-0000-0000-000000000241', 1);
INSERT INTO store_prices (org_id, store_id, product_id, unit_price, version)
VALUES ('00000000-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000012',
        '00000000-0000-0000-0000-000000000241', 8000, 1);
INSERT INTO stock_lots (org_id, store_id, product_id, id, quantity, inventory_value)
VALUES ('00000000-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000012',
        '00000000-0000-0000-0000-000000000241', '00000000-0000-0000-0000-000000000251', 9, 49500);
INSERT INTO shifts (org_id, store_id, register_id, id, cashier_id, opened_at, closed_at, opening_cash, counted_cash)
VALUES ('00000000-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000012',
        '00000000-0000-0000-0000-000000000023', '00000000-0000-0000-0000-000000000261',
        '00000000-0000-0000-0000-000000000035',
        (((statement_timestamp() AT TIME ZONE 'Asia/Tashkent')::date + time '11:00') AT TIME ZONE 'Asia/Tashkent'),
        (((statement_timestamp() AT TIME ZONE 'Asia/Tashkent')::date + time '13:00') AT TIME ZONE 'Asia/Tashkent'), 0, 0);
INSERT INTO sales (org_id, store_id, register_id, id, shift_id, cashier_id, method, total, request_digest, created_at)
VALUES ('00000000-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000012',
        '00000000-0000-0000-0000-000000000023', '00000000-0000-0000-0000-000000000271',
        '00000000-0000-0000-0000-000000000261', '00000000-0000-0000-0000-000000000035',
        'manualCashless', 8000, sha256(convert_to('finance-control-sale', 'UTF8')),
        (((statement_timestamp() AT TIME ZONE 'Asia/Tashkent')::date + time '12:00') AT TIME ZONE 'Asia/Tashkent'));
INSERT INTO sale_lines (org_id, store_id, register_id, sale_id, product_id, lot_id, name, unit, quantity,
    unit_price, line_total, inventory_cost)
VALUES ('00000000-0000-0000-0000-000000000002', '00000000-0000-0000-0000-000000000012',
        '00000000-0000-0000-0000-000000000023', '00000000-0000-0000-0000-000000000271',
        '00000000-0000-0000-0000-000000000241', '00000000-0000-0000-0000-000000000251',
        'Контрольный финансовый товар', 'piece', 1, 8000, 8000, 5500);

COMMIT;
