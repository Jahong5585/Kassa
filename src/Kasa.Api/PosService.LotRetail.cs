using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<LotRetailSettings> GetLotRetailSettings(HttpContext context, Guid store, Guid product, Guid lot,
        CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        return (await ReadLotRetailSettings(db, tx, scope, product, lot, ct)).Settings;
    }

    public async Task<LotRetailSettings> SaveLotRetailSettings(HttpContext context, Guid store, Guid product, Guid lot,
        ChangeLotRetailSettings request, CancellationToken ct)
    {
        if (request.ExpectedVersion is < 0 or int.MaxValue || !IsText(request.Reason, 500) ||
            request.UnitPrice is { } amount && (!IsMoney(amount) || amount is <= 0 or > MaxUnitPrice))
            throw new PosError(400, "invalid_lot_retail_settings");
        var reason = request.Reason.Trim();
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockProduct(db, tx, scope.Organization, product, true, ct);
        var (current, labelId, oldReason, eligible) = await ReadLotRetailSettings(db, tx, scope, product, lot, ct);
        if (current.Version == request.ExpectedVersion + 1 && current.UnitPrice == request.UnitPrice && oldReason == reason)
            return current;
        if (current.Version != request.ExpectedVersion)
            throw new PosError(409, "lot_retail_version_conflict");
        if (request.UnitPrice is { } price && (!eligible || current.BaseUnitPrice is null || price >= current.BaseUnitPrice))
            throw new PosError(409, "lot_markdown_unavailable");

        var label = labelId ?? Guid.NewGuid();
        var next = current with { LabelCode = $"LOT-{label:N}", Version = current.Version + 1, UnitPrice = request.UnitPrice };
        await db.Run(tx, ct, """
            INSERT INTO lot_retail_settings (org_id, store_id, product_id, lot_id, label_id, version, unit_price, reason)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            ON CONFLICT (org_id, store_id, product_id, lot_id) DO UPDATE
            SET version = EXCLUDED.version, unit_price = EXCLUDED.unit_price, reason = EXCLUDED.reason
            """, scope.Organization, store, product, lot, label, next.Version, next.UnitPrice, reason);
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff,
            "stock.lot_retail_changed", lot, ct, product.ToString("D"));
        await tx.CommitAsync(ct);
        return next;
    }

    private static async Task<(LotRetailSettings Settings, Guid? Label, string? Reason, bool Eligible)> ReadLotRetailSettings(
        NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope, Guid product, Guid lot, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT settings.label_id, coalesce(settings.version, 0), settings.unit_price, sp.unit_price, settings.reason,
                   coalesce(sp.active, false) AND l.quantity > 0 AND NOT l.quarantined
                       AND (NOT p.requires_expiry OR l.expiry_date IS NOT NULL)
                       AND (l.expiry_date IS NULL OR l.expiry_date >= (statement_timestamp() AT TIME ZONE $5)::date)
            FROM stock_lots l
            JOIN products p ON (p.org_id, p.id) = (l.org_id, l.product_id)
            LEFT JOIN store_prices sp ON (sp.org_id, sp.store_id, sp.product_id) = (l.org_id, l.store_id, l.product_id)
            LEFT JOIN lot_retail_settings settings ON (settings.org_id, settings.store_id, settings.product_id, settings.lot_id) =
                                                    (l.org_id, l.store_id, l.product_id, l.id)
            WHERE (l.org_id, l.store_id, l.product_id, l.id) = ($1, $2, $3, $4)
            """, scope.Organization, scope.Store, product, lot, scope.TimeZone);
        await using var row = await command.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct))
            throw new PosError(404, "lot_not_found");
        Guid? label = row.IsDBNull(0) ? null : row.GetGuid(0);
        return (new LotRetailSettings(product, lot, label is { } id ? $"LOT-{id:N}" : null,
            row.GetInt32(1), row.DecimalOrNull(2), row.DecimalOrNull(3)), label,
            row.IsDBNull(4) ? null : row.GetString(4), row.GetBoolean(5));
    }
}
