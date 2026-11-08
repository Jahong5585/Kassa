using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<SupplierPaymentAllocation> AllocateSupplierPaymentToInvoices(HttpContext context, Guid store,
        string code, AllocateSupplierPayment request, CancellationToken ct)
    {
        code = SupplierCode(code);
        if (request.OperationId == Guid.Empty || request.PaymentId == Guid.Empty ||
            request.Items is not { Length: >= 1 and <= 25 } ||
            request.Items.Any(item => item is null || item.ReceiptId == Guid.Empty ||
                item.Amount <= 0 || !IsMoney(item.Amount)) ||
            request.Items.Select(item => item.ReceiptId).Distinct().Count() != request.Items.Length ||
            !IsMoney(request.Items.Sum(item => item.Amount)))
            throw new PosError(400, "invalid_supplier_payment_allocation");
        var items = request.Items.OrderBy(item => item.ReceiptId).ToArray();

        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockSupplierAllocation(db, tx, scope, code, request.OperationId, ct);
        var existing = await ReadSupplierPaymentAllocation(db, tx, scope, code, request.OperationId, ct);
        if (existing is not null)
        {
            if (existing.PaymentId != request.PaymentId || !existing.Items.SequenceEqual(items))
                throw new PosError(409, "supplier_allocation_id_reused");
            return existing;
        }
        await LockSupplierPayment(db, tx, scope, code, request.PaymentId, ct);
        var payment = await ReadSupplierPaymentAllocationBalance(db, tx, scope, code, request.PaymentId, ct);
        if (items.Sum(item => item.Amount) > payment.UnallocatedAmount)
            throw new PosError(409, "supplier_payment_allocation_exceeded");

        // Returns and allocations take the same lock for each invoice. A later return may create an overpayment.
        foreach (var item in items)
            await LockSupplierInvoice(db, tx, scope, item.ReceiptId, ct);
        foreach (var item in items)
        {
            var invoice = await RequireSupplierInvoice(db, tx, scope, code, item.ReceiptId, ct);
            if (invoice.OutstandingAmount is not { } outstanding)
                throw new PosError(409, "supplier_invoice_unpriced");
            if (item.Amount > outstanding)
                throw new PosError(409, "supplier_invoice_allocation_exceeded");
        }

        await using var command = db.Sql(tx, """
            INSERT INTO supplier_payment_allocations (org_id, store_id, supplier_code, id, payment_id, allocated_by)
            VALUES ($1, $2, $3, $4, $5, $6) RETURNING allocated_at
            """, scope.Organization, store, code, request.OperationId, request.PaymentId, scope.Staff);
        var allocatedAt = (DateTime)(await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("Saved allocation missing."));
        foreach (var item in items)
        {
            await db.Run(tx, ct, """
                INSERT INTO supplier_payment_allocation_lines (org_id, store_id, supplier_code, allocation_id, receipt_id, amount)
                VALUES ($1, $2, $3, $4, $5, $6)
                """, scope.Organization, store, code, request.OperationId, item.ReceiptId, item.Amount);
        }
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff, "supplier.payment_allocated",
            request.OperationId, ct, code);
        await tx.CommitAsync(ct);
        return new SupplierPaymentAllocation(request.OperationId, request.PaymentId, scope.Staff, allocatedAt, items);
    }

    public async Task<SupplierPaymentAllocationReversal> ReversePaymentAllocation(HttpContext context, Guid store,
        string code, Guid id, ReverseSupplierPaymentAllocation request, CancellationToken ct)
    {
        code = SupplierCode(code);
        if (request.OperationId == Guid.Empty || !IsText(request.Reason, 500))
            throw new PosError(400, "invalid_supplier_allocation_reversal");
        var reason = request.Reason.Trim();
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockOperation(db, tx, $"supplier_allocation_reversal:{scope.Organization}:{store}:{code}:{request.OperationId}", ct);
        var existing = await ReadSupplierAllocationReversal(db, tx, scope, code, request.OperationId, ct);
        if (existing is not null)
        {
            if (existing.AllocationId != id || existing.Reason != reason)
                throw new PosError(409, "supplier_allocation_reversal_id_reused");
            return existing;
        }
        await LockSupplierAllocation(db, tx, scope, code, id, ct);
        var allocation = await ReadSupplierPaymentAllocation(db, tx, scope, code, id, ct)
            ?? throw new PosError(404, "supplier_allocation_not_found");
        await LockSupplierPayment(db, tx, scope, code, allocation.PaymentId, ct);
        foreach (var item in allocation.Items)
            await LockSupplierInvoice(db, tx, scope, item.ReceiptId, ct);
        foreach (var item in allocation.Items)
        {
            var invoice = await RequireSupplierInvoice(db, tx, scope, code, item.ReceiptId, ct);
            RequireCreditAvailableAfterReversal(invoice, item.Amount);
        }
        await using var command = db.Sql(tx, """
            INSERT INTO supplier_payment_allocation_reversals
                (org_id, store_id, supplier_code, id, allocation_id, reason, reversed_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            ON CONFLICT DO NOTHING RETURNING reversed_at
            """, scope.Organization, store, code, request.OperationId, id, reason, scope.Staff);
        if (await command.ExecuteScalarAsync(ct) is not DateTime reversedAt)
            throw new PosError(409, "supplier_allocation_already_reversed");
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff, "supplier.payment_allocation_reversed",
            request.OperationId, ct, code);
        await tx.CommitAsync(ct);
        return new SupplierPaymentAllocationReversal(request.OperationId, id, reason, scope.Staff, reversedAt);
    }

    public async Task<SupplierPaymentAllocation> GetSupplierPaymentAllocation(HttpContext context, Guid store,
        string code, Guid id, CancellationToken ct)
    {
        code = SupplierCode(code);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        return await ReadSupplierPaymentAllocation(db, tx, scope, code, id, ct)
            ?? throw new PosError(404, "supplier_allocation_not_found");
    }

    public async Task<SupplierPaymentAllocationBalance> GetSupplierPaymentAllocationBalance(HttpContext context,
        Guid store, string code, Guid id, CancellationToken ct)
    {
        code = SupplierCode(code);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        return await ReadSupplierPaymentAllocationBalance(db, tx, scope, code, id, ct);
    }

    private static Task LockSupplierPayment(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope, string code,
        Guid id, CancellationToken ct) =>
        LockOperation(db, tx, $"supplier_payment:{scope.Organization}:{scope.Store}:{code}:{id}", ct);

    private static Task LockSupplierAllocation(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope, string code,
        Guid id, CancellationToken ct) =>
        LockOperation(db, tx, $"supplier_allocation:{scope.Organization}:{scope.Store}:{code}:{id}", ct);

    private static Task LockSupplierInvoice(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope, Guid id,
        CancellationToken ct) => LockOperation(db, tx, $"supplier_return:{scope.Organization}:{scope.Store}:{id}", ct);

    private static async Task<SupplierPaymentAllocation?> ReadSupplierPaymentAllocation(NpgsqlConnection db,
        NpgsqlTransaction tx, StoreScope scope, string code, Guid id, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT a.payment_id, a.allocated_by, a.allocated_at, l.receipt_id, l.amount
            FROM supplier_payment_allocations a JOIN supplier_payment_allocation_lines l
                ON (l.org_id, l.store_id, l.supplier_code, l.allocation_id) = (a.org_id, a.store_id, a.supplier_code, a.id)
            WHERE (a.org_id, a.store_id, a.supplier_code, a.id) = ($1, $2, $3, $4)
            ORDER BY l.receipt_id
            """, scope.Organization, scope.Store, code, id);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct))
            return null;
        List<SupplierPaymentAllocationLine> items = [];
        var allocation = new SupplierPaymentAllocation(id, rows.GetGuid(0), rows.GetGuid(1), rows.GetDateTime(2), items);
        do
        {
            items.Add(new SupplierPaymentAllocationLine(rows.GetGuid(3), rows.GetDecimal(4)));
        } while (await rows.ReadAsync(ct));
        return allocation;
    }

    private static async Task<SupplierPaymentAllocationReversal?> ReadSupplierAllocationReversal(NpgsqlConnection db,
        NpgsqlTransaction tx, StoreScope scope, string code, Guid id, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT allocation_id, reason, reversed_by, reversed_at FROM supplier_payment_allocation_reversals
            WHERE (org_id, store_id, supplier_code, id) = ($1, $2, $3, $4)
            """, scope.Organization, scope.Store, code, id);
        await using var rows = await command.ExecuteReaderAsync(ct);
        return await rows.ReadAsync(ct)
            ? new SupplierPaymentAllocationReversal(id, rows.GetGuid(0), rows.GetString(1), rows.GetGuid(2), rows.GetDateTime(3))
            : null;
    }

    private const string SupplierPaymentAmountsJoin = """
            CROSS JOIN LATERAL (
                SELECT coalesce(sum(l.amount), 0) AS amount FROM supplier_payment_allocations a
                JOIN supplier_payment_allocation_lines l ON
                    (l.org_id, l.store_id, l.supplier_code, l.allocation_id) = (a.org_id, a.store_id, a.supplier_code, a.id)
                LEFT JOIN supplier_payment_allocation_reversals r ON
                    (r.org_id, r.store_id, r.supplier_code, r.allocation_id) = (a.org_id, a.store_id, a.supplier_code, a.id)
                WHERE (a.org_id, a.store_id, a.supplier_code, a.payment_id) = (p.org_id, p.store_id, p.supplier_code, p.id)
                    AND r.id IS NULL
            ) applied
            """;

    private static async Task<SupplierPaymentAllocationBalance> ReadSupplierPaymentAllocationBalance(NpgsqlConnection db,
        NpgsqlTransaction tx, StoreScope scope, string code, Guid id, CancellationToken ct)
    {
        await using var command = db.Sql(tx, $"""
            SELECT p.amount, p.payment_date, p.reference, p.recorded_by, p.created_at, applied.amount
            FROM supplier_payments p
            {SupplierPaymentAmountsJoin}
            WHERE (p.org_id, p.store_id, p.supplier_code, p.id) = ($1, $2, $3, $4)
            """, scope.Organization, scope.Store, code, id);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct))
            throw new PosError(404, "supplier_payment_not_found");
        var payment = new SupplierPayment(id, rows.GetDecimal(0), rows.GetFieldValue<DateOnly>(1), rows.GetString(2),
            rows.GetGuid(3), rows.GetDateTime(4));
        return new SupplierPaymentAllocationBalance(payment, rows.GetDecimal(5));
    }
}
