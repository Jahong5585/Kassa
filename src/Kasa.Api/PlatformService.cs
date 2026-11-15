using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Npgsql;

namespace Kasa.Api;

internal sealed class PlatformService(NpgsqlDataSource source, IConfiguration configuration, LocalTestDeveloperMode mode,
    TemporaryPlatformDeveloperAccess temporaryDeveloper)
{
    private sealed record Permission(string Issuer, string Subject, bool Write, bool Local);

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
        await Audit(db, tx, permission, "create_test_store", organization, store, name, ct);
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
        await Audit(db, tx, permission, "rename_test_store", organization, id, name, ct);
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

    private static Task<int> Audit(NpgsqlConnection db, NpgsqlTransaction tx, Permission permission, string action,
        Guid organization, Guid store, string name, CancellationToken ct) => db.Run(tx, ct, """
            INSERT INTO platform_store_events (issuer, subject, action, org_id, store_id, store_name)
            VALUES ($1, $2, $3, $4, $5, $6)
            """, permission.Issuer, permission.Subject, action, organization, store, name);
}
