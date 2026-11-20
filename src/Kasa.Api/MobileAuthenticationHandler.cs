using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Kasa.Api;

internal static class MobileAuthentication
{
    public const string Scheme = "MobileBearer";
    public const string Issuer = "https://kassa.qr-space.uz/mobile-auth";
    public const string MarkerClaim = "kasa_mobile";
    public const string SessionClaim = "kasa_mobile_session_id";
    public const string OrganizationClaim = "kasa_organization_id";
    public const string StaffClaim = "kasa_staff_id";
    public const string DeviceClaim = "kasa_device_id";
    public const string OwnerExpiryClaim = "kasa_owner_role_expires_at";
    public const string AccessPrefix = "kmob_";
    public const string RefreshPrefix = "kmob_refresh_";

    public static bool IsMobile(ClaimsPrincipal principal) =>
        principal.FindFirst(MarkerClaim)?.Value == "true";

    public static (Guid Session, Guid Organization, Guid Staff, Guid Device) Read(ClaimsPrincipal principal)
    {
        if (!IsMobile(principal) ||
            !Guid.TryParse(principal.FindFirst(SessionClaim)?.Value, out var session) ||
            !Guid.TryParse(principal.FindFirst(OrganizationClaim)?.Value, out var organization) ||
            !Guid.TryParse(principal.FindFirst(StaffClaim)?.Value, out var staff) ||
            !Guid.TryParse(principal.FindFirst(DeviceClaim)?.Value, out var device))
            throw new PosError(401, "mobile_session_invalid");
        return (session, organization, staff, device);
    }

    public static string HostSubject(Guid device) => $"device:{device:N}";
}

internal sealed class MobileAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    NpgsqlDataSource source)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private static readonly object FailureItem = new();

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();
        var token = authorization[7..];
        if (!token.StartsWith(MobileAuthentication.AccessPrefix, StringComparison.Ordinal) ||
            token.Length is < 40 or > 200)
            return Fail("mobile_session_invalid");

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        await using var db = await source.OpenConnectionAsync(Context.RequestAborted);
        await using var command = new NpgsqlCommand("""
            SELECT ms.id, ms.org_id, ms.staff_id, ms.device_id, ms.access_expires_at,
                   ms.refresh_expires_at, ms.owner_role_expires_at, ms.revoked_at, s.active
            FROM mobile_sessions ms
            JOIN staff s ON (s.org_id, s.id) = (ms.org_id, ms.staff_id)
            WHERE ms.access_token_digest = $1
            """, db);
        command.Parameters.AddWithValue(digest);
        await using var rows = await command.ExecuteReaderAsync(Context.RequestAborted);
        if (!await rows.ReadAsync(Context.RequestAborted))
            return Fail("mobile_session_invalid");
        var session = rows.GetGuid(0);
        var organization = rows.GetGuid(1);
        var staff = rows.GetGuid(2);
        var device = rows.GetGuid(3);
        var accessExpires = rows.GetDateTime(4);
        var ownerExpires = rows.IsDBNull(6) ? (DateTime?)null : rows.GetDateTime(6);
        var revoked = !rows.IsDBNull(7);
        var active = rows.GetBoolean(8);
        await rows.DisposeAsync();
        if (revoked || !active)
            return Fail("mobile_access_revoked");
        if (accessExpires <= DateTime.UtcNow)
            return Fail("mobile_access_token_expired");

        await using (var touch = new NpgsqlCommand(
            "UPDATE mobile_sessions SET last_seen_at = statement_timestamp() WHERE id = $1 AND revoked_at IS NULL", db))
        {
            touch.Parameters.AddWithValue(session);
            await touch.ExecuteNonQueryAsync(Context.RequestAborted);
        }

        var claims = new List<Claim>
        {
            new("iss", MobileAuthentication.Issuer),
            new("sub", MobileAuthentication.HostSubject(device)),
            new(MobileAuthentication.MarkerClaim, "true"),
            new(MobileAuthentication.SessionClaim, session.ToString()),
            new(MobileAuthentication.OrganizationClaim, organization.ToString()),
            new(MobileAuthentication.StaffClaim, staff.ToString()),
            new(MobileAuthentication.DeviceClaim, device.ToString())
        };
        if (ownerExpires is { } expiry)
            claims.Add(new(MobileAuthentication.OwnerExpiryClaim,
                new DateTimeOffset(DateTime.SpecifyKind(expiry, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString()));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var code = Context.Items.TryGetValue(FailureItem, out var value) && value is string failure
            ? failure
            : "mobile_session_invalid";
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        await Results.Problem(type: $"https://kassa.qr-space.uz/problems/{code.Replace('_', '-')}",
            title: code, statusCode: StatusCodes.Status401Unauthorized,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
                ["traceId"] = Context.TraceIdentifier
            }).ExecuteAsync(Context);
    }

    private AuthenticateResult Fail(string code)
    {
        Context.Items[FailureItem] = code;
        return AuthenticateResult.Fail(code);
    }
}
