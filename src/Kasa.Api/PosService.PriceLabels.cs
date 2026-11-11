using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<WeighedPriceLabel> IssuePriceLabel(HttpContext context, Guid store, Guid product, Guid lot,
        IssueWeighedPriceLabel request, CancellationToken ct)
    {
        if (request.LabelId == Guid.Empty || !IsQuantity(request.Weight))
            throw new PosError(400, "invalid_weighed_price_label");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockProduct(db, tx, scope.Organization, product, true, ct);
        var label = new WeighedPriceLabel(request.LabelId, product, lot, request.Weight, 0);
        await using var command = db.Sql(tx, """
            SELECT coalesce(markdown.unit_price, sp.unit_price)
            FROM stock_lots l
            JOIN products p ON (p.org_id, p.id) = (l.org_id, l.product_id) AND p.unit = 'kg'
            JOIN store_prices sp ON (sp.org_id, sp.store_id, sp.product_id) = (l.org_id, l.store_id, l.product_id)
                AND sp.active
            LEFT JOIN active_lot_markdowns markdown
                ON (markdown.org_id, markdown.store_id, markdown.product_id, markdown.lot_id) =
                    (l.org_id, l.store_id, l.product_id, l.id)
            WHERE (l.org_id, l.store_id, l.product_id, l.id) = ($1, $2, $3, $4)
                AND NOT l.quarantined AND l.quantity >= $5
                AND (NOT p.requires_expiry OR l.expiry_date IS NOT NULL)
                AND (l.expiry_date IS NULL OR l.expiry_date >= (statement_timestamp() AT TIME ZONE $6)::date)
            """, scope.Organization, store, product, lot, request.Weight, scope.TimeZone);
        var existing = await ReadPriceLabel(db, tx, scope.Organization, store, label, ct);
        if (existing is not null)
            return existing;
        if (await command.ExecuteScalarAsync(ct) is not decimal price)
            throw new PosError(409, "weighed_price_label_unavailable");

        label = label with { PrintedTotal = GetCheckoutLineTotal("kg", price, request.Weight) };
        var added = await db.Run(tx, ct, """
            INSERT INTO weighed_price_labels (org_id, store_id, product_id, lot_id, id, weight, printed_total)
            VALUES ($1, $2, $3, $4, $5, $6, $7) ON CONFLICT DO NOTHING
            """, scope.Organization, store, product, lot, label.LabelId, label.Weight, label.PrintedTotal);
        if (added == 0)
            return await ReadPriceLabel(db, tx, scope.Organization, store, label, ct)
                ?? throw new PosError(409, "weighed_price_label_id_reused");
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff,
            "stock.weighed_price_label_issued", label.LabelId, ct, lot.ToString("D"));
        await tx.CommitAsync(ct);
        return label;
    }

    private static async Task<WeighedPriceLabel?> ReadPriceLabel(NpgsqlConnection db, NpgsqlTransaction tx,
        Guid organization, Guid store, WeighedPriceLabel label, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT product_id, lot_id, weight, printed_total FROM weighed_price_labels
            WHERE (org_id, store_id, id) = ($1, $2, $3)
            """, organization, store, label.LabelId);
        await using var row = await command.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct))
            return null;
        if (row.GetGuid(0) != label.ProductId || row.GetGuid(1) != label.LotId || row.GetDecimal(2) != label.Weight)
            throw new PosError(409, "weighed_price_label_id_reused");
        return label with { PrintedTotal = row.GetDecimal(3) };
    }

    private static void ValidateCheckoutPriceLabel(SaleItem item, string unit, decimal lineTotal, decimal? weight,
        decimal? printedTotal, bool sold)
    {
        if (item.PriceLabelId is null)
            return;
        if (unit != "kg" || weight != item.Quantity || printedTotal is null || sold)
            throw new PosError(409, "weighed_price_label_unavailable");
        if (printedTotal != lineTotal)
            throw new PosError(409, "weighed_price_label_changed");
    }
}
