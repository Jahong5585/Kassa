using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<IReadOnlyList<PurchasePriceHistoryItem>> ListPurchasePriceHistory(HttpContext context,
        Guid store, Guid product, int? offset, CancellationToken ct)
    {
        ValidateStockOffset(offset);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await using var command = db.Sql(tx, """
            WITH history AS (
                SELECT h.lot_id, h.source_kind, h.source_id, h.quantity, h.unit_cost, h.total_cost,
                       h.changed_at, h.changed_by,
                       coalesce(nullif(concat_ws(' ', nullif(s.first_name, ''), nullif(s.last_name, '')), ''),
                                nullif(s.display_name, '')) AS changed_by_name,
                       h.reason,
                       lag(h.unit_cost) OVER (ORDER BY h.changed_at, h.lot_id, h.source_kind, h.source_id) AS previous_unit_cost
                FROM stock_lot_purchase_price_history h
                LEFT JOIN staff s ON (s.org_id, s.id) = (h.org_id, h.changed_by)
                WHERE (h.org_id, h.store_id, h.product_id) = ($1, $2, $3)
            )
            SELECT lot_id, source_kind, source_id, quantity, unit_cost, total_cost, changed_at, changed_by,
                   changed_by_name, reason, previous_unit_cost
            FROM history
            ORDER BY changed_at DESC, lot_id DESC, source_kind DESC, source_id DESC
            LIMIT 50 OFFSET $4
            """, scope.Organization, store, product, offset ?? 0);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<PurchasePriceHistoryItem> result = [];
        while (await rows.ReadAsync(ct))
            result.Add(new PurchasePriceHistoryItem(rows.GetGuid(0), rows.GetString(1), rows.GetGuid(2),
                rows.GetDecimal(3), rows.GetDecimal(4), rows.GetDecimal(5), rows.GetDateTime(6),
                rows.IsDBNull(7) ? null : rows.GetGuid(7), rows.IsDBNull(8) ? null : rows.GetString(8),
                rows.GetString(9)) { PreviousUnitCost = rows.DecimalOrNull(10) });
        return result;
    }
}
