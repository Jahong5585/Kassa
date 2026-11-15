using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kasa.Api;

internal sealed record IdentityProvisioningOptions(bool Enabled, Uri? BaseUrl, string Realm, string ClientId,
    string ClientSecret)
{
    internal static IdentityProvisioningOptions Read(IConfiguration configuration, bool development)
    {
        var enabled = configuration.GetValue<bool>("IdentityProvisioning:Enabled");
        var baseValue = configuration["IdentityProvisioning:BaseUrl"];
        var realm = configuration["IdentityProvisioning:Realm"]?.Trim() ?? "kasa";
        var clientId = configuration["IdentityProvisioning:ClientId"]?.Trim() ?? "";
        var clientSecret = configuration["IdentityProvisioning:ClientSecret"] ?? "";
        if (!enabled) return new(false, null, realm, clientId, clientSecret);
        if (!Uri.TryCreate(baseValue, UriKind.Absolute, out var baseUrl) ||
            baseUrl.Scheme is not ("http" or "https") ||
            (!development && baseUrl.Scheme != "https" && baseUrl.Host != "keycloak"))
            throw new InvalidOperationException("IdentityProvisioning:BaseUrl must be HTTPS or the internal keycloak host.");
        if (!Regex.IsMatch(realm, "^[A-Za-z0-9._-]{1,64}$") ||
            !Regex.IsMatch(clientId, "^[A-Za-z0-9._-]{1,128}$") || clientSecret.Length < 32)
            throw new InvalidOperationException("Configure the identity provisioning realm, client and secret.");
        return new(true, new Uri(baseUrl.ToString().TrimEnd('/') + "/"), realm, clientId, clientSecret);
    }
}

internal sealed record ProvisionedIdentity(string Subject, string Username, string TemporaryPassword);

internal sealed partial class IdentityProvisioningService(HttpClient client, IdentityProvisioningOptions options)
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{2,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernamePattern();

    internal async Task<ProvisionedIdentity> Create(string? usernameValue, string? firstNameValue,
        string? lastNameValue, string? emailValue, CancellationToken ct)
    {
        if (!options.Enabled || options.BaseUrl is null) throw new PosError(503, "identity_provisioning_disabled");
        var username = usernameValue?.Trim() ?? "";
        if (!UsernamePattern().IsMatch(username)) throw new PosError(400, "invalid_identity_username");
        var firstName = OptionalText(firstNameValue, 100, "invalid_first_name");
        var lastName = OptionalText(lastNameValue, 100, "invalid_last_name");
        var email = OptionalEmail(emailValue);
        var temporaryPassword = GeneratePassword();
        var token = await AdminToken(ct);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"admin/realms/{Uri.EscapeDataString(options.Realm)}/users");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(new
        {
            username,
            enabled = true,
            emailVerified = false,
            firstName,
            lastName,
            email,
            requiredActions = new[] { "UPDATE_PASSWORD", "CONFIGURE_TOTP" },
            credentials = new[] { new { type = "password", value = temporaryPassword, temporary = true } }
        });
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.Conflict) throw new PosError(409, "identity_username_conflict");
        if (response.StatusCode != HttpStatusCode.Created)
            throw new PosError(503, "identity_provisioning_unavailable");
        var location = response.Headers.Location?.ToString();
        var subject = location?.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        if (subject.Length is < 1 or > 256) throw new PosError(503, "identity_provisioning_invalid_response");
        return new(subject, username, temporaryPassword);
    }

    internal async Task Delete(string subject, CancellationToken ct)
    {
        if (!options.Enabled || options.BaseUrl is null || subject.Length is < 1 or > 256) return;
        try
        {
            var token = await AdminToken(ct);
            using var request = new HttpRequestMessage(HttpMethod.Delete,
                $"admin/realms/{Uri.EscapeDataString(options.Realm)}/users/{Uri.EscapeDataString(subject)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or PosError)
        {
            // Best-effort compensation; Keycloak admin events retain evidence if deletion fails.
        }
    }

    private async Task<string> AdminToken(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"realms/{Uri.EscapeDataString(options.Realm)}/protocol/openid-connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = options.ClientId,
                ["client_secret"] = options.ClientSecret
            })
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new PosError(503, "identity_provisioning_unavailable");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        if (!document.RootElement.TryGetProperty("access_token", out var element) ||
            element.GetString() is not { Length: > 20 } token)
            throw new PosError(503, "identity_provisioning_invalid_response");
        return token;
    }

    private static string? OptionalText(string? value, int maximum, string code)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var result = value.Trim();
        if (result.Length > maximum || result.Any(char.IsControl)) throw new PosError(400, code);
        return result;
    }

    private static string? OptionalEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var result = value.Trim();
        if (result.Length > 254 || result.Any(char.IsControl) ||
            !System.Net.Mail.MailAddress.TryCreate(result, out var address) || address.Address != result)
            throw new PosError(400, "invalid_email");
        return result;
    }

    private static string GeneratePassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnopqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%*-_+";
        const string all = upper + lower + digits + symbols;
        var characters = new char[24];
        characters[0] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
        characters[1] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
        characters[2] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
        characters[3] = symbols[RandomNumberGenerator.GetInt32(symbols.Length)];
        for (var index = 4; index < characters.Length; index++)
            characters[index] = all[RandomNumberGenerator.GetInt32(all.Length)];
        RandomNumberGenerator.Shuffle(characters.AsSpan());
        return new string(characters);
    }
}
