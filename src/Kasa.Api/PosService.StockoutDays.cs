using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<StockoutDay> SaveStockoutDay(HttpContext context, Guid store, Guid product, DateOnly day,
        WriteStockoutDay request, CancellationToken ct)
    {
        if (request.ExpectedVersion is < 0 or int.MaxValue || !IsBusinessDate(day) || !IsText(request.Reason, 500))
            throw new PosError(400, "invalid_stockout_day");
        var reason = request.Reason.Trim();
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        if (day >= await StoreDate(db, tx, scope, ct))
            throw new PosError(400, "stockout_day_not_complete");

        await LockOperation(db, tx, $"stockout:{scope.Organization}:{store}:{product}:{day.DayNumber}", ct);
        var current = await ReadStockoutDay(db, tx, scope, product, day, ct);
        if (current is not null && current.Version == request.ExpectedVersion + 1 &&
            current.Confirmed == request.Confirmed && current.Reason == reason)
            return current;
        if ((current?.Version ?? 0) != request.ExpectedVersion)
            throw new PosError(409, "stockout_version_conflict");
        await using var command = db.Sql(tx, """
            INSERT INTO confirmed_stockout_days (org_id, store_id, product_id, day, confirmed, reason, version,
                recorded_by, recorded_at)
            SELECT org_id, store_id, product_id, $4, $5, $6, $7, $8, statement_timestamp() FROM store_prices
            WHERE (org_id, store_id, product_id) = ($1, $2, $3)
            ON CONFLICT (org_id, store_id, product_id, day) DO UPDATE SET confirmed = EXCLUDED.confirmed,
                reason = EXCLUDED.reason, version = EXCLUDED.version, recorded_by = EXCLUDED.recorded_by,
                recorded_at = EXCLUDED.recorded_at
            RETURNING recorded_at
            """, scope.Organization, store, product, day, request.Confirmed, reason, request.ExpectedVersion + 1, scope.Staff);
        if (await command.ExecuteScalarAsync(ct) is not DateTime recordedAt)
            throw new PosError(404, "product_not_found");
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff,
            request.Confirmed ? "procurement.stockout_confirmed" : "procurement.stockout_retracted", product, ct,
            day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        await tx.CommitAsync(ct);
        return new StockoutDay(product, day, request.Confirmed, reason, request.ExpectedVersion + 1, scope.Staff, recordedAt);
    }

    public async Task<StockoutDay> GetStockoutDay(HttpContext context, Guid store, Guid product, DateOnly day,
        CancellationToken ct)
    {
        if (!IsBusinessDate(day))
            throw new PosError(400, "invalid_stockout_day");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        return await ReadStockoutDay(db, tx, scope, product, day, ct) ?? throw new PosError(404, "stockout_day_not_found");
    }

    private static async Task<StockoutDay?> ReadStockoutDay(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope,
        Guid product, DateOnly day, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT confirmed, reason, version, recorded_by, recorded_at FROM confirmed_stockout_days
            WHERE (org_id, store_id, product_id, day) = ($1, $2, $3, $4)
            """, scope.Organization, scope.Store, product, day);
        await using var rows = await command.ExecuteReaderAsync(ct);
        return await rows.ReadAsync(ct)
            ? new StockoutDay(product, day, rows.GetBoolean(0), rows.GetString(1), rows.GetInt32(2), rows.GetGuid(3),
                rows.GetDateTime(4))
            : null;
    }
}
