using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    private const int MaxBackgroundStockMovementRows = 10000;
    private const int MaxBackgroundStockMovementBytes = 8 * 1024 * 1024;
    private const string StockMovementExportFields = """
        id, CASE WHEN status = 'ready' AND expires_at <= statement_timestamp() THEN 'expired' ELSE status END,
        queued_at, finished_at, expires_at, row_count, error_code
        """;

    public async Task<StockMovementExport> QueueStockMovementExport(HttpContext context, Guid store,
        CreateStockMovementExport request, CancellationToken ct)
    {
        ValidateStockMovementFilters(request.From, request.To, request.ProductId, request.LotId, request.Kind);
        if (request.OperationId == Guid.Empty)
            throw new PosError(400, "invalid_stock_movement_export_operation");

        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockOperation(db, tx, $"stock-movement-export:{scope.Organization}:{store}", ct);
        await using (var existing = db.Sql(tx, $"""
            SELECT {StockMovementExportFields}, from_date, to_date, product_id, lot_id, kind
            FROM stock_movement_exports WHERE (org_id, store_id, actor_id, id) = ($1, $2, $3, $4)
            """, scope.Organization, store, scope.Staff, request.OperationId))
        {
            await using var rows = await existing.ExecuteReaderAsync(ct);
            if (await rows.ReadAsync(ct))
            {
                if (ReadStockMovementExportRequest(rows, 0, 7) != request)
                    throw new PosError(409, "stock_movement_export_operation_conflict");
                return ReadStockMovementExport(rows);
            }
        }

        await RequireStockMovementFilterAccess(db, tx, scope, request.ProductId, request.LotId, ct);
        await using var insert = db.Sql(tx, $"""
            INSERT INTO stock_movement_exports
                (org_id, store_id, actor_id, id, from_date, to_date, product_id, lot_id, kind, time_zone, request_id)
            SELECT $1, $2, $3, $4, $5, $6, NULLIF($7, $12), NULLIF($8, $12), NULLIF($9, ''), $10, $11
            WHERE (SELECT count(*) FROM stock_movement_exports
                   WHERE (org_id, store_id) = ($1, $2)
                     AND status IN ('queued', 'ready')
                     AND (expires_at IS NULL OR expires_at > statement_timestamp())) < 10
            RETURNING {StockMovementExportFields}
            """, scope.Organization, store, scope.Staff, request.OperationId, request.From, request.To,
            request.ProductId ?? Guid.Empty, request.LotId ?? Guid.Empty, request.Kind ?? "", scope.TimeZone,
            context.TraceIdentifier, Guid.Empty);
        StockMovementExport result;
        await using (var rows = await insert.ExecuteReaderAsync(ct))
        {
            if (!await rows.ReadAsync(ct))
                throw new PosError(409, "stock_movement_export_capacity");
            result = ReadStockMovementExport(rows);
        }
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff,
            "reports.stock_movements_export_queued", result.OperationId, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<StockMovementExport> GetStockMovementExport(HttpContext context, Guid store, Guid id,
        CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await using var command = db.Sql(tx, $"""
            SELECT {StockMovementExportFields} FROM stock_movement_exports
            WHERE (org_id, store_id, actor_id, id) = ($1, $2, $3, $4)
            """, scope.Organization, store, scope.Staff, id);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct))
            throw new PosError(404, "stock_movement_export_not_found");
        return ReadStockMovementExport(rows);
    }

    public async Task<byte[]> DownloadStockMovementExport(HttpContext context, Guid store, Guid id, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await using var command = db.Sql(tx, $"""
            SELECT {StockMovementExportFields},
                CASE WHEN status = 'ready' AND expires_at > statement_timestamp() THEN contents END
            FROM stock_movement_exports WHERE (org_id, store_id, actor_id, id) = ($1, $2, $3, $4)
            """, scope.Organization, store, scope.Staff, id);
        byte[] file;
        await using (var rows = await command.ExecuteReaderAsync(ct))
        {
            if (!await rows.ReadAsync(ct))
                throw new PosError(404, "stock_movement_export_not_found");
            var status = ReadStockMovementExport(rows);
            file = status.Status switch
            {
                "ready" => rows.GetFieldValue<byte[]>(7),
                "expired" => throw new PosError(410, "stock_movement_export_expired"),
                "failed" => throw new PosError(409, status.ErrorCode!),
                _ => throw new PosError(409, "stock_movement_export_not_ready")
            };
        }
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff,
            "reports.stock_movements_export_downloaded", id, ct);
        await tx.CommitAsync(ct);
        return file;
    }

    internal async Task<bool> ProcessStockMovementExport(CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.Run(tx, ct, """
            UPDATE stock_movement_exports SET status = 'expired', contents = NULL
            WHERE status = 'ready' AND expires_at <= statement_timestamp()
            """);
        await using var pick = db.Sql(tx, """
            SELECT j.org_id, j.store_id, j.actor_id, j.time_zone, j.request_id,
                a.active AND s.active AND s.role = 'admin', j.id, j.from_date, j.to_date, j.product_id, j.lot_id, j.kind
            FROM stock_movement_exports j JOIN staff_stores a
                ON (a.org_id, a.store_id, a.staff_id) = (j.org_id, j.store_id, j.actor_id)
            JOIN staff s ON (s.org_id, s.id) = (j.org_id, j.actor_id)
            WHERE j.status = 'queued'
            ORDER BY j.queued_at, j.org_id, j.store_id, j.actor_id, j.id
            LIMIT 1 FOR UPDATE OF j SKIP LOCKED
            """);
        StoreScope scope;
        CreateStockMovementExport request;
        string traceId;
        bool active;
        await using (var rows = await pick.ExecuteReaderAsync(ct))
        {
            if (!await rows.ReadAsync(ct))
            {
                await rows.CloseAsync();
                await tx.CommitAsync(ct);
                return false;
            }
            scope = new StoreScope(rows.GetGuid(0), rows.GetGuid(1), rows.GetGuid(2), rows.GetString(3), "admin");
            traceId = rows.GetString(4);
            active = rows.GetBoolean(5);
            request = ReadStockMovementExportRequest(rows, 6, 7);
        }

        const string generationSavepoint = "generate_export";
        await tx.SaveAsync(generationSavepoint, ct);
        string? failure = null;
        byte[]? contents = null;
        var count = 0;
        try
        {
            if (!active)
                throw new PosError(403, "stock_movement_export_access_revoked");
            await db.Run(tx, ct, "SET LOCAL statement_timeout = '20s'");
            var generatedAt = DateTime.UtcNow;
            var entries = await ReadStockMovementEntries(db, tx, scope, request.From, request.To,
                request.ProductId, request.LotId, request.Kind, null, MaxBackgroundStockMovementRows + 1, false, ct);
            count = entries.Count;
            if (count > MaxBackgroundStockMovementRows)
                throw new PosError(409, "stock_movement_background_export_limit");
            var report = new StockMovementReport(scope.Store, scope.TimeZone, request.From, request.To,
                request.ProductId, request.LotId, request.Kind, generatedAt, entries, null);
            contents = FormatStockMovementCsv(report);
            if (contents.Length > MaxBackgroundStockMovementBytes)
                throw new PosError(409, "stock_movement_background_export_limit");
        }
        catch (PosError ex)
        {
            failure = ex.Code;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.QueryCanceled && !ct.IsCancellationRequested)
        {
            failure = "stock_movement_export_timeout";
        }
        await tx.RollbackAsync(generationSavepoint, ct);
        var id = request.OperationId;
        if (failure is null)
        {
            await db.Run(tx, ct, """
                UPDATE stock_movement_exports SET status = 'ready', finished_at = statement_timestamp(),
                    expires_at = statement_timestamp() + interval '24 hours', contents = $5, row_count = $6
                WHERE (org_id, store_id, actor_id, id) = ($1, $2, $3, $4)
                """, scope.Organization, scope.Store, scope.Staff, id, contents!, count);
        }
        else
        {
            await db.Run(tx, ct, """
                UPDATE stock_movement_exports SET status = 'failed', finished_at = statement_timestamp(), error_code = $5
                WHERE (org_id, store_id, actor_id, id) = ($1, $2, $3, $4)
                """, scope.Organization, scope.Store, scope.Staff, id, failure);
        }
        await Audit(db, tx, traceId, scope.Organization, scope.Store, Guid.Empty, scope.Staff,
            failure is null ? "reports.stock_movements_export_ready" : "reports.stock_movements_export_failed",
            id, ct, failure ?? "");
        await tx.CommitAsync(ct);
        return true;
    }

    private static StockMovementExport ReadStockMovementExport(NpgsqlDataReader rows) =>
        new(rows.GetGuid(0), rows.GetString(1), rows.GetDateTime(2), rows.TimeOrNull(3), rows.TimeOrNull(4),
            rows.IsDBNull(5) ? null : rows.GetInt32(5), rows.IsDBNull(6) ? null : rows.GetString(6));

    private static CreateStockMovementExport ReadStockMovementExportRequest(NpgsqlDataReader rows, int id, int from) =>
        new(rows.GetGuid(id), rows.GetFieldValue<DateOnly>(from), rows.GetFieldValue<DateOnly>(from + 1),
            rows.IsDBNull(from + 2) ? null : rows.GetGuid(from + 2),
            rows.IsDBNull(from + 3) ? null : rows.GetGuid(from + 3),
            rows.IsDBNull(from + 4) ? null : rows.GetString(from + 4));
}
