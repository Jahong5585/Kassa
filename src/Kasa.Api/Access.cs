using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Kasa.Api;

internal sealed record StaffRoleSelection(Guid Organization, Guid Staff, Guid Store, string TimeZone,
    IReadOnlyList<string> Roles, bool Owner);
internal sealed record StoreScope(Guid Organization, Guid Store, Guid Staff, string TimeZone, string Role);
internal sealed record RegisterScope(Guid Organization, Guid Store, Guid Register, Guid Staff, string TimeZone);
internal sealed record OperatorSessionIdentity(Guid SessionId, Guid Organization, Guid Store, Guid Staff,
    Guid DeviceId, Guid? RegisterId, string TimeZone, string[] AssignedRoles, bool Owner,
    DateTime CreatedAt, DateTime IdleExpiresAt, DateTime AbsoluteExpiresAt);

internal static class Access
{
    private static readonly string[] OperationalRoles = ["admin", "cashier", "receiver"];
    private static readonly object OperatorSessionItem = new();
    private sealed record Principal(Guid Organization, Guid Staff, Guid? Store, string[] Roles,
        bool Owner, OperatorSessionIdentity? Session);

    public static async Task LockRegister(NpgsqlConnection db, NpgsqlTransaction tx, Guid register,
        CancellationToken ct)
    {
        await using var command = db.Sql(tx, "SELECT pg_advisory_xact_lock(hashtextextended($1::text, 0))", register);
        await command.ExecuteScalarAsync(ct);
    }

