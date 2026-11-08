using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<SupplierPrice> SaveSupplierPrice(HttpContext context, Guid store, string code, string sku,
        WriteSupplierPrice request, CancellationToken ct)
    {
        code = SupplierCode(code);
        sku = SupplierCode(sku);
        if (request.ExpectedVersion is < 0 or int.MaxValue || request.ProductId == Guid.Empty ||
            !IsText(request.PackageUnit, 60) || !IsQuantity(request.UnitsPerPackage) ||
            !IsMoney(request.PackagePrice) || request.PackagePrice is <= 0 or > MaxUnitPrice ||
            !IsBusinessDate(request.ValidFrom) || request.ValidThrough is { } through &&
            (!IsBusinessDate(through) || through < request.ValidFrom))
            throw new PosError(400, "invalid_supplier_price");

        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await RequireActiveSupplier(db, tx, scope, code, ct);
        var current = (await ReadSupplierPriceHistory(db, tx, scope, code, sku, 1, 0, ct)).FirstOrDefault();
        var packageUnit = request.PackageUnit.Trim();
        if (current is not null && current.Version == request.ExpectedVersion + 1 && current.ProductId == request.ProductId &&
            current.PackageUnit == packageUnit && current.UnitsPerPackage == request.UnitsPerPackage &&
            current.PackagePrice == request.PackagePrice && current.ValidFrom == request.ValidFrom &&
            current.ValidThrough == request.ValidThrough)
            return current;
        if ((current?.Version ?? 0) != request.ExpectedVersion)
            throw new PosError(409, "supplier_price_version_conflict");
        if (current is not null && current.ProductId != request.ProductId)
            throw new PosError(409, "supplier_price_product_immutable");
        await ValidateReceivingProducts(db, tx, scope,
            [(request.ProductId, request.UnitsPerPackage, null)], posting: false, ct);

        await using var command = db.Sql(tx, """
            INSERT INTO supplier_prices (org_id, store_id, supplier_code, supplier_sku, version, product_id,
                package_unit, units_per_package, package_price, valid_from, valid_through, created_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, CASE WHEN $13 THEN $11 END, $12)
            RETURNING supplier_sku, version, product_id, package_unit, units_per_package, package_price,
                valid_from, valid_through, created_by, created_at
            """, scope.Organization, store, code, sku, request.ExpectedVersion + 1, request.ProductId, packageUnit,
            request.UnitsPerPackage, request.PackagePrice, request.ValidFrom, request.ValidThrough ?? request.ValidFrom,
            scope.Staff, request.ValidThrough.HasValue);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct))
            throw new InvalidOperationException("Saved supplier price missing.");
        var saved = ReadSupplierPriceRow(rows);
        await rows.CloseAsync();
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff, "supplier.price_updated",
            request.ProductId, ct, code + ":" + sku);
        await tx.CommitAsync(ct);
        return saved;
    }

    public async Task<IReadOnlyList<SupplierPrice>> ListSupplierPrices(HttpContext context, Guid store, string code,
        DateOnly? asOf, int? offset, CancellationToken ct)
    {
        code = SupplierCode(code);
        ValidateStockOffset(offset);
        if (asOf is { } date && !IsBusinessDate(date))
            throw new PosError(400, "invalid_supplier_price_date");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        if (await ReadSupplier(db, tx, scope, code, ct) is null)
            throw new PosError(404, "supplier_not_found");
        var day = asOf ?? await StoreDate(db, tx, scope, ct);
        return await ReadEffectivePrices(db, tx, scope, code, day, "", offset ?? 0, ct);
    }

    public async Task<IReadOnlyList<SupplierPrice>> GetSupplierPriceHistory(HttpContext context, Guid store, string code,
        string sku, int? offset, CancellationToken ct)
    {
        code = SupplierCode(code);
        sku = SupplierCode(sku);
        ValidateStockOffset(offset);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        return await ReadSupplierPriceHistory(db, tx, scope, code, sku, 25, offset ?? 0, ct);
    }

    private static async Task<IReadOnlyList<SupplierPrice>> ReadEffectivePrices(NpgsqlConnection db,
        NpgsqlTransaction tx, StoreScope scope, string code, DateOnly date, string sku, int offset, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT DISTINCT ON (supplier_sku) supplier_sku, version, product_id, package_unit, units_per_package,
                package_price, valid_from, valid_through, created_by, created_at
            FROM supplier_prices WHERE (org_id, store_id, supplier_code) = ($1, $2, $3)
                AND valid_from <= $4 AND (valid_through IS NULL OR valid_through >= $4)
                AND ($5 = '' OR supplier_sku = $5)
            ORDER BY supplier_sku, valid_from DESC, version DESC LIMIT 25 OFFSET $6
            """, scope.Organization, scope.Store, code, date, sku, offset);
        return await ReadSupplierPriceRows(command, ct);
    }

    private static async Task<IReadOnlyList<SupplierPrice>> ReadSupplierPriceHistory(NpgsqlConnection db,
        NpgsqlTransaction tx, StoreScope scope, string code, string sku, int limit, int offset, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT supplier_sku, version, product_id, package_unit, units_per_package, package_price,
                valid_from, valid_through, created_by, created_at
            FROM supplier_prices WHERE (org_id, store_id, supplier_code, supplier_sku) = ($1, $2, $3, $4)
            ORDER BY version DESC LIMIT $5 OFFSET $6
            """, scope.Organization, scope.Store, code, sku, limit, offset);
        return await ReadSupplierPriceRows(command, ct);
    }

    private static async Task<IReadOnlyList<SupplierPrice>> ReadSupplierPriceRows(NpgsqlCommand command, CancellationToken ct)
    {
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<SupplierPrice> result = [];
        while (await rows.ReadAsync(ct))
            result.Add(ReadSupplierPriceRow(rows));
        return result;
    }

    private static SupplierPrice ReadSupplierPriceRow(NpgsqlDataReader rows) =>
        new(rows.GetString(0), rows.GetInt32(1), rows.GetGuid(2), rows.GetString(3), rows.GetDecimal(4), rows.GetDecimal(5),
            rows.GetFieldValue<DateOnly>(6), ReadDate(rows, 7), rows.GetGuid(8), rows.GetDateTime(9));

    private static async Task RequireActiveSupplier(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope,
        string code, CancellationToken ct)
    {
        await LockOperation(db, tx, $"supplier:{scope.Organization}:{scope.Store}:{code}", ct);
        var supplier = await ReadSupplier(db, tx, scope, code, ct) ?? throw new PosError(404, "supplier_not_found");
        if (!supplier.Active)
            throw new PosError(409, "supplier_inactive");
    }

    private static async Task<DateOnly> StoreDate(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope,
        CancellationToken ct, DateTime? at = null)
    {
        await using var command = at is { } timestamp
            ? db.Sql(tx, "SELECT ($2 AT TIME ZONE $1)::date", scope.TimeZone, timestamp)
            : db.Sql(tx, "SELECT (statement_timestamp() AT TIME ZONE $1)::date", scope.TimeZone);
        await using var rows = await command.ExecuteReaderAsync(ct);
        await rows.ReadAsync(ct);
        return rows.GetFieldValue<DateOnly>(0);
    }

    private static bool IsBusinessDate(DateOnly date) => date > DateOnly.MinValue && date < DateOnly.MaxValue;
}
