using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<IReadOnlyList<StockLot>> ListStockLots(HttpContext context, Guid store, Guid? product, int? offset,
        CancellationToken ct)
    {
        ValidateStockOffset(offset);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await using var command = db.Sql(tx, """
            SELECT l.product_id, l.id, p.name, coalesce(p.barcode, '') AS barcode, p.unit, l.expiry_date, l.quantity, l.quarantine_quantity,
                   CASE WHEN sp.active AND NOT l.quarantined AND (NOT p.requires_expiry OR l.expiry_date IS NOT NULL)
                             AND (l.expiry_date IS NULL OR l.expiry_date >= (statement_timestamp() AT TIME ZONE $3)::date)
                        THEN l.quantity ELSE 0 END, l.revision, p.requires_expiry
            FROM stock_lots l
            JOIN products p ON (p.org_id, p.id) = (l.org_id, l.product_id)
            JOIN store_prices sp ON (sp.org_id, sp.store_id, sp.product_id) = (l.org_id, l.store_id, l.product_id)
            WHERE (l.org_id, l.store_id) = ($1, $2) AND ($4 OR l.product_id = $5)
            ORDER BY p.name, l.product_id, l.expiry_date, l.id LIMIT 50 OFFSET $6
            """, scope.Organization, store, scope.TimeZone, product is null, product ?? Guid.Empty, offset ?? 0);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<StockLot> lots = [];
        while (await rows.ReadAsync(ct))
            lots.Add(ReadStockLot(rows));
        return lots;
    }

    public async Task<IReadOnlyList<ExpiringLot>> GetStoreExpiry(HttpContext context, Guid store, string? filter,
        int? offset, CancellationToken ct)
    {
        ValidateExpiryFilter(filter, offset);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        return (await ReadExpiry(db, tx, scope.Organization, store, scope.TimeZone,
            filter ?? DefaultExpiryFilter, offset ?? 0, ExpiryPageSize, includeValue: false, ct))
            .Select(row => row.Lot).ToArray();
    }

    public async Task<IReadOnlyList<ExpiringLot>> GetCashierExpiry(HttpContext context, string? filter, int? offset,
        CancellationToken ct)
    {
        ValidateExpiryFilter(filter, offset);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireCashier(db, tx, context, false, ct);
        return (await ReadExpiry(db, tx, scope.Organization, scope.Store, scope.TimeZone,
            filter ?? DefaultExpiryFilter, offset ?? 0, ExpiryPageSize, includeValue: false, ct))
            .Select(row => row.Lot).ToArray();
    }

    private sealed record ExpiryRow(ExpiringLot Lot, decimal StockQuantity, decimal QuarantineQuantity,
        decimal? InventoryValue);

    private static async Task<List<ExpiryRow>> ReadExpiry(NpgsqlConnection db, NpgsqlTransaction tx,
        Guid org, Guid store, string timeZone, string filter, int offset, int limit, bool includeValue, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            WITH lots AS (
                SELECT l.product_id, l.id, p.name, coalesce(p.barcode, '') AS barcode, p.unit, l.expiry_date, p.requires_expiry,
                       l.expiry_date - (statement_timestamp() AT TIME ZONE $3)::date AS days_left,
                       l.quantity + l.quarantine_quantity AS accounted, l.quantity, l.quarantine_quantity,
                       CASE WHEN $7 THEN l.inventory_value END AS inventory_value
                FROM stock_lots l JOIN products p ON (p.org_id, p.id) = (l.org_id, l.product_id)
                WHERE (l.org_id, l.store_id) = ($1, $2) AND l.quantity + l.quarantine_quantity > 0
                  AND (p.requires_expiry OR l.expiry_date IS NOT NULL)
            )
            SELECT product_id, id, name, barcode, unit, expiry_date, days_left, accounted, requires_expiry,
                   quantity, quarantine_quantity, inventory_value
            FROM lots WHERE CASE $4
                WHEN 'today' THEN days_left = 0
                WHEN 'within3' THEN days_left BETWEEN 0 AND 3
                WHEN 'within7' THEN days_left BETWEEN 0 AND 7
                WHEN 'expired' THEN days_left < 0
                WHEN 'missing' THEN expiry_date IS NULL AND requires_expiry
                ELSE true END
            ORDER BY expiry_date, product_id, id LIMIT $6 OFFSET $5
            """, org, store, timeZone, filter, offset, limit, includeValue);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<ExpiryRow> lots = [];
        while (await rows.ReadAsync(ct))
            lots.Add(new ExpiryRow(new ExpiringLot(rows.GetGuid(0), rows.GetGuid(1), rows.GetString(2), rows.GetString(3),
                rows.GetString(4), ReadDate(rows, 5), rows.IsDBNull(6) ? null : rows.GetInt32(6), rows.GetDecimal(7),
                rows.GetBoolean(8)), rows.GetDecimal(9), rows.GetDecimal(10), rows.DecimalOrNull(11)));
        return lots;
    }

    public async Task<StockOperation> ApplyStockOperation(HttpContext context, Guid store, CreateStockOperation request,
        CancellationToken ct)
    {
        if (request.OperationId == Guid.Empty || request.ProductId == Guid.Empty || request.LotId == Guid.Empty ||
            !IsQuantity(request.Quantity) || !IsText(request.Reason, 500) ||
            request.Kind is not ("quarantine" or "writeOffStock" or "writeOffQuarantine"))
            throw new PosError(400, "invalid_stock_operation");
        var reason = request.Reason.Trim();

        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockStockOperation(db, tx, scope, request.OperationId, ct);
        var existing = (await ReadStockOperations(db, tx, scope, request.OperationId, 0, ct)).SingleOrDefault();
        if (existing is not null)
        {
            var line = existing.Lines.Count == 1 ? existing.Lines[0] : null;
            var delta = request.Kind == "writeOffQuarantine" ? line?.QuarantineDelta : line?.StockDelta;
            if (existing.Kind != request.Kind || existing.Reason != reason || line?.ProductId != request.ProductId ||
                line?.LotId != request.LotId || delta != -request.Quantity)
                throw new PosError(409, "stock_operation_id_reused");
            return existing;
        }

        var current = await ReadLockedLot(db, tx, scope.Organization, store, request.ProductId, request.LotId, ct);
        ValidateLotQuantity(current.Unit, request.Quantity);
        var stock = current.StockQuantity;
        var quarantine = current.QuarantineQuantity;
        switch (request.Kind)
        {
            case "writeOffStock":
                stock -= request.Quantity;
                break;
            case "writeOffQuarantine":
                quarantine -= request.Quantity;
                break;
            case "quarantine":
                stock -= request.Quantity;
                quarantine += request.Quantity;
                break;
        }
        if (!IsStockQuantity(stock) || !IsStockQuantity(quarantine))
            throw new PosError(409, "stock_quantity_conflict");

        var created = await InsertStockOperation(db, tx, scope, request.OperationId, request.Kind, reason, ct);
        var movement = await ChangeLot(db, tx, scope, request.OperationId, current, stock, quarantine, ct);
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff, "stock." + request.Kind,
            request.OperationId, ct);
        await tx.CommitAsync(ct);
        return new StockOperation(request.OperationId, request.Kind, reason, scope.Staff, created, [movement]);
    }

    public async Task<IReadOnlyList<StockOperation>> ListStockOperations(HttpContext context, Guid store, int? offset,
        CancellationToken ct)
    {
        ValidateStockOffset(offset);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        return await ReadStockOperations(db, tx, scope, null, offset ?? 0, ct);
    }

    public async Task<StockOperation> GetStockOperation(HttpContext context, Guid store, Guid id, CancellationToken ct)
    {
        if (id == Guid.Empty)
            throw new PosError(404, "stock_operation_not_found");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        return (await ReadStockOperations(db, tx, scope, id, 0, ct)).SingleOrDefault()
            ?? throw new PosError(404, "stock_operation_not_found");
    }

    private static async Task<IReadOnlyList<StockOperation>> ReadStockOperations(NpgsqlConnection db,
        NpgsqlTransaction tx, StoreScope scope, Guid? id, int offset, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            WITH recent AS (
                SELECT org_id, store_id, id, kind, reason, actor_id, created_at FROM stock_operations
                WHERE (org_id, store_id) = ($1, $2) AND ($3 OR id = $4)
                ORDER BY created_at DESC, id LIMIT 25 OFFSET $5
            )
            SELECT r.id, r.kind, r.reason, r.actor_id, r.created_at, l.product_id, l.lot_id,
                   l.stock_before, l.stock_after, l.quarantine_before, l.quarantine_after, l.inventory_cost_delta
            FROM recent r LEFT JOIN stock_operation_lines l
                ON (l.org_id, l.store_id, l.operation_id) = (r.org_id, r.store_id, r.id)
            ORDER BY r.created_at DESC, r.id, l.product_id, l.lot_id
            """, scope.Organization, scope.Store, id is null, id ?? Guid.Empty, offset);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<StockOperation> result = [];
        List<StockOperationLine>? lines = null;
        while (await rows.ReadAsync(ct))
        {
            var opId = rows.GetGuid(0);
            if (result.Count == 0 || result[^1].OperationId != opId)
            {
                lines = [];
                result.Add(new StockOperation(opId, rows.GetString(1), rows.GetString(2), rows.GetGuid(3),
                    rows.GetDateTime(4), lines));
            }
            if (!rows.IsDBNull(5))
                lines!.Add(new StockOperationLine(rows.GetGuid(5), rows.GetGuid(6), rows.GetDecimal(7),
                    rows.GetDecimal(8), rows.GetDecimal(9), rows.GetDecimal(10))
                { InventoryCostDelta = rows.DecimalOrNull(11) });
        }
        return result;
    }

    private static async Task<StockLot> ReadLockedLot(NpgsqlConnection db, NpgsqlTransaction tx, Guid org,
        Guid store, Guid product, Guid lot, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT l.product_id, l.id, p.name, coalesce(p.barcode, '') AS barcode, p.unit, l.expiry_date, l.quantity, l.quarantine_quantity,
                   0::numeric, l.revision, p.requires_expiry
            FROM stock_lots l JOIN products p ON (p.org_id, p.id) = (l.org_id, l.product_id)
            WHERE (l.org_id, l.store_id, l.product_id, l.id) = ($1, $2, $3, $4)
            FOR UPDATE OF l
            """, org, store, product, lot);
        await using var rows = await command.ExecuteReaderAsync(ct);
        return await rows.ReadAsync(ct) ? ReadStockLot(rows) : throw new PosError(404, "stock_lot_not_found");
    }

    private static StockLot ReadStockLot(NpgsqlDataReader rows) =>
        new(rows.GetGuid(0), rows.GetGuid(1), rows.GetString(2), rows.GetString(3), rows.GetString(4), ReadDate(rows, 5),
            rows.GetDecimal(6), rows.GetDecimal(7), rows.GetDecimal(8), rows.GetInt64(9), rows.GetBoolean(10));

    private static DateOnly? ReadDate(NpgsqlDataReader rows, int index) =>
        rows.IsDBNull(index) ? null : rows.GetFieldValue<DateOnly>(index);

    private static Task LockStockOperation(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope, Guid id,
        CancellationToken ct) => LockOperation(db, tx, $"stock:{scope.Organization}:{scope.Store}:{id}", ct);

    private static async Task<DateTime> InsertStockOperation(NpgsqlConnection db, NpgsqlTransaction tx,
        StoreScope scope, Guid id, string kind, string reason, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            INSERT INTO stock_operations (org_id, store_id, id, kind, reason, actor_id)
            VALUES ($1, $2, $3, $4, $5, $6) ON CONFLICT DO NOTHING RETURNING created_at
            """, scope.Organization, scope.Store, id, kind, reason, scope.Staff);
        return await command.ExecuteScalarAsync(ct) is DateTime created ? created
            : throw new PosError(409, "stock_operation_id_reused");
    }

    private static async Task<StockOperationLine> ChangeLot(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope,
        Guid id, StockLot current, decimal stock, decimal quarantine, CancellationToken ct)
    {
        await using var change = db.Sql(tx, """
            WITH target AS (
                SELECT quantity + quarantine_quantity AS before_quantity,
                    kasa_weighted_cost_delta(inventory_value, quantity + quarantine_quantity,
                        $5 + $6 - quantity - quarantine_quantity) AS delta
                FROM stock_lots
                WHERE (org_id, store_id, product_id, id) = ($1, $2, $3, $4) AND revision = $7
            )
            UPDATE stock_lots l SET quantity = $5, quarantine_quantity = $6,
                inventory_value = kasa_stock_value_after(l.inventory_value, t.before_quantity, $5 + $6, t.delta)
            FROM target t
            WHERE (l.org_id, l.store_id, l.product_id, l.id) = ($1, $2, $3, $4)
            RETURNING t.delta
            """, scope.Organization, scope.Store, current.ProductId, current.LotId, stock, quarantine, current.Revision);
        decimal? cost;
        await using (var rows = await change.ExecuteReaderAsync(ct))
        {
            if (!await rows.ReadAsync(ct))
                throw new PosError(409, "stock_lot_changed");
            cost = rows.DecimalOrNull(0);
        }
        var line = new StockOperationLine(current.ProductId, current.LotId, current.StockQuantity, stock,
            current.QuarantineQuantity, quarantine) { InventoryCostDelta = cost };
        await RecordStockOperationLine(db, tx, scope, id, line, ct);
        return line;
    }

    private static Task<int> RecordStockOperationLine(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope,
        Guid id, StockOperationLine line, CancellationToken ct) =>
        db.Run(tx, ct, """
            INSERT INTO stock_operation_lines (org_id, store_id, operation_id, product_id, lot_id, stock_before,
                stock_after, quarantine_before, quarantine_after, inventory_cost_delta)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
            """, scope.Organization, scope.Store, id, line.ProductId, line.LotId, line.StockBefore, line.StockAfter,
            line.QuarantineBefore, line.QuarantineAfter, line.InventoryCostDelta);

    private static bool IsStockQuantity(decimal value) => value == 0 || IsQuantity(value);

    private static void ValidateLotQuantity(string unit, decimal quantity)
    {
        if (unit == "piece" && quantity != decimal.Truncate(quantity))
            throw new PosError(400, "fractional_piece_quantity");
    }

    private static void ValidateStockOffset(int? offset)
    {
        if (offset is < 0 or > 10_000)
            throw new PosError(400, "invalid_stock_offset");
    }

    private static void ValidateExpiryFilter(string? filter, int? offset)
    {
        ValidateStockOffset(offset);
        if (filter is not (null or "all" or "today" or "within3" or "within7" or "expired" or "missing"))
            throw new PosError(400, "invalid_expiry_filter");
    }
}
