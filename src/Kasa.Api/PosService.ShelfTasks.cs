using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<ShelfTask> MarkShelfRemoval(HttpContext context, CreateShelfTask request, CancellationToken ct)
    {
        if (request.TaskId == Guid.Empty || request.ProductId == Guid.Empty || request.LotId == Guid.Empty ||
            !IsQuantity(request.Quantity))
            throw new PosError(400, "invalid_shelf_task");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireCashier(db, tx, context, false, ct);
        await LockOperation(db, tx, $"shelf:{scope.Organization}:{scope.Store}:{request.TaskId}", ct);
        var existing = (await ReadShelfTasks(db, tx, scope.Organization, scope.Store, request.TaskId, false, 0, ct))
            .SingleOrDefault();
        if (existing is not null)
        {
            if (existing.CreatedBy != scope.Staff || existing.ProductId != request.ProductId ||
                existing.LotId != request.LotId || existing.Quantity != request.Quantity)
                throw new PosError(409, "shelf_task_id_reused");
            return existing;
        }

        var lot = await ReadLockedLot(db, tx, scope.Organization, scope.Store, request.ProductId, request.LotId, ct);
        if (!lot.RequiresExpiry && lot.ExpiryDate is null)
            throw new PosError(409, "shelf_task_expiry_not_tracked");
        ValidateLotQuantity(lot.Unit, request.Quantity);
        if (request.Quantity > lot.StockQuantity)
            throw new PosError(409, "shelf_task_quantity_exceeded");

        await using var insert = db.Sql(tx, """
            INSERT INTO shelf_tasks (org_id, store_id, id, product_id, lot_id, quantity, created_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7) ON CONFLICT DO NOTHING RETURNING created_at
            """, scope.Organization, scope.Store, request.TaskId, request.ProductId, request.LotId, request.Quantity,
            scope.Staff);
        if (await insert.ExecuteScalarAsync(ct) is not DateTime created)
            throw new PosError(409, "shelf_task_already_open");
        await Audit(db, tx, context, scope, "shelf_task.created", request.TaskId, ct);
        await tx.CommitAsync(ct);
        return new ShelfTask(request.TaskId, request.ProductId, request.LotId, request.Quantity, scope.Staff, created,
            null, null, null);
    }

    public async Task<IReadOnlyList<ShelfTask>> ListShelfTasks(HttpContext context, Guid store, bool? openOnly, int? offset,
        CancellationToken ct)
    {
        ValidateStockOffset(offset);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        return await ReadShelfTasks(db, tx, scope.Organization, store, null, openOnly ?? true, offset ?? 0, ct);
    }

    public async Task<ShelfTask> ResolveShelfTask(HttpContext context, Guid store, Guid id, ResolveShelfTask request,
        CancellationToken ct)
    {
        if (!IsText(request.Resolution, 500))
            throw new PosError(400, "invalid_shelf_task_resolution");
        var resolution = request.Resolution.Trim();
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockOperation(db, tx, $"shelf:{scope.Organization}:{store}:{id}", ct);
        var task = (await ReadShelfTasks(db, tx, scope.Organization, store, id, false, 0, ct)).SingleOrDefault()
            ?? throw new PosError(404, "shelf_task_not_found");
        if (task.ResolvedAt is not null)
        {
            if (task.Resolution != resolution)
                throw new PosError(409, "shelf_task_already_resolved");
            return task;
        }

        await using var command = db.Sql(tx, """
            UPDATE shelf_tasks SET resolved_by = $4, resolved_at = statement_timestamp(), resolution = $5
            WHERE (org_id, store_id, id) = ($1, $2, $3) RETURNING resolved_at
            """, scope.Organization, store, id, scope.Staff, resolution);
        var closed = (DateTime)(await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("Updated shelf task missing."));
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff, "shelf_task.resolved", id, ct);
        await tx.CommitAsync(ct);
        return task with { ResolvedBy = scope.Staff, ResolvedAt = closed, Resolution = resolution };
    }

    private static async Task<IReadOnlyList<ShelfTask>> ReadShelfTasks(NpgsqlConnection db, NpgsqlTransaction tx,
        Guid org, Guid store, Guid? id, bool openOnly, int offset, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT id, product_id, lot_id, quantity, created_by, created_at, resolved_by, resolved_at, resolution
            FROM shelf_tasks WHERE (org_id, store_id) = ($1, $2) AND ($3 OR id = $4) AND (NOT $5 OR resolved_at IS NULL)
            ORDER BY created_at DESC, id LIMIT 25 OFFSET $6
            """, org, store, id is null, id ?? Guid.Empty, openOnly, offset);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<ShelfTask> result = [];
        while (await rows.ReadAsync(ct))
            result.Add(new ShelfTask(rows.GetGuid(0), rows.GetGuid(1), rows.GetGuid(2), rows.GetDecimal(3), rows.GetGuid(4),
                rows.GetDateTime(5), rows.IsDBNull(6) ? null : rows.GetGuid(6), rows.TimeOrNull(7),
                rows.IsDBNull(8) ? null : rows.GetString(8)));
        return result;
    }
}
