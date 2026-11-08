using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<SupplierReturn> ReturnToSupplier(HttpContext context, Guid store, CreateSupplierReturn request,
        CancellationToken ct)
    {
        if (request.ReturnId == Guid.Empty || request.ReceiptId == Guid.Empty || !IsText(request.Reason, 500) ||
            request.Items is not { Length: >= 1 and <= 100 } ||
            request.Items.Any(item => item is null || item.ProductId == Guid.Empty || item.LotId == Guid.Empty ||
                !IsQuantity(item.Quantity) || item.Bucket is not ("stock" or "quarantine")) ||
            request.Items.Select(item => (item.ProductId, item.LotId)).Distinct().Count() != request.Items.Length)
            throw new PosError(400, "invalid_supplier_return");
        var reason = request.Reason.Trim();
        var items = request.Items.OrderBy(item => item.ProductId).ThenBy(item => item.LotId).ToArray();

        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockStockOperation(db, tx, scope, request.ReturnId, ct);
        var existing = await ReadSupplierReturn(db, tx, scope, request.ReturnId, ct);
        if (existing is not null)
        {
            if (existing.ReceiptId != request.ReceiptId || existing.Reason != reason ||
                !existing.Lines.Select(line => new SupplierReturnItem(line.ProductId, line.LotId, line.Quantity, line.Bucket))
                    .SequenceEqual(items))
                throw new PosError(409, "supplier_return_id_reused");
            return existing;
        }
        await LockSupplierInvoice(db, tx, scope, request.ReceiptId, ct);
        var receipt = await ReadReceipt(db, tx, scope, request.ReceiptId, ct)
            ?? throw new PosError(404, "receipt_not_found");
        if (receipt.Status != "posted")
            throw new PosError(409, "supplier_return_requires_posted_receipt");

        var sources = await ReadReturnSource(db, tx, scope, request.ReceiptId, ct);
        var lines = new List<SupplierReturnLine>(items.Length);
        foreach (var item in items)
        {
            if (!sources.TryGetValue((item.ProductId, item.LotId), out var origin))
                throw new PosError(409, "supplier_return_item_not_in_receipt");
            var cumulative = origin.Returned + item.Quantity;
            if (cumulative > origin.Received)
                throw new PosError(409, "supplier_return_quantity_exceeded");
            decimal? credit = origin.Cost is decimal cost
                ? decimal.Round(cost * (cumulative / origin.Received), 2, MidpointRounding.AwayFromZero) - origin.Credited
                : null;
            lines.Add(new SupplierReturnLine(item.ProductId, item.LotId, item.Quantity, item.Bucket, credit));
        }
        var knownCredit = lines.Sum(line => line.CreditAmount ?? 0);
        if (!IsMoney(knownCredit))
            throw new PosError(400, "invalid_supplier_return_total");
        decimal? total = lines.Any(line => line.CreditAmount is null) ? null : knownCredit;
        var created = await InsertStockOperation(db, tx, scope, request.ReturnId, "supplierReturn", reason, ct);
        await db.Run(tx, ct, """
            INSERT INTO supplier_returns (org_id, store_id, id, receipt_id, credit_amount)
            VALUES ($1, $2, $3, $4, $5)
            """, scope.Organization, store, request.ReturnId, request.ReceiptId, total);

        foreach (var line in lines)
        {
            var lot = await ReadLockedLot(db, tx, scope.Organization, store, line.ProductId, line.LotId, ct);
            ValidateLotQuantity(lot.Unit, line.Quantity);
            var stock = lot.StockQuantity - (line.Bucket == "stock" ? line.Quantity : 0);
            var quarantine = lot.QuarantineQuantity - (line.Bucket == "quarantine" ? line.Quantity : 0);
            if (!IsStockQuantity(stock) || !IsStockQuantity(quarantine))
                throw new PosError(409, "stock_quantity_conflict");
            await ChangeLot(db, tx, scope, request.ReturnId, lot, stock, quarantine, ct);
            await db.Run(tx, ct, """
                INSERT INTO supplier_return_lines (org_id, store_id, return_id, receipt_id, product_id, lot_id,
                    quantity, bucket, credit_amount)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
                """, scope.Organization, store, request.ReturnId, request.ReceiptId, line.ProductId, line.LotId,
                line.Quantity, line.Bucket, line.CreditAmount);
        }
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff, "supplier.return_posted",
            request.ReturnId, ct);
        await tx.CommitAsync(ct);
        return new SupplierReturn(request.ReturnId, request.ReceiptId, receipt.SupplierRef, reason, scope.Staff,
            created, total, lines);
    }

    public async Task<SupplierReturn> GetSupplierReturn(HttpContext context, Guid store, Guid id, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        return await ReadSupplierReturn(db, tx, scope, id, ct) ?? throw new PosError(404, "supplier_return_not_found");
    }

    private static async Task<Dictionary<(Guid, Guid), (decimal Received, decimal? Cost, decimal Returned, decimal Credited)>>
        ReadReturnSource(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope, Guid receipt, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT m.product_id, m.lot_id, m.quantity, c.total, previous.quantity, previous.amount
            FROM receiving_movements m LEFT JOIN receiving_movement_costs c ON
                (c.org_id, c.store_id, c.receipt_id, c.product_id, c.lot_id) =
                (m.org_id, m.store_id, m.receipt_id, m.product_id, m.lot_id)
            CROSS JOIN LATERAL (
                SELECT coalesce(sum(quantity), 0) AS quantity, coalesce(sum(credit_amount), 0) AS amount
                FROM supplier_return_lines WHERE (org_id, store_id, receipt_id, product_id, lot_id) =
                    (m.org_id, m.store_id, m.receipt_id, m.product_id, m.lot_id)
            ) previous
            WHERE (m.org_id, m.store_id, m.receipt_id) = ($1, $2, $3)
            """, scope.Organization, scope.Store, receipt);
        await using var rows = await command.ExecuteReaderAsync(ct);
        var result = new Dictionary<(Guid, Guid), (decimal, decimal?, decimal, decimal)>();
        while (await rows.ReadAsync(ct))
            result.Add((rows.GetGuid(0), rows.GetGuid(1)),
                (rows.GetDecimal(2), rows.DecimalOrNull(3), rows.GetDecimal(4), rows.GetDecimal(5)));
        return result;
    }

    private static async Task<SupplierReturn?> ReadSupplierReturn(NpgsqlConnection db, NpgsqlTransaction tx,
        StoreScope scope, Guid id, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT r.receipt_id, d.supplier_ref, s.reason, s.actor_id, s.created_at, r.credit_amount,
                l.product_id, l.lot_id, l.quantity, l.bucket, l.credit_amount
            FROM supplier_returns r JOIN stock_operations s ON (s.org_id, s.store_id, s.id) = (r.org_id, r.store_id, r.id)
            JOIN receiving_documents d ON (d.org_id, d.store_id, d.id) = (r.org_id, r.store_id, r.receipt_id)
            JOIN supplier_return_lines l ON (l.org_id, l.store_id, l.return_id) = (r.org_id, r.store_id, r.id)
            WHERE (r.org_id, r.store_id, r.id) = ($1, $2, $3)
            """, scope.Organization, scope.Store, id);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct))
            return null;
        List<SupplierReturnLine> lines = [];
        var result = new SupplierReturn(id, rows.GetGuid(0), rows.GetString(1), rows.GetString(2), rows.GetGuid(3),
            rows.GetDateTime(4), rows.DecimalOrNull(5), lines);
        do
        {
            lines.Add(new SupplierReturnLine(rows.GetGuid(6), rows.GetGuid(7), rows.GetDecimal(8), rows.GetString(9),
                rows.DecimalOrNull(10)));
        } while (await rows.ReadAsync(ct));
        return result with { Lines = lines.OrderBy(line => line.ProductId).ThenBy(line => line.LotId).ToArray() };
    }
}
