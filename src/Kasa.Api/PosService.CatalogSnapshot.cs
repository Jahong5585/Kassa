using Npgsql;

namespace Kasa.Api;

public sealed record CatalogSnapshotItem(CatalogProduct Product, IReadOnlyList<ProductBarcode> Barcodes);
public sealed record CatalogSnapshotPage(Guid StoreId, DateTimeOffset GeneratedAt,
    IReadOnlyList<CatalogSnapshotItem> Items, Guid? NextAfter)
{
    public long ChangeCursor { get; init; }
}

internal sealed partial class PosService
{
    // Keyset pagination is scoped by the authenticated register, never by a
    // client-supplied store. Each page has a consistent price/barcode/lot view.
    public async Task<CatalogSnapshotPage> GetCashierCatalogSnapshot(HttpContext context, Guid? after,
        CancellationToken ct)
    {
        if (after == Guid.Empty) throw new PosError(400, "invalid_catalog_cursor");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var scope = await Access.RequireCashier(db, tx, context, false, ct);
        long changeCursor;
        await using (var cursorCommand = db.Sql(tx, """
            SELECT coalesce(max(cursor_id), 0) FROM catalog_change_feed
            WHERE (org_id, store_id) = ($1, $2)
            """, scope.Organization, scope.Store))
            changeCursor = Convert.ToInt64(await cursorCommand.ExecuteScalarAsync(ct));
        const int pageSize = 50;
        var cards = new List<CatalogProduct>();
        var lots = new Dictionary<Guid, List<CatalogLot>>();
        var codes = new Dictionary<Guid, List<ProductBarcode>>();
        bool more;
        await using (var command = db.Sql(tx, """
            SELECT p.id, p.name, coalesce(p.barcode, ''), p.unit, sp.unit_price, sp.version,
                   p.requires_expiry, CASE WHEN promo.enabled AND p.unit = 'piece' THEN promo.version END,
                   EXISTS (SELECT 1 FROM stock_lots risk
                     WHERE (risk.org_id, risk.store_id, risk.product_id) = (p.org_id, sp.store_id, p.id)
                       AND (risk.quarantine_quantity > 0 OR (risk.quantity > 0 AND
                         (risk.quarantined OR risk.expiry_date < (now() AT TIME ZONE $4)::date
                           OR (p.requires_expiry AND risk.expiry_date IS NULL)))))
            FROM products p JOIN store_prices sp ON (sp.org_id, sp.product_id) = (p.org_id, p.id)
            LEFT JOIN store_quantity_promotions promo ON (promo.org_id, promo.store_id, promo.product_id) =
                                                         (sp.org_id, sp.store_id, sp.product_id)
            WHERE (p.org_id, sp.store_id) = ($1, $2) AND sp.active AND sp.unit_price > 0 AND p.id > $3
            ORDER BY p.id LIMIT 51
            """, scope.Organization, scope.Store, after ?? Guid.Empty, scope.TimeZone))
        await using (var rows = await command.ExecuteReaderAsync(ct))
        {
            while (await rows.ReadAsync(ct))
            {
                var id = rows.GetGuid(0);
                lots[id] = [];
                codes[id] = [];
                cards.Add(new CatalogProduct(id, rows.GetString(1), rows.GetString(2), rows.GetString(3),
                    rows.GetDecimal(4), rows.GetInt32(5), rows.GetBoolean(6), lots[id])
                { PromotionVersion = rows.IsDBNull(7) ? null : rows.GetInt32(7), RequiresLotVerification = rows.GetBoolean(8) });
            }
            more = cards.Count > pageSize;
            if (more) cards.RemoveAt(pageSize);
        }
        if (cards.Count > 0)
        {
            var ids = cards.Select(card => card.ProductId).ToArray();
            await using (var command = db.Sql(tx, """
                SELECT product_id, code, quantity_factor FROM product_barcodes
                WHERE org_id = $1 AND product_id = ANY($2) ORDER BY product_id, code COLLATE "C"
                """, scope.Organization, ids))
            await using (var rows = await command.ExecuteReaderAsync(ct))
                while (await rows.ReadAsync(ct))
                    codes[rows.GetGuid(0)].Add(new ProductBarcode(rows.GetString(1), rows.GetDecimal(2)));

            await using (var command = db.Sql(tx, """
                SELECT p.id, l.id, l.expiry_date, l.quantity, markdown.unit_price, markdown.version
                FROM products p
                JOIN LATERAL (
                    SELECT sl.id, sl.expiry_date, sl.quantity FROM stock_lots sl
                    WHERE (sl.org_id, sl.store_id, sl.product_id) = (p.org_id, $2, p.id)
                      AND NOT sl.quarantined AND sl.quantity > 0
                      AND (NOT p.requires_expiry OR sl.expiry_date IS NOT NULL)
                      AND (sl.expiry_date IS NULL OR sl.expiry_date >= (now() AT TIME ZONE $4)::date)
                    ORDER BY sl.expiry_date, sl.id LIMIT 100
                ) l ON true
                LEFT JOIN active_lot_markdowns markdown ON
                    (markdown.org_id, markdown.store_id, markdown.product_id, markdown.lot_id) =
                    (p.org_id, $2, p.id, l.id)
                WHERE p.org_id = $1 AND p.id = ANY($3)
                ORDER BY p.id, l.expiry_date, l.id
                """, scope.Organization, scope.Store, ids, scope.TimeZone))
            await using (var rows = await command.ExecuteReaderAsync(ct))
                while (await rows.ReadAsync(ct))
                    lots[rows.GetGuid(0)].Add(new CatalogLot(rows.GetGuid(1), ReadDate(rows, 2), rows.GetDecimal(3))
                    { MarkdownUnitPrice = rows.DecimalOrNull(4), LotPriceVersion = rows.IsDBNull(5) ? null : rows.GetInt32(5) });
        }
        return new CatalogSnapshotPage(scope.Store, DateTimeOffset.UtcNow,
            cards.Select(card => new CatalogSnapshotItem(card, codes[card.ProductId])).ToArray(),
            more ? cards[^1].ProductId : null)
        { ChangeCursor = changeCursor };
    }
}
