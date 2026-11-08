using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<IReadOnlyList<SaleHistoryEntry>> GetCashierSales(HttpContext context, Guid? receiptId,
        Guid? shiftId, DateOnly? date, decimal? amount, int? offset, CancellationToken ct)
    {
        ValidateSaleHistoryFilter(date, amount, offset);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireCashier(db, tx, context, false, ct);
        return await ReadSaleHistory(db, tx, scope.Organization, scope.Store, scope.TimeZone, scope.Register,
            scope.Staff, receiptId, shiftId, date, amount, offset ?? 0, ct);
    }

    public async Task<IReadOnlyList<SaleHistoryEntry>> GetAdminSales(HttpContext context, Guid store, Guid? registerId,
        Guid? cashierId, Guid? receiptId, Guid? shiftId, DateOnly? date, decimal? amount, int? offset, CancellationToken ct)
    {
        ValidateSaleHistoryFilter(date, amount, offset);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        return await ReadSaleHistory(db, tx, scope.Organization, store, scope.TimeZone, registerId, cashierId, receiptId,
            shiftId, date, amount, offset ?? 0, ct);
    }

    public async Task<SaleResult> GetAdminSale(HttpContext context, Guid store, Guid register, Guid id,
        CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        return (await ReadSale(db, tx, scope.Organization, store, register, null, id, ct))?.Sale
            ?? throw new PosError(404, "sale_not_found");
    }

    private static async Task<IReadOnlyList<SaleHistoryEntry>> ReadSaleHistory(NpgsqlConnection db, NpgsqlTransaction tx,
        Guid org, Guid store, string timeZone, Guid? register, Guid? cashier, Guid? receipt, Guid? shift,
        DateOnly? date, decimal? amount, int offset, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            WITH matched AS (
                SELECT org_id, store_id, register_id, id, shift_id, cashier_id, created_at, method, total
                FROM sales WHERE (org_id, store_id) = ($1, $2)
                    AND ($3 OR register_id = $4) AND ($5 OR cashier_id = $6)
                    AND ($7 OR id = $8) AND ($9 OR shift_id = $10)
                    AND ($11 OR (created_at >= ($12::timestamp AT TIME ZONE $13)
                             AND created_at < (($12 + 1)::timestamp AT TIME ZONE $13)))
                    AND ($14 OR total = $15)
                ORDER BY created_at DESC, id, register_id LIMIT 25 OFFSET $16
            )
            SELECT s.id, s.register_id, s.shift_id, s.cashier_id, s.created_at, s.method, s.total,
                refunded.amount, CASE WHEN refunded.count = 0 THEN 'none'
                    WHEN sold.quantity = returned.quantity THEN 'full' ELSE 'partial' END
            FROM matched s
            CROSS JOIN LATERAL (
                SELECT coalesce(sum(total), 0) AS amount, count(*) AS count FROM refunds r
                WHERE (r.org_id, r.store_id, r.source_register_id, r.sale_id) = (s.org_id, s.store_id, s.register_id, s.id)
            ) refunded
            CROSS JOIN LATERAL (
                SELECT sum(quantity) AS quantity FROM sale_lines l
                WHERE (l.org_id, l.store_id, l.register_id, l.sale_id) = (s.org_id, s.store_id, s.register_id, s.id)
            ) sold
            CROSS JOIN LATERAL (
                SELECT sum(quantity) AS quantity FROM refund_lines l
                WHERE (l.org_id, l.store_id, l.source_register_id, l.sale_id) = (s.org_id, s.store_id, s.register_id, s.id)
            ) returned
            ORDER BY s.created_at DESC, s.id, s.register_id
            """, org, store, register is null, register ?? Guid.Empty, cashier is null, cashier ?? Guid.Empty,
            receipt is null, receipt ?? Guid.Empty, shift is null, shift ?? Guid.Empty,
            date is null, date ?? new DateOnly(2000, 1, 1), timeZone, amount is null, amount ?? 0m, offset);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<SaleHistoryEntry> result = [];
        while (await rows.ReadAsync(ct))
            result.Add(new SaleHistoryEntry(rows.GetGuid(0), rows.GetGuid(1), rows.GetGuid(2), rows.GetGuid(3),
                rows.GetDateTime(4), rows.GetString(5), rows.GetDecimal(6), rows.GetDecimal(7), rows.GetString(8)));
        return result;
    }

    private static void ValidateSaleHistoryFilter(DateOnly? date, decimal? amount, int? offset)
    {
        ValidateStockOffset(offset);
        if (date == DateOnly.MaxValue || date == DateOnly.MinValue || amount is { } total && !IsMoney(total))
            throw new PosError(400, "invalid_sale_history_filter");
    }
}
