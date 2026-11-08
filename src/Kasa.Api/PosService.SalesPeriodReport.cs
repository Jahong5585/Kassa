using System.Globalization;
using System.Text;
using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<SalesPeriodReport> GetSalesPeriod(HttpContext context, Guid store, DateOnly from, DateOnly to,
        Guid? register, Guid? cashier, CancellationToken ct, bool export = false)
    {
        if (!IsValidReportPeriod(from, to) ||
            register == Guid.Empty || cashier == Guid.Empty)
            throw new PosError(400, "invalid_sales_report_filter");

        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        if (register.HasValue || cashier.HasValue)
        {
            await using var filters = db.Sql(tx, """
                SELECT $3 OR EXISTS (SELECT 1 FROM registers WHERE (org_id, store_id, id) = ($1, $2, $4)),
                       $5 OR EXISTS (SELECT 1 FROM staff_stores WHERE (org_id, store_id, staff_id) = ($1, $2, $6))
                """, scope.Organization, store, register is null, register ?? Guid.Empty, cashier is null, cashier ?? Guid.Empty);
            await using var rows = await filters.ExecuteReaderAsync(ct);
            await rows.ReadAsync(ct);
            if (!rows.GetBoolean(0))
                throw new PosError(404, "register_not_found");
            if (!rows.GetBoolean(1))
                throw new PosError(404, "cashier_not_found");
        }

        var report = await ReadSalesPeriod(db, tx, scope, from, to, register, cashier, ct);
        if (export)
        {
            var filter = FormattableString.Invariant($"{scope.Role}/{from:yyyy-MM-dd}/{to:yyyy-MM-dd}/{cashier?.ToString() ?? "all"}");
            await Audit(db, tx, context, scope.Organization, store, register ?? Guid.Empty, scope.Staff,
                "reports.sales_csv_exported", store, ct, filter);
            await tx.CommitAsync(ct);
        }
        return report;
    }

    private static bool IsValidReportPeriod(DateOnly from, DateOnly to) =>
        IsBusinessDate(from) && IsBusinessDate(to) && to >= from && to.DayNumber - from.DayNumber < 93;

    private static async Task<SalesPeriodReport> ReadSalesPeriod(NpgsqlConnection db, NpgsqlTransaction tx,
        StoreScope scope, DateOnly from, DateOnly to, Guid? register, Guid? cashier, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            WITH bounds AS (
                SELECT $4::timestamp AT TIME ZONE $3 AS start_at,
                       (($5::date + 1)::timestamp AT TIME ZONE $3) AS end_at
            ), days AS (
                SELECT $4::date + n AS day FROM generate_series(0, $5::date - $4::date) AS calendar(n)
            ), paid AS (
                SELECT (s.created_at AT TIME ZONE $3)::date AS day, count(*) AS sales,
                    sum(s.total) FILTER (WHERE s.method = 'cash') AS cash,
                    sum(s.total) FILTER (WHERE s.method = 'manualCashless') AS cashless,
                    sum(discounts.amount) AS discounts
                FROM sales s CROSS JOIN bounds b
                CROSS JOIN LATERAL (
                    SELECT sum(l.discount_amount) AS amount FROM sale_lines l
                    WHERE (l.org_id, l.store_id, l.register_id, l.sale_id) = (s.org_id, s.store_id, s.register_id, s.id)
                ) discounts
                WHERE (s.org_id, s.store_id) = ($1, $2) AND s.created_at >= b.start_at AND s.created_at < b.end_at
                    AND ($6 OR s.register_id = $7) AND ($8 OR s.cashier_id = $9)
                GROUP BY 1
            ), returned AS (
                SELECT (r.created_at AT TIME ZONE $3)::date AS day, count(*) AS refunds,
                    sum(r.total) FILTER (WHERE r.method = 'cash') AS cash,
                    sum(r.total) FILTER (WHERE r.method = 'manualCashless') AS cashless
                FROM refunds r CROSS JOIN bounds b
                WHERE (r.org_id, r.store_id) = ($1, $2) AND r.created_at >= b.start_at AND r.created_at < b.end_at
                    AND ($6 OR r.register_id = $7) AND ($8 OR r.cashier_id = $9)
                GROUP BY 1
            ), moved AS (
                SELECT (m.created_at AT TIME ZONE $3)::date AS day,
                    sum(m.amount) FILTER (WHERE m.direction = 'in') AS cash_in,
                    sum(m.amount) FILTER (WHERE m.direction = 'out') AS cash_out
                FROM cash_movements m CROSS JOIN bounds b
                WHERE (m.org_id, m.store_id) = ($1, $2) AND m.created_at >= b.start_at AND m.created_at < b.end_at
                    AND ($6 OR m.register_id = $7) AND ($8 OR m.cashier_id = $9)
                GROUP BY 1
            )
            SELECT d.day, statement_timestamp(), coalesce(p.sales, 0), coalesce(r.refunds, 0),
                coalesce(p.cash, 0), coalesce(p.cashless, 0), coalesce(r.cash, 0), coalesce(r.cashless, 0),
                coalesce(p.discounts, 0), coalesce(m.cash_in, 0), coalesce(m.cash_out, 0)
            FROM days d LEFT JOIN paid p USING (day) LEFT JOIN returned r USING (day) LEFT JOIN moved m USING (day)
            ORDER BY d.day
            """, scope.Organization, scope.Store, scope.TimeZone, from, to, register is null, register ?? Guid.Empty,
            cashier is null, cashier ?? Guid.Empty);

        await using var rows = await command.ExecuteReaderAsync(ct);
        var generatedAt = default(DateTime);
        List<SalesPeriodDay> days = [];
        while (await rows.ReadAsync(ct))
        {
            generatedAt = rows.GetDateTime(1);
            days.Add(new SalesPeriodDay(rows.GetFieldValue<DateOnly>(0), new SalesPeriodAmounts(
                rows.GetInt64(2), rows.GetInt64(3), rows.GetDecimal(4), rows.GetDecimal(5), rows.GetDecimal(6),
                rows.GetDecimal(7), rows.GetDecimal(8), rows.GetDecimal(9), rows.GetDecimal(10))));
        }
        var totals = new SalesPeriodAmounts(days.Sum(day => day.Amounts.SaleCount), days.Sum(day => day.Amounts.RefundCount),
            days.Sum(day => day.Amounts.CashSales), days.Sum(day => day.Amounts.ManualCashlessSales),
            days.Sum(day => day.Amounts.CashRefunds), days.Sum(day => day.Amounts.ManualCashlessRefunds),
            days.Sum(day => day.Amounts.Discounts), days.Sum(day => day.Amounts.CashIn), days.Sum(day => day.Amounts.CashOut));
        return new SalesPeriodReport(scope.Store, register, cashier, scope.TimeZone, from, to, generatedAt, totals, days);
    }

    internal static byte[] FormatSalesPeriodCsv(SalesPeriodReport report)
    {
        var csv = new StringBuilder("\uFEFF");
        csv.Append("row_type,date,from,to,store_id,register_id,cashier_id,time_zone,generated_at,sale_count,refund_count,")
            .Append("gross_sales,discounts,sales,refunds,net_sales,cash_sales,manual_cashless_sales,cash_refunds,")
            .Append("manual_cashless_refunds,net_cash,net_manual_cashless,average_check,cash_in,cash_out\r\n");
        var filter = FormattableString.Invariant(
            $"{report.From:yyyy-MM-dd},{report.To:yyyy-MM-dd},{report.StoreId},{report.RegisterId},{report.CashierId},");
        var scope = filter + QuoteCsvText(report.TimeZone) + "," + report.GeneratedAt.ToString("O", CultureInfo.InvariantCulture);
        foreach (var day in report.Days)
            AppendSalesCsvRow(csv, "day", day.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), scope, day.Amounts);
        AppendSalesCsvRow(csv, "total", "", scope, report.Total);
        return Encoding.UTF8.GetBytes(csv.ToString());
    }

    private static string QuoteCsvText(string text)
    {
        // Escape CSV syntax and neutralize a leading spreadsheet formula in a text field.
        var trimmed = text.TrimStart();
        if (trimmed.Length > 0 && "=+-@".Contains(trimmed[0]) || text.StartsWith('\t') ||
            text.StartsWith('\r') || text.StartsWith('\n'))
            text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    private static void AppendSalesCsvRow(StringBuilder csv, string type, string date, string scope,
        SalesPeriodAmounts value)
    {
        csv.Append(CultureInfo.InvariantCulture,
            $"{type},{date},{scope},{value.SaleCount},{value.RefundCount},{value.GrossSales:F2},{value.Discounts:F2},");
        csv.Append(CultureInfo.InvariantCulture,
            $"{value.Sales:F2},{value.Refunds:F2},{value.NetSales:F2},{value.CashSales:F2},{value.ManualCashlessSales:F2},");
        csv.Append(CultureInfo.InvariantCulture,
            $"{value.CashRefunds:F2},{value.ManualCashlessRefunds:F2},{value.NetCash:F2},{value.NetManualCashless:F2},");
        csv.Append(CultureInfo.InvariantCulture, $"{value.AverageCheck:F2},{value.CashIn:F2},{value.CashOut:F2}\r\n");
    }
}
