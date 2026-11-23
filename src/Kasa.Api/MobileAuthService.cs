using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Npgsql;

namespace Kasa.Api;

internal sealed partial class MobileAuthService(NpgsqlDataSource source, OperatorPasswordHasher passwords)
{
    private static readonly TimeSpan AccessLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(30);
    private static readonly TimeSpan OwnerRoleLifetime = TimeSpan.FromMinutes(30);
    private static readonly string[] OperationalRoles = ["admin", "cashier", "receiver"];

    private sealed record Credential(Guid Organization, Guid Staff, string Login, string PasswordHash,
        DateTime? LockedUntil, string DisplayName, bool Owner);
    private sealed record SessionTokens(string AccessToken, byte[] AccessDigest, DateTime AccessExpiresAt,
        string RefreshToken, byte[] RefreshDigest, DateTime RefreshExpiresAt);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._@+\\-]{2,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex LoginPattern();

    public async Task<MobileCredentialConfigured> ConfigureOwn(HttpContext context,
        ConfigureOwnMobileCredentials request, CancellationToken ct)
    {
        if (MobileAuthentication.IsMobile(context.User) || request.DeviceId == Guid.Empty)
            throw new PosError(403, "oidc_identity_confirmation_required");
        var login = ValidateLogin(request.Login);
        OperatorPasswordHasher.Validate(request.Password);
        var (_, subject) = Access.ReadHost(context);
        var hash = await passwords.Hash(request.Password, ct);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using var owner = db.Sql(tx, """
            SELECT s.org_id, s.id
            FROM staff s
            WHERE s.subject = $1 AND s.identity_kind = 'oidc' AND s.active
              AND EXISTS (SELECT 1 FROM staff_stores m
                          JOIN staff_store_roles r ON (r.org_id, r.store_id, r.staff_id) =
                              (m.org_id, m.store_id, m.staff_id)
                          WHERE (m.org_id, m.staff_id) = (s.org_id, s.id) AND m.active AND r.role = 'owner')
            """, subject);
        await using var rows = await owner.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct)) throw new PosError(403, "owner_access_required");
        var organization = rows.GetGuid(0);
        var staff = rows.GetGuid(1);
        await rows.DisposeAsync();
        try
        {
            await using var credential = db.Sql(tx, """
                INSERT INTO employee_credentials (org_id, staff_id, password_hash, password_changed_at,
                    failed_attempts, locked_until, must_change_password, version,
                    mobile_login, mobile_login_normalized, mobile_enabled)
                VALUES ($1, $2, $3, now(), 0, NULL, false, 1, $4, $5, true)
                ON CONFLICT (org_id, staff_id) DO UPDATE
                SET password_hash = EXCLUDED.password_hash, password_changed_at = now(), failed_attempts = 0,
                    locked_until = NULL, must_change_password = false,
                    mobile_login = EXCLUDED.mobile_login,
                    mobile_login_normalized = EXCLUDED.mobile_login_normalized,
                    mobile_enabled = true, version = employee_credentials.version + 1
                RETURNING version
                """, organization, staff, hash, login.Display, login.Normalized);
            var version = (int)(await credential.ExecuteScalarAsync(ct) ?? 1);
            await RevokeStaffMobileSessions(db, tx, organization, staff, "mobile_credentials_changed", ct);
            await Audit(db, tx, context, "mobile.credentials_configured", organization, staff, null,
                request.DeviceId, "succeeded", null, ct);
            await tx.CommitAsync(ct);
            return new(staff, login.Display, true, version);
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new PosError(409, "mobile_login_conflict");
        }
    }

    public async Task<MobileCredentialConfigured> ConfigureEmployee(HttpContext context, Guid store, Guid employee,
        ConfigureEmployeeMobileCredentials request, CancellationToken ct)
    {
        if (employee == Guid.Empty || request.ExpectedVersion < 1)
            throw new PosError(400, "validation_failed");
        var login = ValidateLogin(request.Login);
        var reason = ValidateReason(request.Reason);
        string? hash = null;
        if (request.NewPassword is not null)
        {
            OperatorPasswordHasher.Validate(request.NewPassword);
            hash = await passwords.Hash(request.NewPassword, ct);
        }
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var owner = await Access.RequireOperatorSession(db, tx, context, ct);
        if (owner.Store != store) throw new PosError(409, "operator_store_mismatch");
        var activeRole = context.Request.Headers["X-Active-Role"].ToString();
        if (string.IsNullOrWhiteSpace(activeRole))
            throw new PosError(403, "active_role_required");
        if (activeRole != "admin")
            throw new PosError(403, "active_role_denied");
        if (!owner.Owner) throw new PosError(403, "owner_access_required");
        Access.RequireOwnerReauthentication(context);

        await using var updateStaff = db.Sql(tx, """
            UPDATE staff s SET version = version + 1, updated_at = now()
            WHERE (s.org_id, s.id, s.version) = ($1, $2, $3) AND s.identity_kind = 'local'
              AND EXISTS (SELECT 1 FROM staff_stores m
                          WHERE (m.org_id, m.store_id, m.staff_id) = (s.org_id, $4, s.id) AND m.active)
              AND NOT EXISTS (SELECT 1 FROM staff_store_roles r
                              WHERE (r.org_id, r.store_id, r.staff_id, r.role) = (s.org_id, $4, s.id, 'owner'))
            RETURNING version
            """, owner.Organization, employee, request.ExpectedVersion, store);
        var value = await updateStaff.ExecuteScalarAsync(ct);
        if (value is not int version)
            throw new PosError(409, "employee_version_conflict");
        try
        {
            var changed = hash is null
                ? await db.Run(tx, ct, """
                    UPDATE employee_credentials
                    SET mobile_login = $3, mobile_login_normalized = $4, mobile_enabled = true,
                        version = version + 1
                    WHERE (org_id, staff_id) = ($1, $2)
                    """, owner.Organization, employee, login.Display, login.Normalized)
                : await db.Run(tx, ct, """
                    UPDATE employee_credentials
                    SET password_hash = $3, password_changed_at = now(), failed_attempts = 0, locked_until = NULL,
                        must_change_password = false, mobile_login = $4, mobile_login_normalized = $5,
                        mobile_enabled = true, version = version + 1
                    WHERE (org_id, staff_id) = ($1, $2)
                    """, owner.Organization, employee, hash, login.Display, login.Normalized);
            if (changed != 1) throw new PosError(409, "employee_credentials_missing");
            await RevokeStaffMobileSessions(db, tx, owner.Organization, employee, "mobile_credentials_changed", ct);
            await db.Run(tx, ct, """
                UPDATE operator_sessions SET revoked_at = now(), revoked_reason = 'mobile_credentials_changed'
                WHERE (org_id, staff_id) = ($1, $2) AND revoked_at IS NULL
                """, owner.Organization, employee);
            await db.Run(tx, ct, """
                INSERT INTO operator_audit_events (action, organization_id, store_id, actor_staff_id,
                    target_staff_id, active_role, device_id, trace_id, result, reason)
                VALUES ('employee.mobile_credentials_updated', $1, $2, $3, $4, 'admin', $5, $6, 'succeeded', $7)
                """, owner.Organization, store, owner.Staff, employee, owner.DeviceId, context.TraceIdentifier, reason);
            var mobileSession = MobileAuthentication.IsMobile(context.User)
                ? MobileAuthentication.Read(context.User).Session
                : (Guid?)null;
            await Audit(db, tx, context, "mobile.credentials_configured", owner.Organization, employee, mobileSession,
                owner.DeviceId, "succeeded", reason, ct);
            await tx.CommitAsync(ct);
            return new(employee, login.Display, true, version);
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new PosError(409, "mobile_login_conflict");
        }
    }

    public async Task<MobileSessionCreated> Login(HttpContext context, MobileLogin request, CancellationToken ct)
    {
        if (request.DeviceId == Guid.Empty) throw new PosError(400, "validation_failed");
        var login = ValidateLogin(request.Login);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var credential = await VerifyCredential(db, tx, context, login.Normalized, request.Password,
            request.DeviceId, ct);
        var result = await CreateSession(db, tx, context, credential, request.DeviceId, "mobile.login_succeeded", ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<MobileSessionCreated> Switch(HttpContext context, MobileSwitchOperator request,
        CancellationToken ct)
    {
        var current = MobileAuthentication.Read(context.User);
        var login = ValidateLogin(request.Login);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var credential = await VerifyCredential(db, tx, context, login.Normalized, request.Password,
            current.Device, ct);
        var result = await CreateSession(db, tx, context, credential, current.Device, "mobile.operator_switched", ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<MobileSessionCreated> Refresh(HttpContext context, MobileRefresh request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken) ||
            !request.RefreshToken.StartsWith(MobileAuthentication.RefreshPrefix, StringComparison.Ordinal) ||
            request.RefreshToken.Length is < 48 or > 300)
            throw new PosError(400, "validation_failed");
        var digest = Digest(request.RefreshToken);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using var command = db.Sql(tx, """
            SELECT ms.id, ms.org_id, ms.staff_id, ms.device_id, ms.refresh_expires_at,
                   ms.owner_role_expires_at, ms.revoked_at,
                   COALESCE(NULLIF(s.display_name, ''), s.subject, s.id::text), s.active,
                   EXISTS (SELECT 1 FROM staff_stores m
                           JOIN staff_store_roles r ON (r.org_id, r.store_id, r.staff_id) =
                               (m.org_id, m.store_id, m.staff_id)
                           WHERE (m.org_id, m.staff_id) = (s.org_id, s.id) AND m.active AND r.role = 'owner')
            FROM mobile_sessions ms
            JOIN staff s ON (s.org_id, s.id) = (ms.org_id, ms.staff_id)
            JOIN employee_credentials c ON (c.org_id, c.staff_id) = (ms.org_id, ms.staff_id) AND c.mobile_enabled
            WHERE ms.refresh_token_digest = $1 FOR UPDATE OF ms
            """, digest);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct)) throw new PosError(401, "mobile_refresh_token_invalid");
        var session = rows.GetGuid(0); var organization = rows.GetGuid(1); var staff = rows.GetGuid(2);
        var device = rows.GetGuid(3); var refreshExpires = rows.GetDateTime(4);
        var ownerExpires = rows.IsDBNull(5) ? (DateTime?)null : rows.GetDateTime(5);
        var revoked = !rows.IsDBNull(6); var display = rows.GetString(7); var active = rows.GetBoolean(8);
        var isOwner = rows.GetBoolean(9);
        await rows.DisposeAsync();
        if (revoked || !active) throw new PosError(401, "mobile_access_revoked");
        if (refreshExpires <= DateTime.UtcNow)
        {
            await db.Run(tx, ct, """
                UPDATE mobile_sessions SET revoked_at = now(), revoked_reason = 'refresh_expired'
                WHERE id = $1 AND revoked_at IS NULL
                """, session);
            await tx.CommitAsync(ct);
            throw new PosError(401, "mobile_refresh_token_expired");
        }
        var tokens = NewTokens(refreshExpires);
        await db.Run(tx, ct, """
            UPDATE mobile_sessions SET access_token_digest = $2, refresh_token_digest = $3,
                access_expires_at = $4, last_seen_at = now(), version = version + 1
            WHERE id = $1 AND revoked_at IS NULL
            """, session, tokens.AccessDigest, tokens.RefreshDigest, tokens.AccessExpiresAt);
        await Audit(db, tx, context, "mobile.token_refreshed", organization, staff, session, device,
            "succeeded", null, ct);
        await tx.CommitAsync(ct);
        return new("Bearer", tokens.AccessToken, tokens.AccessExpiresAt, tokens.RefreshToken, refreshExpires,
            session, device, new(staff, display, isOwner), ownerExpires);
    }

    public async Task<CurrentMobileSession> Current(HttpContext context, CancellationToken ct)
    {
        var current = MobileAuthentication.Read(context.User);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT ms.access_expires_at, ms.refresh_expires_at, ms.owner_role_expires_at,
                   COALESCE(NULLIF(s.display_name, ''), s.subject, s.id::text),
                   EXISTS (SELECT 1 FROM staff_stores m
                           JOIN staff_store_roles r ON (r.org_id, r.store_id, r.staff_id) =
                               (m.org_id, m.store_id, m.staff_id)
                           WHERE (m.org_id, m.staff_id) = (s.org_id, s.id) AND m.active AND r.role = 'owner')
            FROM mobile_sessions ms JOIN staff s ON (s.org_id, s.id) = (ms.org_id, ms.staff_id)
            WHERE ms.id = $1 AND ms.revoked_at IS NULL
            """, db);
        command.Parameters.AddWithValue(current.Session);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct)) throw new PosError(401, "mobile_session_invalid");
        return new(current.Session, current.Device, new(current.Staff, rows.GetString(3), rows.GetBoolean(4)),
            rows.GetDateTime(0), rows.GetDateTime(1), rows.IsDBNull(2) ? null : rows.GetDateTime(2));
    }

    public async Task Logout(HttpContext context, CancellationToken ct)
    {
        var current = MobileAuthentication.Read(context.User);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await db.Run(tx, ct, """
            UPDATE mobile_sessions SET revoked_at = now(), revoked_reason = 'logout'
            WHERE id = $1 AND revoked_at IS NULL
            """, current.Session);
        await RevokeDeviceOperatorSessions(db, tx, current.Device, current.Staff, "mobile_logout", ct);
        await Audit(db, tx, context, "mobile.logout", current.Organization, current.Staff, current.Session,
            current.Device, "succeeded", null, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<MobileOwnerReauthenticationResult> ReauthenticateOwner(HttpContext context,
        MobileOwnerReauthentication request, CancellationToken ct)
    {
        var current = MobileAuthentication.Read(context.User);
        var login = ValidateLogin(request.Login);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using (var owner = db.Sql(tx, """
            SELECT 1 FROM staff_stores m
            JOIN staff_store_roles r ON (r.org_id, r.store_id, r.staff_id) = (m.org_id, m.store_id, m.staff_id)
            WHERE (m.org_id, m.store_id, m.staff_id) = ($1, $2, $3) AND m.active AND r.role = 'owner'
            """, current.Organization, request.StoreId, current.Staff))
        {
            if (await owner.ExecuteScalarAsync(ct) is null) throw new PosError(403, "owner_access_required");
        }
        var credential = await VerifyCredential(db, tx, context, login.Normalized, request.Password,
            current.Device, ct);
        if ((credential.Organization, credential.Staff) != (current.Organization, current.Staff))
            throw new PosError(401, "mobile_credentials_invalid");
        var now = DateTime.UtcNow;
        var expires = now.Add(OwnerRoleLifetime);
        await db.Run(tx, ct, """
            UPDATE mobile_sessions SET owner_verified_at = $2, owner_role_expires_at = $3
            WHERE id = $1 AND revoked_at IS NULL
            """, current.Session, now, expires);
        await Audit(db, tx, context, "mobile.owner_reauthenticated", current.Organization, current.Staff,
            current.Session, current.Device, "succeeded", null, ct);
        await tx.CommitAsync(ct);
        return new(expires);
    }

    public async Task<OperatorSessionCreated> CreateOperatorSession(HttpContext context,
        CreateMobileOperatorSession request, CancellationToken ct)
    {
        var current = MobileAuthentication.Read(context.User);
        if (request.StoreId == Guid.Empty) throw new PosError(400, "validation_failed");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using var command = db.Sql(tx, """
            SELECT s.first_name, s.last_name, COALESCE(NULLIF(s.display_name, ''), s.subject, s.id::text),
                   array_agg(r.role ORDER BY r.role), bool_or(r.role = 'owner'), s.identity_kind = 'local',
                   s.active AND m.active
            FROM staff s JOIN staff_stores m ON (m.org_id, m.staff_id) = (s.org_id, s.id)
            JOIN staff_store_roles r ON (r.org_id, r.store_id, r.staff_id) = (m.org_id, m.store_id, m.staff_id)
            WHERE (m.org_id, m.store_id, m.staff_id) = ($1, $2, $3)
            GROUP BY s.org_id, s.id, m.active
            """, current.Organization, request.StoreId, current.Staff);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct)) throw new PosError(403, "store_access_denied");
        var assigned = rows.GetFieldValue<string[]>(3); var owner = rows.GetBoolean(4);
        var roles = owner ? OperationalRoles : OperationalRoles.Where(assigned.Contains).Distinct().Order().ToArray();
        var item = new OperatorItem(current.Staff, rows.IsDBNull(0) ? null : rows.GetString(0),
            rows.IsDBNull(1) ? null : rows.GetString(1), rows.GetString(2), roles, owner, rows.GetBoolean(5),
            rows.GetBoolean(6));
        await rows.DisposeAsync();
        if (!item.Active || roles.Length == 0) throw new PosError(403, "store_access_denied");

        var hostSubject = MobileAuthentication.HostSubject(current.Device);
        Guid? registerId = null;
        if (roles.Contains("cashier", StringComparer.Ordinal))
        {
            await using var bound = db.Sql(tx, """
                SELECT b.register_id
                FROM register_device_bindings b
                JOIN registers r ON (r.org_id, r.store_id, r.id) = (b.org_id, b.store_id, b.register_id) AND r.active
                JOIN cashier_registers a ON (a.org_id, a.store_id, a.register_id) = (b.org_id, b.store_id, b.register_id)
                WHERE (b.org_id, b.store_id, b.device_id) = ($1, $2, $3)
                  AND b.host_issuer = $4 AND b.host_subject = $5 AND b.revoked_at IS NULL AND a.staff_id = $6
                """, current.Organization, request.StoreId, current.Device, MobileAuthentication.Issuer,
                hostSubject, current.Staff);
            if (await bound.ExecuteScalarAsync(ct) is Guid register) registerId = register;
        }
        await RevokeDeviceOperatorSessions(db, tx, current.Device, current.Staff, "mobile_operator_replaced", ct);
        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var now = DateTime.UtcNow;
        var idle = now.AddMinutes(roles.Contains("admin", StringComparer.Ordinal) ? 15 : 30);
        var absolute = now.AddHours(12);
        var session = Guid.NewGuid();
        await db.Run(tx, ct, """
            INSERT INTO operator_sessions (id, org_id, store_id, staff_id, host_issuer, host_subject, device_id,
                register_id, token_digest, created_at, last_seen_at, idle_expires_at, absolute_expires_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7,
                NULLIF($8, '00000000-0000-0000-0000-000000000000'::uuid), $9, $10, $10, $11, $12)
            """, session, current.Organization, request.StoreId, current.Staff, MobileAuthentication.Issuer,
            hostSubject, current.Device, registerId ?? Guid.Empty, Digest(token), now, idle, absolute);
        await db.Run(tx, ct, """
            INSERT INTO operator_audit_events (action, organization_id, store_id, actor_staff_id, target_staff_id,
                device_id, register_id, trace_id, result)
            VALUES ('operator.login_succeeded', $1, $2, $3, $3, $4,
                NULLIF($5, '00000000-0000-0000-0000-000000000000'::uuid), $6, 'succeeded')
            """, current.Organization, request.StoreId, current.Staff, current.Device, registerId ?? Guid.Empty,
            context.TraceIdentifier);
        await tx.CommitAsync(ct);
        return new(token, session, current.Organization, request.StoreId, item, idle, absolute, registerId);
    }

    private async Task<Credential> VerifyCredential(NpgsqlConnection db, NpgsqlTransaction tx, HttpContext context,
        string normalizedLogin, string? password, Guid device, CancellationToken ct)
    {
        var loginDigest = Digest(normalizedLogin);
        var ipDigest = Digest(context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        await using (var rate = db.Sql(tx, """
            SELECT locked_until FROM mobile_login_rate_limits
            WHERE (login_digest, device_id, ip_digest) = ($1, $2, $3)
            """, loginDigest, device, ipDigest))
        {
            if (await rate.ExecuteScalarAsync(ct) is DateTime locked && locked > DateTime.UtcNow)
            {
                context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling((locked - DateTime.UtcNow).TotalSeconds)).ToString();
                throw new PosError(429, "mobile_login_rate_limited");
            }
        }
        Credential? candidate = null;
        await using (var command = db.Sql(tx, """
            SELECT c.org_id, c.staff_id, c.mobile_login, c.password_hash, c.locked_until,
                   COALESCE(NULLIF(s.display_name, ''), s.subject, s.id::text),
                   EXISTS (SELECT 1 FROM staff_stores m
                           JOIN staff_store_roles r ON (r.org_id, r.store_id, r.staff_id) =
                               (m.org_id, m.store_id, m.staff_id)
                           WHERE (m.org_id, m.staff_id) = (s.org_id, s.id) AND m.active AND r.role = 'owner')
            FROM employee_credentials c JOIN staff s ON (s.org_id, s.id) = (c.org_id, c.staff_id)
            WHERE c.mobile_enabled AND c.mobile_login_normalized = $1 AND s.active
            """, normalizedLogin))
        await using (var rows = await command.ExecuteReaderAsync(ct))
        {
            if (await rows.ReadAsync(ct))
                candidate = new(rows.GetGuid(0), rows.GetGuid(1), rows.GetString(2), rows.GetString(3),
                    rows.IsDBNull(4) ? null : rows.GetDateTime(4), rows.GetString(5), rows.GetBoolean(6));
        }
        if (candidate?.LockedUntil is { } credentialLocked && credentialLocked > DateTime.UtcNow)
        {
            context.Response.Headers.RetryAfter = Math.Max(1,
                (int)Math.Ceiling((credentialLocked - DateTime.UtcNow).TotalSeconds)).ToString();
            throw new PosError(429, "mobile_login_rate_limited");
        }
        var validPasswordShape = password is { Length: >= 8 and <= 128 } && !password.Contains('\0');
        var valid = candidate is not null && validPasswordShape &&
                    await passwords.Verify(password, candidate.PasswordHash, ct);
        if (candidate is null || !validPasswordShape)
        {
            var dummy = validPasswordShape ? password! : "invalid-password-placeholder";
            _ = await passwords.Hash(dummy, ct);
        }
        if (!valid)
        {
            var delay = await RecordFailedLogin(db, tx, loginDigest, device, ipDigest, candidate, ct);
            await Audit(db, tx, context, "mobile.login_failed", candidate?.Organization, candidate?.Staff, null,
                device, "failed", null, ct);
            await tx.CommitAsync(ct);
            if (delay >= TimeSpan.FromSeconds(30))
                context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds)).ToString();
            throw new PosError(delay >= TimeSpan.FromSeconds(30) ? 429 : 401,
                delay >= TimeSpan.FromSeconds(30) ? "mobile_login_rate_limited" : "mobile_credentials_invalid");
        }
        await db.Run(tx, ct, """
            UPDATE employee_credentials SET failed_attempts = 0, locked_until = NULL
            WHERE (org_id, staff_id) = ($1, $2)
            """, candidate!.Organization, candidate.Staff);
        await db.Run(tx, ct, """
            DELETE FROM mobile_login_rate_limits
            WHERE (login_digest, device_id, ip_digest) = ($1, $2, $3)
            """, loginDigest, device, ipDigest);
        return candidate;
    }

    private async Task<MobileSessionCreated> CreateSession(NpgsqlConnection db, NpgsqlTransaction tx,
        HttpContext context, Credential credential, Guid device, string action, CancellationToken ct)
    {
        await db.Run(tx, ct, """
            UPDATE mobile_sessions SET revoked_at = now(), revoked_reason = 'device_session_replaced'
            WHERE device_id = $1 AND revoked_at IS NULL
            """, device);
        await db.Run(tx, ct, """
            UPDATE operator_sessions SET revoked_at = now(), revoked_reason = 'mobile_session_replaced'
            WHERE host_issuer = $1 AND host_subject = $2 AND revoked_at IS NULL
            """, MobileAuthentication.Issuer, MobileAuthentication.HostSubject(device));
        var tokens = NewTokens();
        var session = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var ownerExpires = credential.Owner ? now.Add(OwnerRoleLifetime) : (DateTime?)null;
        if (ownerExpires is { } expires)
        {
            await db.Run(tx, ct, """
                INSERT INTO mobile_sessions (id, org_id, staff_id, device_id, access_token_digest,
                    refresh_token_digest, created_at, last_seen_at, access_expires_at, refresh_expires_at,
                    owner_verified_at, owner_role_expires_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $7, $8, $9, $7, $10)
                """, session, credential.Organization, credential.Staff, device, tokens.AccessDigest,
                tokens.RefreshDigest, now, tokens.AccessExpiresAt, tokens.RefreshExpiresAt, expires);
        }
        else
        {
            await db.Run(tx, ct, """
                INSERT INTO mobile_sessions (id, org_id, staff_id, device_id, access_token_digest,
                    refresh_token_digest, created_at, last_seen_at, access_expires_at, refresh_expires_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $7, $8, $9)
                """, session, credential.Organization, credential.Staff, device, tokens.AccessDigest,
                tokens.RefreshDigest, now, tokens.AccessExpiresAt, tokens.RefreshExpiresAt);
        }
        await Audit(db, tx, context, action, credential.Organization, credential.Staff, session, device,
            "succeeded", null, ct);
        return new("Bearer", tokens.AccessToken, tokens.AccessExpiresAt, tokens.RefreshToken,
            tokens.RefreshExpiresAt, session, device,
            new(credential.Staff, credential.DisplayName, credential.Owner), ownerExpires);
    }

    private static SessionTokens NewTokens(DateTime? refreshExpiresAt = null)
    {
        var now = DateTime.UtcNow;
        var access = MobileAuthentication.AccessPrefix + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var refresh = MobileAuthentication.RefreshPrefix + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(48));
        var refreshExpiry = refreshExpiresAt ?? now.Add(RefreshLifetime);
        return new(access, Digest(access), now.Add(AccessLifetime), refresh, Digest(refresh), refreshExpiry);
    }

    private static async Task<TimeSpan> RecordFailedLogin(NpgsqlConnection db, NpgsqlTransaction tx,
        byte[] loginDigest, Guid device, byte[] ipDigest, Credential? candidate, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            INSERT INTO mobile_login_rate_limits (login_digest, device_id, ip_digest, failed_attempts, last_failed_at)
            VALUES ($1, $2, $3, 1, now())
            ON CONFLICT (login_digest, device_id, ip_digest) DO UPDATE
            SET failed_attempts = mobile_login_rate_limits.failed_attempts + 1, last_failed_at = now()
            RETURNING failed_attempts
            """, loginDigest, device, ipDigest);
        var attempts = (int)(await command.ExecuteScalarAsync(ct) ?? 1);
        var seconds = attempts switch { < 3 => 0, 3 => 2, 4 => 10, 5 => 30, 6 => 120, _ => 300 };
        var lockedUntil = DateTime.UtcNow.AddSeconds(seconds);
        await db.Run(tx, ct, """
            UPDATE mobile_login_rate_limits SET locked_until = $4
            WHERE (login_digest, device_id, ip_digest) = ($1, $2, $3)
            """, loginDigest, device, ipDigest, lockedUntil);
        if (candidate is not null)
            await db.Run(tx, ct, """
                UPDATE employee_credentials SET failed_attempts = failed_attempts + 1,
                    locked_until = GREATEST(COALESCE(locked_until, '-infinity'::timestamptz), $3)
                WHERE (org_id, staff_id) = ($1, $2)
                """, candidate.Organization, candidate.Staff, lockedUntil);
        return TimeSpan.FromSeconds(seconds);
    }

    private static async Task RevokeStaffMobileSessions(NpgsqlConnection db, NpgsqlTransaction tx, Guid organization,
        Guid staff, string reason, CancellationToken ct)
    {
        await db.Run(tx, ct, """
            UPDATE mobile_sessions SET revoked_at = now(), revoked_reason = $3
            WHERE (org_id, staff_id) = ($1, $2) AND revoked_at IS NULL
            """, organization, staff, reason);
        await db.Run(tx, ct, """
            UPDATE operator_sessions SET revoked_at = now(), revoked_reason = $3
            WHERE (org_id, staff_id) = ($1, $2) AND host_issuer = $4 AND revoked_at IS NULL
            """, organization, staff, reason, MobileAuthentication.Issuer);
    }

    private static Task<int> RevokeDeviceOperatorSessions(NpgsqlConnection db, NpgsqlTransaction tx, Guid device,
        Guid staff, string reason, CancellationToken ct) => db.Run(tx, ct, """
            UPDATE operator_sessions SET revoked_at = now(), revoked_reason = $3
            WHERE device_id = $1 AND staff_id = $2 AND host_issuer = $4 AND revoked_at IS NULL
            """, device, staff, reason, MobileAuthentication.Issuer);

    private static Task<int> Audit(NpgsqlConnection db, NpgsqlTransaction tx, HttpContext context, string action,
        Guid? organization, Guid? staff, Guid? session, Guid device, string result, string? reason,
        CancellationToken ct) => db.Run(tx, ct, """
            INSERT INTO mobile_auth_audit_events (action, organization_id, staff_id, session_id, device_id,
                trace_id, result, reason)
            VALUES ($1, NULLIF($2, '00000000-0000-0000-0000-000000000000'::uuid),
                NULLIF($3, '00000000-0000-0000-0000-000000000000'::uuid),
                NULLIF($4, '00000000-0000-0000-0000-000000000000'::uuid), $5, $6, $7, NULLIF($8, ''))
            """, action, organization ?? Guid.Empty, staff ?? Guid.Empty, session ?? Guid.Empty, device,
            context.TraceIdentifier, result, reason ?? "");

    internal static (string Display, string Normalized) ValidateLogin(string? value)
    {
        var display = value?.Normalize(NormalizationForm.FormKC).Trim() ?? "";
        if (!LoginPattern().IsMatch(display)) throw new PosError(400, "invalid_mobile_login");
        return (display, display.ToUpperInvariant());
    }

    private static string ValidateReason(string? value)
    {
        var reason = value?.Trim() ?? "";
        if (reason.Length is < 1 or > 500 || reason.Any(char.IsControl))
            throw new PosError(400, "validation_failed");
        return reason;
    }

    private static byte[] Digest(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));
}
