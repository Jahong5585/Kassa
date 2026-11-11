using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<QuantityPromotion> GetQuantityPromotion(HttpContext context, Guid store, Guid product,
        CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        return (await ReadQuantityPromotion(db, tx, scope, product, ct)).Promotion;
    }

    public async Task<QuantityPromotion> SaveQuantityPromotion(HttpContext context, Guid store, Guid product,
        ChangeQuantityPromotion request, CancellationToken ct)
    {
        if (request.ExpectedVersion is < 0 or int.MaxValue || !IsText(request.Reason, 500))
            throw new PosError(400, "invalid_quantity_promotion");
        var reason = request.Reason.Trim();
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockProduct(db, tx, scope.Organization, product, true, ct);
        var current = await ReadQuantityPromotion(db, tx, scope, product, ct);
        if (current.Promotion.Version == request.ExpectedVersion + 1 &&
            current.Promotion.Enabled == request.Enabled && current.Reason == reason)
            return current.Promotion;
        if (current.Promotion.Version != request.ExpectedVersion)
            throw new PosError(409, "quantity_promotion_version_conflict");
        if (request.Enabled && !current.Eligible)
            throw new PosError(409, "quantity_promotion_unavailable");
        var next = new QuantityPromotion(request.ExpectedVersion + 1, request.Enabled);
        await db.Run(tx, ct, """
            INSERT INTO store_quantity_promotions (org_id, store_id, product_id, version, enabled, reason)
            VALUES ($1, $2, $3, $4, $5, $6)
            ON CONFLICT (org_id, store_id, product_id) DO UPDATE
            SET version = EXCLUDED.version, enabled = EXCLUDED.enabled, reason = EXCLUDED.reason
            """, scope.Organization, store, product, next.Version, next.Enabled, reason);
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff,
            next.Enabled ? "product.quantity_promotion_enabled" : "product.quantity_promotion_disabled", product, ct);
        await tx.CommitAsync(ct);
        return next;
    }

    private static async Task<(QuantityPromotion Promotion, string? Reason, bool Eligible)> ReadQuantityPromotion(
        NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope, Guid product, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT coalesce(promo.version, 0), coalesce(promo.enabled, false), promo.reason,
                   p.unit = 'piece' AND sp.active AND sp.unit_price IS NOT NULL
            FROM store_prices sp
            JOIN products p ON (p.org_id, p.id) = (sp.org_id, sp.product_id)
            LEFT JOIN store_quantity_promotions promo ON (promo.org_id, promo.store_id, promo.product_id) =
                                                        (sp.org_id, sp.store_id, sp.product_id)
            WHERE (sp.org_id, sp.store_id, sp.product_id) = ($1, $2, $3)
            """, scope.Organization, scope.Store, product);
        await using var row = await command.ExecuteReaderAsync(ct);
        if (!await row.ReadAsync(ct))
            throw new PosError(404, "product_not_found");
        return (new QuantityPromotion(row.GetInt32(0), row.GetBoolean(1)),
            row.IsDBNull(2) ? null : row.GetString(2), row.GetBoolean(3));
    }

    public async Task<SaleQuote> QuoteCheckout(HttpContext context, Guid shift, QuoteSale request, CancellationToken ct)
    {
        var items = ValidateBasketItems(request.Items);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireCashier(db, tx, context, false, ct);
        await RequireOpenShift(db, tx, scope, shift, ct);
        await using var command = db.Sql(tx, """
            SELECT p.name, p.unit, coalesce(markdown.unit_price, sp.unit_price), sp.version,
                   CASE WHEN markdown.version IS NULL AND promo.enabled AND p.unit = 'piece' THEN promo.version END,
                   l.quantity, markdown.version, label.weight, label.printed_total, label.sold_sale_id IS NOT NULL
            FROM unnest($3::uuid[], $4::uuid[], $6::uuid[]) WITH ORDINALITY AS item(product_id, lot_id, price_label_id, position)
            LEFT JOIN store_prices sp ON (sp.org_id, sp.store_id, sp.product_id) = ($1, $2, item.product_id) AND sp.active
            LEFT JOIN products p ON (p.org_id, p.id) = (sp.org_id, sp.product_id)
            LEFT JOIN store_quantity_promotions promo ON (promo.org_id, promo.store_id, promo.product_id) =
                                                        (sp.org_id, sp.store_id, sp.product_id)
            LEFT JOIN stock_lots l ON (l.org_id, l.store_id, l.product_id, l.id) =
                (sp.org_id, sp.store_id, sp.product_id, item.lot_id)
                AND NOT l.quarantined AND (NOT p.requires_expiry OR l.expiry_date IS NOT NULL)
                AND (l.expiry_date IS NULL OR l.expiry_date >= (statement_timestamp() AT TIME ZONE $5)::date)
            LEFT JOIN active_lot_markdowns markdown ON (markdown.org_id, markdown.store_id, markdown.product_id, markdown.lot_id) =
                                                       (sp.org_id, sp.store_id, sp.product_id, item.lot_id)
            LEFT JOIN weighed_price_labels label ON (label.org_id, label.store_id, label.product_id, label.lot_id, label.id) =
                                                   (sp.org_id, sp.store_id, sp.product_id, item.lot_id, item.price_label_id)
            ORDER BY item.position
            """, scope.Organization, scope.Store, items.Select(item => item.ProductId).ToArray(),
            items.Select(item => item.LotId).ToArray(), scope.TimeZone,
            items.Select(item => item.PriceLabelId ?? Guid.Empty).ToArray());
        var lines = new List<SaleLine>(items.Length);
        await using var rows = await command.ExecuteReaderAsync(ct);
        foreach (var item in items)
        {
            if (!await rows.ReadAsync(ct) || rows.IsDBNull(0))
                throw new PosError(404, "product_not_found");
            var unit = rows.GetString(1);
            if (rows.IsDBNull(2))
                throw new PosError(409, "sale_price_not_set");
            if (rows.GetInt32(3) != item.PriceVersion)
                throw new PosError(409, "price_changed");
            var lotPriceVersion = rows.IsDBNull(6) ? (int?)null : rows.GetInt32(6);
            if (lotPriceVersion != item.LotPriceVersion)
                throw new PosError(409, "lot_price_changed");
            var promotion = rows.IsDBNull(4) ? (int?)null : rows.GetInt32(4);
            if (promotion != item.PromotionVersion)
                throw new PosError(409, "quantity_promotion_changed");
            if (rows.IsDBNull(5) || rows.GetDecimal(5) < item.Quantity)
                throw new PosError(409, "lot_not_sellable_or_insufficient_stock");
            var price = rows.GetDecimal(2);
            var lineTotal = GetCheckoutLineTotal(unit, price, item.Quantity);
            ValidateCheckoutPriceLabel(item, unit, lineTotal, rows.DecimalOrNull(7), rows.DecimalOrNull(8), rows.GetBoolean(9));
            lines.Add(new SaleLine(item.ProductId, item.LotId, rows.GetString(0), unit, item.Quantity, price, lineTotal)
                { PromotionVersion = promotion, LotPriceVersion = lotPriceVersion });
        }
        var discount = ApplyQuantityPromotions(lines);
        var total = lines.Sum(line => line.LineTotal);
        if (!IsMoney(total) || total == 0)
            throw new PosError(400, "invalid_sale_total");
        return new SaleQuote(total, discount);
    }

    private static decimal GetCheckoutLineTotal(string unit, decimal price, decimal quantity)
    {
        if (unit == "piece" && quantity != decimal.Truncate(quantity))
            throw new PosError(400, "fractional_piece_quantity");
        var total = decimal.Round(price * quantity, 2, MidpointRounding.AwayFromZero);
        if (!IsMoney(total) || total == 0)
            throw new PosError(400, "invalid_line_total");
        return total;
    }

    private static decimal ApplyQuantityPromotions(List<SaleLine> lines)
    {
        decimal applied = 0;
        var eligible = Enumerable.Range(0, lines.Count).Where(index => lines[index].PromotionVersion.HasValue)
            .GroupBy(index => lines[index].ProductId);
        foreach (var group in eligible)
        {
            var free = decimal.Floor(group.Sum(index => lines[index].Quantity) / 3);
            if (free == 0)
                continue;
            var discount = free * lines[group.First()].UnitPrice;
            var gross = group.Sum(index => lines[index].LineTotal);
            AllocateDiscount(lines, group, discount, gross);
            applied += discount;
        }
        return applied;
    }
}
