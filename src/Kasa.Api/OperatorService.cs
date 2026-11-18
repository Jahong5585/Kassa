using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Npgsql;

namespace Kasa.Api;

internal sealed class OperatorService(NpgsqlDataSource source, OperatorPasswordHasher passwords)
{
    private static readonly string[] OwnerRoles = ["admin", "cashier", "receiver"];
    private static readonly string[] EmployeeRoles = ["admin", "cashier", "receiver"];
    private sealed record HostScope(Guid Organization, Guid Store, Guid Owner, string OrganizationName,
        string StoreName, string TimeZone, string Issuer, string Subject);

    public async Task<MyStorePage> ListMyStores(HttpContext context, int offset, CancellationToken ct)
    {
        if (offset < 0)
            throw new PosError(400, "validation_failed");
        var (_, subject) = Access.ReadHost(context);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT o.id, o.name, st.id, st.name, st.time_zone,
                   EXISTS (SELECT 1 FROM staff_store_roles r
                           WHERE (r.org_id, r.store_id, r.staff_id, r.role) = (s.org_id, ss.store_id, s.id, 'owner')),
                   s.active AND ss.active
            FROM staff s
            JOIN staff_stores ss ON (ss.org_id, ss.staff_id) = (s.org_id, s.id)
            JOIN stores st ON (st.org_id, st.id) = (ss.org_id, ss.store_id)
            JOIN organizations o ON o.id = st.org_id
            WHERE s.subject = $1 AND s.identity_kind = 'oidc' AND s.active AND ss.active
            ORDER BY lower(o.name), o.id, lower(st.name), st.id
            LIMIT 51 OFFSET $2
            """, db);
        command.Parameters.AddWithValue(subject);
        command.Parameters.AddWithValue(offset);
        await using var rows = await command.ExecuteReaderAsync(ct);
        var items = new List<MyStoreItem>();
        while (await rows.ReadAsync(ct))
            items.Add(new(rows.GetGuid(0), rows.GetString(1), rows.GetGuid(2), rows.GetString(3), rows.GetString(4),
                UtcOffsetMinutes(rows.GetString(4)), rows.GetBoolean(5), rows.GetBoolean(6)));
        var nextOffset = items.Count > 50 ? offset + 50 : (int?)null;
        if (nextOffset.HasValue)
            items.RemoveAt(items.Count - 1);
        return new(items, nextOffset);
    }

    private static int UtcOffsetMinutes(string timeZone) => checked((int)TimeZoneInfo
        .FindSystemTimeZoneById(timeZone).GetUtcOffset(DateTimeOffset.UtcNow).TotalMinutes);

    public async Task<StoreOperatorPage> ListOperators(HttpContext context, Guid store, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var host = await RequireOwner(db, tx, context, store, ct);
        var items = await ReadOperators(db, tx, host.Organization, store, ct);
        return new(store, items);
    }

    public async Task<OperatorSessionCreated> CreateSession(HttpContext context, Guid store,
        CreateOperatorSession request, CancellationToken ct)
    {
        if (request.OperatorId == Guid.Empty || request.DeviceId == Guid.Empty)
            throw new PosError(400, "validation_failed");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var host = await RequireOwner(db, tx, context, store, ct);
        var candidate = await ReadOperator(db, tx, host.Organization, store, request.OperatorId, includeCredential: true, ct);
        if (candidate is null || !candidate.Item.Active)
        {
            await Audit(db, tx, context, "operator.login_failed", host.Organization, store, host.Owner, null,
                null, request.DeviceId, null, "failed", null, ct);
            await tx.CommitAsync(ct);
            throw new PosError(401, "operator_credentials_invalid");
        }

        if (candidate.Item.IsOwner)
        {
            if (candidate.Item.OperatorId != host.Owner || request.Password is not null)
                throw new PosError(401, "operator_credentials_invalid");
        }
        else
        {
            if (request.Password is null || candidate.PasswordHash is null)
                throw new PosError(401, "operator_credentials_invalid");
            var ipDigest = SHA256.HashData(Encoding.UTF8.GetBytes(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown"));
            var retry = await ReadLoginLock(db, tx, host.Organization, store, candidate.Item.OperatorId,
                request.DeviceId, ipDigest, candidate.LockedUntil, ct);
            if (retry > TimeSpan.Zero)
            {
                context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retry.TotalSeconds)).ToString();
                throw new PosError(429, "operator_login_rate_limited");
            }
            if (!await passwords.Verify(request.Password, candidate.PasswordHash, ct))
            {
                var delay = await RecordFailedLogin(db, tx, host.Organization, store, candidate.Item.OperatorId,
                    request.DeviceId, ipDigest, ct);
                await Audit(db, tx, context, "operator.login_failed", host.Organization, store, host.Owner,
                    candidate.Item.OperatorId, null, request.DeviceId, null, "failed", null, ct);
                await tx.CommitAsync(ct);
                if (delay >= TimeSpan.FromSeconds(30))
                    context.Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(delay.TotalSeconds)).ToString();
                throw new PosError(delay >= TimeSpan.FromSeconds(30) ? 429 : 401,
                    delay >= TimeSpan.FromSeconds(30) ? "operator_login_rate_limited" : "operator_credentials_invalid");
            }
            await db.Run(tx, ct, """
                UPDATE employee_credentials SET failed_attempts = 0, locked_until = NULL
                WHERE (org_id, staff_id) = ($1, $2)
                """, host.Organization, candidate.Item.OperatorId);
            await db.Run(tx, ct, """
                DELETE FROM operator_login_rate_limits
                WHERE (org_id, store_id, staff_id, device_id, ip_digest) = ($1, $2, $3, $4, $5)
                """, host.Organization, store, candidate.Item.OperatorId, request.DeviceId, ipDigest);
        }

        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var sessionId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var idle = now.AddMinutes(candidate.Item.Roles.Contains("admin", StringComparer.Ordinal) ? 15 : 30);
        var absolute = now.AddHours(12);
        Guid? registerId = null;
        if (candidate.Item.Roles.Contains("cashier", StringComparer.Ordinal))
        {
            await using var boundRegister = db.Sql(tx, """
                SELECT b.register_id
                FROM register_device_bindings b
                JOIN registers r ON (r.org_id, r.store_id, r.id) = (b.org_id, b.store_id, b.register_id) AND r.active
                JOIN cashier_registers a ON (a.org_id, a.store_id, a.register_id) = (b.org_id, b.store_id, b.register_id)
                WHERE (b.org_id, b.store_id, b.device_id) = ($1, $2, $3)
                  AND b.host_issuer = $4 AND b.host_subject = $5 AND b.revoked_at IS NULL
                  AND a.staff_id = $6
                """, host.Organization, store, request.DeviceId, host.Issuer, host.Subject, candidate.Item.OperatorId);
            var value = await boundRegister.ExecuteScalarAsync(ct);
            if (value is Guid assignedRegister) registerId = assignedRegister;
        }
        await db.Run(tx, ct, """
            INSERT INTO operator_sessions (id, org_id, store_id, staff_id, host_issuer, host_subject, device_id,
                register_id, token_digest, created_at, last_seen_at, idle_expires_at, absolute_expires_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7,
                NULLIF($8, '00000000-0000-0000-0000-000000000000'::uuid), $9, $10, $10, $11, $12)
            """, sessionId, host.Organization, store, candidate.Item.OperatorId, host.Issuer, host.Subject,
            request.DeviceId, registerId ?? Guid.Empty, digest, now, idle, absolute);
        if (candidate.Item.IsOwner)
            await Audit(db, tx, context, "oidc.owner_login_succeeded", host.Organization, store, host.Owner,
                host.Owner, null, request.DeviceId, null, "succeeded", null, ct);
        await Audit(db, tx, context, "operator.login_succeeded", host.Organization, store, host.Owner,
            candidate.Item.OperatorId, null, request.DeviceId, null, "succeeded", null, ct);
        await tx.CommitAsync(ct);
        return new(token, sessionId, host.Organization, store, candidate.Item, idle, absolute, registerId);
    }

    public async Task<OperatorRegisterPage> ListCurrentRegisters(HttpContext context, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var session = await Access.RequireOperatorSession(db, tx, context, ct);
        if (!session.Owner && !session.AssignedRoles.Contains("cashier", StringComparer.Ordinal))
            return new([]);
        var (issuer, subject) = Access.ReadHost(context);
        await using var command = db.Sql(tx, """
            SELECT r.id, r.name, r.active, r.version, b.id
            FROM registers r
            JOIN cashier_registers a ON (a.org_id, a.store_id, a.register_id) = (r.org_id, r.store_id, r.id)
            LEFT JOIN register_device_bindings b
              ON (b.org_id, b.store_id, b.register_id, b.device_id) = (r.org_id, r.store_id, r.id, $4)
             AND b.host_issuer = $5 AND b.host_subject = $6 AND b.revoked_at IS NULL
            WHERE (r.org_id, r.store_id, a.staff_id) = ($1, $2, $3) AND r.active
            ORDER BY lower(r.name), r.id
            """, session.Organization, session.Store, session.Staff, session.DeviceId, issuer, subject);
        await using var rows = await command.ExecuteReaderAsync(ct);
        var items = new List<OperatorRegisterItem>();
        while (await rows.ReadAsync(ct))
            items.Add(new(rows.GetGuid(0), rows.GetString(1), rows.GetBoolean(2), rows.GetInt32(3), true,
                !rows.IsDBNull(4), rows.IsDBNull(4) ? null : rows.GetGuid(4)));
        return new(items);
    }

    public async Task<RegisterDeviceBindingCreated> BindRegisterDevice(HttpContext context, Guid store, Guid register,
        CreateRegisterDeviceBinding request, CancellationToken ct)
    {
        var deviceName = request.DeviceName?.Trim() ?? "";
        if (deviceName.Length is < 1 or > 120 || deviceName.Any(char.IsControl))
            throw new PosError(400, "invalid_device_name");
        var reason = ValidateReason(request.Reason);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var owner = await RequireOwnerAdmin(db, tx, context, store, ct);
        await Access.LockRegister(db, tx, register, ct);
        await using (var target = db.Sql(tx, """
            SELECT active FROM registers WHERE (org_id, store_id, id) = ($1, $2, $3)
            """, owner.Organization, store, register))
        {
            var active = await target.ExecuteScalarAsync(ct);
            if (active is null) throw new PosError(404, "register_not_found");
            if (active is not true) throw new PosError(409, "register_disabled");
        }

        Guid? previousBinding = null;
        Guid? previousRegister = null;
        Guid? previousStore = null;
        await using (var existing = db.Sql(tx, """
            SELECT id, store_id, register_id FROM register_device_bindings
            WHERE (org_id, device_id) = ($1, $2) AND revoked_at IS NULL FOR UPDATE
            """, owner.Organization, owner.DeviceId))
        await using (var rows = await existing.ExecuteReaderAsync(ct))
        {
            if (await rows.ReadAsync(ct))
            {
                previousBinding = rows.GetGuid(0);
                previousStore = rows.GetGuid(1);
                previousRegister = rows.GetGuid(2);
            }
        }
        if (previousRegister is { } oldRegister &&
            (oldRegister != register || previousStore != store))
        {
            await using var shift = db.Sql(tx, """
                SELECT 1 FROM shifts WHERE (org_id, store_id, register_id) = ($1, $2, $3) AND closed_at IS NULL
                """, owner.Organization, previousStore, oldRegister);
            if (await shift.ExecuteScalarAsync(ct) is not null)
                throw new PosError(409, "register_device_binding_shift_open");
        }
        if (previousBinding is { } oldBinding)
            await db.Run(tx, ct, """
                UPDATE register_device_bindings
                SET revoked_at = now(), revoked_by = $3, revoked_reason = 'device_rebound', version = version + 1
                WHERE (org_id, id) = ($1, $2) AND revoked_at IS NULL
                """, owner.Organization, oldBinding, owner.Staff);

        var token = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var bindingId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var (issuer, subject) = Access.ReadHost(context);
        await db.Run(tx, ct, """
            INSERT INTO register_device_bindings (id, org_id, store_id, register_id, device_id, device_name,
                token_digest, host_issuer, host_subject, created_by, created_at, last_seen_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $11)
            """, bindingId, owner.Organization, store, register, owner.DeviceId, deviceName, digest, issuer, subject,
            owner.Staff, now);
        await Audit(db, tx, context, "register.device_bound", owner.Organization, store, owner.Staff, null,
            "admin", owner.DeviceId, register, "succeeded", reason, ct);
        await tx.CommitAsync(ct);
        return new(bindingId, store, register, owner.DeviceId, deviceName, token, 1, now);
    }

    public async Task<IReadOnlyList<RegisterDeviceBindingItem>> ListRegisterDeviceBindings(HttpContext context,
        Guid store, Guid register, int offset, int limit, CancellationToken ct)
    {
        if (offset < 0 || limit is < 1 or > 100) throw new PosError(400, "validation_failed");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var owner = await RequireOwnerAdmin(db, tx, context, store, ct);
        await using var command = db.Sql(tx, """
            SELECT b.id, b.store_id, b.register_id, b.device_id, b.device_name, b.version,
                   b.created_at, b.last_seen_at, b.revoked_at
            FROM register_device_bindings b
            WHERE (b.org_id, b.store_id, b.register_id) = ($1, $2, $3)
            ORDER BY b.created_at DESC, b.id LIMIT $4 OFFSET $5
            """, owner.Organization, store, register, limit, offset);
        await using var rows = await command.ExecuteReaderAsync(ct);
        var result = new List<RegisterDeviceBindingItem>();
        while (await rows.ReadAsync(ct))
            result.Add(new(rows.GetGuid(0), rows.GetGuid(1), rows.GetGuid(2), rows.GetGuid(3), rows.GetString(4),
                rows.GetInt32(5), rows.GetDateTime(6), rows.GetDateTime(7), rows.IsDBNull(8) ? null : rows.GetDateTime(8)));
        return result;
    }

    public async Task RevokeRegisterDeviceBinding(HttpContext context, Guid store, Guid register, Guid binding,
        RevokeRegisterDeviceBinding request, CancellationToken ct)
    {
        if (request.ExpectedVersion < 1) throw new PosError(400, "validation_failed");
        var reason = ValidateReason(request.Reason);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var owner = await RequireOwnerAdmin(db, tx, context, store, ct);
        await using var command = db.Sql(tx, """
            UPDATE register_device_bindings
            SET revoked_at = now(), revoked_by = $6, revoked_reason = $7, version = version + 1
            WHERE (org_id, store_id, register_id, id, version) = ($1, $2, $3, $4, $5)
              AND revoked_at IS NULL
            RETURNING device_id
            """, owner.Organization, store, register, binding, request.ExpectedVersion, owner.Staff, reason);
        var device = await command.ExecuteScalarAsync(ct);
        if (device is not Guid deviceId) throw new PosError(409, "register_device_binding_version_conflict");
        await db.Run(tx, ct, """
            UPDATE operator_sessions SET register_id = NULL
            WHERE (org_id, store_id, device_id, register_id) = ($1, $2, $3, $4) AND revoked_at IS NULL
            """, owner.Organization, store, deviceId, register);
        await Audit(db, tx, context, "register.device_binding_revoked", owner.Organization, store, owner.Staff, null,
            "admin", deviceId, register, "succeeded", reason, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<CurrentOperatorSession> Current(HttpContext context, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var session = await Access.RequireOperatorSession(db, tx, context, ct);
        var row = await ReadOperator(db, tx, session.Organization, session.Store, session.Staff, false, ct)
                  ?? throw new PosError(401, "operator_session_invalid");
        await using var store = db.Sql(tx, "SELECT name FROM stores WHERE (org_id, id) = ($1, $2)",
            session.Organization, session.Store);
        var name = (string)(await store.ExecuteScalarAsync(ct) ?? throw new PosError(404, "store_not_found"));
        await tx.CommitAsync(ct);
        return new(session.SessionId, new(session.Organization, session.Store, name, session.TimeZone), row.Item,
            session.CreatedAt, session.IdleExpiresAt, session.AbsoluteExpiresAt, session.RegisterId);
    }

    public async Task Logout(HttpContext context, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var session = await Access.RequireOperatorSession(db, tx, context, ct);
        await db.Run(tx, ct, """
            UPDATE operator_sessions SET revoked_at = now(), revoked_reason = 'logout'
            WHERE id = $1 AND revoked_at IS NULL
            """, session.SessionId);
        await Audit(db, tx, context, "operator.logout", session.Organization, session.Store, session.Staff,
            session.Staff, context.Request.Headers["X-Active-Role"].ToString(), session.DeviceId, session.RegisterId,
            "succeeded", null, ct);
        await tx.CommitAsync(ct);
    }

    public async Task ChangePassword(HttpContext context, ChangeOperatorPassword request, CancellationToken ct)
    {
        OperatorPasswordHasher.Validate(request.NewPassword);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var session = await Access.RequireOperatorSession(db, tx, context, ct);
        await using var credential = db.Sql(tx, """
            SELECT password_hash FROM employee_credentials
            WHERE (org_id, staff_id) = ($1, $2) FOR UPDATE
            """, session.Organization, session.Staff);
        if (await credential.ExecuteScalarAsync(ct) is not string currentHash ||
            !await passwords.Verify(request.CurrentPassword, currentHash, ct))
            throw new PosError(401, "operator_credentials_invalid");
        var hash = await passwords.Hash(request.NewPassword, ct);
        await db.Run(tx, ct, """
            UPDATE employee_credentials SET password_hash = $3, password_changed_at = now(),
                failed_attempts = 0, locked_until = NULL, must_change_password = false, version = version + 1
            WHERE (org_id, staff_id) = ($1, $2)
            """, session.Organization, session.Staff, hash);
        await db.Run(tx, ct, """
            UPDATE staff SET version = version + 1, updated_at = now() WHERE (org_id, id) = ($1, $2)
            """, session.Organization, session.Staff);
        await db.Run(tx, ct, """
            UPDATE operator_sessions SET revoked_at = now(), revoked_reason = 'password_changed'
            WHERE (org_id, staff_id) = ($1, $2) AND id <> $3 AND revoked_at IS NULL
            """, session.Organization, session.Staff, session.SessionId);
        await Audit(db, tx, context, "operator.session_revoked", session.Organization, session.Store, session.Staff,
            session.Staff, context.Request.Headers["X-Active-Role"].ToString(), session.DeviceId, session.RegisterId,
            "succeeded", "password_changed", ct);
        await tx.CommitAsync(ct);
    }

    public async Task<EmployeePage> ListEmployees(HttpContext context, Guid store, int offset, int limit,
        CancellationToken ct)
    {
        if (offset < 0 || limit is < 1 or > 100) throw new PosError(400, "validation_failed");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var owner = await RequireOwnerAdmin(db, tx, context, store, ct);
        await using var command = db.Sql(tx, """
            SELECT s.id, s.first_name, s.last_name, s.display_name,
                   array_agg(r.role ORDER BY r.role), s.active AND m.active, s.version, s.created_at, s.updated_at
            FROM staff s JOIN staff_stores m ON (m.org_id, m.staff_id) = (s.org_id, s.id)
            JOIN staff_store_roles r ON (r.org_id, r.store_id, r.staff_id) = (m.org_id, m.store_id, m.staff_id)
            WHERE (m.org_id, m.store_id) = ($1, $2) AND s.identity_kind = 'local'
              AND NOT EXISTS (SELECT 1 FROM staff_store_roles x
                              WHERE (x.org_id, x.store_id, x.staff_id, x.role) = (m.org_id, m.store_id, m.staff_id, 'owner'))
            GROUP BY s.org_id, s.id, m.active ORDER BY lower(s.display_name), s.id LIMIT $3 OFFSET $4
            """, owner.Organization, store, limit + 1, offset);
        await using var rows = await command.ExecuteReaderAsync(ct);
        var items = new List<EmployeeItem>();
        while (await rows.ReadAsync(ct)) items.Add(ReadEmployee(rows));
        var next = items.Count > limit ? offset + limit : (int?)null;
        if (items.Count > limit) items.RemoveAt(limit);
        return new(items, next);
    }

    public async Task<EmployeeItem> CreateEmployee(HttpContext context, Guid store, CreateEmployee request,
        CancellationToken ct)
    {
        if (!Guid.TryParse(context.Request.Headers["Idempotency-Key"].ToString(), out var key))
            throw new PosError(400, "validation_failed");
        if (request.EmployeeId == Guid.Empty) throw new PosError(400, "validation_failed");
        var (first, last, display) = ValidateName(request.FirstName, request.LastName);
        var roles = ValidateRoles(request.Roles);
        var reason = ValidateReason(request.Reason);
        OperatorPasswordHasher.Validate(request.Password);
        var digest = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request));
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var owner = await RequireOwnerAdmin(db, tx, context, store, ct);
        await using (var replay = db.Sql(tx, """
            SELECT request_digest, response_body::text FROM employee_idempotency
            WHERE (org_id, store_id, idempotency_key) = ($1, $2, $3)
            """, owner.Organization, store, key))
        await using (var rows = await replay.ExecuteReaderAsync(ct))
        {
            if (await rows.ReadAsync(ct))
            {
                if (!CryptographicOperations.FixedTimeEquals(rows.GetFieldValue<byte[]>(0), digest))
                    throw new PosError(409, "idempotency_conflict");
                return JsonSerializer.Deserialize<EmployeeItem>(rows.GetString(1), WebJson.Options)
                       ?? throw new PosError(409, "idempotency_conflict");
            }
        }
        var hash = await passwords.Hash(request.Password, ct);
        var inserted = await db.Run(tx, ct, """
            INSERT INTO staff (org_id, id, subject, role, active, first_name, last_name, display_name, identity_kind)
            VALUES ($1, $2, NULL, $3, true, $4, $5, $6, 'local') ON CONFLICT DO NOTHING
            """, owner.Organization, request.EmployeeId, roles[0], first, last, display);
        if (inserted != 1) throw new PosError(409, "employee_already_exists");
        await db.Run(tx, ct, """
            INSERT INTO staff_stores (org_id, staff_id, store_id, active) VALUES ($1, $2, $3, true)
            """, owner.Organization, request.EmployeeId, store);
        await db.Run(tx, ct, """
            INSERT INTO employee_credentials (org_id, staff_id, password_hash) VALUES ($1, $2, $3)
            """, owner.Organization, request.EmployeeId, hash);
        foreach (var role in roles)
        {
            await db.Run(tx, ct, """
                INSERT INTO staff_roles (org_id, staff_id, role) VALUES ($1, $2, $3) ON CONFLICT DO NOTHING
                """, owner.Organization, request.EmployeeId, role);
            await db.Run(tx, ct, """
                INSERT INTO staff_store_roles (org_id, store_id, staff_id, role, created_by)
                VALUES ($1, $2, $3, $4, $5)
                ON CONFLICT (org_id, store_id, staff_id, role)
                DO UPDATE SET created_by = EXCLUDED.created_by
                """, owner.Organization, store, request.EmployeeId, role, owner.Staff);
        }
        var result = await ReadEmployee(db, tx, owner.Organization, store, request.EmployeeId, ct);
        var body = JsonSerializer.Serialize(result, WebJson.Options);
        await db.Run(tx, ct, """
            INSERT INTO employee_idempotency (org_id, store_id, idempotency_key, request_digest, employee_id, response_body)
            VALUES ($1, $2, $3, $4, $5, $6::jsonb)
            """, owner.Organization, store, key, digest, request.EmployeeId, body);
        await Audit(db, tx, context, "employee.created", owner.Organization, store, owner.Staff,
            request.EmployeeId, "admin", owner.DeviceId, null, "succeeded", reason, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    public Task<EmployeeItem> UpdateProfile(HttpContext context, Guid store, Guid employee,
        UpdateEmployeeProfile request, CancellationToken ct) => ChangeEmployee(context, store, employee,
            request.ExpectedVersion, request.Reason, "employee.profile_updated", async (db, tx, owner) =>
            {
                var (first, last, display) = ValidateName(request.FirstName, request.LastName);
                return await db.Run(tx, ct, """
                    UPDATE staff SET first_name = $4, last_name = $5, display_name = $6,
                        version = version + 1, updated_at = now()
                    WHERE (org_id, id, version) = ($1, $2, $3) AND identity_kind = 'local'
                    """, owner.Organization, employee, request.ExpectedVersion, first, last, display);
            }, revoke: false, ct);

    public Task<EmployeeItem> UpdateRoles(HttpContext context, Guid store, Guid employee,
        UpdateEmployeeRoles request, CancellationToken ct)
    {
        var roles = ValidateRoles(request.Roles);
        return ChangeEmployee(context, store, employee, request.ExpectedVersion, request.Reason,
            "employee.roles_updated", async (db, tx, owner) =>
            {
                var changed = await db.Run(tx, ct, """
                    UPDATE staff SET role = $4, version = version + 1, updated_at = now()
                    WHERE (org_id, id, version) = ($1, $2, $3) AND identity_kind = 'local'
                    """, owner.Organization, employee, request.ExpectedVersion, roles[0]);
                if (changed != 1) return changed;
                await db.Run(tx, ct, "DELETE FROM staff_store_roles WHERE (org_id, store_id, staff_id) = ($1, $2, $3)",
                    owner.Organization, store, employee);
                foreach (var role in roles)
                    await db.Run(tx, ct, """
                        INSERT INTO staff_store_roles (org_id, store_id, staff_id, role, created_by)
                        VALUES ($1, $2, $3, $4, $5)
                        """, owner.Organization, store, employee, role, owner.Staff);
                await SyncLegacyRoles(db, tx, owner.Organization, employee, ct);
                return changed;
            }, revoke: true, ct);
    }

    public Task<EmployeeItem> ResetPassword(HttpContext context, Guid store, Guid employee,
        ResetEmployeePassword request, CancellationToken ct) => ChangeEmployee(context, store, employee,
            request.ExpectedVersion, request.Reason, "employee.password_reset", async (db, tx, owner) =>
            {
                OperatorPasswordHasher.Validate(request.NewPassword);
                var hash = await passwords.Hash(request.NewPassword, ct);
                var changed = await db.Run(tx, ct, """
                    UPDATE staff SET version = version + 1, updated_at = now()
                    WHERE (org_id, id, version) = ($1, $2, $3) AND identity_kind = 'local'
                    """, owner.Organization, employee, request.ExpectedVersion);
                if (changed == 1)
                    await db.Run(tx, ct, """
                        UPDATE employee_credentials SET password_hash = $3, password_changed_at = now(),
                            failed_attempts = 0, locked_until = NULL, must_change_password = false, version = version + 1
                        WHERE (org_id, staff_id) = ($1, $2)
                        """, owner.Organization, employee, hash);
                return changed;
            }, revoke: true, ct);

    public Task<EmployeeItem> Disable(HttpContext context, Guid store, Guid employee, ChangeEmployeeState request,
        CancellationToken ct) => SetState(context, store, employee, request, false, "employee.disabled", ct);

    public Task<EmployeeItem> Enable(HttpContext context, Guid store, Guid employee, ChangeEmployeeState request,
        CancellationToken ct) => SetState(context, store, employee, request, true, "employee.enabled", ct);

    public async Task<RegisterDevice> UpdateRegisterCashiers(HttpContext context, Guid store, Guid register,
        UpdateRegisterCashiers request, CancellationToken ct)
    {
        if (request.ExpectedVersion < 1 || request.CashierIds.Length < 1 || request.CashierIds.Distinct().Count() != request.CashierIds.Length)
            throw new PosError(400, "validation_failed");
        var reason = ValidateReason(request.Reason);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var owner = await RequireOwnerAdmin(db, tx, context, store, ct);
        await Access.LockRegister(db, tx, register, ct);
        await using var version = db.Sql(tx, """
            UPDATE registers SET version = version + 1
            WHERE (org_id, store_id, id, version) = ($1, $2, $3, $4) AND active RETURNING name, version
            """, owner.Organization, store, register, request.ExpectedVersion);
        await using var rows = await version.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct)) throw new PosError(409, "register_version_conflict");
        var name = rows.GetString(0); var newVersion = rows.GetInt32(1); await rows.DisposeAsync();
        foreach (var cashier in request.CashierIds)
        {
            await using var allowed = db.Sql(tx, """
                SELECT 1 FROM staff s JOIN staff_stores m ON (m.org_id, m.staff_id) = (s.org_id, s.id)
                JOIN staff_store_roles r ON (r.org_id, r.store_id, r.staff_id) = (m.org_id, m.store_id, m.staff_id)
                WHERE (m.org_id, m.store_id, m.staff_id) = ($1, $2, $3) AND s.active AND m.active
                  AND r.role IN ('cashier', 'owner')
                """, owner.Organization, store, cashier);
            if (await allowed.ExecuteScalarAsync(ct) is null) throw new PosError(404, "employee_not_found");
        }
        await db.Run(tx, ct, "DELETE FROM cashier_registers WHERE (org_id, store_id, register_id) = ($1, $2, $3)",
            owner.Organization, store, register);
        foreach (var cashier in request.CashierIds)
            await db.Run(tx, ct, """
                INSERT INTO cashier_registers (org_id, staff_id, store_id, register_id) VALUES ($1, $2, $3, $4)
                """, owner.Organization, cashier, store, register);
        await db.Run(tx, ct, """
            UPDATE operator_sessions os SET register_id = NULL
            WHERE (os.org_id, os.store_id, os.register_id) = ($1, $2, $3) AND os.revoked_at IS NULL
              AND NOT EXISTS (SELECT 1 FROM cashier_registers a
                              WHERE (a.org_id, a.store_id, a.register_id, a.staff_id) =
                                    (os.org_id, os.store_id, os.register_id, os.staff_id))
            """, owner.Organization, store, register);
        await Audit(db, tx, context, "register.cashiers_updated", owner.Organization, store, owner.Staff, null,
            "admin", owner.DeviceId, register, "succeeded", reason, ct);
        await tx.CommitAsync(ct);
        return new(register, name, true, newVersion, request.CashierIds);
    }

    private Task<EmployeeItem> SetState(HttpContext context, Guid store, Guid employee, ChangeEmployeeState request,
        bool active, string action, CancellationToken ct) => ChangeEmployee(context, store, employee,
            request.ExpectedVersion, request.Reason, action, async (db, tx, owner) =>
                await db.Run(tx, ct, """
                    UPDATE staff SET active = $4, version = version + 1, updated_at = now()
                    WHERE (org_id, id, version) = ($1, $2, $3) AND identity_kind = 'local' AND active <> $4
                    """, owner.Organization, employee, request.ExpectedVersion, active), revoke: !active, ct);

    private async Task<EmployeeItem> ChangeEmployee(HttpContext context, Guid store, Guid employee,
        int expectedVersion, string reasonValue, string action,
        Func<NpgsqlConnection, NpgsqlTransaction, OperatorSessionIdentity, Task<int>> change, bool revoke,
        CancellationToken ct)
    {
        if (employee == Guid.Empty || expectedVersion < 1) throw new PosError(400, "validation_failed");
        var reason = ValidateReason(reasonValue);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var owner = await RequireOwnerAdmin(db, tx, context, store, ct);
        if (!await IsEditableEmployee(db, tx, owner.Organization, store, employee, ct))
            throw new PosError(404, "employee_not_found");
        if (await change(db, tx, owner) != 1) throw new PosError(409, "employee_version_conflict");
        if (revoke)
        {
            await RevokeSessions(db, tx, owner.Organization, employee, action, ct);
            await Audit(db, tx, context, "operator.session_revoked", owner.Organization, store, owner.Staff,
                employee, "admin", owner.DeviceId, null, "succeeded", action, ct);
        }
        var result = await ReadEmployee(db, tx, owner.Organization, store, employee, ct);
        await Audit(db, tx, context, action, owner.Organization, store, owner.Staff, employee, "admin",
            owner.DeviceId, null, "succeeded", reason, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    private async Task<OperatorSessionIdentity> RequireOwnerAdmin(NpgsqlConnection db, NpgsqlTransaction tx,
        HttpContext context, Guid store, CancellationToken ct)
    {
        var session = await Access.RequireOperatorSession(db, tx, context, ct);
        if (session.Store != store) throw new PosError(409, "operator_store_mismatch");
        var role = context.Request.Headers["X-Active-Role"].ToString();
        if (string.IsNullOrEmpty(role)) throw new PosError(403, "active_role_required");
        if (role != "admin") throw new PosError(403, "active_role_denied");
        if (!session.Owner) throw new PosError(403, "owner_access_required");
        return session;
    }

    private async Task<HostScope> RequireOwner(NpgsqlConnection db, NpgsqlTransaction tx, HttpContext context,
        Guid store, CancellationToken ct)
    {
        var (issuer, subject) = Access.ReadHost(context);
        await using var command = db.Sql(tx, """
            SELECT s.org_id, s.id, o.name, st.name, st.time_zone
            FROM staff s JOIN staff_stores m ON (m.org_id, m.staff_id) = (s.org_id, s.id) AND m.active
            JOIN staff_store_roles r ON (r.org_id, r.store_id, r.staff_id) = (m.org_id, m.store_id, m.staff_id)
            JOIN stores st ON (st.org_id, st.id) = (m.org_id, m.store_id)
            JOIN organizations o ON o.id = st.org_id
            WHERE s.subject = $1 AND s.identity_kind = 'oidc' AND s.active AND m.store_id = $2 AND r.role = 'owner'
            """, subject, store);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct)) throw new PosError(403, "store_access_denied");
        return new(rows.GetGuid(0), store, rows.GetGuid(1), rows.GetString(2), rows.GetString(3), rows.GetString(4),
            issuer, subject);
    }

    private sealed record OperatorRow(OperatorItem Item, string? PasswordHash, DateTime? LockedUntil);

    private static async Task<List<OperatorItem>> ReadOperators(NpgsqlConnection db, NpgsqlTransaction tx, Guid org,
        Guid store, CancellationToken ct)
    {
        await using var command = db.Sql(tx, OperatorSql(false), org, store, Guid.Empty);
        await using var rows = await command.ExecuteReaderAsync(ct);
        var result = new List<OperatorItem>();
        while (await rows.ReadAsync(ct)) result.Add(ReadOperatorRow(rows, false).Item);
        return result.OrderBy(x => x.IsOwner ? 1 : 0).ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<OperatorRow?> ReadOperator(NpgsqlConnection db, NpgsqlTransaction tx, Guid org,
        Guid store, Guid staff, bool includeCredential, CancellationToken ct)
    {
        await using var command = db.Sql(tx, OperatorSql(true), org, store, staff);
        await using var rows = await command.ExecuteReaderAsync(ct);
        return await rows.ReadAsync(ct) ? ReadOperatorRow(rows, includeCredential) : null;
    }

    private static string OperatorSql(bool one) => $"""
        SELECT s.id, s.first_name, s.last_name, COALESCE(NULLIF(s.display_name, ''), s.subject, s.id::text),
               array_agg(r.role ORDER BY r.role), bool_or(r.role = 'owner'), s.identity_kind = 'local',
               s.active AND m.active, {(one ? "c.password_hash, c.locked_until" : "NULL::text, NULL::timestamptz")}
        FROM staff s JOIN staff_stores m ON (m.org_id, m.staff_id) = (s.org_id, s.id)
        JOIN staff_store_roles r ON (r.org_id, r.store_id, r.staff_id) = (m.org_id, m.store_id, m.staff_id)
        LEFT JOIN employee_credentials c ON (c.org_id, c.staff_id) = (s.org_id, s.id)
        WHERE (m.org_id, m.store_id) = ($1, $2)
          AND (s.identity_kind = 'local' OR EXISTS (SELECT 1 FROM staff_store_roles own
               WHERE (own.org_id, own.store_id, own.staff_id, own.role) = (m.org_id, m.store_id, m.staff_id, 'owner')))
          AND ({(!one).ToString().ToLowerInvariant()} OR s.id = $3)
        GROUP BY s.org_id, s.id, m.active, c.password_hash, c.locked_until
        """;

    private static OperatorRow ReadOperatorRow(NpgsqlDataReader rows, bool includeCredential)
    {
        var assigned = rows.GetFieldValue<string[]>(4);
        var owner = rows.GetBoolean(5);
        var roles = owner ? OwnerRoles : assigned.Where(EmployeeRoles.Contains).Distinct().Order().ToArray();
        return new(new(rows.GetGuid(0), rows.IsDBNull(1) ? null : rows.GetString(1),
                rows.IsDBNull(2) ? null : rows.GetString(2), rows.GetString(3), roles, owner,
                rows.GetBoolean(6), rows.GetBoolean(7)),
            includeCredential && !rows.IsDBNull(8) ? rows.GetString(8) : null,
            includeCredential && !rows.IsDBNull(9) ? rows.GetDateTime(9) : null);
    }

    private static async Task<EmployeeItem> ReadEmployee(NpgsqlConnection db, NpgsqlTransaction tx, Guid org,
        Guid store, Guid employee, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT s.id, s.first_name, s.last_name, s.display_name, array_agg(r.role ORDER BY r.role),
                   s.active AND m.active, s.version, s.created_at, s.updated_at
            FROM staff s JOIN staff_stores m ON (m.org_id, m.staff_id) = (s.org_id, s.id)
            JOIN staff_store_roles r ON (r.org_id, r.store_id, r.staff_id) = (m.org_id, m.store_id, m.staff_id)
            WHERE (m.org_id, m.store_id, m.staff_id) = ($1, $2, $3) AND s.identity_kind = 'local'
            GROUP BY s.org_id, s.id, m.active
            """, org, store, employee);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct)) throw new PosError(404, "employee_not_found");
        return ReadEmployee(rows);
    }

    private static EmployeeItem ReadEmployee(NpgsqlDataReader rows) => new(rows.GetGuid(0), rows.GetString(1),
        rows.GetString(2), rows.GetString(3), rows.GetFieldValue<string[]>(4), rows.GetBoolean(5), rows.GetInt32(6),
        rows.GetDateTime(7), rows.GetDateTime(8));

    private static async Task<bool> IsEditableEmployee(NpgsqlConnection db, NpgsqlTransaction tx, Guid org,
        Guid store, Guid employee, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT 1 FROM staff s JOIN staff_stores m ON (m.org_id, m.staff_id) = (s.org_id, s.id)
            WHERE (m.org_id, m.store_id, m.staff_id) = ($1, $2, $3) AND s.identity_kind = 'local'
              AND NOT EXISTS (SELECT 1 FROM staff_store_roles r WHERE
                  (r.org_id, r.store_id, r.staff_id, r.role) = (m.org_id, m.store_id, m.staff_id, 'owner'))
            """, org, store, employee);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<TimeSpan> ReadLoginLock(NpgsqlConnection db, NpgsqlTransaction tx, Guid org, Guid store,
        Guid staff, Guid device, byte[] ip, DateTime? credentialLock, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT GREATEST($6::timestamptz, locked_until) FROM operator_login_rate_limits
            WHERE (org_id, store_id, staff_id, device_id, ip_digest) = ($1, $2, $3, $4, $5)
            """, org, store, staff, device, ip, credentialLock ?? DateTime.UnixEpoch);
        var value = await command.ExecuteScalarAsync(ct);
        var until = value is DateTime time ? time : credentialLock;
        return until is { } locked && locked > DateTime.UtcNow ? locked - DateTime.UtcNow : TimeSpan.Zero;
    }

    private static async Task<TimeSpan> RecordFailedLogin(NpgsqlConnection db, NpgsqlTransaction tx, Guid org,
        Guid store, Guid staff, Guid device, byte[] ip, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            INSERT INTO operator_login_rate_limits (org_id, store_id, staff_id, device_id, ip_digest,
                failed_attempts, locked_until, last_failed_at)
            VALUES ($1, $2, $3, $4, $5, 1, NULL, now())
            ON CONFLICT (org_id, store_id, staff_id, device_id, ip_digest) DO UPDATE
            SET failed_attempts = operator_login_rate_limits.failed_attempts + 1, last_failed_at = now()
            RETURNING failed_attempts
            """, org, store, staff, device, ip);
        var attempts = (int)(await command.ExecuteScalarAsync(ct) ?? 1);
        var seconds = attempts switch { < 3 => 0, 3 => 2, 4 => 10, 5 => 30, 6 => 120, _ => 300 };
        var until = DateTime.UtcNow.AddSeconds(seconds);
        await db.Run(tx, ct, """
            UPDATE operator_login_rate_limits SET locked_until = $6
            WHERE (org_id, store_id, staff_id, device_id, ip_digest) = ($1, $2, $3, $4, $5)
            """, org, store, staff, device, ip, until);
        await db.Run(tx, ct, """
            UPDATE employee_credentials SET failed_attempts = failed_attempts + 1,
                locked_until = CASE WHEN $3 > COALESCE(locked_until, '-infinity') THEN $3 ELSE locked_until END
            WHERE (org_id, staff_id) = ($1, $2)
            """, org, staff, until);
        return TimeSpan.FromSeconds(seconds);
    }

    private static async Task RevokeSessions(NpgsqlConnection db, NpgsqlTransaction tx, Guid org, Guid staff,
        string reason, CancellationToken ct) => await db.Run(tx, ct, """
            UPDATE operator_sessions SET revoked_at = now(), revoked_reason = $3
            WHERE (org_id, staff_id) = ($1, $2) AND revoked_at IS NULL
            """, org, staff, reason);

    private static async Task SyncLegacyRoles(NpgsqlConnection db, NpgsqlTransaction tx, Guid org, Guid staff,
        CancellationToken ct)
    {
        await db.Run(tx, ct, "DELETE FROM staff_roles WHERE (org_id, staff_id) = ($1, $2) AND role <> 'owner'", org, staff);
        await db.Run(tx, ct, """
            INSERT INTO staff_roles (org_id, staff_id, role)
            SELECT DISTINCT org_id, staff_id, role FROM staff_store_roles
            WHERE (org_id, staff_id) = ($1, $2) AND role <> 'owner' ON CONFLICT DO NOTHING
            """, org, staff);
    }

    private static (string First, string Last, string Display) ValidateName(string? firstValue, string? lastValue)
    {
        var first = firstValue?.Trim() ?? ""; var last = lastValue?.Trim() ?? "";
        if (first.Length is < 1 or > 100 || last.Length is < 1 or > 100 ||
            first.Any(char.IsControl) || last.Any(char.IsControl))
            throw new PosError(400, "invalid_employee_name");
        return (first, last, $"{first} {last}");
    }

    private static string[] ValidateRoles(string[]? values)
    {
        var roles = values?.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() ?? [];
        if (roles.Length is < 1 or > 3 || roles.Any(role => !EmployeeRoles.Contains(role, StringComparer.Ordinal)))
            throw new PosError(400, "invalid_employee_roles");
        return roles;
    }

    private static string ValidateReason(string? value)
    {
        var reason = value?.Trim() ?? "";
        if (reason.Length is < 1 or > 500 || reason.Any(char.IsControl))
            throw new PosError(400, "validation_failed");
        return reason;
    }

    private static Task<int> Audit(NpgsqlConnection db, NpgsqlTransaction tx, HttpContext context, string action,
        Guid org, Guid store, Guid? actor, Guid? target, string? role, Guid? device, Guid? register, string result,
        string? reason, CancellationToken ct) => db.Run(tx, ct, """
            INSERT INTO operator_audit_events (action, organization_id, store_id, actor_staff_id, target_staff_id,
                active_role, device_id, register_id, trace_id, result, reason)
            VALUES ($1, $2, $3, NULLIF($4, '00000000-0000-0000-0000-000000000000'::uuid),
                NULLIF($5, '00000000-0000-0000-0000-000000000000'::uuid), NULLIF($6, ''),
                NULLIF($7, '00000000-0000-0000-0000-000000000000'::uuid),
                NULLIF($8, '00000000-0000-0000-0000-000000000000'::uuid), $9, $10, NULLIF($11, ''))
            """, action, org, store, actor ?? Guid.Empty, target ?? Guid.Empty, role ?? "", device ?? Guid.Empty,
            register ?? Guid.Empty, context.TraceIdentifier, result, reason ?? "");
}

internal static class WebJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
