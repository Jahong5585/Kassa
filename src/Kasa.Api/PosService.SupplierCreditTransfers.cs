using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<SupplierCreditTransfer> SaveSupplierCreditTransfer(HttpContext context, Guid store,
        string code, TransferSupplierCredit request, CancellationToken ct)
    {
        code = SupplierCode(code);
        if (request.OperationId == Guid.Empty || request.SourceReceiptId == Guid.Empty ||
            request.TargetReceiptId == Guid.Empty || request.SourceReceiptId == request.TargetReceiptId ||
            request.Amount <= 0 || !IsMoney(request.Amount) || !IsText(request.Reason, 500))
            throw new PosError(400, "invalid_supplier_credit_transfer");
        var reason = request.Reason.Trim();
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockSupplierCreditTransfer(db, tx, scope, code, request.OperationId, ct);
        var existing = await ReadSupplierCreditTransfer(db, tx, scope, code, request.OperationId, ct);
        if (existing is not null)
        {
            if (existing.SourceReceiptId != request.SourceReceiptId || existing.TargetReceiptId != request.TargetReceiptId ||
                existing.Amount != request.Amount || existing.Reason != reason)
                throw new PosError(409, "supplier_credit_transfer_id_reused");
            return existing;
        }

        await LockCreditTransferInvoices(db, tx, scope, request.SourceReceiptId, request.TargetReceiptId, ct);
        var original = await RequireSupplierInvoice(db, tx, scope, code, request.SourceReceiptId, ct);
        var target = await RequireSupplierInvoice(db, tx, scope, code, request.TargetReceiptId, ct);
        if (original.OverpaidAmount is not { } available || target.OutstandingAmount is not { } outstanding)
            throw new PosError(409, "supplier_invoice_unpriced");
        if (request.Amount > available)
            throw new PosError(409, "supplier_credit_transfer_exceeded");
        if (request.Amount > outstanding)
            throw new PosError(409, "supplier_credit_transfer_exceeds_target_debt");

        await using var command = db.Sql(tx, """
            INSERT INTO supplier_credit_transfers (org_id, store_id, supplier_code, id, source_receipt_id,
                target_receipt_id, amount, reason, recorded_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9) RETURNING recorded_at
            """, scope.Organization, store, code, request.OperationId, request.SourceReceiptId, request.TargetReceiptId,
            request.Amount, reason, scope.Staff);
        var recordedAt = (DateTime)(await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("Saved credit transfer missing."));
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff, "supplier.credit_transferred",
            request.OperationId, ct, code);
        await tx.CommitAsync(ct);
        return new SupplierCreditTransfer(request.OperationId, request.SourceReceiptId, request.TargetReceiptId,
            request.Amount, reason, scope.Staff, recordedAt);
    }

    public async Task<SupplierCreditTransferReversal> ReverseCreditTransfer(HttpContext context, Guid store,
        string code, Guid id, ReverseSupplierCreditTransfer request, CancellationToken ct)
    {
        code = SupplierCode(code);
        if (request.OperationId == Guid.Empty || !IsText(request.Reason, 500))
            throw new PosError(400, "invalid_supplier_credit_transfer_reversal");
        var reason = request.Reason.Trim();
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockOperation(db, tx, $"supplier_credit_reversal:{scope.Organization}:{store}:{code}:{request.OperationId}", ct);
        var existing = await ReadSupplierCreditReversal(db, tx, scope, code, request.OperationId, byTransfer: false, ct);
        if (existing is not null)
        {
            if (existing.TransferId != id || existing.Reason != reason)
                throw new PosError(409, "supplier_credit_reversal_id_reused");
            return existing;
        }
        await LockSupplierCreditTransfer(db, tx, scope, code, id, ct);
        var transfer = await ReadSupplierCreditTransfer(db, tx, scope, code, id, ct)
            ?? throw new PosError(404, "supplier_credit_transfer_not_found");
        if (await ReadSupplierCreditReversal(db, tx, scope, code, id, byTransfer: true, ct) is not null)
            throw new PosError(409, "supplier_credit_transfer_already_reversed");
        await LockCreditTransferInvoices(db, tx, scope, transfer.SourceReceiptId, transfer.TargetReceiptId, ct);
        var target = await RequireSupplierInvoice(db, tx, scope, code, transfer.TargetReceiptId, ct);
        RequireCreditAvailableAfterReversal(target, transfer.Amount);

        await using var command = db.Sql(tx, """
            INSERT INTO supplier_credit_transfer_reversals
                (org_id, store_id, supplier_code, id, transfer_id, reason, reversed_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7) RETURNING reversed_at
            """, scope.Organization, store, code, request.OperationId, id, reason, scope.Staff);
        var reversedAt = (DateTime)(await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("Saved credit transfer reversal missing."));
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff, "supplier.credit_transfer_reversed",
            request.OperationId, ct, code);
        await tx.CommitAsync(ct);
        return new SupplierCreditTransferReversal(request.OperationId, id, reason, scope.Staff, reversedAt);
    }

    public async Task<SupplierCreditTransfer> GetSupplierCreditTransfer(HttpContext context, Guid store,
        string code, Guid id, CancellationToken ct)
    {
        code = SupplierCode(code);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        return await ReadSupplierCreditTransfer(db, tx, scope, code, id, ct)
            ?? throw new PosError(404, "supplier_credit_transfer_not_found");
    }

    public async Task<IReadOnlyList<SupplierInvoiceCreditTransfer>> ListSupplierInvoiceCreditTransfers(HttpContext context,
        Guid store, string code, Guid id, int? offset, CancellationToken ct)
    {
        code = SupplierCode(code);
        ValidateStockOffset(offset);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await RequireSupplierInvoice(db, tx, scope, code, id, ct);
        await using var command = db.Sql(tx, """
            SELECT c.id, c.source_receipt_id, c.target_receipt_id, c.amount, c.reason, c.recorded_by, c.recorded_at,
                   r.id, r.reason, r.reversed_by, r.reversed_at
            FROM supplier_credit_transfers c LEFT JOIN supplier_credit_transfer_reversals r
                ON (r.org_id, r.store_id, r.supplier_code, r.transfer_id) = (c.org_id, c.store_id, c.supplier_code, c.id)
            WHERE (c.org_id, c.store_id, c.supplier_code) = ($1, $2, $3)
                AND (c.source_receipt_id = $4 OR c.target_receipt_id = $4)
            ORDER BY c.recorded_at DESC, c.id LIMIT 25 OFFSET $5
            """, scope.Organization, store, code, id, offset ?? 0);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<SupplierInvoiceCreditTransfer> result = [];
        while (await rows.ReadAsync(ct))
        {
            var transfer = ReadSupplierCreditTransferRow(rows);
            var reversal = rows.IsDBNull(7) ? null : new SupplierCreditTransferReversal(rows.GetGuid(7),
                transfer.OperationId, rows.GetString(8), rows.GetGuid(9), rows.GetDateTime(10));
            result.Add(new SupplierInvoiceCreditTransfer(transfer, reversal));
        }
        return result;
    }

    private static Task LockSupplierCreditTransfer(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope,
        string code, Guid id, CancellationToken ct) =>
        LockOperation(db, tx, $"supplier_credit_transfer:{scope.Organization}:{scope.Store}:{code}:{id}", ct);

    private static async Task LockCreditTransferInvoices(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope,
        Guid first, Guid second, CancellationToken ct)
    {
        if (first.CompareTo(second) > 0)
            (first, second) = (second, first);
        await LockSupplierInvoice(db, tx, scope, first, ct);
        await LockSupplierInvoice(db, tx, scope, second, ct);
    }

    private static void RequireCreditAvailableAfterReversal(SupplierInvoiceSettlement invoice, decimal amount)
    {
        if (invoice.CreditSentAmount > 0 && (invoice.Balance is not { } balance || balance + amount > 0))
            throw new PosError(409, "supplier_credit_transfer_depends_on_settlement");
    }

    private static async Task<SupplierCreditTransfer?> ReadSupplierCreditTransfer(NpgsqlConnection db, NpgsqlTransaction tx,
        StoreScope scope, string code, Guid id, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT id, source_receipt_id, target_receipt_id, amount, reason, recorded_by, recorded_at
            FROM supplier_credit_transfers WHERE (org_id, store_id, supplier_code, id) = ($1, $2, $3, $4)
            """, scope.Organization, scope.Store, code, id);
        await using var rows = await command.ExecuteReaderAsync(ct);
        return await rows.ReadAsync(ct) ? ReadSupplierCreditTransferRow(rows) : null;
    }

    private static SupplierCreditTransfer ReadSupplierCreditTransferRow(NpgsqlDataReader rows) =>
        new(rows.GetGuid(0), rows.GetGuid(1), rows.GetGuid(2), rows.GetDecimal(3), rows.GetString(4),
            rows.GetGuid(5), rows.GetDateTime(6));

    private static async Task<SupplierCreditTransferReversal?> ReadSupplierCreditReversal(NpgsqlConnection db,
        NpgsqlTransaction tx, StoreScope scope, string code, Guid id, bool byTransfer, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT id, transfer_id, reason, reversed_by, reversed_at FROM supplier_credit_transfer_reversals
            WHERE (org_id, store_id, supplier_code) = ($1, $2, $3) AND
                (($5 AND transfer_id = $4) OR (NOT $5 AND id = $4))
            """, scope.Organization, scope.Store, code, id, byTransfer);
        await using var rows = await command.ExecuteReaderAsync(ct);
        return await rows.ReadAsync(ct) ? new SupplierCreditTransferReversal(rows.GetGuid(0), rows.GetGuid(1),
            rows.GetString(2), rows.GetGuid(3), rows.GetDateTime(4)) : null;
    }
}
