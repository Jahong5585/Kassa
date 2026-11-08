namespace Kasa.Api;

internal sealed partial class PosService
{
    private const int StockSnapshotPageSize = 50;

    public async Task<StockSnapshotHeader> CaptureStockSnapshot(HttpContext context, Guid store,
        CaptureStockSnapshot request, CancellationToken ct)
    {
        if (request.OperationId == Guid.Empty)
            throw new PosError(400, "invalid_stock_snapshot_operation");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockOperation(db, tx, $"stock-snapshot:{scope.Organization}:{store}", ct);
        await using (var saved = db.Sql(tx, """
            SELECT id, snapshot_date, captured_at, time_zone, captured_by
            FROM stock_valuation_snapshots WHERE (org_id, store_id, id) = ($1, $2, $3)
            """, scope.Organization, store, request.OperationId))
        {
            await using var rows = await saved.ExecuteReaderAsync(ct);
            if (await rows.ReadAsync(ct))
                return ReadStockSnapshotHeader(rows, store);
        }

        await using var capture = db.Sql(tx, """
            WITH snapshot AS (
                INSERT INTO stock_valuation_snapshots
                    (org_id, store_id, snapshot_date, id, captured_at, time_zone, captured_by)
                VALUES ($1, $2, (statement_timestamp() AT TIME ZONE $4)::date,
                        $3, statement_timestamp(), $4, $5)
                ON CONFLICT DO NOTHING
                RETURNING org_id, store_id, snapshot_date, id, captured_at, time_zone, captured_by
            ), lines AS (
                INSERT INTO stock_valuation_snapshot_lines
                    (org_id, store_id, snapshot_date, product_id, lot_id, product_name, unit,
                        stock_quantity, quarantine_quantity, inventory_value)
                SELECT s.org_id, s.store_id, s.snapshot_date, l.product_id, l.id, p.name, p.unit,
                    l.quantity, l.quarantine_quantity, l.inventory_value
                FROM snapshot s JOIN stock_lots l ON (l.org_id, l.store_id) = (s.org_id, s.store_id)
                JOIN products p ON (p.org_id, p.id) = (l.org_id, l.product_id)
                WHERE l.quantity + l.quarantine_quantity > 0
            )
            SELECT id, snapshot_date, captured_at, time_zone, captured_by FROM snapshot
            """, scope.Organization, store, request.OperationId, scope.TimeZone, scope.Staff);
        StockSnapshotHeader result;
        await using (var rows = await capture.ExecuteReaderAsync(ct))
        {
            if (!await rows.ReadAsync(ct))
                throw new PosError(409, "stock_snapshot_date_exists");
            result = ReadStockSnapshotHeader(rows, store);
        }
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff,
            "stock.valuation_snapshot_captured", result.OperationId, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<StockSnapshotPage> GetStockSnapshot(HttpContext context, Guid store, DateOnly date, Guid? product,
        int offset, CancellationToken ct)
    {
        if (!IsBusinessDate(date) || product == Guid.Empty || offset < 0 || offset > int.MaxValue - StockSnapshotPageSize)
            throw new PosError(400, "invalid_stock_snapshot_filter");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await using var command = db.Sql(tx, """
            SELECT s.id, s.snapshot_date, s.captured_at, s.time_zone, s.captured_by,
                p.product_id, p.lot_id, p.product_name, p.unit, p.stock_quantity, p.quarantine_quantity, p.inventory_value
            FROM stock_valuation_snapshots s
            LEFT JOIN LATERAL (
                SELECT product_id, lot_id, product_name, unit, stock_quantity, quarantine_quantity, inventory_value
                FROM stock_valuation_snapshot_lines
                WHERE (org_id, store_id, snapshot_date) = (s.org_id, s.store_id, s.snapshot_date)
                  AND ($4 OR product_id = $5)
                ORDER BY product_id, lot_id LIMIT $6 OFFSET $7
            ) p ON true
            WHERE (s.org_id, s.store_id, s.snapshot_date) = ($1, $2, $3)
            ORDER BY p.product_id, p.lot_id
            """, scope.Organization, store, date, product is null, product ?? Guid.Empty, StockSnapshotPageSize + 1, offset);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct))
            throw new PosError(404, "stock_snapshot_not_found");
        var snapshot = ReadStockSnapshotHeader(rows, store);
        List<StockSnapshotLine> items = [];
        int? next = null;
        do
        {
            if (rows.IsDBNull(5))
                break;
            if (items.Count == StockSnapshotPageSize)
            {
                next = offset + StockSnapshotPageSize;
                break;
            }
            items.Add(new StockSnapshotLine(rows.GetGuid(5), rows.GetGuid(6), rows.GetString(7), rows.GetString(8),
                rows.GetDecimal(9), rows.GetDecimal(10), rows.DecimalOrNull(11)));
        } while (await rows.ReadAsync(ct));
        return new StockSnapshotPage(snapshot, product, items, next);
    }

    private static StockSnapshotHeader ReadStockSnapshotHeader(Npgsql.NpgsqlDataReader rows, Guid store) =>
        new(rows.GetGuid(0), store, rows.GetFieldValue<DateOnly>(1), rows.GetDateTime(2), rows.GetString(3), rows.GetGuid(4));
}