    public static async Task<OperatorSessionIdentity> RequireOperatorSession(NpgsqlConnection db,
        NpgsqlTransaction tx, HttpContext context, CancellationToken ct)
    {
        if (context.Items.TryGetValue(OperatorSessionItem, out var cached) && cached is OperatorSessionIdentity identity)
            return identity;
        var token = context.Request.Headers["X-Operator-Session"].ToString();
        if (token.Length is < 32 or > 200) throw new PosError(403, "operator_session_required");
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var (issuer, subject) = ReadHost(context);
        await using var command = db.Sql(tx, """
            SELECT os.id, os.org_id, os.store_id, os.staff_id, os.device_id, os.register_id, st.time_zone,
                   array_agg(DISTINCT r.role ORDER BY r.role), bool_or(r.role = 'owner'),
                   os.created_at, os.idle_expires_at, os.absolute_expires_at
            FROM operator_sessions os
            JOIN staff s ON (s.org_id, s.id) = (os.org_id, os.staff_id) AND s.active
            JOIN staff_stores m ON (m.org_id, m.store_id, m.staff_id) = (os.org_id, os.store_id, os.staff_id) AND m.active
            JOIN stores st ON (st.org_id, st.id) = (os.org_id, os.store_id)
            JOIN staff_store_roles r ON (r.org_id, r.store_id, r.staff_id) = (os.org_id, os.store_id, os.staff_id)
            WHERE os.token_digest = $1 AND os.host_issuer = $2 AND os.host_subject = $3
              AND os.revoked_at IS NULL AND os.idle_expires_at > now() AND os.absolute_expires_at > now()
            GROUP BY os.id, os.org_id, os.store_id, os.staff_id, os.device_id, os.register_id, st.time_zone,
                     os.created_at, os.idle_expires_at, os.absolute_expires_at
            """, digest, issuer, subject);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct))
        {
            await rows.DisposeAsync();
            await using var known = db.Sql(tx, """
                SELECT 1 FROM operator_sessions
                WHERE token_digest = $1 AND host_issuer = $2 AND host_subject = $3
                """, digest, issuer, subject);
            var exists = await known.ExecuteScalarAsync(ct) is not null;
            throw new PosError(401, exists ? "operator_session_expired" : "operator_session_invalid");
        }
        identity = new OperatorSessionIdentity(rows.GetGuid(0), rows.GetGuid(1), rows.GetGuid(2), rows.GetGuid(3),
            rows.GetGuid(4), rows.IsDBNull(5) ? null : rows.GetGuid(5), rows.GetString(6),
            rows.GetFieldValue<string[]>(7), rows.GetBoolean(8), rows.GetDateTime(9), rows.GetDateTime(10),
            rows.GetDateTime(11));
        await rows.DisposeAsync();
        var idleMinutes = identity.AssignedRoles.Contains("admin", StringComparer.Ordinal) || identity.Owner ? 15 : 30;
        await using var touch = db.Sql(tx, """
            UPDATE operator_sessions
            SET last_seen_at = statement_timestamp(),
                idle_expires_at = LEAST(statement_timestamp() + make_interval(mins => $2), absolute_expires_at)
            WHERE id = $1 AND revoked_at IS NULL
              AND idle_expires_at > statement_timestamp() AND absolute_expires_at > statement_timestamp()
            RETURNING idle_expires_at
            """, identity.SessionId, idleMinutes);
        var idleExpiresAt = (DateTime)(await touch.ExecuteScalarAsync(ct)
            ?? throw new PosError(401, "operator_session_expired"));
        identity = identity with { IdleExpiresAt = idleExpiresAt };
        context.Items[OperatorSessionItem] = identity;
        return identity;
    }

    public static async Task<StaffRoleSelection> ReadRolesForStore(NpgsqlConnection db, NpgsqlTransaction tx,
        HttpContext context, Guid store, CancellationToken ct)
    {
        var principal = await ReadPrincipal(db, tx, context, ct);
        if (principal.Store is { } sessionStore && sessionStore != store)
            throw new PosError(409, "operator_store_mismatch");
        var assignment = await ReadStoreAssignment(db, tx, principal, store, ct);
        return new StaffRoleSelection(principal.Organization, principal.Staff, store, assignment.TimeZone,
            AvailableRoles(assignment.Roles, assignment.Owner), assignment.Owner);
    }

    public static async Task<StoreScope> RequireStore(NpgsqlConnection db, NpgsqlTransaction tx,
        HttpContext context, IConfiguration configuration, Guid store, bool adminOnly, CancellationToken ct,
        string missingCode = "store_not_found")
    {
        var principal = await ReadPrincipal(db, tx, context, ct);
        if (principal.Store is { } sessionStore && sessionStore != store)
            throw new PosError(409, "operator_store_mismatch");
        var assignment = await ReadStoreAssignment(db, tx, principal, store, ct, missingCode);
        var activeRole = ResolveActiveRole(context, AvailableRoles(assignment.Roles, assignment.Owner));
        if (activeRole != "admin" && (adminOnly || activeRole != "receiver"))
            throw new PosError(403, "active_role_denied");
        // Store admin is operational. Platform hardware/MFA checks remain isolated in PlatformService.
        return new StoreScope(principal.Organization, store, principal.Staff, assignment.TimeZone, activeRole);
    }

    public static async Task<RegisterScope> RequireCashier(NpgsqlConnection db, NpgsqlTransaction tx,
        HttpContext context, bool write, CancellationToken ct)
    {
        if (!Guid.TryParse(context.Request.Headers["X-Register-Id"].ToString(), out var register))
            throw new PosError(403, "register_access_denied");
        var key = context.Request.Headers["X-Register-Key"].ToString();
        if (RegisterKeyDigest(key) is not { } digest) throw new PosError(403, "register_access_denied");
        if (write) await LockRegister(db, tx, register, ct);
        var principal = await ReadPrincipal(db, tx, context, ct);
        var activeRole = ResolveActiveRole(context, AvailableRoles(principal.Roles, principal.Owner));
        if (activeRole != "cashier") throw new PosError(403, "active_role_denied");
        await using var command = db.Sql(tx, """
            SELECT r.store_id, s.time_zone, r.key_digest
            FROM registers r
            JOIN cashier_registers a ON (a.org_id, a.store_id, a.register_id) = (r.org_id, r.store_id, r.id)
            JOIN staff_stores m ON (m.org_id, m.store_id, m.staff_id) = (a.org_id, a.store_id, a.staff_id) AND m.active
            JOIN stores s ON (s.org_id, s.id) = (r.org_id, r.store_id)
            WHERE r.org_id = $1 AND a.staff_id = $2 AND r.id = $3 AND r.active
            """, principal.Organization, principal.Staff, register);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct) || !CryptographicOperations.FixedTimeEquals(rows.GetFieldValue<byte[]>(2), digest))
            throw new PosError(403, "register_access_denied");
        var store = rows.GetGuid(0);
        if (principal.Store is { } sessionStore && sessionStore != store)
            throw new PosError(409, "operator_store_mismatch");
        var scope = new RegisterScope(principal.Organization, store, register, principal.Staff, rows.GetString(1));
        await rows.DisposeAsync();
        if (principal.Session is { RegisterId: null } session)
            await db.Run(tx, ct, "UPDATE operator_sessions SET register_id = $2 WHERE id = $1 AND register_id IS NULL",
                session.SessionId, register);
        return scope;
    }

    private static async Task<Principal> ReadPrincipal(NpgsqlConnection db, NpgsqlTransaction tx, HttpContext context,
        CancellationToken ct)
    {
        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        var hasOperatorHeader = !string.IsNullOrEmpty(context.Request.Headers["X-Operator-Session"]);
        if (hasOperatorHeader || configuration.GetValue<bool>("Auth:RequireOperatorSession"))
        {
            var session = await RequireOperatorSession(db, tx, context, ct);
            return new Principal(session.Organization, session.Staff, session.Store, session.AssignedRoles,
                session.Owner, session);
        }
        var subject = context.User.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 256)
            throw new PosError(403, "staff_access_denied");
        await using var command = db.Sql(tx, """
            SELECT s.org_id, s.id, array_agg(r.role ORDER BY r.role), bool_or(r.role = 'owner')
            FROM staff s JOIN staff_roles r ON (r.org_id, r.staff_id) = (s.org_id, s.id)
            WHERE s.subject = $1 AND s.active GROUP BY s.org_id, s.id
            """, subject);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct)) throw new PosError(403, "staff_access_denied");
        return new Principal(rows.GetGuid(0), rows.GetGuid(1), null, rows.GetFieldValue<string[]>(2),
            rows.GetBoolean(3), null);
    }

    private static async Task<(string TimeZone, string[] Roles, bool Owner)> ReadStoreAssignment(
        NpgsqlConnection db, NpgsqlTransaction tx, Principal principal, Guid store, CancellationToken ct,
        string missingCode = "store_not_found")
    {
        await using var command = db.Sql(tx, """
            SELECT s.time_zone, array_agg(r.role ORDER BY r.role), bool_or(r.role = 'owner')
            FROM staff_stores a
            JOIN stores s ON (a.org_id, a.store_id) = (s.org_id, s.id)
            JOIN staff_store_roles r ON (r.org_id, r.store_id, r.staff_id) = (a.org_id, a.store_id, a.staff_id)
            WHERE (a.org_id, a.staff_id, a.store_id) = ($1, $2, $3) AND a.active
            GROUP BY s.time_zone
            """, principal.Organization, principal.Staff, store);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct)) throw new PosError(404, missingCode);
        return (rows.GetString(0), rows.GetFieldValue<string[]>(1), rows.GetBoolean(2));
    }

    private static string[] AvailableRoles(string[] assigned, bool owner) => owner
        ? OperationalRoles
        : OperationalRoles.Where(assigned.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static string ResolveActiveRole(HttpContext context, IReadOnlyCollection<string> available)
    {
        var requested = context.Request.Headers["X-Active-Role"].ToString();
        if (string.IsNullOrEmpty(requested))
        {
            if (available.Count != 1) throw new PosError(403, "active_role_required");
            return available.Single();
        }
        if (!OperationalRoles.Contains(requested, StringComparer.Ordinal))
            throw new PosError(400, "active_role_invalid");
        if (!available.Contains(requested, StringComparer.Ordinal))
            throw new PosError(403, "active_role_denied");
        return requested;
    }

    internal static (string Issuer, string Subject) ReadHost(HttpContext context)
    {
        var issuer = context.User.FindFirst(JwtRegisteredClaimNames.Iss)?.Value ??
                     context.User.FindFirst("iss")?.Value ?? "";
        var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ??
                      context.User.FindFirst("sub")?.Value ?? "";
        if (issuer.Length is < 1 or > 500 || subject.Length is < 1 or > 256)
            throw new PosError(401, "oidc_session_expired");
        return (issuer, subject);
    }

    public static byte[]? RegisterKeyDigest(string? key) => key is { Length: 64 } && key.All(Uri.IsHexDigit)
        ? SHA256.HashData(Encoding.UTF8.GetBytes(key)) : null;
}
