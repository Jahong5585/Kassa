using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<CashMovement> SaveCashMovement(HttpContext context, Guid shift, CreateCashMovement request,
        CancellationToken ct)
    {
        if (request.OperationId == Guid.Empty || request.Direction is not ("in" or "out") ||
            !IsMoney(request.Amount) || request.Amount <= 0 || !IsText(request.Reason, 500))
            throw new PosError(400, "invalid_cash_movement");
        var reason = request.Reason.Trim();
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireCashier(db, tx, context, true, ct);
        var existing = (await ReadCashMovements(db, tx, scope.Organization, scope.Store, scope.Register, shift,
            scope.Staff, request.OperationId, 0, ct)).SingleOrDefault();
        if (existing is not null)
        {
            if (existing.Direction != request.Direction || existing.Amount != request.Amount || existing.Reason != reason)
                throw new PosError(409, "cash_movement_id_reused");
            return existing;
        }
        await RequireOpenShift(db, tx, scope, shift, ct);
        if (request.Direction == "out")
        {
            var balance = await ReadShift(db, tx, scope.Organization, scope.Store, scope.Register, shift, null, ct);
            if (balance?.ExpectedCash is not decimal expected || request.Amount > expected)
                throw new PosError(409, "insufficient_cash_balance");
        }

        await using var command = db.Sql(tx, """
            INSERT INTO cash_movements (org_id, store_id, register_id, id, shift_id, cashier_id, direction, amount, reason)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9) ON CONFLICT DO NOTHING RETURNING created_at
            """, scope.Organization, scope.Store, scope.Register, request.OperationId, shift, scope.Staff,
            request.Direction, request.Amount, reason);
        if (await command.ExecuteScalarAsync(ct) is not DateTime created)
            throw new PosError(409, "cash_movement_id_reused");
        await Audit(db, tx, context, scope, "cash." + request.Direction, request.OperationId, ct);
        await tx.CommitAsync(ct);
        return new CashMovement(request.OperationId, shift, scope.Staff, request.Direction, request.Amount, reason, created);
    }

    public async Task<IReadOnlyList<CashMovement>> ListCashierMovements(HttpContext context, Guid shift, int? offset,
        CancellationToken ct)
    {
        ValidateStockOffset(offset);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireCashier(db, tx, context, false, ct);
        return await ReadCashMovements(db, tx, scope.Organization, scope.Store, scope.Register, shift, scope.Staff,
            null, offset ?? 0, ct);
    }

    public async Task<IReadOnlyList<CashMovement>> ListAdminMovements(HttpContext context, Guid store, Guid register,
        Guid shift, int? offset, CancellationToken ct)
    {
        ValidateStockOffset(offset);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        return await ReadCashMovements(db, tx, scope.Organization, store, register, shift, null, null, offset ?? 0, ct);
    }

    private static async Task<IReadOnlyList<CashMovement>> ReadCashMovements(NpgsqlConnection db, NpgsqlTransaction tx,
        Guid org, Guid store, Guid register, Guid shift, Guid? cashier, Guid? id, int offset, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT id, cashier_id, direction, amount, reason, created_at FROM cash_movements
            WHERE (org_id, store_id, register_id, shift_id) = ($1, $2, $3, $4)
              AND ($5 OR cashier_id = $6) AND ($7 OR id = $8)
            ORDER BY created_at DESC, id LIMIT 25 OFFSET $9
            """, org, store, register, shift, cashier is null, cashier ?? Guid.Empty, id is null, id ?? Guid.Empty, offset);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<CashMovement> result = [];
        while (await rows.ReadAsync(ct))
            result.Add(new CashMovement(rows.GetGuid(0), shift, rows.GetGuid(1), rows.GetString(2),
                rows.GetDecimal(3), rows.GetString(4), rows.GetDateTime(5)));
        return result;
    }
}
