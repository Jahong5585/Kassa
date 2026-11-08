using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<PurchaseOrder> CreatePurchaseOrder(HttpContext context, Guid store, CreatePurchaseOrder request,
        CancellationToken ct)
    {
        if (request.OrderId == Guid.Empty || !IsText(request.SupplierRef, 160) ||
            request.Items is not { Length: >= 1 and <= 100 })
            throw new PosError(400, "invalid_purchase_order");
        var products = new HashSet<Guid>();
        foreach (var item in request.Items)
        {
            if (item is null || item.ProductId == Guid.Empty || !IsQuantity(item.Quantity) || !products.Add(item.ProductId))
                throw new PosError(400, "invalid_purchase_order_item");
        }

        var items = request.Items.OrderBy(item => item.ProductId).ToArray();
        var supplier = request.SupplierRef.Trim();
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockOperation(db, tx, $"order:{scope.Organization}:{store}:{request.OrderId}", ct);
        var existing = await ReadPurchaseOrder(db, tx, scope, request.OrderId, ct);
        if (existing is not null)
        {
            if (existing.SupplierRef != supplier ||
                !existing.Lines.Select(line => new OrderItem(line.ProductId, line.Ordered)).SequenceEqual(items))
                throw new PosError(409, "purchase_order_id_reused");
            return existing;
        }

        await ValidateSupplier(db, tx, scope, supplier, ct);
        await ValidateReceivingProducts(db, tx, scope,
            items.Select(item => (item.ProductId, item.Quantity, (DateOnly?)null)), posting: false, ct);
        var added = await db.Run(tx, ct, """
            INSERT INTO purchase_orders (org_id, store_id, id, supplier_ref, created_by) VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT DO NOTHING
            """, scope.Organization, store, request.OrderId, supplier, scope.Staff);
        if (added != 1)
            throw new PosError(409, "purchase_order_id_reused");
        foreach (var item in items)
        {
            await db.Run(tx, ct, """
                INSERT INTO purchase_order_lines (org_id, store_id, order_id, product_id, ordered)
                VALUES ($1, $2, $3, $4, $5)
                """, scope.Organization, store, request.OrderId, item.ProductId, item.Quantity);
        }
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff, "purchase_order.created",
            request.OrderId, ct);
        await tx.CommitAsync(ct);
        return new PurchaseOrder(request.OrderId, supplier, scope.Staff, "awaiting",
            items.Select(item => new OrderLine(item.ProductId, item.Quantity, 0, item.Quantity)).ToArray());
    }

    public async Task<IReadOnlyList<PurchaseOrderSummary>> ListPurchaseOrders(HttpContext context, Guid store,
        bool? openOnly, int? offset, CancellationToken ct)
    {
        if (offset is < 0 or > 10_000)
            throw new PosError(400, "invalid_purchase_order_filter");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, false, ct);
        await using var command = db.Sql(tx, """
            SELECT p.id, p.supplier_ref,
                CASE WHEN bool_and(l.received = l.ordered) THEN 'received'
                     WHEN bool_or(l.received > 0) THEN 'partial' ELSE 'awaiting' END, p.expected_delivery_date
            FROM purchase_orders p JOIN purchase_order_lines l
              ON (p.org_id, p.store_id, p.id) = (l.org_id, l.store_id, l.order_id)
            WHERE (p.org_id, p.store_id) = ($1, $2) AND p.stage IN ('registered', 'sent')
            GROUP BY p.org_id, p.store_id, p.id
            HAVING NOT $3 OR bool_or(l.received < l.ordered)
            ORDER BY p.id LIMIT 25 OFFSET $4
            """, scope.Organization, store, openOnly ?? true, offset ?? 0);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<PurchaseOrderSummary> result = [];
        while (await rows.ReadAsync(ct))
            result.Add(new PurchaseOrderSummary(rows.GetGuid(0), rows.GetString(1), rows.GetString(2))
                { ExpectedDeliveryDate = ReadDate(rows, 3) });
        return result;
    }

    public async Task<PurchaseOrder> GetPurchaseOrder(HttpContext context, Guid store, Guid id, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, false, ct);
        return await ReadPurchaseOrder(db, tx, scope, id, ct) ?? throw new PosError(404, "purchase_order_not_found");
    }

    private static async Task<PurchaseOrder?> ReadPurchaseOrder(NpgsqlConnection db, NpgsqlTransaction tx,
        StoreScope scope, Guid id, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT p.supplier_ref, p.created_by, l.product_id, l.ordered, l.received, p.expected_delivery_date
            FROM purchase_orders p JOIN purchase_order_lines l
              ON (p.org_id, p.store_id, p.id) = (l.org_id, l.store_id, l.order_id)
            WHERE (p.org_id, p.store_id, p.id) = ($1, $2, $3) AND p.stage IN ('registered', 'sent')
            """, scope.Organization, scope.Store, id);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct))
            return null;
        var supplier = rows.GetString(0);
        var author = rows.GetGuid(1);
        var delivery = ReadDate(rows, 5);
        List<OrderLine> lines = [];
        do
        {
            var ordered = rows.GetDecimal(3);
            var received = rows.GetDecimal(4);
            lines.Add(new OrderLine(rows.GetGuid(2), ordered, received, ordered - received));
        } while (await rows.ReadAsync(ct));
        var status = lines.All(line => line.Remaining == 0) ? "received" :
            lines.Any(line => line.Received > 0) ? "partial" : "awaiting";
        return new PurchaseOrder(id, supplier, author, status, lines.OrderBy(line => line.ProductId).ToArray())
            { ExpectedDeliveryDate = delivery };
    }

    private static async Task ValidateReceiptOrder(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope,
        Guid? orderId, string supplier, ReceiptItem[] items, CancellationToken ct)
    {
        if (orderId is null)
            return;
        var order = await ReadPurchaseOrder(db, tx, scope, orderId.Value, ct)
            ?? throw new PosError(404, "purchase_order_not_found");
        if (!string.Equals(order.SupplierRef, supplier, StringComparison.OrdinalIgnoreCase))
            throw new PosError(409, "receipt_supplier_mismatch");
        var ordered = order.Lines.Select(line => line.ProductId).ToHashSet();
        if (items.Any(item => !ordered.Contains(item.ProductId)))
            throw new PosError(409, "receipt_product_not_on_order");
    }

    private static async Task ApplyOrderReceipt(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope,
        Guid orderId, ReceiptItem[] items, CancellationToken ct)
    {
        // Items are already sorted by product. Concurrent receipts update order lines in that order.
        foreach (var group in items.GroupBy(item => item.ProductId))
        {
            var updated = await db.Run(tx, ct, """
                UPDATE purchase_order_lines SET received = received + $5
                WHERE (org_id, store_id, order_id, product_id) = ($1, $2, $3, $4)
                    AND received + $5 <= ordered
                """, scope.Organization, scope.Store, orderId, group.Key, group.Sum(item => item.Quantity));
            if (updated != 1)
                throw new PosError(409, "receipt_order_quantity_exceeded");
        }
    }
}
