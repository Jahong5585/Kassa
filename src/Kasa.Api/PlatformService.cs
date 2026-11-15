using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Npgsql;

namespace Kasa.Api;

internal sealed class PlatformService(NpgsqlDataSource source, IConfiguration configuration, LocalTestDeveloperMode mode,
    TemporaryPlatformDeveloperAccess temporaryDeveloper, IdentityProvisioningService identities)
{
    private sealed record Permission(string Issuer, string Subject, bool Write, bool Local);

    internal async Task<PlatformIdentity> Identity(HttpContext context, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var permission = await Require(db, tx, context, write: false, ct);
        return new(permission.Issuer, permission.Subject, true, permission.Write);
    }

    internal async Task<PlatformStorePage> ListStores(HttpContext context, int offset, CancellationToken ct)
    {
        ValidateOffset(offset);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await Require(db, tx, context, write: false, ct);
        await using var command = db.Sql(tx, """
            SELECT o.id, o.name, s.id, s.name, s.time_zone, s.platform_version, s.is_test,
              (SELECT st.subject FROM staff st
               JOIN staff_stores ss ON ss.org_id = st.org_id AND ss.staff_id = st.id AND ss.active
               JOIN staff_roles sr ON sr.org_id = st.org_id AND sr.staff_id = st.id AND sr.role = 'owner'
               WHERE st.org_id = o.id AND ss.store_id = s.id AND st.active
               ORDER BY st.id LIMIT 1)
            FROM organizations o JOIN stores s ON s.org_id = o.id
            WHERE o.is_test AND s.is_test
            ORDER BY lower(o.name), o.id, lower(s.name), s.id
            OFFSET $1 LIMIT 101
            """, offset);
        await using var rows = await command.ExecuteReaderAsync(ct);
        var result = new List<PlatformStoreItem>();
        while (await rows.ReadAsync(ct))
            result.Add(new(rows.GetGuid(0), rows.GetString(1), rows.GetGuid(2), rows.GetString(3), rows.GetString(4),
                rows.GetInt32(5), rows.GetBoolean(6), rows.IsDBNull(7) ? null : rows.GetString(7)));
        var next = result.Count > 100 ? offset + 100 : (int?)null;
        if (result.Count > 100) result.RemoveAt(100);
        return new(result, next);
    }

    internal async Task<PlatformStoreItem> CreateStore(HttpContext context, CreatePlatformStore request,
        CancellationToken ct)
    {
        if (request.OrganizationId == Guid.Empty || request.StoreId == Guid.Empty)
            throw new PosError(400, "invalid_platform_store_create");
        var organizationName = ValidateName(request.OrganizationName);
        var storeName = ValidateName(request.StoreName);
        var ownerSubject = string.IsNullOrWhiteSpace(request.OwnerSubject)
            ? null : ValidateSubject(request.OwnerSubject);
        var reason = ValidateReason(request.Reason);
        if (request.TimeZone != "Asia/Tashkent") throw new PosError(400, "invalid_time_zone");

        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var permission = await Require(db, tx, context, write: true, ct);

        await using (var organization = db.Sql(tx, "SELECT name, is_test FROM organizations WHERE id = $1",
                         request.OrganizationId))
        await using (var rows = await organization.ExecuteReaderAsync(ct))
        {
            if (await rows.ReadAsync(ct))
            {
                if (!rows.GetBoolean(1)) throw new PosError(409, "production_organization_forbidden");
                if (!string.Equals(rows.GetString(0), organizationName, StringComparison.Ordinal))
                    throw new PosError(409, "organization_name_conflict");
            }
            else
            {
                await rows.DisposeAsync();
                await db.Run(tx, ct, "INSERT INTO organizations (id, name, is_test) VALUES ($1, $2, true)",
                    request.OrganizationId, organizationName);
            }
        }

        if (await Exists(db, tx, "SELECT 1 FROM stores WHERE id = $1", request.StoreId, ct))
            throw new PosError(409, "store_id_conflict");
        await db.Run(tx, ct, """
            INSERT INTO stores (org_id, id, name, time_zone, is_test, platform_version)
            VALUES ($1, $2, $3, $4, true, 1)
            """, request.OrganizationId, request.StoreId, storeName, request.TimeZone);
        if (ownerSubject is not null)
            await EnsureOwner(db, tx, request.OrganizationId, request.StoreId, ownerSubject, ct);
        await Audit(db, tx, permission, "create_test_store", request.OrganizationId, request.StoreId, storeName,
            reason, null, storeName, ct);
        await tx.CommitAsync(ct);
        return new(request.OrganizationId, organizationName, request.StoreId, storeName, request.TimeZone, 1, true,
            ownerSubject);
    }

    internal async Task<PlatformStoreItem> RenameStore(HttpContext context, Guid organizationId, Guid storeId,
        RenamePlatformStore request, CancellationToken ct)
    {
        if (organizationId == Guid.Empty || storeId == Guid.Empty || request.ExpectedVersion < 1)
            throw new PosError(400, "invalid_platform_store_change");
        var name = ValidateName(request.Name);
        var reason = ValidateReason(request.Reason);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var permission = await Require(db, tx, context, write: true, ct);
        await using var update = db.Sql(tx, """
            UPDATE stores s SET name = $1, platform_version = platform_version + 1
            FROM organizations o
            WHERE s.org_id = $2 AND s.id = $3 AND s.platform_version = $4
              AND o.id = s.org_id AND s.is_test AND o.is_test
            RETURNING o.name, s.time_zone, s.platform_version,
              (SELECT store_name FROM platform_store_events
               WHERE org_id = $2 AND store_id = $3 ORDER BY id DESC LIMIT 1)
            """, name, organizationId, storeId, request.ExpectedVersion);
        await using var rows = await update.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct))
        {
            await rows.DisposeAsync();
            if (await Exists(db, tx, "SELECT 1 FROM stores WHERE org_id = $1 AND id = $2 AND is_test",
                    organizationId, storeId, ct))
                throw new PosError(409, "platform_store_version_conflict");
            throw new PosError(404, "test_store_not_found");
        }
        var organizationName = rows.GetString(0);
        var zone = rows.GetString(1);
        var version = rows.GetInt32(2);
        var previousName = rows.IsDBNull(3) ? null : rows.GetString(3);
        await rows.DisposeAsync();
        await Audit(db, tx, permission, "rename_test_store", organizationId, storeId, name, reason,
            previousName, name, ct);
        await using var owner = db.Sql(tx, """
            SELECT st.subject FROM staff st
            JOIN staff_stores ss ON ss.org_id = st.org_id AND ss.staff_id = st.id AND ss.active
            JOIN staff_roles sr ON sr.org_id = st.org_id AND sr.staff_id = st.id AND sr.role = 'owner'
            WHERE st.org_id = $1 AND ss.store_id = $2 AND st.active ORDER BY st.id LIMIT 1
            """, organizationId, storeId);
        var ownerSubject = await owner.ExecuteScalarAsync(ct) as string;
        await tx.CommitAsync(ct);
        return new(organizationId, organizationName, storeId, name, zone, version, true, ownerSubject);
    }

    internal async Task<PlatformOwnerAccount> CreateOwnerAccount(HttpContext context, Guid organizationId,
        Guid storeId, CreatePlatformOwnerAccount request, CancellationToken ct)
    {
        if (organizationId == Guid.Empty || storeId == Guid.Empty)
            throw new PosError(400, "invalid_platform_store_change");
        var reason = ValidateReason(request.Reason);
        var replaceSubject = string.IsNullOrWhiteSpace(request.ReplaceSubject)
            ? null : ValidateSubject(request.ReplaceSubject);

        await using (var preflightDb = await source.OpenConnectionAsync(ct))
        await using (var preflightTx = await preflightDb.BeginTransactionAsync(ct))
        {
            await Require(preflightDb, preflightTx, context, write: true, ct);
            await VerifyOwnerTarget(preflightDb, preflightTx, organizationId, storeId, replaceSubject, ct);
        }

        var provisioned = await identities.Create(request.Username, request.FirstName, request.LastName, request.Email, ct);
        try
        {
            await using var db = await source.OpenConnectionAsync(ct);
            await using var tx = await db.BeginTransactionAsync(ct);
            var permission = await Require(db, tx, context, write: true, ct);
            var storeName = await VerifyOwnerTarget(db, tx, organizationId, storeId, replaceSubject, ct);
            if (await Exists(db, tx, "SELECT 1 FROM staff WHERE org_id = $1 AND subject = $2",
                    organizationId, provisioned.Subject, ct))
                throw new PosError(409, "owner_subject_conflict");
            if (replaceSubject is null)
            {
                await EnsureOwner(db, tx, organizationId, storeId, provisioned.Subject, ct);
            }
            else
            {
                var updated = await db.Run(tx, ct, """
                    UPDATE staff st SET subject = $1
                    FROM staff_stores ss, staff_roles sr
                    WHERE st.org_id = $2 AND st.subject = $3
                      AND ss.org_id = st.org_id AND ss.staff_id = st.id AND ss.store_id = $4 AND ss.active
                      AND sr.org_id = st.org_id AND sr.staff_id = st.id AND sr.role = 'owner'
                    """, provisioned.Subject, organizationId, replaceSubject, storeId);
                if (updated != 1) throw new PosError(409, "platform_owner_changed");
            }
            await Audit(db, tx, permission, "assign_owner", organizationId, storeId, storeName, reason,
                null, null, ct);
            await tx.CommitAsync(ct);
            return new(organizationId, storeId, provisioned.Subject, provisioned.Username,
                provisioned.TemporaryPassword, true, ["UPDATE_PASSWORD"]);
        }
        catch
        {
            await identities.Delete(provisioned.Subject, CancellationToken.None);
            throw;
        }
    }

    internal async Task<PlatformAuditPage> AuditEvents(HttpContext context, int offset, CancellationToken ct)
    {
        ValidateOffset(offset);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await Require(db, tx, context, write: false, ct);
        await using var command = db.Sql(tx, """
            SELECT id, occurred_at, action, org_id, store_id, previous_name, new_name, reason
            FROM platform_store_events ORDER BY occurred_at DESC, id DESC OFFSET $1 LIMIT 101
            """, offset);
        await using var rows = await command.ExecuteReaderAsync(ct);
        var result = new List<PlatformAuditEvent>();
        while (await rows.ReadAsync(ct))
            result.Add(new(rows.GetInt64(0), rows.GetDateTime(1), rows.GetString(2), rows.GetGuid(3), rows.GetGuid(4),
                rows.IsDBNull(5) ? null : rows.GetString(5), rows.IsDBNull(6) ? null : rows.GetString(6),
                rows.GetString(7)));
        var next = result.Count > 100 ? offset + 100 : (int?)null;
        if (result.Count > 100) result.RemoveAt(100);
        return new(result, next);
    }

    internal async Task<IReadOnlyList<PlatformTestStore>> List(HttpContext context, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await Require(db, tx, context, write: false, ct);
        await using var command = db.Sql(tx, """
            SELECT o.id, s.id, s.name, s.time_zone, s.is_test
            FROM organizations o JOIN stores s ON s.org_id = o.id
            WHERE o.is_test AND s.is_test ORDER BY lower(s.name), s.id
            """);
        await using var rows = await command.ExecuteReaderAsync(ct);
        var result = new List<PlatformTestStore>();
        while (await rows.ReadAsync(ct))
            result.Add(new(rows.GetGuid(0), rows.GetGuid(1), rows.GetString(2), rows.GetString(3), rows.GetBoolean(4)));
        return result;
    }

    internal async Task<PlatformTestStore> Create(HttpContext context, CreatePlatformTestStore request, CancellationToken ct)
    {
        var name = ValidateName(request.Name);
        if (request.TimeZone is not (null or "Asia/Tashkent")) throw new PosError(400, "invalid_time_zone");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var permission = await Require(db, tx, context, write: true, ct);
        var organization = Guid.NewGuid();
        var store = Guid.NewGuid();
        await db.Run(tx, ct, "INSERT INTO organizations (id, name, is_test) VALUES ($1, $2, true)",
            organization, "TEST · " + name);
        await db.Run(tx, ct, """
            INSERT INTO stores (org_id, id, name, time_zone, is_test) VALUES ($1, $2, $3, 'Asia/Tashkent', true)
            """, organization, store, name);
        await Audit(db, tx, permission, "create_test_store", organization, store, name,
            "legacy_test_store_api", null, name, ct);
        await tx.CommitAsync(ct);
        return new(organization, store, name, "Asia/Tashkent", true);
    }

    internal async Task<PlatformTestStore> Rename(HttpContext context, Guid id, RenamePlatformTestStore request,
        CancellationToken ct)
    {
        if (id == Guid.Empty) throw new PosError(400, "invalid_store_id");
        var name = ValidateName(request.Name);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var permission = await Require(db, tx, context, write: true, ct);
        await using var update = db.Sql(tx, """
            UPDATE stores s SET name = $1
            FROM organizations o
            WHERE s.id = $2 AND o.id = s.org_id AND s.is_test AND o.is_test
            RETURNING s.org_id, s.time_zone
            """, name, id);
        await using var rows = await update.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct)) throw new PosError(404, "test_store_not_found");
        var organization = rows.GetGuid(0);
        var zone = rows.GetString(1);
        await rows.DisposeAsync();
        await Audit(db, tx, permission, "rename_test_store", organization, id, name,
            "legacy_test_store_api", null, name, ct);
        await tx.CommitAsync(ct);
        return new(organization, id, name, zone, true);
    }

    private async Task<Permission> Require(NpgsqlConnection db, NpgsqlTransaction tx, HttpContext context,
        bool write, CancellationToken ct)
    {
        var issuer = context.User.FindFirst(JwtRegisteredClaimNames.Iss)?.Value ??
                     context.User.FindFirst("iss")?.Value ?? "";
        var subject = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ??
                      context.User.FindFirst("sub")?.Value ?? "";
        var localClaim = context.User.FindFirst(LocalTestDeveloperMode.Claim)?.Value == "true";
        var local = localClaim && issuer == LocalTestDeveloperMode.Issuer &&
                    subject == LocalTestDeveloperMode.Subject && mode.IsLocalRequest(context);
        if (localClaim && !local) throw new PosError(403, "local_test_loopback_required");
        if (!local)
        {
            var requiredAcr = configuration["Auth:AdminAcr"];
            var acr = context.User.FindFirst("acr")?.Value;
            var hardware = context.User.FindAll("amr").Any(x => x.Value is "fido2" or "webauthn" or "hwk");
            var usb = context.User.FindFirst("usb_key_verified")?.Value == "true";
            var temporaryMfaOnly = temporaryDeveloper.Allows(context.User, issuer, subject, requiredAcr);
            if (!temporaryMfaOnly && (string.IsNullOrWhiteSpace(requiredAcr) || acr != requiredAcr || !hardware || !usb))
                throw new PosError(403, "platform_hardware_auth_required");
        }
        if (issuer.Length is < 1 or > 500 || subject.Length is < 1 or > 256)
            throw new PosError(403, "platform_access_denied");
        await using var command = db.Sql(tx, """
            SELECT can_read, can_write FROM platform_staff
            WHERE issuer = $1 AND subject = $2 AND active
            """, issuer, subject);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct) || !rows.GetBoolean(0) || (write && !rows.GetBoolean(1)))
            throw new PosError(403, "platform_access_denied");
        return new(issuer, subject, rows.GetBoolean(1), local);
    }

    private static string ValidateName(string? value)
    {
        var name = value?.Trim() ?? "";
        if (name.Length is < 1 or > 120 || name.Any(char.IsControl)) throw new PosError(400, "invalid_store_name");
        return name;
    }

    private static string ValidateSubject(string? value)
    {
        var subject = value?.Trim() ?? "";
        if (subject.Length is < 1 or > 256 || subject.Any(char.IsControl))
            throw new PosError(400, "invalid_owner_subject");
        return subject;
    }

    private static string ValidateReason(string? value)
    {
        var reason = value?.Trim() ?? "";
        if (reason.Length is < 1 or > 500 || reason.Any(char.IsControl))
            throw new PosError(400, "invalid_reason");
        return reason;
    }

    private static void ValidateOffset(int offset)
    {
        if (offset is < 0 or > 1_000_000) throw new PosError(400, "invalid_offset");
    }

    private static async Task<bool> Exists(NpgsqlConnection db, NpgsqlTransaction tx, string sql,
        object first, CancellationToken ct)
    {
        await using var command = db.Sql(tx, sql, first);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<bool> Exists(NpgsqlConnection db, NpgsqlTransaction tx, string sql,
        object first, object second, CancellationToken ct)
    {
        await using var command = db.Sql(tx, sql, first, second);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task EnsureOwner(NpgsqlConnection db, NpgsqlTransaction tx, Guid organizationId,
        Guid storeId, string subject, CancellationToken ct)
    {
        Guid staffId;
        await using (var command = db.Sql(tx, "SELECT org_id, id FROM staff WHERE subject = $1", subject))
        await using (var rows = await command.ExecuteReaderAsync(ct))
        {
            if (await rows.ReadAsync(ct))
            {
                if (rows.GetGuid(0) != organizationId) throw new PosError(409, "owner_subject_conflict");
                staffId = rows.GetGuid(1);
            }
            else
            {
                staffId = Guid.NewGuid();
                await rows.DisposeAsync();
                await db.Run(tx, ct, """
                    INSERT INTO staff (org_id, id, subject, role, active) VALUES ($1, $2, $3, 'admin', true)
                    """, organizationId, staffId, subject);
            }
        }
        await db.Run(tx, ct, """
            INSERT INTO staff_roles (org_id, staff_id, role) VALUES ($1, $2, 'owner') ON CONFLICT DO NOTHING
            """, organizationId, staffId);
        await db.Run(tx, ct, """
            INSERT INTO staff_stores (org_id, staff_id, store_id, active) VALUES ($1, $2, $3, true)
            ON CONFLICT (org_id, staff_id, store_id) DO UPDATE SET active = true
            """, organizationId, staffId, storeId);
    }

    private static async Task<string> VerifyOwnerTarget(NpgsqlConnection db, NpgsqlTransaction tx,
        Guid organizationId, Guid storeId, string? replaceSubject, CancellationToken ct)
    {
        await using var store = db.Sql(tx, """
            SELECT s.name FROM stores s JOIN organizations o ON o.id = s.org_id
            WHERE s.org_id = $1 AND s.id = $2 AND s.is_test AND o.is_test
            """, organizationId, storeId);
        var storeName = await store.ExecuteScalarAsync(ct) as string;
        if (storeName is null) throw new PosError(404, "test_store_not_found");
        await using var owner = db.Sql(tx, """
            SELECT st.subject FROM staff st
            JOIN staff_stores ss ON ss.org_id = st.org_id AND ss.staff_id = st.id AND ss.active
            JOIN staff_roles sr ON sr.org_id = st.org_id AND sr.staff_id = st.id AND sr.role = 'owner'
            WHERE st.org_id = $1 AND ss.store_id = $2 AND st.active
            ORDER BY st.id LIMIT 2
            """, organizationId, storeId);
        await using var rows = await owner.ExecuteReaderAsync(ct);
        var subjects = new List<string>();
        while (await rows.ReadAsync(ct)) subjects.Add(rows.GetString(0));
        if (replaceSubject is null)
        {
            if (subjects.Count > 0) throw new PosError(409, "platform_owner_already_assigned");
        }
        else if (subjects.Count != 1 || !string.Equals(subjects[0], replaceSubject, StringComparison.Ordinal))
        {
            throw new PosError(409, "platform_owner_changed");
        }
        return storeName;
    }

    private static Task<int> Audit(NpgsqlConnection db, NpgsqlTransaction tx, Permission permission, string action,
        Guid organization, Guid store, string name, string reason, string? previousName, string? newName,
        CancellationToken ct) => db.Run(tx, ct, """
            INSERT INTO platform_store_events
                (issuer, subject, action, org_id, store_id, store_name, reason, previous_name, new_name)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            """, permission.Issuer, permission.Subject, action, organization, store, name, reason, previousName, newName);
}
