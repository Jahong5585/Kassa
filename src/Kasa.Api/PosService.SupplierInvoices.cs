using System.Globalization;
using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<IReadOnlyList<SupplierInvoiceSettlement>> ListSupplierInvoices(HttpContext context, Guid store,
        string code, int? offset, CancellationToken ct)
    {
        code = SupplierCode(code);
        ValidateStockOffset(offset);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        if (await ReadSupplier(db, tx, scope, code, ct) is null)
            throw new PosError(404, "supplier_not_found");
        return await ReadSupplierInvoices(db, tx, scope, code, null, offset ?? 0, ct);
    }

    public async Task<SupplierInvoiceSettlement> GetSupplierInvoice(HttpContext context, Guid store, string code,
        Guid id, CancellationToken ct)
    {
        code = SupplierCode(code);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        return await RequireSupplierInvoice(db, tx, scope, code, id, ct);
    }

    public async Task<SupplierInvoiceTerms> SaveSupplierInvoiceDueDate(HttpContext context, Guid store,
        string code, Guid id, SetSupplierInvoiceDueDate request, CancellationToken ct)
    {
        code = SupplierCode(code);
        if (request.ExpectedVersion is < 0 or int.MaxValue ||
            request.DueDate is { } dueDate && !IsBusinessDate(dueDate))
            throw new PosError(400, "invalid_supplier_invoice_terms");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockSupplierInvoice(db, tx, scope, id, ct);
        var invoice = await RequireSupplierInvoice(db, tx, scope, code, id, ct);
        if (invoice.Terms.Version == request.ExpectedVersion + 1 && invoice.Terms.DueDate == request.DueDate)
            return invoice.Terms;
        if (invoice.Terms.Version != request.ExpectedVersion)
            throw new PosError(409, "supplier_invoice_terms_version_conflict");
        if (request.DueDate < invoice.InvoiceDate)
            throw new PosError(400, "supplier_invoice_due_date_before_invoice");
        var terms = new SupplierInvoiceTerms(request.DueDate, request.ExpectedVersion + 1);
        var dateText = request.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
        await db.Run(tx, ct, """
            INSERT INTO supplier_invoice_terms (org_id, store_id, receipt_id, due_date, version)
            VALUES ($1, $2, $3, NULLIF($4, '')::date, $5)
            ON CONFLICT (org_id, store_id, receipt_id) DO UPDATE
                SET due_date = EXCLUDED.due_date, version = EXCLUDED.version
            """, scope.Organization, store, id, dateText, terms.Version);
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff, "supplier.invoice_due_date_set",
            id, ct, $"{code}/{terms.Version}/{(dateText.Length == 0 ? "cleared" : dateText)}");
        await tx.CommitAsync(ct);
        return terms;
    }

    public async Task<IReadOnlyList<SupplierInvoiceAllocation>> ListSupplierInvoiceAllocations(HttpContext context,
        Guid store, string code, Guid id, int? offset, CancellationToken ct)
    {
        code = SupplierCode(code);
        ValidateStockOffset(offset);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await RequireSupplierInvoice(db, tx, scope, code, id, ct);
        await using var command = db.Sql(tx, """
            SELECT a.id, a.payment_id, l.amount, p.payment_date, p.reference,
                   a.allocated_by, a.allocated_at, r.id, r.reversed_at, r.reversed_by, r.reason
            FROM supplier_payment_allocation_lines l
            JOIN supplier_payment_allocations a ON
                (a.org_id, a.store_id, a.supplier_code, a.id) = (l.org_id, l.store_id, l.supplier_code, l.allocation_id)
            JOIN supplier_payments p ON
                (p.org_id, p.store_id, p.supplier_code, p.id) = (a.org_id, a.store_id, a.supplier_code, a.payment_id)
            LEFT JOIN supplier_payment_allocation_reversals r ON
                (r.org_id, r.store_id, r.supplier_code, r.allocation_id) = (a.org_id, a.store_id, a.supplier_code, a.id)
            WHERE (l.org_id, l.store_id, l.supplier_code, l.receipt_id) = ($1, $2, $3, $4)
            ORDER BY a.allocated_at DESC, a.id LIMIT 25 OFFSET $5
            """, scope.Organization, store, code, id, offset ?? 0);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<SupplierInvoiceAllocation> result = [];
        while (await rows.ReadAsync(ct))
            result.Add(new SupplierInvoiceAllocation(rows.GetGuid(0), rows.GetGuid(1), rows.GetDecimal(2),
                rows.GetFieldValue<DateOnly>(3), rows.GetString(4), rows.GetGuid(5), rows.GetDateTime(6),
                rows.IsDBNull(7) ? null : rows.GetGuid(7), rows.TimeOrNull(8),
                rows.IsDBNull(9) ? null : rows.GetGuid(9), rows.IsDBNull(10) ? null : rows.GetString(10)));
        return result;
    }

    private static async Task<SupplierInvoiceSettlement> RequireSupplierInvoice(NpgsqlConnection db,
        NpgsqlTransaction tx, StoreScope scope, string code, Guid id, CancellationToken ct) =>
        (await ReadSupplierInvoices(db, tx, scope, code, id, 0, ct)).SingleOrDefault()
            ?? throw new PosError(404, "supplier_invoice_not_found");

    private const string SupplierInvoiceAmountsJoins = """
            LEFT JOIN supplier_invoice_terms t
                ON (t.org_id, t.store_id, t.receipt_id) = (i.org_id, i.store_id, i.id)
            CROSS JOIN LATERAL (
                SELECT sum(total) AS amount FROM receiving_movement_costs
                WHERE (org_id, store_id, receipt_id) = (i.org_id, i.store_id, i.id)
            ) costs
            CROSS JOIN LATERAL (
                SELECT CASE WHEN count(*) = count(credit_amount) THEN coalesce(sum(credit_amount), 0) END AS amount
                FROM supplier_returns WHERE (org_id, store_id, receipt_id) = (i.org_id, i.store_id, i.id)
            ) credits
            CROSS JOIN LATERAL (
                SELECT coalesce(sum(l.amount), 0) AS amount FROM supplier_payment_allocation_lines l
                LEFT JOIN supplier_payment_allocation_reversals r ON
                    (r.org_id, r.store_id, r.supplier_code, r.allocation_id) = (l.org_id, l.store_id, l.supplier_code, l.allocation_id)
                WHERE (l.org_id, l.store_id, l.supplier_code, l.receipt_id) = (i.org_id, i.store_id, i.supplier_code, i.id)
                    AND r.id IS NULL
            ) applied
            CROSS JOIN LATERAL (
                SELECT coalesce(sum(c.amount) FILTER (WHERE c.source_receipt_id = i.id), 0) AS sent,
                       coalesce(sum(c.amount) FILTER (WHERE c.target_receipt_id = i.id), 0) AS received
                FROM supplier_credit_transfers c LEFT JOIN supplier_credit_transfer_reversals r
                    ON (r.org_id, r.store_id, r.supplier_code, r.transfer_id) = (c.org_id, c.store_id, c.supplier_code, c.id)
                WHERE (c.org_id, c.store_id, c.supplier_code) = (i.org_id, i.store_id, i.supplier_code)
                    AND (c.source_receipt_id = i.id OR c.target_receipt_id = i.id) AND r.id IS NULL
            ) transfers
            """;

    private static async Task<IReadOnlyList<SupplierInvoiceSettlement>> ReadSupplierInvoices(NpgsqlConnection db,
        NpgsqlTransaction tx, StoreScope scope, string code, Guid? id, int offset, CancellationToken ct)
    {
        await using var command = db.Sql(tx, $"""
            WITH invoices AS (
                SELECT org_id, store_id, id, invoice_number, invoice_date, posted_at, $3::text AS supplier_code
                FROM receiving_documents
                WHERE (org_id, store_id) = ($1, $2) AND upper(supplier_ref) = $3 AND status = 'posted'
                    AND ($4 OR id = $5)
                ORDER BY invoice_date DESC, id LIMIT 25 OFFSET $6
            )
            SELECT i.id, i.invoice_number, i.invoice_date, i.posted_at, (statement_timestamp() AT TIME ZONE $7)::date,
                t.due_date, coalesce(t.version, 0), costs.amount, credits.amount, applied.amount, transfers.sent, transfers.received
            FROM invoices i
            {SupplierInvoiceAmountsJoins}
            ORDER BY i.invoice_date DESC, i.id
            """, scope.Organization, scope.Store, code, id is null, id ?? Guid.Empty, offset, scope.TimeZone);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<SupplierInvoiceSettlement> result = [];
        while (await rows.ReadAsync(ct))
            result.Add(new SupplierInvoiceSettlement(rows.GetGuid(0), rows.GetString(1), rows.GetFieldValue<DateOnly>(2),
                rows.GetDateTime(3), rows.GetFieldValue<DateOnly>(4), new SupplierInvoiceTerms(ReadDate(rows, 5), rows.GetInt32(6)),
                rows.DecimalOrNull(7), rows.DecimalOrNull(8), rows.GetDecimal(9))
            {
                CreditSentAmount = rows.GetDecimal(10),
                CreditReceivedAmount = rows.GetDecimal(11)
            });
        return result;
    }
}
