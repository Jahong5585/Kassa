using Npgsql;

namespace Kasa.Api;

public sealed record SupplierProductPriceOption(string SupplierSku, int PriceVersion, Guid ProductId,
    string ProductName, string Unit, string PackageUnit, decimal UnitsPerPackage, decimal PackagePrice,
    DateOnly ValidFrom, DateOnly? ValidThrough, string Category, string Brand, int? ShelfLifeDays);

public sealed record SupplierProductPriceTemplate(Guid ProductId, string ProductName, string Unit,
    bool RequiresExpiry, string Category, string Brand, int? ShelfLifeDays, string SupplierCode,
    IReadOnlyList<SupplierProductPriceOption> Options);

internal sealed partial class PosService
{
    public async Task<SupplierProductPriceTemplate> GetSupplierProductPriceTemplate(HttpContext context, Guid store,
        string code, Guid productId, CancellationToken ct)
    {
        code = SupplierCode(code);
        if (productId == Guid.Empty) throw new PosError(400, "invalid_product_id");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, false, ct);
        if (await ReadSupplier(db, tx, scope, code, ct) is null)
            throw new PosError(404, "supplier_not_found");

        await using var product = db.Sql(tx, """
            SELECT p.name, p.unit, p.requires_expiry, coalesce(p.category, ''), coalesce(p.brand, ''),
                   p.shelf_life_days
            FROM products p JOIN store_prices sp ON (sp.org_id, sp.product_id) = (p.org_id, p.id)
            WHERE (p.org_id, sp.store_id, p.id) = ($1, $2, $3)
            """, scope.Organization, scope.Store, productId);
        await using var rows = await product.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct)) throw new PosError(404, "product_not_found");
        var name = rows.GetString(0);
        var unit = rows.GetString(1);
        var requiresExpiry = rows.GetBoolean(2);
        var category = rows.GetString(3);
        var brand = rows.GetString(4);
        int? shelfLife = rows.IsDBNull(5) ? null : rows.GetInt32(5);
        await rows.DisposeAsync();
        var options = await ReadSupplierProductPriceOptions(db, tx, scope, code, productId, 0, ct);
        return new SupplierProductPriceTemplate(productId, name, unit, requiresExpiry, category, brand, shelfLife,
            code, options);
    }

    public async Task<IReadOnlyList<SupplierProductPriceOption>> ListSupplierProductPriceOptions(HttpContext context,
        Guid store, string code, Guid? productId, int? offset, CancellationToken ct)
    {
        code = SupplierCode(code);
        ValidateStockOffset(offset);
        if (productId == Guid.Empty) throw new PosError(400, "invalid_product_id");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, false, ct);
        if (await ReadSupplier(db, tx, scope, code, ct) is null)
            throw new PosError(404, "supplier_not_found");
        return await ReadSupplierProductPriceOptions(db, tx, scope, code, productId, offset ?? 0, ct);
    }

    private static async Task<IReadOnlyList<SupplierProductPriceOption>> ReadSupplierProductPriceOptions(
        NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope, string code, Guid? productId, int offset,
        CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            WITH effective AS (
                SELECT DISTINCT ON (price.supplier_sku)
                       price.supplier_sku, price.version, price.product_id, price.package_unit,
                       price.units_per_package, price.package_price, price.valid_from, price.valid_through
                FROM supplier_prices price
                WHERE (price.org_id, price.store_id, price.supplier_code) = ($1, $2, $3)
                  AND ($4::uuid IS NULL OR price.product_id = $4)
                  AND price.valid_from <= (statement_timestamp() AT TIME ZONE $5)::date
                  AND (price.valid_through IS NULL OR
                       price.valid_through >= (statement_timestamp() AT TIME ZONE $5)::date)
                ORDER BY price.supplier_sku, price.valid_from DESC, price.version DESC
            )
            SELECT effective.supplier_sku, effective.version, effective.product_id, p.name, p.unit,
                   effective.package_unit, effective.units_per_package, effective.package_price,
                   effective.valid_from, effective.valid_through, coalesce(p.category, ''),
                   coalesce(p.brand, ''), p.shelf_life_days
            FROM effective JOIN products p ON (p.org_id, p.id) = ($1, effective.product_id)
            JOIN store_prices sp ON (sp.org_id, sp.store_id, sp.product_id) = ($1, $2, effective.product_id)
            ORDER BY p.name COLLATE "C", effective.supplier_sku COLLATE "C"
            LIMIT 25 OFFSET $6
            """, scope.Organization, scope.Store, code, productId, scope.TimeZone, offset);
        await using var rows = await command.ExecuteReaderAsync(ct);
        var result = new List<SupplierProductPriceOption>();
        while (await rows.ReadAsync(ct))
            result.Add(new SupplierProductPriceOption(rows.GetString(0), rows.GetInt32(1), rows.GetGuid(2),
                rows.GetString(3), rows.GetString(4), rows.GetString(5), rows.GetDecimal(6), rows.GetDecimal(7),
                rows.GetFieldValue<DateOnly>(8), ReadDate(rows, 9), rows.GetString(10), rows.GetString(11),
                rows.IsDBNull(12) ? null : rows.GetInt32(12)));
        return result;
    }
}
