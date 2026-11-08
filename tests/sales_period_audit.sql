DO $$
BEGIN
    IF has_any_column_privilege('kasa_api', 'public.audit_events', 'SELECT') OR
       has_any_column_privilege('kasa_api', 'public.audit_events', 'UPDATE') OR
       has_table_privilege('kasa_api', 'public.audit_events', 'DELETE') OR
       NOT has_function_privilege('kasa_api', 'public.read_store_audit(uuid,uuid,date,date,text,text,timestamptz,bigint,integer)', 'EXECUTE') THEN
        RAISE EXCEPTION 'Unexpected audit table or projection privileges';
    END IF;
    IF (SELECT count(*) FROM receiving_documents WHERE status = 'cancelled') < 2 OR EXISTS (
        SELECT 1 FROM receiving_documents d
        WHERE d.status = 'cancelled' AND
            (SELECT count(*) FROM audit_events a
             WHERE (a.org_id, a.store_id, a.object_id, a.staff_id) = (d.org_id, d.store_id, d.id, d.cancelled_by)
               AND a.action = 'receipt.cancelled' AND a.request_id <> '') <> 1
    ) THEN
        RAISE EXCEPTION 'Unexpected audit records for cancelled receipts';
    END IF;
    IF (SELECT count(*) FROM audit_events WHERE action = 'reports.sales_csv_exported') <> 1 OR NOT EXISTS (
        SELECT 1 FROM audit_events WHERE action = 'reports.sales_csv_exported'
          AND org_id = '00000000-0000-0000-0000-000000000001'
          AND store_id = '00000000-0000-0000-0000-000000000011'
          AND register_id = '00000000-0000-0000-0000-000000000022'
          AND staff_id = '00000000-0000-0000-0000-000000000036'
          AND object_code LIKE 'admin/%/00000000-0000-0000-0000-000000000032'
          AND request_id <> ''
    ) THEN
        RAISE EXCEPTION 'Unexpected audit record for sales period export';
    END IF;
    IF (SELECT count(*) FROM audit_events WHERE action = 'reports.stock_movements_csv_exported') <> 2 OR NOT EXISTS (
        SELECT 1 FROM audit_events WHERE action = 'reports.stock_movements_csv_exported'
          AND org_id = '00000000-0000-0000-0000-000000000001'
          AND store_id = '00000000-0000-0000-0000-000000000011'
          AND staff_id = '00000000-0000-0000-0000-000000000036'
          AND register_id IS NULL
          AND object_code LIKE 'admin/%/00000000-0000-0000-0000-000000000041/00000000-0000-0000-0000-000000000052/writeOff'
          AND request_id <> ''
    ) THEN
        RAISE EXCEPTION 'Unexpected audit record for stock movement export';
    END IF;
    IF (SELECT count(*) FROM audit_events WHERE action = 'reports.profit_csv_exported') <> 1 OR NOT EXISTS (
        SELECT 1 FROM audit_events WHERE action = 'reports.profit_csv_exported'
          AND org_id = '00000000-0000-0000-0000-000000000001'
          AND store_id = '00000000-0000-0000-0000-000000000011'
          AND staff_id = '00000000-0000-0000-0000-000000000036'
          AND register_id IS NULL AND object_code LIKE 'admin/%/%' AND request_id <> ''
    ) THEN
        RAISE EXCEPTION 'Unexpected audit record for profit export';
    END IF;
    IF (SELECT count(*) FROM audit_events WHERE action = 'reports.stock_losses_csv_exported') <> 1 OR NOT EXISTS (
        SELECT 1 FROM audit_events WHERE action = 'reports.stock_losses_csv_exported'
          AND org_id = '00000000-0000-0000-0000-000000000001'
          AND store_id = '00000000-0000-0000-0000-000000000011'
          AND staff_id = '00000000-0000-0000-0000-000000000036'
          AND register_id IS NULL AND object_code LIKE 'admin/%/%/all/all/all' AND request_id <> ''
    ) THEN
        RAISE EXCEPTION 'Unexpected audit record for stock loss export';
    END IF;
    IF (SELECT count(*) FROM audit_events WHERE action = 'reports.stock_expiry_csv_exported') <> 1 OR NOT EXISTS (
        SELECT 1 FROM audit_events WHERE action = 'reports.stock_expiry_csv_exported'
          AND store_id = '00000000-0000-0000-0000-000000000011'
          AND staff_id = '00000000-0000-0000-0000-000000000036'
          AND object_code = 'admin/all' AND request_id <> ''
    ) THEN
        RAISE EXCEPTION 'Unexpected audit record for stock expiry export';
    END IF;
    IF (SELECT count(*) FROM audit_events WHERE action IN ('register.registered', 'register.key_rotated', 'register.revoked')) <> 3
       OR (SELECT count(DISTINCT action) FROM audit_events WHERE action IN ('register.registered', 'register.key_rotated', 'register.revoked')
         AND store_id = '00000000-0000-0000-0000-000000000011'
         AND object_id = '00000000-0000-0000-0000-000000000029'
         AND object_id = register_id AND staff_id = '00000000-0000-0000-0000-000000000038'
         AND request_id <> ''
         AND (object_code IS NULL OR object_code = '00000000-0000-0000-0000-000000000031')) <> 3 THEN
        RAISE EXCEPTION 'Unexpected audit records for register administration';
    END IF;
    IF (SELECT count(*) FROM audit_events WHERE action = 'staff.store_access_granted') <> 4 OR
       (SELECT count(*) FROM audit_events WHERE action = 'staff.store_access_revoked'
        AND store_id = '00000000-0000-0000-0000-000000000011'
        AND staff_id = '00000000-0000-0000-0000-000000000038' AND request_id <> '') <> 2 THEN
        RAISE EXCEPTION 'Unexpected audit records for staff access changes';
    END IF;
    IF (SELECT count(*) FROM audit_events WHERE action = 'reports.stock_valuation_csv_exported') <> 3 OR
        (SELECT count(*) FROM audit_events WHERE action = 'reports.stock_valuation_csv_exported'
          AND store_id = '00000000-0000-0000-0000-000000000011'
          AND staff_id = '00000000-0000-0000-0000-000000000038'
          AND object_code IN ('admin/00000000-0000-0000-0000-000000002801', 'admin/00000000-0000-0000-0000-000000000041')
          AND request_id <> '') <> 2 OR NOT EXISTS (
        SELECT 1 FROM audit_events WHERE action = 'reports.stock_valuation_csv_exported'
          AND store_id = '00000000-0000-0000-0000-000000000012'
          AND staff_id = '00000000-0000-0000-0000-000000000035'
          AND object_code = 'admin/00000000-0000-0000-0000-000000000242' AND request_id <> ''
    ) THEN
        RAISE EXCEPTION 'Unexpected audit records for stock valuation exports';
    END IF;
    IF (SELECT count(*) FROM audit_events WHERE action = 'supplier.payment_allocated'
            AND staff_id = '00000000-0000-0000-0000-000000000037' AND object_code = 'PROC-01') <> 5
        OR (SELECT count(*) FROM audit_events WHERE action = 'supplier.payment_allocation_reversed'
            AND staff_id = '00000000-0000-0000-0000-000000000037' AND object_code = 'PROC-01') <> 1 THEN
        RAISE EXCEPTION 'Unexpected audit records for invoice allocations';
    END IF;
    IF (SELECT count(*) FROM audit_events WHERE action = 'supplier.credit_transferred'
            AND staff_id = '00000000-0000-0000-0000-000000000037' AND object_code = 'PROC-01') <> 4
        OR (SELECT count(*) FROM audit_events WHERE action = 'supplier.credit_transfer_reversed'
            AND staff_id = '00000000-0000-0000-0000-000000000037' AND object_code = 'PROC-01') <> 3 THEN
        RAISE EXCEPTION 'Unexpected audit records for supplier credit transfers';
    END IF;
    IF EXISTS (
        SELECT 1 FROM opening_stock_batches b
        JOIN stock_operations o ON (o.org_id, o.store_id, o.id) = (b.org_id, b.store_id, b.id)
        WHERE (SELECT count(*) FROM audit_events a WHERE a.action = 'stock.opening_posted'
            AND (a.org_id, a.store_id, a.object_id) = (b.org_id, b.store_id, b.id)
            AND a.staff_id = o.actor_id
            AND a.request_id <> '') <> 1
    ) OR (SELECT count(*) FROM audit_events WHERE action = 'stock.opening_csv_exported'
          AND staff_id = '00000000-0000-0000-0000-000000000037' AND request_id <> '') <> 1 THEN
        RAISE EXCEPTION 'Unexpected audit records for opening stock';
    END IF;
    IF (SELECT count(*) FROM audit_events WHERE action = 'inventory.scope_refreshed'
            AND staff_id = '00000000-0000-0000-0000-000000000038' AND request_id <> '') <> 1
        OR (SELECT count(*) FROM audit_events a JOIN inventories i
            ON (a.org_id, a.store_id, a.object_id) = (i.org_id, i.store_id, i.id)
            WHERE a.action = 'inventory.posted' AND i.scope_kind <> 'selected'
              AND a.staff_id = '00000000-0000-0000-0000-000000000038' AND a.request_id <> '') <> 2 THEN
        RAISE EXCEPTION 'Unexpected audit records for scoped inventory';
    END IF;
    IF (SELECT count(*) FROM stock_valuation_snapshots) <> 1 OR NOT EXISTS (
        SELECT 1 FROM audit_events a JOIN stock_valuation_snapshots s
          ON (a.org_id, a.store_id, a.object_id, a.staff_id) = (s.org_id, s.store_id, s.id, s.captured_by)
        WHERE a.action = 'stock.valuation_snapshot_captured' AND a.request_id <> ''
        GROUP BY s.id HAVING count(*) = 1
    ) THEN
        RAISE EXCEPTION 'Unexpected audit records for stock snapshots';
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM stock_movement_exports j JOIN staff s ON (s.org_id, s.id) = (j.org_id, j.actor_id)
        WHERE s.subject = 'admin-export-smoke' AND j.status = 'ready'
            AND (SELECT count(*) FROM audit_events a
                 WHERE (a.org_id, a.store_id, a.staff_id, a.object_id) = (j.org_id, j.store_id, j.actor_id, j.id)
                   AND a.request_id = j.request_id
                   AND a.action IN ('reports.stock_movements_export_queued', 'reports.stock_movements_export_ready')) = 2
            AND (SELECT count(*) FROM audit_events a
                 WHERE (a.org_id, a.store_id, a.staff_id, a.object_id) = (j.org_id, j.store_id, j.actor_id, j.id)
                   AND a.request_id <> '' AND a.action = 'reports.stock_movements_export_downloaded') = 2
    ) OR NOT EXISTS (
        SELECT 1 FROM stock_movement_exports
        WHERE id = '00000000-0000-0000-0000-000000003198' AND status = 'expired' AND contents IS NULL
    ) OR NOT EXISTS (
        SELECT 1 FROM stock_movement_exports j JOIN audit_events a
          ON (a.org_id, a.store_id, a.staff_id, a.object_id) = (j.org_id, j.store_id, j.actor_id, j.id)
        WHERE j.status = 'failed' AND j.contents IS NULL AND a.request_id = j.request_id
          AND a.action = 'reports.stock_movements_export_failed'
          AND a.object_code = 'stock_movement_background_export_limit'
    ) THEN
        RAISE EXCEPTION 'Unexpected background stock movement export state or audit records';
    END IF;
    IF (SELECT count(*) FROM register_diagnostics) <> 4 OR EXISTS (
        SELECT 1 FROM register_diagnostics d
        WHERE (SELECT count(*) FROM audit_events a
            WHERE (a.org_id, a.store_id, a.register_id, a.staff_id, a.object_id) =
                (d.org_id, d.store_id, d.register_id, d.cashier_id, d.id)
              AND a.action = 'register.diagnostic_recorded' AND a.object_code = d.code AND a.request_id <> '') <> 1
    ) OR NOT EXISTS (
        SELECT 1 FROM sales s JOIN sale_lines l
          ON (l.org_id, l.store_id, l.register_id, l.sale_id) = (s.org_id, s.store_id, s.register_id, s.id)
        WHERE s.cashier_id = '00000000-0000-0000-0000-000000000040'
        HAVING count(*) = 1 AND sum(l.quantity) = 0.100
    ) THEN
        RAISE EXCEPTION 'Unexpected register diagnostic audit or sale movements';
    END IF;
END
$$;
