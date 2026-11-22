using System.Globalization;
using System.Text.Json;
using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<QuickReceivingTemplate> GetQuickReceivingTemplate(HttpContext context, Guid store, Guid id,
        CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, false, ct);
        var card = await ReadProductCard(db, tx, scope, id, ct) ?? throw new PosError(404, "product_not_found");
        if (!card.Active) throw new PosError(409, "receipt_product_not_available");
        // Last actual receipt, scoped to this store. Unknown cost stays unknown, never zero.
        await using var command = db.Sql(tx, """
            SELECT unit_cost, manufactured_on, expiry_date FROM (
                SELECT q.unit_cost, q.manufactured_on, l.expiry_date, b.posted_at, q.lot_id
                FROM quick_receiving_lines q JOIN quick_receivings b USING (org_id, store_id, operation_id)
                JOIN stock_lots l ON (l.org_id,l.store_id,l.product_id,l.id) =
                    (q.org_id,q.store_id,q.product_id,q.lot_id)
                WHERE (q.org_id,q.store_id,q.product_id) = ($1,$2,$3)
                UNION ALL
                SELECT CASE WHEN m.quantity > 0 THEN round(c.total / m.quantity, 2) END,
                    NULLIF(i.value->>'ManufacturedOn','')::date, m.expiry_date, d.posted_at, m.lot_id
                FROM receiving_documents d JOIN (
                    SELECT r.*, l.expiry_date FROM receiving_movements r JOIN stock_lots l
                    ON (l.org_id,l.store_id,l.product_id,l.id) = (r.org_id,r.store_id,r.product_id,r.lot_id)
                ) m ON (m.org_id,m.store_id,m.receipt_id) = (d.org_id,d.store_id,d.id)
                LEFT JOIN receiving_movement_costs c ON (c.org_id,c.store_id,c.receipt_id,c.product_id,c.lot_id) =
                    (m.org_id,m.store_id,m.receipt_id,m.product_id,m.lot_id)
                LEFT JOIN LATERAL jsonb_array_elements(d.draft_body) i(value)
                    ON i.value->>'ProductId' = m.product_id::text AND i.value->>'LotId' = m.lot_id::text
                WHERE (d.org_id,d.store_id,m.product_id) = ($1,$2,$3) AND d.status = 'posted'
                UNION ALL
                SELECT CASE WHEN m.stock_after + m.quarantine_after > 0
                    THEN round(o.total_cost / (m.stock_after + m.quarantine_after), 2) END,
                    NULL::date, o.expiry_date, b.created_at, o.lot_id
                FROM opening_stock_lines o JOIN stock_operations b
                    ON (b.org_id,b.store_id,b.id) = (o.org_id,o.store_id,o.operation_id)
                JOIN stock_operation_lines m
                    ON (m.org_id,m.store_id,m.operation_id,m.product_id,m.lot_id) =
                       (o.org_id,o.store_id,o.operation_id,o.product_id,o.lot_id)
                WHERE (o.org_id,o.store_id,o.product_id) = ($1,$2,$3)
            ) x ORDER BY posted_at DESC, lot_id LIMIT 1
            """, scope.Organization, store, id);
        await using var rows = await command.ExecuteReaderAsync(ct);
        return await rows.ReadAsync(ct)
            ? new(card, rows.DecimalOrNull(0), ReadDate(rows,1), ReadDate(rows,2))
            : new(card, null, null, null);
    }

    internal static void ValidateQuickReceiving(QuickReceivingRequest request)
    {
        if (request is null || request.OperationId == Guid.Empty || request.Items is not { Length: >= 1 and <= 100 } ||
            request.Items.Any(i => i is null) || request.Items.Select(i => i.ProductId).Distinct().Count() != request.Items.Length ||
            request.Items.Select(i => i.LotId).Distinct().Count() != request.Items.Length)
            throw new PosError(400, "invalid_quick_receiving");
        foreach (var i in request.Items)
        {
            if (i.ProductId == Guid.Empty || i.LotId == Guid.Empty || i.ExpectedVersion < 0 ||
                i.ExpectedPriceVersion < 0 || i.ExpectedVersion == int.MaxValue || i.ExpectedPriceVersion == int.MaxValue || (i.ExpectedVersion == 0) != (i.ExpectedPriceVersion == 0) ||
                i.Product is null || !IsQuantity(i.Quantity) ||
                i.UnitCost is {} cost && (!IsMoney(cost) || cost > MaxUnitPrice) ||
                i.SalePrice is {} price && (!IsMoney(price) || price <= 0 || price > MaxUnitPrice) ||
                i.ManufacturedOn is {} made && !IsBusinessDate(made) ||
                i.ExpiryDate is {} expiry && !IsBusinessDate(expiry))
                throw new PosError(400, "invalid_quick_receiving_line");
            ValidateProductCard(i.ProductId, new WriteProductCard(i.ExpectedVersion, i.Product.Name, i.Product.Unit,
                i.Product.RequiresExpiry, null, i.Product.Category, i.Product.Brand, null, null, i.Product.Barcodes)
                { ShelfLifeDays = i.Product.ShelfLifeDays });
            ValidateLotQuantity(i.Product.Unit, i.Quantity);
            if (i.UnitCost is {} unitCost && !IsMoney(decimal.Round(unitCost * i.Quantity, 2, MidpointRounding.AwayFromZero)))
                throw new PosError(400, "invalid_quick_receiving_line");
        }
    }

    public async Task<QuickReceivingResult> PostQuickReceiving(HttpContext context, Guid store,
        QuickReceivingRequest request, CancellationToken ct)
    {
        ValidateQuickReceiving(request);
        var body = JsonSerializer.Serialize(request);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, false, ct);
        await LockOperation(db, tx, $"quick_receiving:{scope.Organization}:{store}:{request.OperationId}", ct);
        await using (var command = db.Sql(tx, """
            SELECT body = $4::jsonb, result FROM quick_receivings
            WHERE (org_id,store_id,operation_id) = ($1,$2,$3)
            """, scope.Organization, store, request.OperationId, body))
        await using (var replay = await command.ExecuteReaderAsync(ct))
            if (await replay.ReadAsync(ct))
            {
                if (!replay.GetBoolean(0)) throw new PosError(409, "quick_receiving_id_reused");
                return JsonSerializer.Deserialize<QuickReceivingResult>(replay.GetString(1))!;
            }
        await LockInventoryMembership(db, tx, scope.Organization, ct, shared: true);
        foreach (var code in request.Items.SelectMany(i => i.Product.Barcodes).Select(b => b.Code).Distinct().Order(StringComparer.Ordinal))
            await LockOperation(db, tx, $"receiving_barcode:{scope.Organization}:{code}", ct);
        await LockLotCreationProducts(db, tx, scope, request.Items.Select(i => i.ProductId), ct);
        foreach (var id in request.Items.Select(i => i.ProductId).Order())
            await LockProduct(db, tx, scope.Organization, id, true, ct);
        var today = await StoreDate(db, tx, scope, ct);
        foreach (var i in request.Items.OrderBy(i => i.ProductId))
        {
            var old = await ReadProductCard(db, tx, scope, i.ProductId, ct);
            if (old is null ? i.ExpectedVersion != 0 : old.Version != i.ExpectedVersion || old.PriceVersion != i.ExpectedPriceVersion)
                throw new PosError(409, "quick_receiving_product_changed");
            if (old is not null && !old.Active) throw new PosError(409, "receipt_product_not_available");
            var value = ValidateProductCard(i.ProductId, new WriteProductCard(i.ExpectedVersion, i.Product.Name, i.Product.Unit,
                i.Product.RequiresExpiry, old?.Sku, i.Product.Category, i.Product.Brand, old?.SupplierRef, old?.TaxProfile,
                i.Product.Barcodes) { ShelfLifeDays = i.Product.ShelfLifeDays });
            if (old is null || value.Name != old.Name || value.Unit != old.Unit || value.RequiresExpiry != old.RequiresExpiry ||
                value.Category != old.Category || value.Brand != old.Brand || value.ShelfLifeDays != old.ShelfLifeDays ||
                !value.Barcodes.OrderBy(b => b.Code).SequenceEqual(old.Barcodes.OrderBy(b => b.Code)))
                await WriteCard(db, tx, context, scope, value, i.ExpectedVersion, ct);
            if (old?.UnitPrice != i.SalePrice)
                await WritePrice(db, tx, context, scope, i.ProductId,
                    new WriteProductPrice(old?.PriceVersion ?? 1, i.SalePrice, true, "quick_receiving"), ct);
            if (i.ManufacturedOn > today || i.ExpiryDate < today || i.Product.RequiresExpiry && i.ExpiryDate is null ||
                i.ManufacturedOn is {} made && (i.ExpiryDate is null || i.ExpiryDate < made ||
                    i.Product.ShelfLifeDays is {} days && (made > DateOnly.MaxValue.AddDays(-days) || made.AddDays(days) != i.ExpiryDate)))
                throw new PosError(400, "quick_receiving_dates_invalid");
        }
        var result = new QuickReceivingResult(request.OperationId, store, DateTime.UtcNow, request.Items);
        await db.Run(tx, ct, """
            INSERT INTO quick_receivings (org_id,store_id,operation_id,body,result,created_by,posted_at)
            VALUES ($1,$2,$3,$4::jsonb,$5::jsonb,$6,$7)
            """, scope.Organization, store, request.OperationId, body, JsonSerializer.Serialize(result), scope.Staff, result.PostedAt);
        foreach (var i in request.Items.OrderBy(i => i.ProductId))
        {
            decimal? total = i.UnitCost is {} cost ? decimal.Round(cost * i.Quantity, 2, MidpointRounding.AwayFromZero) : null;
            var inserted = await db.Run(tx, ct, """
                INSERT INTO stock_lots (org_id,store_id,product_id,id,expiry_date,quantity,inventory_value)
                VALUES ($1,$2,$3,$4,NULLIF($5,'')::date,$6,$7) ON CONFLICT DO NOTHING
                """, scope.Organization, store, i.ProductId, i.LotId,
                i.ExpiryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "", i.Quantity, total);
            if (inserted != 1) throw new PosError(409, "receipt_lot_conflict");
            await db.Run(tx, ct, """
                INSERT INTO quick_receiving_lines
                    (org_id,store_id,operation_id,product_id,lot_id,quantity,unit_cost,total_cost,manufactured_on)
                VALUES ($1,$2,$3,$4,$5,$6,$7,$8,NULLIF($9,'')::date)
                """, scope.Organization, store, request.OperationId, i.ProductId, i.LotId, i.Quantity, i.UnitCost, total,
                i.ManufacturedOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "");
        }
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff,
            "receiving.quick_posted", request.OperationId, ct);
        await tx.CommitAsync(ct);
        return result;
    }
}
