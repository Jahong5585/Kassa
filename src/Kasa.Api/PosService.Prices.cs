using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    private const decimal MaxUnitPrice = 999_999_999_999.99m;

    public async Task<ProductPrice> SaveStoreProductPrice(HttpContext context, Guid store, Guid product,
        WriteProductPrice request, CancellationToken ct)
    {
        ValidateProductPrice(request);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        var result = await WritePrice(db, tx, context, scope, product, request, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<IReadOnlyList<ProductPrice>> ListProductPrices(HttpContext context, Guid store, Guid product,
        int? offset, CancellationToken ct)
    {
        ValidateStockOffset(offset);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        return await ReadPriceHistory(db, tx, scope, product, 25, offset ?? 0, ct);
    }

    private static void ValidateProductPrice(WriteProductPrice request)
    {
        if (request.ExpectedVersion is < 1 or int.MaxValue ||
            request.UnitPrice is { } amount && (!IsMoney(amount) || amount is <= 0 or > MaxUnitPrice) ||
            !IsText(request.Reason, 500))
            throw new PosError(400, "invalid_product_price");
    }

    private static async Task<ProductPrice> WritePrice(NpgsqlConnection db, NpgsqlTransaction tx, HttpContext context,
        StoreScope scope, Guid product, WriteProductPrice request, CancellationToken ct)
    {
        await LockProduct(db, tx, scope.Organization, product, true, ct);
        var current = (await ReadPriceHistory(db, tx, scope, product, 1, 0, ct)).FirstOrDefault()
            ?? throw new PosError(404, "product_not_found");
        var reason = request.Reason.Trim();
        if (current.Version == request.ExpectedVersion + 1 && current.UnitPrice == request.UnitPrice &&
            current.Active == request.Active && current.Reason == reason)
            return current;
        if (current.Version != request.ExpectedVersion)
            throw new PosError(409, "product_price_version_conflict");

        await db.Run(tx, ct, """
            UPDATE store_prices SET unit_price = $4, active = $5, version = version + 1
            WHERE (org_id, store_id, product_id) = ($1, $2, $3)
            """, scope.Organization, scope.Store, product, request.UnitPrice, request.Active);
        var saved = await InsertPriceHistory(db, tx, scope, product, reason, ct);
        await Audit(db, tx, context, scope.Organization, scope.Store, Guid.Empty, scope.Staff,
            "product.price_changed", product, ct);
        return saved;
    }

    private static async Task<ProductPrice> InsertPriceHistory(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope,
        Guid product, string reason, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            INSERT INTO store_price_history (org_id, store_id, product_id, version, unit_price, active, reason, changed_by)
            SELECT org_id, store_id, product_id, version, unit_price, active, $4, $5 FROM store_prices
            WHERE (org_id, store_id, product_id) = ($1, $2, $3)
            RETURNING version, unit_price, active, reason, changed_by, recorded_at
            """, scope.Organization, scope.Store, product, reason, scope.Staff);
        await using var rows = await command.ExecuteReaderAsync(ct);
        return await rows.ReadAsync(ct) ? ReadPriceRow(rows) : throw new InvalidOperationException("Price version missing.");
    }

    private static async Task<IReadOnlyList<ProductPrice>> ReadPriceHistory(NpgsqlConnection db, NpgsqlTransaction tx,
        StoreScope scope, Guid product, int limit, int offset, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT version, unit_price, active, reason, changed_by, recorded_at
            FROM store_price_history WHERE (org_id, store_id, product_id) = ($1, $2, $3)
            ORDER BY version DESC LIMIT $4 OFFSET $5
            """, scope.Organization, scope.Store, product, limit, offset);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<ProductPrice> result = [];
        while (await rows.ReadAsync(ct))
            result.Add(ReadPriceRow(rows));
        return result;
    }

    private static ProductPrice ReadPriceRow(NpgsqlDataReader rows) =>
        new(rows.GetInt32(0), rows.DecimalOrNull(1), rows.GetBoolean(2), rows.GetString(3),
            rows.IsDBNull(4) ? null : rows.GetGuid(4), rows.GetDateTime(5));
}
