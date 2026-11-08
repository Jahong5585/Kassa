namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<StoreExceptionReport> GetStoreExceptions(HttpContext context, Guid store, string? code,
        int offset, int limit, CancellationToken ct)
    {
        if (code is not (null or "missingRequiredExpiry" or "invoiceScanPending" or "invoiceScanRejected" or "cashDifference"
                or "saleOutcomeUnknown" or "printFailed" or "syncFailed") ||
            limit is < 1 or > 100 || offset < 0 || offset > int.MaxValue - limit)
            throw new PosError(400, "invalid_exception_filter");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await using var command = db.Sql(tx, $"""
            WITH exceptions AS (
                SELECT 'missingRequiredExpiry' AS code, l.id AS object_id, l.product_id,
                    NULL::uuid AS receipt_id, NULL::uuid AS register_id, NULL::timestamptz AS occurred_at,
                    NULL::numeric AS cash_difference, NULL::uuid AS shift_id, NULL::uuid AS operation_id
                FROM stock_lots l JOIN products p ON (p.org_id, p.id) = (l.org_id, l.product_id)
                WHERE (l.org_id, l.store_id) = ($1, $2) AND l.expiry_date IS NULL
                    AND p.requires_expiry AND l.quantity + l.quarantine_quantity > 0
                    AND $3 IN ('', 'missingRequiredExpiry')
                UNION ALL
                SELECT CASE f.scan_status WHEN 'pending' THEN 'invoiceScanPending' ELSE 'invoiceScanRejected' END,
                    f.id, NULL, f.receipt_id, NULL, f.created_at, NULL, NULL, NULL
                FROM invoice_files f JOIN receiving_documents d
                    ON (d.org_id, d.store_id, d.id) = (f.org_id, f.store_id, f.receipt_id)
                WHERE (f.org_id, f.store_id) = ($1, $2) AND f.scan_status IN ('pending', 'rejected')
                    AND d.status IN ('draft', 'submitted') AND
                    ($3 = '' OR (f.scan_status = 'pending' AND $3 = 'invoiceScanPending')
                             OR (f.scan_status = 'rejected' AND $3 = 'invoiceScanRejected'))
                UNION ALL
                SELECT 'cashDifference', s.id, NULL, NULL, s.register_id, s.closed_at, difference.amount, NULL, NULL
                FROM shifts s
                {ShiftMoneyJoins}
                CROSS JOIN LATERAL (SELECT s.counted_cash - ({ShiftExpectedCashSql}) AS amount) difference
                WHERE (s.org_id, s.store_id) = ($1, $2) AND s.closed_at IS NOT NULL
                    AND difference.amount <> 0 AND $3 IN ('', 'cashDifference')
                UNION ALL
                SELECT d.code, d.id, NULL, NULL, d.register_id, d.received_at, NULL, d.shift_id, d.operation_id
                FROM register_diagnostics d
                WHERE (d.org_id, d.store_id) = ($1, $2) AND ($3 = '' OR $3 = d.code)
                    AND (d.code <> 'saleOutcomeUnknown' OR NOT EXISTS (
                        SELECT 1 FROM sales s
                        WHERE (s.org_id, s.store_id, s.register_id, s.id) = (d.org_id, d.store_id, d.register_id, d.operation_id)
                          AND (s.shift_id, s.cashier_id) = (d.shift_id, d.cashier_id)
                    ))
            )
            SELECT code, object_id, product_id, receipt_id, register_id, occurred_at, cash_difference, shift_id, operation_id
            FROM exceptions ORDER BY code, object_id, product_id, receipt_id, register_id LIMIT $4 OFFSET $5
            """, scope.Organization, store, code ?? "", limit + 1, offset);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<StoreException> items = [];
        int? next = null;
        while (await rows.ReadAsync(ct))
        {
            if (items.Count == limit)
            {
                next = offset + limit;
                break;
            }
            items.Add(new StoreException(rows.GetString(0), rows.GetGuid(1),
                rows.IsDBNull(2) ? null : rows.GetGuid(2), rows.IsDBNull(3) ? null : rows.GetGuid(3),
                rows.IsDBNull(4) ? null : rows.GetGuid(4), rows.TimeOrNull(5), rows.DecimalOrNull(6))
            {
                ShiftId = rows.IsDBNull(7) ? null : rows.GetGuid(7),
                OperationId = rows.IsDBNull(8) ? null : rows.GetGuid(8)
            });
        }
        return new StoreExceptionReport(store, scope.TimeZone, code, items, next);
    }
}
