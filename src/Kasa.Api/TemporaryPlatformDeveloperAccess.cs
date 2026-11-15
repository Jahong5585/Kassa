using System.Globalization;
using System.Security.Claims;

namespace Kasa.Api;

internal sealed record TemporaryPlatformDeveloperAccess(
    bool Enabled, string Issuer, string Subject, DateTimeOffset ExpiresAt)
{
    internal static TemporaryPlatformDeveloperAccess Read(IConfiguration configuration)
    {
        if (!configuration.GetValue<bool>("Auth:AllowTemporaryPlatformDeveloperMfaOnly"))
            return new(false, "", "", DateTimeOffset.MinValue);

        var issuer = configuration["Auth:TemporaryPlatformDeveloperIssuer"]?.Trim() ?? "";
        var subject = configuration["Auth:TemporaryPlatformDeveloperSubject"]?.Trim() ?? "";
        var rawExpiry = configuration["Auth:TemporaryPlatformDeveloperExpiresAt"]?.Trim() ?? "";
        var authority = configuration["Auth:Authority"]?.Trim() ?? "";
        if (issuer.Length is < 1 or > 500 || issuer != authority || !Guid.TryParse(subject, out _) ||
            !DateTimeOffset.TryParse(rawExpiry, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expiry))
            throw new InvalidOperationException("Temporary platform developer access requires the configured OIDC issuer, UUID subject and UTC expiry.");

        var now = DateTimeOffset.UtcNow;
        if (expiry <= now) return new(false, issuer, subject, expiry);
        if (expiry > now.AddHours(24).AddMinutes(5))
            throw new InvalidOperationException("Temporary platform developer access cannot exceed 24 hours.");
        return new(true, issuer, subject, expiry);
    }

    internal bool Allows(ClaimsPrincipal principal, string issuer, string subject, string? requiredAcr)
    {
        if (!Enabled || DateTimeOffset.UtcNow >= ExpiresAt || issuer != Issuer || subject != Subject ||
            string.IsNullOrWhiteSpace(requiredAcr) || principal.FindFirst("acr")?.Value != requiredAcr)
            return false;

        var values = principal.FindAll("auth_time").Select(claim => claim.Value).ToArray();
        if (values.Length != 1 || !long.TryParse(values[0], out var authenticatedAt)) return false;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return authenticatedAt <= now + 30 && now - authenticatedAt <= 600;
    }
}
