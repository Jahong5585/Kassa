using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<SupplierPayment> SaveSupplierPayment(HttpContext context, Guid store, string code,
        RecordSupplierPayment request, CancellationToken ct)
    {
        code = SupplierCode(code);
        if (request.OperationId == Guid.Empty || !IsMoney(request.Amount) || request.Amount <= 0 ||
            !IsBusinessDate(request.PaymentDate) || !IsText(request.Reference, 300))
            throw new PosError(400, "invalid_supplier_payment");
        var reference = request.Reference.Trim();

        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        if (await ReadSupplier(db, tx, scope, code, ct) is null)
            throw new PosError(404, "supplier_not_found");
        await LockSupplierPayment(db, tx, scope, code, request.OperationId, ct);
        var existing = (await ReadSupplierPayments(db, tx, scope, code, request.OperationId, 0, ct)).SingleOrDefault();
        if (existing is not null)
        {
            if (existing.Amount != request.Amount || existing.PaymentDate != request.PaymentDate ||
                existing.Reference != reference)
                throw new PosError(409, "supplier_payment_id_reused");
            return existing;
        }
        if (request.PaymentDate > await StoreDate(db, tx, scope, ct))
            throw new PosError(400, "supplier_payment_date_in_future");

        await using var command = db.Sql(tx, """
            INSERT INTO supplier_payments (org_id, store_id, supplier_code, id, amount, payment_date,
                reference, recorded_by) VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            ON CONFLICT DO NOTHING RETURNING created_at
            """, scope.Organization, store, code, request.OperationId, request.Amount, request.PaymentDate,
            reference, scope.Staff);
        if (await command.ExecuteScalarAsync(ct) is not DateTime created)
            throw new PosError(409, "supplier_payment_duplicate_reference");
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff, "supplier.payment_recorded",
            request.OperationId, ct, code);
        await tx.CommitAsync(ct);
        return new SupplierPayment(request.OperationId, request.Amount, request.PaymentDate, reference, scope.Staff, created);
    }

    public async Task<IReadOnlyList<SupplierPayment>> ListSupplierPayments(HttpContext context, Guid store, string code,
        int? offset, CancellationToken ct)
    {
        code = SupplierCode(code);
        ValidateStockOffset(offset);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        return await ReadSupplierPayments(db, tx, scope, code, null, offset ?? 0, ct);
    }

    public async Task<SupplierBalance> ReadSupplierBalance(HttpContext context, Guid store, string code, CancellationToken ct)
    {
        code = SupplierCode(code);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        if (await ReadSupplier(db, tx, scope, code, ct) is null)
            throw new PosError(404, "supplier_not_found");

        await using var command = db.Sql(tx, """
            WITH invoices AS (
                SELECT id, org_id, store_id FROM receiving_documents
                WHERE (org_id, store_id) = ($1, $2) AND status = 'posted' AND upper(supplier_ref) = $3
            ), costs AS (
                SELECT i.id, sum(c.total) AS total FROM invoices i
                LEFT JOIN receiving_movement_costs c ON (i.org_id, i.store_id, i.id) = (c.org_id, c.store_id, c.receipt_id)
                GROUP BY i.id
            ), credits AS (
                SELECT r.id, r.credit_amount, sum(l.credit_amount) AS known
                FROM invoices i JOIN supplier_returns r ON (i.org_id, i.store_id, i.id) = (r.org_id, r.store_id, r.receipt_id)
                JOIN supplier_return_lines l ON (r.org_id, r.store_id, r.id) = (l.org_id, l.store_id, l.return_id)
                GROUP BY r.org_id, r.store_id, r.id
            )
            SELECT (SELECT coalesce(sum(total), 0) FROM costs),
                   (SELECT coalesce(sum(known), 0) FROM credits),
                   (SELECT coalesce(sum(amount), 0) FROM supplier_payments
                       WHERE (org_id, store_id, supplier_code) = ($1, $2, $3)),
                   (SELECT count(*) FROM costs WHERE total IS NULL),
                   (SELECT count(*) FROM credits WHERE credit_amount IS NULL)
            """, scope.Organization, store, code);
        await using var rows = await command.ExecuteReaderAsync(ct);
        await rows.ReadAsync(ct);
        var invoiced = rows.GetDecimal(0);
        var credits = rows.GetDecimal(1);
        var paid = rows.GetDecimal(2);
        return new SupplierBalance(code, invoiced, credits, paid, invoiced - credits - paid, rows.GetInt64(3), rows.GetInt64(4));
    }

    private static async Task<IReadOnlyList<SupplierPayment>> ReadSupplierPayments(NpgsqlConnection db, NpgsqlTransaction tx,
        StoreScope scope, string code, Guid? id, int offset, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT id, amount, payment_date, reference, recorded_by, created_at
            FROM supplier_payments WHERE (org_id, store_id, supplier_code) = ($1, $2, $3) AND ($4 OR id = $5)
            ORDER BY payment_date DESC, created_at DESC, id LIMIT 25 OFFSET $6
            """, scope.Organization, scope.Store, code, id is null, id ?? Guid.Empty, offset);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<SupplierPayment> result = [];
        while (await rows.ReadAsync(ct))
            result.Add(new SupplierPayment(rows.GetGuid(0), rows.GetDecimal(1), rows.GetFieldValue<DateOnly>(2),
                rows.GetString(3), rows.GetGuid(4), rows.GetDateTime(5)));
        return result;
    }
}
