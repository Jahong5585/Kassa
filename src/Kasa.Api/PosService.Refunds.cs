using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<RefundOptions> GetRefundOptions(HttpContext context, Guid sourceRegister, Guid sale,
        CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireCashier(db, tx, context, false, ct);
        return await ReadRefundOptions(db, tx, scope.Organization, scope.Store, sourceRegister, sale, ct);
    }

    public async Task<RefundOptions> GetAdminRefundOptions(HttpContext context, Guid store, Guid sourceRegister,
        Guid sale, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        return await ReadRefundOptions(db, tx, scope.Organization, store, sourceRegister, sale, ct);
    }

    public async Task<RefundApproval> ApproveRefund(HttpContext context, Guid store, ApproveRefund request,
        CancellationToken ct)
    {
        var refund = request.Refund ?? throw new PosError(400, "invalid_refund_approval");
        var (items, digest) = ValidateRefund(refund);
        if (request.RegisterId == Guid.Empty || !IsMoney(request.Amount))
            throw new PosError(400, "invalid_refund_approval");

        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockOperation(db, tx, $"refund:{scope.Organization}:{store}:{refund.OperationId}", ct);

        var existing = await ReadApproval(db, tx, scope.Organization, store, refund.OperationId, ct);
        if (existing is { } saved)
        {
            if (saved.Approval.RegisterId != request.RegisterId || saved.Approval.Amount != request.Amount ||
                !CryptographicOperations.FixedTimeEquals(saved.Digest, digest))
                throw new PosError(409, "refund_approval_conflict");
            return saved.Approval;
        }

        await using var completed = db.Sql(tx, """
            SELECT 1 FROM refunds WHERE (org_id, store_id, id) = ($1, $2, $3)
            """, scope.Organization, store, refund.OperationId);
        if (await completed.ExecuteScalarAsync(ct) is not null)
            throw new PosError(409, "refund_already_completed");

        await using var target = db.Sql(tx, """
            SELECT 1 FROM shifts WHERE (org_id, store_id, register_id, id) = ($1, $2, $3, $4) AND closed_at IS NULL
            """, scope.Organization, store, request.RegisterId, refund.ShiftId);
        if (await target.ExecuteScalarAsync(ct) is null)
            throw new PosError(409, "active_shift_required");
        await LockOperation(db, tx, $"sale:{scope.Organization}:{store}:{refund.SourceRegisterId}:{refund.SaleId}", ct);
        var options = await ReadRefundOptions(db, tx, scope.Organization, store, refund.SourceRegisterId, refund.SaleId, ct);
        if (PriceRefund(options, items).Sum(line => line.Amount) != request.Amount)
            throw new PosError(409, "refund_amount_changed");
        await db.Run(tx, ct, """
            INSERT INTO refund_approvals (org_id, store_id, id, register_id, approved_by, amount, request_digest)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            """, scope.Organization, store, refund.OperationId, request.RegisterId, scope.Staff, request.Amount, digest);
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff,
            "refund.approved", refund.OperationId, ct);
        await tx.CommitAsync(ct);
        return new RefundApproval(refund.OperationId, request.RegisterId, request.Amount, scope.Staff);
    }

    public async Task<RefundResult> CreateRefund(HttpContext context, CreateRefund request, CancellationToken ct)
    {
        var (items, digest) = ValidateRefund(request);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireCashier(db, tx, context, true, ct);
        await LockOperation(db, tx, $"refund:{scope.Organization}:{scope.Store}:{request.OperationId}", ct);

        var existing = await ReadRefund(db, tx, scope, request.OperationId, ct);
        if (existing is { } saved)
        {
            if (!CryptographicOperations.FixedTimeEquals(saved.Digest, digest))
                throw new PosError(409, "operation_id_reused");
            return saved.Refund;
        }

        await RequireOpenShift(db, tx, scope, request.ShiftId, ct);
        // The original sale is shared across registers. Serialize every refund against that sale.
        await LockOperation(db, tx, $"sale:{scope.Organization}:{scope.Store}:{request.SourceRegisterId}:{request.SaleId}", ct);
        var options = await ReadRefundOptions(db, tx, scope.Organization, scope.Store, request.SourceRegisterId, request.SaleId, ct);
        var lines = PriceRefund(options, items);
        var total = lines.Sum(line => line.Amount);
        var approval = await ReadApproval(db, tx, scope.Organization, scope.Store, request.OperationId, ct);
        Guid approvedBy = Guid.Empty;
        if (approval is { } authorization)
        {
            if (authorization.Approval.RegisterId != scope.Register || authorization.Approval.Amount != total ||
                !CryptographicOperations.FixedTimeEquals(authorization.Digest, digest))
                throw new PosError(409, "refund_approval_conflict");
            approvedBy = authorization.Approval.ApprovedBy;
        }
        var limit = configuration.GetValue<decimal>("Refunds:CashierLimit");
        if (approvedBy == Guid.Empty && (limit <= 0 || total + options.Lines.Sum(line => line.ReturnedAmount) > limit))
            throw new PosError(409, "refund_approval_required");

        var created = DateTime.UtcNow;
        created = created.AddTicks(-(created.Ticks % 10));
        var inserted = await db.Run(tx, ct, """
            INSERT INTO refunds (org_id, store_id, id, register_id, shift_id, cashier_id, source_register_id,
                sale_id, method, total, reason, request_digest, approved_by, created_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12,
                NULLIF($13, '00000000-0000-0000-0000-000000000000'::uuid), $14)
            ON CONFLICT DO NOTHING
            """, scope.Organization, scope.Store, request.OperationId, scope.Register, request.ShiftId,
            scope.Staff, request.SourceRegisterId, request.SaleId, options.Method, total, request.Reason.Trim(), digest,
            approvedBy, created);
        if (inserted != 1)
            throw new PosError(409, "operation_id_reused");

        var alreadyReturned = options.Lines.ToDictionary(line => (line.ProductId, line.LotId), line => line.ReturnedQuantity);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var (destination, cost) = await RestoreLot(db, tx, scope, request.SourceRegisterId, request.SaleId,
                line, alreadyReturned[(line.ProductId, line.LotId)], ct);
            lines[i] = line with { Destination = destination };
            await db.Run(tx, ct, """
                INSERT INTO refund_lines (org_id, store_id, refund_id, source_register_id, sale_id, product_id,
                    lot_id, quantity, amount, destination, inventory_cost)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
                """, scope.Organization, scope.Store, request.OperationId, request.SourceRegisterId,
                request.SaleId, line.ProductId, line.LotId, line.Quantity, line.Amount, destination, cost);
        }

        await Audit(db, tx, context, scope, "refund.committed", request.OperationId, ct);
        await tx.CommitAsync(ct);
        return new RefundResult(request.OperationId, request.ShiftId, request.SourceRegisterId, request.SaleId,
            options.Method, total, request.Reason.Trim(), created, approvedBy == Guid.Empty ? null : approvedBy,
            lines, ReceiptMark);
    }

    public async Task<RefundResult> GetRefund(HttpContext context, Guid id, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireCashier(db, tx, context, false, ct);
        return (await ReadRefund(db, tx, scope, id, ct))?.Refund ?? throw new PosError(404, "refund_not_found");
    }

    private static (RefundItem[] Items, byte[] Digest) ValidateRefund(CreateRefund request)
    {
        if (request.OperationId == Guid.Empty || request.ShiftId == Guid.Empty ||
            request.SourceRegisterId == Guid.Empty || request.SaleId == Guid.Empty ||
            !IsText(request.Reason, 500) ||
            request.Items is not { Length: >= 1 and <= 100 })
            throw new PosError(400, "invalid_refund");
        var distinct = new HashSet<(Guid, Guid)>();
        foreach (var item in request.Items)
        {
            if (item is null || item.ProductId == Guid.Empty || item.LotId == Guid.Empty ||
                !IsQuantity(item.Quantity) || item.Destination is not ("stock" or "quarantine") ||
                !distinct.Add((item.ProductId, item.LotId)))
                throw new PosError(400, "invalid_refund_item");
        }
        var items = request.Items.OrderBy(item => item.ProductId).ThenBy(item => item.LotId).ToArray();
        var digest = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            request.ShiftId, request.SourceRegisterId, request.SaleId, Reason = request.Reason.Trim(),
            Items = items.Select(item => new
            {
                item.ProductId, item.LotId, item.Destination,
                Quantity = item.Quantity.ToString("G29", CultureInfo.InvariantCulture)
            })
        }));
        return (items, digest);
    }

    private static RefundLine[] PriceRefund(RefundOptions options, RefundItem[] items)
    {
        var original = options.Lines.ToDictionary(line => (line.ProductId, line.LotId));
        var result = new RefundLine[items.Length];
        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i];
            if (!original.TryGetValue((item.ProductId, item.LotId), out var sold))
                throw new PosError(409, "refund_item_not_on_sale");
            if (sold.Unit == "piece" && item.Quantity != decimal.Truncate(item.Quantity))
                throw new PosError(400, "fractional_piece_quantity");
            var cumulative = sold.ReturnedQuantity + item.Quantity;
            if (cumulative > sold.SoldQuantity)
                throw new PosError(409, "refund_quantity_exceeded");

            var cumulativeAmount = RoundedShare(sold.SoldAmount, cumulative, sold.SoldQuantity);
            result[i] = new RefundLine(item.ProductId, item.LotId, item.Quantity,
                cumulativeAmount - sold.ReturnedAmount, item.Destination);
        }
        return result;
    }

    private static async Task<RefundOptions> ReadRefundOptions(NpgsqlConnection db, NpgsqlTransaction tx,
        Guid org, Guid store, Guid register, Guid sale, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT p.method, l.product_id, l.lot_id, l.name, l.unit, l.quantity, l.line_total,
                   returned.quantity, returned.amount
            FROM sales p JOIN sale_lines l
              ON (p.org_id, p.store_id, p.register_id, p.id) = (l.org_id, l.store_id, l.register_id, l.sale_id)
            CROSS JOIN LATERAL (
                SELECT coalesce(sum(quantity), 0) AS quantity, coalesce(sum(amount), 0) AS amount
                FROM refund_lines
                WHERE (org_id, store_id, source_register_id, sale_id, product_id, lot_id) =
                      (l.org_id, l.store_id, l.register_id, l.sale_id, l.product_id, l.lot_id)
            ) returned
            WHERE (p.org_id, p.store_id, p.register_id, p.id) = ($1, $2, $3, $4)
            ORDER BY l.product_id, l.lot_id
            """, org, store, register, sale);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct))
            throw new PosError(404, "sale_not_found");
        var method = rows.GetString(0);
        List<RefundableLine> lines = [];
        do
        {
            lines.Add(new RefundableLine(rows.GetGuid(1), rows.GetGuid(2), rows.GetString(3), rows.GetString(4),
                rows.GetDecimal(5), rows.GetDecimal(6), rows.GetDecimal(7), rows.GetDecimal(8)));
        } while (await rows.ReadAsync(ct));
        return new RefundOptions(register, sale, method, lines);
    }

    private static async Task<(string Destination, decimal? Cost)> RestoreLot(NpgsqlConnection db, NpgsqlTransaction tx,
        RegisterScope scope, Guid sourceRegister, Guid sale, RefundLine line, decimal returned, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            WITH target AS (
                SELECT l.org_id, l.store_id, l.product_id, l.id,
                    l.quantity + l.quarantine_quantity AS before_quantity,
                    kasa_weighted_cost_delta(s.inventory_cost, s.quantity, $11 + $7) -
                        kasa_weighted_cost_delta(s.inventory_cost, s.quantity, $11) AS cost,
                    ($5 = 'quarantine' OR l.quarantined OR (p.requires_expiry AND l.expiry_date IS NULL)
                       OR (l.expiry_date < (statement_timestamp() AT TIME ZONE $6)::date) IS TRUE) AS quarantine
                FROM stock_lots l JOIN products p ON (l.org_id, l.product_id) = (p.org_id, p.id)
                JOIN sale_lines s ON (s.org_id, s.store_id, s.register_id, s.sale_id, s.product_id, s.lot_id) =
                    (l.org_id, l.store_id, $9, $10, l.product_id, l.id)
                WHERE (l.org_id, l.store_id, l.product_id, l.id) = ($1, $2, $3, $4)
                FOR UPDATE OF l
            )
            UPDATE stock_lots l SET quantity = l.quantity + CASE WHEN t.quarantine THEN 0 ELSE $7 END,
                quarantine_quantity = l.quarantine_quantity + CASE WHEN t.quarantine THEN $7 ELSE 0 END,
                inventory_value = kasa_stock_value_after(l.inventory_value, t.before_quantity,
                    t.before_quantity + $7, t.cost)
            FROM target t WHERE (l.org_id, l.store_id, l.product_id, l.id) = (t.org_id, t.store_id, t.product_id, t.id)
                AND l.quantity + CASE WHEN t.quarantine THEN 0 ELSE $7 END <= $8
                AND l.quarantine_quantity + CASE WHEN t.quarantine THEN $7 ELSE 0 END <= $8
            RETURNING CASE WHEN t.quarantine THEN 'quarantine' ELSE 'stock' END, t.cost
            """, scope.Organization, scope.Store, line.ProductId, line.LotId, line.Destination, scope.TimeZone,
            line.Quantity, MaxQuantity, sourceRegister, sale, returned);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct))
            throw new PosError(409, "stock_restore_conflict");
        return (rows.GetString(0), rows.DecimalOrNull(1));
    }

    private static async Task<(RefundApproval Approval, byte[] Digest)?> ReadApproval(NpgsqlConnection db,
        NpgsqlTransaction tx, Guid org, Guid store, Guid id, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT register_id, amount, approved_by, request_digest FROM refund_approvals
            WHERE (org_id, store_id, id) = ($1, $2, $3)
            """, org, store, id);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct))
            return null;
        return (new RefundApproval(id, rows.GetGuid(0), rows.GetDecimal(1), rows.GetGuid(2)), rows.GetFieldValue<byte[]>(3));
    }

    private static async Task<(RefundResult Refund, byte[] Digest)?> ReadRefund(NpgsqlConnection db,
        NpgsqlTransaction tx, RegisterScope scope, Guid id, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT r.shift_id, r.source_register_id, r.sale_id, r.method, r.total, r.reason, r.created_at,
                r.approved_by, r.request_digest, l.product_id, l.lot_id, l.quantity, l.amount, l.destination
            FROM refunds r JOIN refund_lines l ON (r.org_id, r.store_id, r.id) = (l.org_id, l.store_id, l.refund_id)
            WHERE (r.org_id, r.store_id, r.id, r.register_id, r.cashier_id) = ($1, $2, $3, $4, $5)
            """, scope.Organization, scope.Store, id, scope.Register, scope.Staff);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct))
            return null;
        List<RefundLine> lines = [];
        var result = new RefundResult(id, rows.GetGuid(0), rows.GetGuid(1), rows.GetGuid(2), rows.GetString(3),
            rows.GetDecimal(4), rows.GetString(5), rows.GetDateTime(6), rows.IsDBNull(7) ? null : rows.GetGuid(7),
            lines, ReceiptMark);
        var digest = rows.GetFieldValue<byte[]>(8);
        do
        {
            lines.Add(new RefundLine(rows.GetGuid(9), rows.GetGuid(10), rows.GetDecimal(11), rows.GetDecimal(12),
                rows.GetString(13)));
        } while (await rows.ReadAsync(ct));
        return (result with { Lines = lines.OrderBy(line => line.ProductId).ThenBy(line => line.LotId).ToArray() }, digest);
    }

    private static async Task LockOperation(NpgsqlConnection db, NpgsqlTransaction tx, string key, CancellationToken ct,
        bool shared = false)
    {
        var sql = shared ? "SELECT pg_advisory_xact_lock_shared(hashtextextended($1, 1))"
            : "SELECT pg_advisory_xact_lock(hashtextextended($1, 1))";
        await using var command = db.Sql(tx, sql, key);
        await command.ExecuteNonQueryAsync(ct);
    }
}
