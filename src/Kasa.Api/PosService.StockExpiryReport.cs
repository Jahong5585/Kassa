using System.Globalization;
using System.Text;

namespace Kasa.Api;

internal sealed partial class PosService
{
    private const string DefaultExpiryFilter = "within7";
    private const int ExpiryPageSize = 50;
    private const int MaxStockExpiryExportRows = 1000;

    public async Task<StockExpiryReport> GetStockExpiryReport(HttpContext context, Guid store, string? filter,
        int offset, CancellationToken ct, bool export = false)
    {
        ValidateExpiryFilter(filter, null);
        if (offset < 0 || offset > int.MaxValue - ExpiryPageSize)
            throw new PosError(400, "invalid_stock_offset");
        var appliedFilter = filter ?? DefaultExpiryFilter;
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        var take = export ? MaxStockExpiryExportRows : ExpiryPageSize;
        var rows = await ReadExpiry(db, tx, scope.Organization, store, scope.TimeZone, appliedFilter, offset,
            take + 1, includeValue: true, ct);
        var hasMore = rows.Count > take;
        if (export)
        {
            if (hasMore)
                throw new PosError(409, "stock_expiry_export_limit");
            await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff,
                "reports.stock_expiry_csv_exported", store, ct, $"{scope.Role}/{appliedFilter}");
            await tx.CommitAsync(ct);
        }
        var items = rows.Take(take).Select(row => new StockExpiryEntry(row.Lot.ProductId, row.Lot.LotId,
            row.Lot.Name, row.Lot.Unit, row.Lot.ExpiryDate, row.Lot.DaysRemaining, row.StockQuantity,
            row.QuarantineQuantity, row.InventoryValue)).ToArray();
        return new StockExpiryReport(store, scope.TimeZone, appliedFilter, items, hasMore ? offset + take : null);
    }

    internal static byte[] FormatStockExpiryCsv(StockExpiryReport report)
    {
        var csv = new StringBuilder("\uFEFF");
        csv.Append("store_id,time_zone,filter,access_role,product_id,lot_id,product_name,unit,expiry_date,days_left,")
            .Append("stock_quantity,quarantine_quantity,inventory_value\r\n");
        var filters = $"{report.StoreId},{QuoteCsvText(report.TimeZone)},{report.Filter},admin";
        foreach (var row in report.Items)
        {
            csv.Append(CultureInfo.InvariantCulture,
                $"{filters},{row.ProductId},{row.LotId},{QuoteCsvText(row.ProductName)},{row.Unit},{row.ExpiryDate:yyyy-MM-dd},{row.DaysLeft},");
            csv.Append(CultureInfo.InvariantCulture,
                $"{row.StockQuantity:F3},{row.QuarantineQuantity:F3},{row.InventoryValue:F2}\r\n");
        }
        return Encoding.UTF8.GetBytes(csv.ToString());
    }
}
