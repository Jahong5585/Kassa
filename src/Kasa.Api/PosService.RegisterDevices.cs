using System.Security.Cryptography;
using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<IReadOnlyList<RegisterDevice>> ListRegisterDevices(HttpContext context, Guid store, int? offset,
        CancellationToken ct)
    {
        ValidateStockOffset(offset);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        var devices = await ReadRegisterDevices(db, tx, scope, null, offset ?? 0, false, ct);
        return devices.Select(row => row.Device).ToArray();
    }

    public async Task<RegisterDevice> GetRegisterDevice(HttpContext context, Guid store, Guid id, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        return (await ReadRegisterDevices(db, tx, scope, id, 0, false, ct)).SingleOrDefault().Device
            ?? throw new PosError(404, "register_not_found");
    }

    public async Task<RegisterDevice> RegisterDevice(HttpContext context, Guid store, RegisterNewDevice request,
        CancellationToken ct)
    {
        if (request.RegisterId == Guid.Empty || request.CashierId == Guid.Empty || !IsText(request.Name, 120) ||
            Access.RegisterKeyDigest(request.RegisterKey) is not { } digest)
            throw new PosError(400, "invalid_register_device");
        var name = request.Name.Trim();
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await Access.LockRegister(db, tx, request.RegisterId, ct);
        var (current, currentDigest) = (await ReadRegisterDevices(db, tx, scope, request.RegisterId, 0, true, ct))
            .SingleOrDefault();
        if (current is not null)
        {
            if (current is { Active: true, Version: 1 } && current.Name == name &&
                current.CashierIds.SequenceEqual([request.CashierId]) &&
                CryptographicOperations.FixedTimeEquals(currentDigest, digest))
                return current;
            throw new PosError(409, "register_id_reused");
        }

        await using var cashier = db.Sql(tx, """
            SELECT 1 FROM staff_stores a JOIN staff s ON (s.org_id, s.id) = (a.org_id, a.staff_id)
            WHERE (a.org_id, a.store_id, a.staff_id) = ($1, $2, $3) AND a.active AND s.active AND s.role = 'cashier'
            """, scope.Organization, store, request.CashierId);
        if (await cashier.ExecuteScalarAsync(ct) is null)
            throw new PosError(404, "cashier_not_found");
        var inserted = await db.Run(tx, ct, """
            INSERT INTO registers (org_id, store_id, id, name, key_digest)
            VALUES ($1, $2, $3, $4, $5) ON CONFLICT (id) DO NOTHING
            """, scope.Organization, store, request.RegisterId, name, digest);
        if (inserted != 1)
            throw new PosError(409, "register_id_reused");
        await db.Run(tx, ct, """
            INSERT INTO cashier_registers (org_id, staff_id, store_id, register_id) VALUES ($1, $2, $3, $4)
            """, scope.Organization, request.CashierId, store, request.RegisterId);
        await Audit(db, tx, context, scope.Organization, store, request.RegisterId, scope.Staff,
            "register.registered", request.RegisterId, ct, request.CashierId.ToString());
        await tx.CommitAsync(ct);
        return new RegisterDevice(request.RegisterId, name, true, 1, [request.CashierId]);
    }

    public Task<RegisterDevice> RotateRegisterDeviceKey(HttpContext context, Guid store, Guid id, RotateDeviceKey request,
        CancellationToken ct)
    {
        var digest = Access.RegisterKeyDigest(request.RegisterKey) ?? throw new PosError(400, "invalid_register_key");
        return ChangeRegisterDevice(context, store, id, request.ExpectedVersion, digest, ct);
    }

    public Task<RegisterDevice> RevokeRegisterDevice(HttpContext context, Guid store, Guid id, RevokeDevice request,
        CancellationToken ct) => ChangeRegisterDevice(context, store, id, request.ExpectedVersion, null, ct);

    private async Task<RegisterDevice> ChangeRegisterDevice(HttpContext context, Guid store, Guid id, int expectedVersion,
        byte[]? newDigest, CancellationToken ct)
    {
        if (expectedVersion < 1 || expectedVersion == int.MaxValue)
            throw new PosError(400, "invalid_register_version");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await Access.LockRegister(db, tx, id, ct);
        var (device, digest) = (await ReadRegisterDevices(db, tx, scope, id, 0, newDigest is not null, ct))
            .SingleOrDefault();
        if (device is null)
            throw new PosError(404, "register_not_found");
        var rotating = newDigest is not null;
        var sameKey = rotating && CryptographicOperations.FixedTimeEquals(digest, newDigest);
        if (device.Version == expectedVersion + 1 && (rotating ? device.Active && sameKey : !device.Active))
            return device;
        if (device.Version != expectedVersion || !device.Active)
            throw new PosError(409, "register_version_conflict");
        if (sameKey)
            throw new PosError(409, "register_key_unchanged");

        if (rotating)
        {
            await db.Run(tx, ct, """
                UPDATE registers SET key_digest = $4, version = version + 1
                WHERE (org_id, store_id, id) = ($1, $2, $3)
                """, scope.Organization, store, id, newDigest);
        }
        else
        {
            await db.Run(tx, ct, """
                UPDATE registers SET active = false, version = version + 1
                WHERE (org_id, store_id, id) = ($1, $2, $3)
                """, scope.Organization, store, id);
        }
        await Audit(db, tx, context, scope.Organization, store, id, scope.Staff,
            rotating ? "register.key_rotated" : "register.revoked", id, ct);
        await tx.CommitAsync(ct);
        return device with { Active = rotating, Version = expectedVersion + 1 };
    }

    private static async Task<List<(RegisterDevice Device, byte[]? Digest)>> ReadRegisterDevices(NpgsqlConnection db,
        NpgsqlTransaction tx, StoreScope scope, Guid? id, int offset, bool includeDigest, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT r.id, r.name, r.active, r.version, ARRAY(
                SELECT a.staff_id FROM cashier_registers a
                JOIN staff_stores m ON (m.org_id, m.store_id, m.staff_id) = (a.org_id, a.store_id, a.staff_id) AND m.active
                JOIN staff s ON (s.org_id, s.id) = (a.org_id, a.staff_id) AND s.active
                WHERE (a.org_id, a.store_id, a.register_id) = (r.org_id, r.store_id, r.id)
                ORDER BY a.staff_id
            ), CASE WHEN $6 THEN r.key_digest END
            FROM registers r WHERE (r.org_id, r.store_id) = ($1, $2) AND ($3 OR r.id = $4)
            ORDER BY r.id LIMIT 50 OFFSET $5
            """, scope.Organization, scope.Store, id is null, id ?? Guid.Empty, offset, includeDigest);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<(RegisterDevice, byte[]?)> result = [];
        while (await rows.ReadAsync(ct))
        {
            var device = new RegisterDevice(rows.GetGuid(0), rows.GetString(1), rows.GetBoolean(2), rows.GetInt32(3),
                rows.GetFieldValue<Guid[]>(4));
            result.Add((device, rows.IsDBNull(5) ? null : rows.GetFieldValue<byte[]>(5)));
        }
        return result;
    }
}
