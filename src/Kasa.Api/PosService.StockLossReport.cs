using System.Globalization;
using System.Text;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<StockLossReport> GetStockLossReport(HttpContext context, Guid store, DateOnly from, DateOnly to,
        Guid? product, string? after, int limit, CancellationToken ct, bool export = false)
    {
        var report = await GetStockMovementReport(context, store, from, to, product, null, null, after, limit, ct,
            export, lossesOnly: true);
        var items = report.Items.Select(row => new StockLossEntry(row.Kind, row.DocumentId, row.OccurredAt,
            row.LocalDate, row.ProductId, row.ProductName, row.Unit, row.LotId, -row.TotalDelta,
            row.Reason, -row.InventoryCostDelta)).ToArray();
        return new StockLossReport(report.StoreId, report.TimeZone, report.From, report.To, report.ProductId,
            report.GeneratedAt, items, report.NextCursor);
    }

    internal static byte[] FormatStockLossCsv(StockLossReport report)
    {
        var csv = new StringBuilder("\uFEFF");
        csv.Append("store_id,time_zone,from,to,filter_product_id,generated_at,access_role,")
            .Append("local_date,occurred_at,kind,document_id,product_id,product_name,unit,lot_id,quantity,reason,cost\r\n");
        var scope = FormattableString.Invariant(
            $"{report.StoreId},{QuoteCsvText(report.TimeZone)},{report.From:yyyy-MM-dd},{report.To:yyyy-MM-dd},{report.ProductId},{report.GeneratedAt:O},admin");
        foreach (var row in report.Items)
        {
            csv.Append(CultureInfo.InvariantCulture,
                $"{scope},{row.LocalDate:yyyy-MM-dd},{row.OccurredAt:O},{row.Kind},{row.DocumentId},{row.ProductId},");
            csv.Append(CultureInfo.InvariantCulture,
                $"{QuoteCsvText(row.ProductName)},{row.Unit},{row.LotId},{row.Quantity:F3},{QuoteCsvText(row.Reason ?? "")},{row.Cost:F2}\r\n");
        }
        return Encoding.UTF8.GetBytes(csv.ToString());
    }
}
