using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<IReadOnlyList<StoreStaffAccess>> ListStoreStaff(HttpContext context, Guid store, int? offset,
        CancellationToken ct)
    {
        ValidateStockOffset(offset);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        return await ReadStoreStaff(db, tx, scope, null, offset ?? 0, ct);
    }

    public async Task<StoreStaffAccess> ConnectStaffToStore(HttpContext context, Guid store, ConnectStoreStaff request,
        CancellationToken ct)
    {
        if (!IsText(request.Subject, 256) || request.Subject != request.Subject.Trim() ||
            request.Role is not ("admin" or "receiver" or "cashier") ||
            (request.Role == "cashier") != request.RegisterId.HasValue || request.RegisterId == Guid.Empty)
            throw new PosError(400, "invalid_staff_access");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await LockStoreStaffAccess(db, tx, context, store, ct);
        if (request.RegisterId is { } register)
        {
            await Access.LockRegister(db, tx, register, ct);
            await using var activeRegister = db.Sql(tx, """
                SELECT 1 FROM registers WHERE (org_id, store_id, id) = ($1, $2, $3) AND active
                """, scope.Organization, store, register);
            if (await activeRegister.ExecuteScalarAsync(ct) is null)
                throw new PosError(404, "register_not_found");
        }

        await db.Run(tx, ct, """
            INSERT INTO staff (org_id, id, subject, role) VALUES ($1, $2, $3, $4)
            ON CONFLICT (subject) DO NOTHING
            """, scope.Organization, Guid.NewGuid(), request.Subject, request.Role);
        await using var account = db.Sql(tx, """
            SELECT id FROM staff WHERE org_id = $1 AND subject = $2 AND role = $3 AND active
            """, scope.Organization, request.Subject, request.Role);
        if (await account.ExecuteScalarAsync(ct) is not Guid staffId)
            throw new PosError(409, "staff_account_conflict");
        var connected = await db.Run(tx, ct, """
            INSERT INTO staff_stores (org_id, staff_id, store_id) VALUES ($1, $2, $3) ON CONFLICT DO NOTHING
            """, scope.Organization, staffId, store);
        var assignment = (await ReadStoreStaff(db, tx, scope, staffId, 0, ct)).Single();
        if (!assignment.Active)
            throw new PosError(409, "staff_access_revoked");
        var assignedRegister = 0;
        if (request.RegisterId is { } registerId)
        {
            assignedRegister = await db.Run(tx, ct, """
                INSERT INTO cashier_registers (org_id, staff_id, store_id, register_id) VALUES ($1, $2, $3, $4)
                ON CONFLICT DO NOTHING
                """, scope.Organization, staffId, store, registerId);
        }
        if (connected != 0 || assignedRegister != 0)
            await Audit(db, tx, context, scope.Organization, store, request.RegisterId ?? Guid.Empty, scope.Staff,
                "staff.store_access_granted", staffId, ct, request.Role);
        var result = (await ReadStoreStaff(db, tx, scope, staffId, 0, ct)).Single();
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<StoreStaffAccess> RevokeStoreStaff(HttpContext context, Guid store, Guid staffId,
        CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await LockStoreStaffAccess(db, tx, context, store, ct);
        if (scope.Staff == staffId)
            throw new PosError(409, "staff_self_revocation");
        var existing = (await ReadStoreStaff(db, tx, scope, staffId, 0, ct)).SingleOrDefault()
            ?? throw new PosError(404, "staff_not_found");
        var revoked = await db.Run(tx, ct, """
            UPDATE staff_stores SET active = false WHERE (org_id, staff_id, store_id) = ($1, $2, $3) AND active
            """, scope.Organization, staffId, store);
        if (revoked != 0)
            await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff,
                "staff.store_access_revoked", staffId, ct, existing.Role);
        await tx.CommitAsync(ct);
        return existing with { Active = false };
    }

    private async Task<StoreScope> LockStoreStaffAccess(NpgsqlConnection db, NpgsqlTransaction tx, HttpContext context,
        Guid store, CancellationToken ct)
    {
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockOperation(db, tx, $"store-staff:{scope.Organization}:{store}", ct);
        // If two administrators revoke each other, the second must still have access after taking the lock.
        return await Access.RequireStore(db, tx, context, configuration, store, true, ct);
    }

    private static async Task<List<StoreStaffAccess>> ReadStoreStaff(NpgsqlConnection db, NpgsqlTransaction tx,
        StoreScope scope, Guid? staffId, int offset, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT s.id, s.subject, s.role, s.active AND a.active, ARRAY(
                SELECT r.register_id FROM cashier_registers r
                WHERE (r.org_id, r.store_id, r.staff_id) = (a.org_id, a.store_id, a.staff_id)
                ORDER BY r.register_id
            )
            FROM staff_stores a JOIN staff s ON (s.org_id, s.id) = (a.org_id, a.staff_id)
            WHERE (a.org_id, a.store_id) = ($1, $2) AND ($3 OR a.staff_id = $4)
            ORDER BY s.subject, s.id LIMIT 50 OFFSET $5
            """, scope.Organization, scope.Store, staffId is null, staffId ?? Guid.Empty, offset);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<StoreStaffAccess> result = [];
        while (await rows.ReadAsync(ct))
            result.Add(new StoreStaffAccess(rows.GetGuid(0), rows.GetString(1), rows.GetString(2), rows.GetBoolean(3),
                rows.GetFieldValue<Guid[]>(4)));
        return result;
    }
}
