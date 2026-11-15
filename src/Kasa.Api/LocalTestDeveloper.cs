using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace Kasa.Api;

internal sealed record LocalTestDeveloperMode(bool Enabled, string Audience, byte[]? SigningKey, int TokenMinutes)
{
    internal const string Issuer = "kasa-local";
    internal const string Subject = "developer-test";
    internal const string Claim = "local_test_developer";

    internal static LocalTestDeveloperMode Read(WebApplicationBuilder builder, string audience)
    {
        var enabled = builder.Configuration.GetValue<bool>("Auth:AllowLocalTestDeveloper");
        if (!enabled) return new(false, audience, null, 0);
        if (!builder.Environment.IsDevelopment())
            throw new InvalidOperationException("Auth:AllowLocalTestDeveloper is permitted only in Development.");

        var urls = builder.Configuration["urls"];
        if (string.IsNullOrWhiteSpace(urls))
            throw new InvalidOperationException("Local test developer mode requires ASPNETCORE_URLS with an explicit 127.0.0.1 address.");
        foreach (var value in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                uri.Host != IPAddress.Loopback.ToString() || uri.Port is < 1 or > 65535 ||
                uri.Scheme is not ("http" or "https"))
                throw new InvalidOperationException("Local test developer mode may listen only on an explicit 127.0.0.1 URL.");
        }
        foreach (var endpoint in builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            var value = endpoint["Url"];
            if (value is not null && (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                                      uri.Host != IPAddress.Loopback.ToString()))
                throw new InvalidOperationException("Kestrel endpoints must use 127.0.0.1 in local test developer mode.");
        }

        var rawKey = builder.Configuration["Auth:LocalTestDeveloperSigningKey"];
        if (rawKey is null || Encoding.UTF8.GetByteCount(rawKey) < 32)
            throw new InvalidOperationException("Configure a local test developer signing key of at least 32 bytes.");
        var minutes = builder.Configuration.GetValue("Auth:LocalTestDeveloperTokenMinutes", 30);
        if (minutes is < 1 or > 60)
            throw new InvalidOperationException("Local test developer token lifetime must be between 1 and 60 minutes.");
        return new(true, audience, Encoding.UTF8.GetBytes(rawKey), minutes);
    }

    internal bool IsLocalRequest(HttpContext context) =>
        Enabled && context.Connection.RemoteIpAddress is { } address && address.Equals(IPAddress.Loopback);
}

internal static class LocalDeveloperPassword
{
    private const int Iterations = 210_000;

    internal static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var digest = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(digest)}";
    }

    internal static bool Verify(string password, string encoded)
    {
        try
        {
            var parts = encoded.Split('$');
            if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" ||
                !int.TryParse(parts[1], out var iterations) || iterations is < 100_000 or > 1_000_000)
                return false;
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            if (salt.Length != 16 || expected.Length != 32) return false;
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }
}

internal sealed class LocalDeveloperLoginService(NpgsqlDataSource source, LocalTestDeveloperMode mode)
{
    private readonly string dummyHash = LocalDeveloperPassword.Hash(Convert.ToHexString(RandomNumberGenerator.GetBytes(24)));

    internal async Task<LocalDeveloperToken> Login(HttpContext context, LocalDeveloperLogin request, CancellationToken ct)
    {
        if (!mode.IsLocalRequest(context)) throw new PosError(403, "local_test_loopback_required");
        var subject = request.Login?.Trim() ?? "";
        var password = request.Password ?? "";
        if (subject.Length is < 1 or > 256 || password.Length is < 1 or > 256)
            throw new PosError(401, "invalid_credentials");

        await using var db = await source.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT password_hash, can_read, can_write, active
            FROM platform_staff WHERE issuer = $1 AND subject = $2
            """, db);
        command.Parameters.AddWithValue(LocalTestDeveloperMode.Issuer);
        command.Parameters.AddWithValue(subject);
        await using var rows = await command.ExecuteReaderAsync(ct);
        string? hash = null;
        var canRead = false;
        var canWrite = false;
        var active = false;
        if (await rows.ReadAsync(ct))
        {
            hash = rows.IsDBNull(0) ? null : rows.GetString(0);
            canRead = rows.GetBoolean(1);
            canWrite = rows.GetBoolean(2);
            active = rows.GetBoolean(3);
        }
        await rows.DisposeAsync();
        var accepted = LocalDeveloperPassword.Verify(password, hash ?? dummyHash) && active &&
                       subject == LocalTestDeveloperMode.Subject && canRead;
        await using (var audit = new NpgsqlCommand("""
            INSERT INTO platform_login_audit (issuer, subject, succeeded) VALUES ($1, $2, $3)
            """, db))
        {
            audit.Parameters.AddWithValue(LocalTestDeveloperMode.Issuer);
            audit.Parameters.AddWithValue(subject);
            audit.Parameters.AddWithValue(accepted);
            await audit.ExecuteNonQueryAsync(ct);
        }
        if (!accepted) throw new PosError(401, "invalid_credentials");

        var now = DateTime.UtcNow;
        var expires = now.AddMinutes(mode.TokenMinutes);
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(mode.SigningKey!) { KeyId = "local-test-developer" }, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, subject),
            new Claim(LocalTestDeveloperMode.Claim, "true", ClaimValueTypes.Boolean),
            new Claim("platform_read", canRead ? "true" : "false", ClaimValueTypes.Boolean),
            new Claim("platform_write", canWrite ? "true" : "false", ClaimValueTypes.Boolean),
            new Claim(JwtRegisteredClaimNames.Iat, new DateTimeOffset(now).ToUnixTimeSeconds().ToString(),
                ClaimValueTypes.Integer64),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };
        var jwt = new JwtSecurityToken(LocalTestDeveloperMode.Issuer, mode.Audience, claims, now, expires, credentials);
        return new LocalDeveloperToken(new JwtSecurityTokenHandler().WriteToken(jwt), "Bearer",
            checked((int)(expires - now).TotalSeconds), expires);
    }
}
