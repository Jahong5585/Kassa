using System.Globalization;
using System.Text;
using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<ProfitReport> GetProfitReport(HttpContext context, Guid store, DateOnly from, DateOnly to,
        CancellationToken ct, bool export = false)
    {
        if (!IsValidReportPeriod(from, to))
            throw new PosError(400, "invalid_profit_report_period");

        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        var report = await ReadProfitReport(db, tx, scope, from, to, ct);
        if (export)
        {
            var filter = FormattableString.Invariant($"{scope.Role}/{from:yyyy-MM-dd}/{to:yyyy-MM-dd}");
            await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff,
                "reports.profit_csv_exported", store, ct, filter);
            await tx.CommitAsync(ct);
        }
        return report;
    }

    private static async Task<ProfitReport> ReadProfitReport(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope,
        DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            WITH bounds AS (
                SELECT $4::timestamp AT TIME ZONE $3 AS start_at,
                       (($5::date + 1)::timestamp AT TIME ZONE $3) AS end_at
            ), days AS (
                SELECT $4::date + n AS day FROM generate_series(0, $5::date - $4::date) AS calendar(n)
            ), paid AS (
                SELECT (s.created_at AT TIME ZONE $3)::date AS day, sum(s.total) AS revenue,
                    sum(cost.amount) AS known_cost, sum(cost.unvalued)::bigint AS unvalued
                FROM sales s CROSS JOIN bounds b
                CROSS JOIN LATERAL (
                    SELECT sum(l.inventory_cost) AS amount, count(*) FILTER (WHERE l.inventory_cost IS NULL) AS unvalued
                    FROM sale_lines l
                    WHERE (l.org_id, l.store_id, l.register_id, l.sale_id) = (s.org_id, s.store_id, s.register_id, s.id)
                ) cost
                WHERE (s.org_id, s.store_id) = ($1, $2) AND s.created_at >= b.start_at AND s.created_at < b.end_at
                GROUP BY 1
            ), refunded AS (
                SELECT (r.created_at AT TIME ZONE $3)::date AS day, sum(r.total) AS revenue,
                    sum(cost.amount) AS known_cost, sum(cost.unvalued)::bigint AS unvalued
                FROM refunds r CROSS JOIN bounds b
                CROSS JOIN LATERAL (
                    SELECT sum(l.inventory_cost) AS amount, count(*) FILTER (WHERE l.inventory_cost IS NULL) AS unvalued
                    FROM refund_lines l WHERE (l.org_id, l.store_id, l.refund_id) = (r.org_id, r.store_id, r.id)
                ) cost
                WHERE (r.org_id, r.store_id) = ($1, $2) AND r.created_at >= b.start_at AND r.created_at < b.end_at
                GROUP BY 1
            ), losses AS (
                SELECT (o.created_at AT TIME ZONE $3)::date AS day,
                    sum(-l.inventory_cost_delta) FILTER (WHERE o.kind IN ('writeOffStock', 'writeOffQuarantine')) AS writeoffs,
                    sum(-l.inventory_cost_delta) FILTER (WHERE o.kind = 'inventory') AS shortages,
                    count(*) FILTER (WHERE l.inventory_cost_delta IS NULL) AS unvalued
                FROM stock_operations o CROSS JOIN bounds b
                JOIN stock_operation_lines l ON (l.org_id, l.store_id, l.operation_id) = (o.org_id, o.store_id, o.id)
                WHERE (o.org_id, o.store_id) = ($1, $2) AND o.created_at >= b.start_at AND o.created_at < b.end_at
                  AND o.kind IN ('writeOffStock', 'writeOffQuarantine', 'inventory')
                  AND l.stock_after + l.quarantine_after < l.stock_before + l.quarantine_before
                GROUP BY 1
            )
            SELECT d.day, statement_timestamp(), coalesce(p.revenue, 0), coalesce(r.revenue, 0),
                coalesce(p.known_cost, 0), coalesce(r.known_cost, 0), coalesce(p.unvalued, 0), coalesce(r.unvalued, 0),
                coalesce(l.writeoffs, 0), coalesce(l.shortages, 0), coalesce(l.unvalued, 0)
            FROM days d LEFT JOIN paid p USING (day) LEFT JOIN refunded r USING (day) LEFT JOIN losses l USING (day)
            ORDER BY d.day
            """, scope.Organization, scope.Store, scope.TimeZone, from, to);

        await using var rows = await command.ExecuteReaderAsync(ct);
        var generatedAt = default(DateTime);
        List<ProfitDay> days = [];
        while (await rows.ReadAsync(ct))
        {
            generatedAt = rows.GetDateTime(1);
            days.Add(new ProfitDay(rows.GetFieldValue<DateOnly>(0), new ProfitAmounts(
                rows.GetDecimal(2), rows.GetDecimal(3), rows.GetDecimal(4), rows.GetDecimal(5),
                rows.GetInt64(6), rows.GetInt64(7), rows.GetDecimal(8), rows.GetDecimal(9), rows.GetInt64(10))));
        }
        var total = new ProfitAmounts(days.Sum(day => day.Amounts.Sales), days.Sum(day => day.Amounts.Refunds),
            days.Sum(day => day.Amounts.KnownSaleCost), days.Sum(day => day.Amounts.KnownRefundCost),
            days.Sum(day => day.Amounts.UnvaluedSaleLineCount), days.Sum(day => day.Amounts.UnvaluedRefundLineCount),
            days.Sum(day => day.Amounts.KnownWriteOffCost), days.Sum(day => day.Amounts.KnownInventoryShortageCost),
            days.Sum(day => day.Amounts.UnvaluedExpenseLineCount));
        return new ProfitReport(scope.Store, scope.TimeZone, from, to, generatedAt, InventoryCostMethod, total, days);
    }

    internal static byte[] FormatProfitReportCsv(ProfitReport report)
    {
        var csv = new StringBuilder("\uFEFF");
        csv.Append("row_type,date,from,to,store_id,time_zone,generated_at,access_role,cost_method,")
            .Append("sales,refunds,net_revenue,known_sale_cost,known_refund_cost,unvalued_sale_line_count,")
            .Append("unvalued_refund_line_count,cost_of_goods_sold,gross_profit,gross_margin_percent,")
            .Append("known_write_off_cost,known_inventory_shortage_cost,unvalued_expense_line_count,inventory_expenses\r\n");
        var scope = FormattableString.Invariant(
            $"{report.From:yyyy-MM-dd},{report.To:yyyy-MM-dd},{report.StoreId},{QuoteCsvText(report.TimeZone)},{report.GeneratedAt:O},admin,{report.CostMethod}");
        foreach (var day in report.Days)
            AppendProfitCsvRow(csv, "day", day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), scope, day.Amounts);
        AppendProfitCsvRow(csv, "total", "", scope, report.Total);
        return Encoding.UTF8.GetBytes(csv.ToString());
    }

    private static void AppendProfitCsvRow(StringBuilder csv, string type, string date, string scope, ProfitAmounts value)
    {
        csv.Append(CultureInfo.InvariantCulture,
            $"{type},{date},{scope},{value.Sales:F2},{value.Refunds:F2},{value.NetRevenue:F2},{value.KnownSaleCost:F2},{value.KnownRefundCost:F2},");
        csv.Append(CultureInfo.InvariantCulture,
            $"{value.UnvaluedSaleLineCount},{value.UnvaluedRefundLineCount},{value.CostOfGoodsSold:F2},{value.GrossProfit:F2},{value.GrossMarginPercent:F2},");
        csv.Append(CultureInfo.InvariantCulture,
            $"{value.KnownWriteOffCost:F2},{value.KnownInventoryShortageCost:F2},{value.UnvaluedExpenseLineCount},{value.InventoryExpenses:F2}\r\n");
    }
}
