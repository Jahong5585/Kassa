using System.Globalization;
using System.Text;
using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    private const int StockValuationPageSize = 50;
    private const int MaxStockValuationExportRows = 1000;
    private const string InventoryCostMethod = "movingWeightedAverageByLot";

    public async Task<StockValuation> GetStockValuation(HttpContext context, Guid store, Guid? product, int offset,
        CancellationToken ct, bool export = false)
    {
        if (product == Guid.Empty || offset < 0 || offset > int.MaxValue - StockValuationPageSize)
            throw new PosError(400, "invalid_stock_valuation_filter");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        if (product is { } selected)
        {
            await using var filter = db.Sql(tx, """
                SELECT 1 FROM store_prices WHERE (org_id, store_id, product_id) = ($1, $2, $3)
                """, scope.Organization, store, selected);
            if (await filter.ExecuteScalarAsync(ct) is null)
                throw new PosError(404, "product_not_found");
        }
        var take = export ? MaxStockValuationExportRows : StockValuationPageSize;
        var report = await ReadStockValuation(db, tx, scope, product, offset, take, ct);
        if (export)
        {
            if (report.NextOffset.HasValue)
                throw new PosError(409, "stock_valuation_export_limit");
            var filter = $"{scope.Role}/{product?.ToString() ?? "all"}";
            await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff,
                "reports.stock_valuation_csv_exported", store, ct, filter);
            await tx.CommitAsync(ct);
        }
        return report;
    }

    private static async Task<StockValuation> ReadStockValuation(NpgsqlConnection db, NpgsqlTransaction tx,
        StoreScope scope, Guid? product, int offset, int take, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            WITH values_by_lot AS (
                SELECT product_id, id, quantity, quarantine_quantity, inventory_value,
                    CASE WHEN quantity = 0 THEN 0 ELSE
                        kasa_weighted_cost_delta(inventory_value, quantity + quarantine_quantity, quantity)
                    END AS stock_value
                FROM stock_lots WHERE (org_id, store_id) = ($1, $2) AND ($3 OR product_id = $4)
                  AND quantity + quarantine_quantity > 0
            ), balances AS (
                SELECT *, CASE WHEN quarantine_quantity = 0 THEN 0 ELSE inventory_value - stock_value END AS quarantine_value
                FROM values_by_lot
            ), totals AS (
                SELECT coalesce(sum(stock_value), 0) AS stock_value, coalesce(sum(quarantine_value), 0) AS quarantine_value,
                    count(*) FILTER (WHERE inventory_value IS NULL) AS unknown_count
                FROM balances
            ), page AS (
                SELECT * FROM balances ORDER BY product_id, id LIMIT $5 OFFSET $6
            )
            SELECT statement_timestamp(), t.stock_value, t.quarantine_value, t.unknown_count,
                   b.product_id, b.id, p.name, p.unit, b.quantity, b.quarantine_quantity, b.stock_value, b.quarantine_value
            FROM totals t LEFT JOIN page b ON true
            LEFT JOIN products p ON (p.org_id, p.id) = ($1, b.product_id)
            ORDER BY b.product_id, b.id
            """, scope.Organization, scope.Store, product is null, product ?? Guid.Empty, take + 1, offset);
        await using var rows = await command.ExecuteReaderAsync(ct);
        await rows.ReadAsync(ct);
        var timestamp = rows.GetDateTime(0);
        var stock = rows.GetDecimal(1);
        var quarantine = rows.GetDecimal(2);
        var unknown = rows.GetInt64(3);
        List<StockValuationLine> items = [];
        int? next = null;
        do
        {
            if (rows.IsDBNull(4))
                break;
            if (items.Count == take)
            {
                next = offset + take;
                break;
            }
            items.Add(new StockValuationLine(rows.GetGuid(4), rows.GetGuid(5), rows.GetString(6), rows.GetString(7),
                rows.GetDecimal(8), rows.GetDecimal(9), rows.DecimalOrNull(10), rows.DecimalOrNull(11)));
        } while (await rows.ReadAsync(ct));
        return new StockValuation(scope.Store, product, InventoryCostMethod, timestamp, stock, quarantine, unknown,
            items, next) { TimeZone = scope.TimeZone };
    }

    internal static byte[] FormatStockValuationCsv(StockValuation report)
    {
        var csv = new StringBuilder("\uFEFF");
        csv.Append("row_type,store_id,filter_product_id,time_zone,generated_at,access_role,cost_method,")
            .Append("product_id,lot_id,product_name,unit,stock_quantity,quarantine_quantity,")
            .Append("stock_value,quarantine_value,total_value,known_stock_value,known_quarantine_value,known_value,unvalued_lot_count\r\n");
        var scope = FormattableString.Invariant(
            $"{report.StoreId},{report.ProductId},{QuoteCsvText(report.TimeZone)},{report.GeneratedAt:O},admin,{report.Method}");
        foreach (var row in report.Items)
        {
            csv.Append(CultureInfo.InvariantCulture,
                $"lot,{scope},{row.ProductId},{row.LotId},{QuoteCsvText(row.ProductName)},{row.Unit},{row.StockQuantity:F3},{row.QuarantineQuantity:F3},");
            csv.Append(CultureInfo.InvariantCulture, $"{row.StockValue:F2},{row.QuarantineValue:F2},{row.TotalValue:F2},,,,\r\n");
        }
        csv.Append(CultureInfo.InvariantCulture,
            $"total,{scope},,,,,,,,,{report.TotalValue:F2},{report.KnownStockValue:F2},{report.KnownQuarantineValue:F2},{report.KnownValue:F2},{report.UnvaluedLotCount}\r\n");
        return Encoding.UTF8.GetBytes(csv.ToString());
    }
}
