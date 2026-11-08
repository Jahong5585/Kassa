-- The smoke test registers device 29, records one sale, closes its shift and revokes the device.
SELECT s.id AS sale_id, s.method, s.total, s.created_at, sh.id AS shift_id, sh.closed_at, sh.counted_cash,
       r.active AS register_active, l.product_id, l.lot_id, -l.quantity AS movement_quantity,
       sl.quantity AS remaining_quantity, sl.quarantine_quantity, sl.inventory_value, a.id AS audit_id
FROM sales s
JOIN shifts sh ON (sh.org_id, sh.store_id, sh.register_id, sh.id) =
    (s.org_id, s.store_id, s.register_id, s.shift_id)
JOIN registers r ON (r.org_id, r.store_id, r.id) = (s.org_id, s.store_id, s.register_id)
JOIN sale_lines l ON (l.org_id, l.store_id, l.register_id, l.sale_id) =
    (s.org_id, s.store_id, s.register_id, s.id)
JOIN stock_lots sl ON (sl.org_id, sl.store_id, sl.product_id, sl.id) = (l.org_id, l.store_id, l.product_id, l.lot_id)
JOIN audit_events a ON (a.org_id, a.store_id, a.register_id, a.object_id) =
    (s.org_id, s.store_id, s.register_id, s.id) AND a.action = 'sale.committed'
WHERE s.org_id = '00000000-0000-0000-0000-000000000001'
  AND s.store_id = '00000000-0000-0000-0000-000000000011'
  AND s.register_id = '00000000-0000-0000-0000-000000000029';
