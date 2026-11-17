INSERT INTO staff_stores (org_id, staff_id, store_id, active)
VALUES ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000003050',
        '00000000-0000-0000-0000-000000000013', true)
ON CONFLICT (org_id, staff_id, store_id) DO UPDATE SET active = true;

INSERT INTO products (org_id, id, name, barcode, unit, requires_expiry)
VALUES ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000009023',
        'Operator checkout fixture', '2100000090231', 'piece', false);
INSERT INTO product_barcodes (org_id, code, product_id, quantity_factor)
VALUES ('00000000-0000-0000-0000-000000000001', '2100000090231',
        '00000000-0000-0000-0000-000000009023', 1);
INSERT INTO store_prices (org_id, store_id, product_id, unit_price, version)
VALUES ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000011',
        '00000000-0000-0000-0000-000000009023', 1000, 1);
INSERT INTO stock_lots (org_id, store_id, product_id, id, quantity)
VALUES ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000011',
        '00000000-0000-0000-0000-000000009023', '00000000-0000-0000-0000-000000009024', 10);
