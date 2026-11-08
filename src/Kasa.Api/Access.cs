using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Kasa.Api;

internal sealed record Staff(Guid Organization, Guid Id, string Role);
internal sealed record StoreScope(Guid Organization, Guid Store, Guid Staff, string TimeZone, string Role);
internal sealed record RegisterScope(Guid Organization, Guid Store, Guid Register, Guid Staff, string TimeZone);

internal static class Access
{
    public static async Task LockRegister(NpgsqlConnection db, NpgsqlTransaction tx, Guid register, CancellationToken ct)
    {
        await using var command = db.Sql(tx, "SELECT pg_advisory_xact_lock(hashtextextended($1::text, 0))", register);
        await command.ExecuteScalarAsync(ct);
    }

    public static async Task<Staff> RequireStaff(NpgsqlConnection db, NpgsqlTransaction tx, HttpContext context,
        string role, CancellationToken ct, string alternateRole = "")
    {
        var subject = context.User.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 256)
            throw new PosError(403, "staff_access_denied");

        await using var command = db.Sql(tx, """
            SELECT org_id, id, role FROM staff WHERE subject = $1 AND active AND role IN ($2, $3)
            """, subject, role, alternateRole);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct))
            throw new PosError(403, "staff_access_denied");
        return new Staff(rows.GetGuid(0), rows.GetGuid(1), rows.GetString(2));
    }

    public static async Task<StoreScope> RequireStore(NpgsqlConnection db, NpgsqlTransaction tx,
        HttpContext context, IConfiguration configuration, Guid store, bool adminOnly, CancellationToken ct,
        string missingCode = "store_not_found")
    {
        var staff = await RequireStaff(db, tx, context, "admin", ct, adminOnly ? "" : "receiver");
        if (staff.Role == "admin" && configuration["Auth:AdminAcr"] is { Length: > 0 } acr &&
            context.User.FindFirst("acr")?.Value != acr)
            throw new PosError(403, "admin_mfa_required");

        await using var command = db.Sql(tx, """
            SELECT s.time_zone FROM staff_stores a JOIN stores s ON (a.org_id, a.store_id) = (s.org_id, s.id)
            WHERE (a.org_id, a.staff_id, a.store_id) = ($1, $2, $3) AND a.active
            """, staff.Organization, staff.Id, store);
        if (await command.ExecuteScalarAsync(ct) is not string zone)
            throw new PosError(404, missingCode);
        return new StoreScope(staff.Organization, store, staff.Id, zone, staff.Role);
    }

    public static async Task<RegisterScope> RequireCashier(NpgsqlConnection db, NpgsqlTransaction tx,
        HttpContext context, bool write, CancellationToken ct)
    {
        if (!Guid.TryParse(context.Request.Headers["X-Register-Id"].ToString(), out var register))
            throw new PosError(403, "register_access_denied");
        var key = context.Request.Headers["X-Register-Key"].ToString();
        if (RegisterKeyDigest(key) is not { } digest)
            throw new PosError(403, "register_access_denied");
        if (write)
        {
            // A register lock orders its shifts, sales and retries through COMMIT.
            await LockRegister(db, tx, register, ct);
        }

        var staff = await RequireStaff(db, tx, context, "cashier", ct);
        await using var command = db.Sql(tx, """
            SELECT r.store_id, s.time_zone, r.key_digest
            FROM registers r
            JOIN cashier_registers a ON (a.org_id, a.store_id, a.register_id) = (r.org_id, r.store_id, r.id)
            JOIN staff_stores m ON (m.org_id, m.store_id, m.staff_id) = (a.org_id, a.store_id, a.staff_id) AND m.active
            JOIN stores s ON (s.org_id, s.id) = (r.org_id, r.store_id)
            WHERE r.org_id = $1 AND a.staff_id = $2 AND r.id = $3 AND r.active
            """, staff.Organization, staff.Id, register);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct) || !CryptographicOperations.FixedTimeEquals(rows.GetFieldValue<byte[]>(2), digest))
            throw new PosError(403, "register_access_denied");
        return new RegisterScope(staff.Organization, rows.GetGuid(0), register, staff.Id, rows.GetString(1));
    }

    public static byte[]? RegisterKeyDigest(string? key) => key is { Length: 64 } && key.All(Uri.IsHexDigit)
        ? SHA256.HashData(Encoding.UTF8.GetBytes(key)) : null;
}
