namespace Kasa.Api;

internal sealed partial class PosService
{
    private const int SupplierSettlementPageSize = 25;

    public async Task<SupplierSettlementReport> GetSupplierSettlementReport(HttpContext context, Guid store,
        string? code, int offset, CancellationToken ct)
    {
        if (code is not null)
            code = SupplierCode(code);
        if (offset < 0 || offset > int.MaxValue - SupplierSettlementPageSize)
            throw new PosError(400, "invalid_supplier_report_offset");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await using var command = db.Sql(tx, $"""
            WITH clock AS (
                SELECT (statement_timestamp() AT TIME ZONE $3)::date AS day
            ), suppliers_page AS (
                SELECT org_id, store_id, code, name, active FROM suppliers
                WHERE (org_id, store_id) = ($1, $2) AND ($4 = '' OR code = $4)
                ORDER BY code LIMIT $5 OFFSET $6
            ), invoices AS (
                SELECT r.org_id, r.store_id, r.id, p.code AS supplier_code
                FROM suppliers_page p JOIN receiving_documents r
                    ON (r.org_id, r.store_id, upper(r.supplier_ref)) = (p.org_id, p.store_id, p.code)
                WHERE r.status = 'posted'
            ), invoice_amounts AS (
                SELECT i.supplier_code, costs.amount AS invoiced, t.due_date,
                    costs.amount - credits.amount - applied.amount + transfers.sent - transfers.received AS balance
                FROM invoices i
                {SupplierInvoiceAmountsJoins}
            ), invoice_summary AS (
                SELECT supplier_code, coalesce(sum(invoiced), 0) AS invoiced,
                    sum(balance) FILTER (WHERE balance > 0) AS outstanding,
                    sum(balance) FILTER (WHERE balance > 0 AND due_date < clock.day) AS overdue,
                    count(*) FILTER (WHERE invoiced IS NULL) AS unpriced_invoices,
                    count(*) FILTER (WHERE balance IS NULL) AS unpriced_settlements,
                    count(*) FILTER (WHERE balance IS NULL AND due_date < clock.day) AS unpriced_overdue
                FROM invoice_amounts CROSS JOIN clock GROUP BY supplier_code
            ), return_summary AS (
                SELECT i.supplier_code, sum(known.amount) AS credits,
                    count(*) FILTER (WHERE r.credit_amount IS NULL) AS unpriced_returns
                FROM invoices i JOIN supplier_returns r
                    ON (r.org_id, r.store_id, r.receipt_id) = (i.org_id, i.store_id, i.id)
                CROSS JOIN LATERAL (
                    SELECT sum(l.credit_amount) AS amount FROM supplier_return_lines l
                    WHERE (l.org_id, l.store_id, l.return_id) = (r.org_id, r.store_id, r.id)
                    HAVING count(*) > 0
                ) known
                GROUP BY i.supplier_code
            ), payment_summary AS (
                SELECT p.supplier_code, sum(p.amount) AS paid, sum(p.amount - applied.amount) AS advance
                FROM suppliers_page sp JOIN supplier_payments p
                    ON (p.org_id, p.store_id, p.supplier_code) = (sp.org_id, sp.store_id, sp.code)
                {SupplierPaymentAmountsJoin}
                GROUP BY p.supplier_code
            )
            SELECT clock.day, p.code, p.name, p.active, coalesce(i.invoiced, 0), coalesce(r.credits, 0),
                coalesce(s.paid, 0), coalesce(s.advance, 0), coalesce(i.outstanding, 0), coalesce(i.overdue, 0),
                coalesce(i.unpriced_invoices, 0), coalesce(r.unpriced_returns, 0),
                coalesce(i.unpriced_settlements, 0), coalesce(i.unpriced_overdue, 0)
            FROM clock LEFT JOIN suppliers_page p ON true
            LEFT JOIN invoice_summary i ON i.supplier_code = p.code
            LEFT JOIN return_summary r ON r.supplier_code = p.code
            LEFT JOIN payment_summary s ON s.supplier_code = p.code
            ORDER BY p.code
            """, scope.Organization, store, scope.TimeZone, code ?? "", SupplierSettlementPageSize + 1, offset);
        await using var rows = await command.ExecuteReaderAsync(ct);
        await rows.ReadAsync(ct);
        var asOf = rows.GetFieldValue<DateOnly>(0);
        List<SupplierSettlementSummary> items = [];
        int? next = null;
        do
        {
            if (rows.IsDBNull(1))
                break;
            if (items.Count == SupplierSettlementPageSize)
            {
                next = offset + SupplierSettlementPageSize;
                break;
            }
            items.Add(new SupplierSettlementSummary(rows.GetString(1), rows.GetString(2), rows.GetBoolean(3),
                rows.GetDecimal(4), rows.GetDecimal(5), rows.GetDecimal(6), rows.GetDecimal(7), rows.GetDecimal(8),
                rows.GetDecimal(9), rows.GetInt64(10), rows.GetInt64(11), rows.GetInt64(12), rows.GetInt64(13)));
        } while (await rows.ReadAsync(ct));
        return new SupplierSettlementReport(store, scope.TimeZone, asOf, code, items, next);
    }
}
