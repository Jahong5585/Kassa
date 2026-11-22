using System.Text.Json;
using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<IReadOnlyList<ProductSearchResult>> ListReceivingProducts(HttpContext context, Guid store,
        string? query, int? offset, CancellationToken ct)
    {
        if (query is not null && !IsText(query, 100))
            throw new PosError(400, "invalid_catalog_search");
        ValidateStockOffset(offset);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, false, ct);
        return await SearchProducts(db, tx, scope.Organization, store, query?.Trim() ?? "", false,
            offset ?? 0, ct);
    }

    public async Task<ReceivingProduct> GetReceivingProductById(HttpContext context, Guid store, Guid id,
        CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, false, ct);
        var card = await ReadProductCard(db, tx, scope, id, ct)
            ?? throw new PosError(404, "product_not_found");
        return ReceivingCard(card);
    }

    public async Task<SharedReceivingProduct> GetSharedReceivingProduct(HttpContext context, Guid store,
        string barcode, CancellationToken ct)
    {
        if (!IsBarcode(barcode)) throw new PosError(400, "invalid_barcode");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, false, ct);
        // Public product attributes only. Never expose source store, IDs, prices, stock or suppliers.
        // Test organisations can consult real metadata; their own data stays within that organisation.
        await using var command = db.Sql(tx, """
            SELECT p.name, p.unit, p.requires_expiry, p.category, p.brand, p.shelf_life_days
            FROM product_barcodes b JOIN products p ON (p.org_id, p.id) = (b.org_id, b.product_id)
            JOIN organizations o ON o.id = p.org_id
            WHERE b.code = $1 AND (NOT o.is_test OR p.org_id = $2)
              AND EXISTS (SELECT 1 FROM store_prices sp JOIN stores s
                ON (s.org_id, s.id) = (sp.org_id, sp.store_id)
                WHERE (sp.org_id, sp.product_id) = (p.org_id, p.id) AND sp.active
                  AND (NOT s.is_test OR p.org_id = $2))
            ORDER BY (p.org_id = $2) DESC, p.revision DESC, p.org_id, p.id LIMIT 1
            """, barcode, scope.Organization);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct)) throw new PosError(404, "shared_product_not_found");
        return new SharedReceivingProduct(barcode, rows.GetString(0), rows.GetString(1), rows.GetBoolean(2),
            rows.GetString(3), rows.GetString(4), rows.IsDBNull(5) ? null : rows.GetInt32(5));
    }

    public async Task<ReceivingProduct> CreateReceivingProduct(HttpContext context, Guid store, Guid requestId,
        ReceivingProductInput input, decimal? price, CancellationToken ct)
    {
        if (input is null || input.Barcodes is not { Length: <= 30 } || requestId == Guid.Empty)
            throw new PosError(400, "invalid_product_card");
        var card = ValidateProductCard(requestId, new WriteProductCard(0, input.Name, input.Unit,
            input.RequiresExpiry, null, input.Category, input.Brand, null, null, input.Barcodes)
            { ShelfLifeDays = input.ShelfLifeDays });
        if (price is { } amount && (!IsMoney(amount) || amount <= 0 || amount > MaxUnitPrice))
            throw new PosError(400, "invalid_product_price");
        var body = JsonSerializer.Serialize(new { card.Name, card.Unit, card.RequiresExpiry, card.Category,
            card.Brand, card.ShelfLifeDays, card.Barcodes, Price = price });

        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, false, ct);
        await LockOperation(db, tx, $"receiving_product:{scope.Organization}:{store}:{requestId}", ct);
        await using (var replay = db.Sql(tx, """
            SELECT body = $4::jsonb, result FROM receiving_product_creations
            WHERE (org_id, store_id, request_id) = ($1, $2, $3)
            """, scope.Organization, store, requestId, body))
        await using (var rows = await replay.ExecuteReaderAsync(ct))
        {
            if (await rows.ReadAsync(ct))
            {
                if (!rows.GetBoolean(0)) throw new PosError(409, "product_id_reused");
                return JsonSerializer.Deserialize<ReceivingProduct>(rows.GetString(1))
                    ?? throw new InvalidOperationException("Missing receiving product result.");
            }
        }
        await LockInventoryMembership(db, tx, scope.Organization, ct, shared: true);
        foreach (var code in card.Barcodes.Select(b => b.Code).Order(StringComparer.Ordinal))
            await LockOperation(db, tx, $"receiving_barcode:{scope.Organization}:{code}", ct);

        // If this organisation already has a card in a different store, attach that immutable card.
        Guid? existing = null;
        await using (var codes = db.Sql(tx, """
            SELECT DISTINCT product_id FROM product_barcodes WHERE org_id = $1 AND code = ANY($2)
            """, scope.Organization, card.Barcodes.Select(b => b.Code).ToArray()))
        await using (var rows = await codes.ExecuteReaderAsync(ct))
            while (await rows.ReadAsync(ct))
            {
                if (existing is not null && existing != rows.GetGuid(0))
                    throw new PosError(409, "product_barcode_in_use");
                existing = rows.GetGuid(0);
            }
        var productId = existing ?? requestId;
        await LockProduct(db, tx, scope.Organization, productId, true, ct);
        if (await ReadProductCard(db, tx, scope, productId, ct) is not null)
            throw new PosError(409, price is null ? "product_barcode_in_use" : "initial_sale_price_conflict");
        if (existing is not null)
        {
            await db.Run(tx, ct, """
                INSERT INTO store_prices (org_id, store_id, product_id, unit_price, version)
                VALUES ($1, $2, $3, NULL, 1)
                """, scope.Organization, store, productId);
            await InsertPriceHistory(db, tx, scope, productId, "product_created", ct);
            card = await ReadProductCard(db, tx, scope, productId, ct)
                ?? throw new InvalidOperationException("Attached product missing.");
            if (input.Barcodes.Any(requested => !card.Barcodes.Any(b => b.Code == requested.Code)))
                throw new PosError(409, "product_barcode_in_use");
        }
        else card = await WriteCard(db, tx, context, scope, card, 0, ct);
        if (price is not null)
            await WritePrice(db, tx, context, scope, productId, new WriteProductPrice(1, price, true,
                "receiving_initial_price"), ct);
        var scanned = input.Barcodes.Length == 0 ? null : card.Barcodes.First(b => b.Code == input.Barcodes[0].Code);
        var result = ReceivingCard(card) with
            { Barcode = scanned?.Code ?? "", QuantityFactor = scanned?.QuantityFactor ?? 1, InitialSalePrice = price };
        await db.Run(tx, ct, """
            INSERT INTO receiving_product_creations (org_id, store_id, request_id, body, result, created_by)
            VALUES ($1, $2, $3, $4::jsonb, $5::jsonb, $6)
            """, scope.Organization, store, requestId, body, JsonSerializer.Serialize(result), scope.Staff);
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff,
            "receiving.product_created", productId, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    private static ReceivingProduct ReceivingCard(ProductCard card) => new(card.ProductId, card.Name,
        card.Barcodes.FirstOrDefault()?.Code ?? "", card.Unit, card.RequiresExpiry)
        { Category = card.Category, Brand = card.Brand, ShelfLifeDays = card.ShelfLifeDays,
            QuantityFactor = card.Barcodes.FirstOrDefault()?.QuantityFactor ?? 1 };

    private static async Task ValidateReceivingMetadata(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope,
        ReceiptItem[] items, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow,
            TimeZoneInfo.FindSystemTimeZoneById(scope.TimeZone)));
        foreach (var item in items.Where(i => i.ManufacturedOn is not null))
        {
            var made = item.ManufacturedOn.GetValueOrDefault();
            var card = await ReadProductCard(db, tx, scope, item.ProductId, ct)
                ?? throw new PosError(404, "product_not_found");
            if (made > today || card.ShelfLifeDays is not { } days ||
                made > DateOnly.MaxValue.AddDays(-days) || made.AddDays(days) != item.ExpiryDate)
                throw new PosError(400, "invalid_manufacture_date");
        }
    }
}
