using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kasa.Api;

internal static partial class DesktopUpdates
{
    private const int MaxManifestBytes = 32768;
    private const long MaxInstallerBytes = 128 * 1024 * 1024;
    private static readonly string[] Keys = ["schemaVersion", "channel", "version", "file", "sha256", "bytes",
        "signerThumbprint", "publishedAtUtc", "releaseNotes"];

    internal static void MapDesktopUpdates(this WebApplication app)
    {
        // Installer and manifest contain no credentials; downloads also work before employee login.
        app.MapGet("/desktop-updates/latest.json", (IConfiguration config, HttpContext context) =>
        {
            try
            {
                var (root, bytes, manifest) = ReadManifest(config);
                using var file = OpenVerifiedInstaller(root, manifest);
                context.Response.Headers.CacheControl = "no-store";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                return Results.Bytes(bytes, "application/json");
            }
            catch (Exception ex) when (IsRepositoryError(ex)) { return Unavailable(); }
        }).AllowAnonymous().WithName("LatestDesktopUpdate");
        app.MapGet("/desktop-updates/{file}", (string file, IConfiguration config, HttpContext context) =>
        {
            try
            {
                var (root, _, manifest) = ReadManifest(config);
                if (!string.Equals(file, manifest.File, StringComparison.Ordinal)) return Unavailable();
                var stream = OpenVerifiedInstaller(root, manifest);
                context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                return Results.File(stream, "application/octet-stream", manifest.File, enableRangeProcessing: true);
            }
            catch (Exception ex) when (IsRepositoryError(ex)) { return Unavailable(); }
        }).AllowAnonymous().WithName("DownloadDesktopUpdate");
    }

    private static IResult Unavailable() => Results.Problem(statusCode: 404, title: "desktop_update_unavailable",
        extensions: new Dictionary<string, object?> { ["code"] = "desktop_update_unavailable" });

    private static bool IsRepositoryError(Exception ex) => ex is IOException or UnauthorizedAccessException or
        JsonException or FormatException or InvalidOperationException or ArgumentException or OverflowException;

    internal static (string Root, byte[] Bytes, DesktopUpdateManifest Manifest) ReadManifest(IConfiguration config)
    {
        var path = config["DesktopUpdates:Root"];
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new FormatException();
        var root = Path.GetFullPath(path);
        EnsureRegularPath(root);
        var manifestPath = Path.Combine(root, "latest.json");
        EnsureRegularPath(manifestPath);
        using var input = File.OpenRead(manifestPath);
        if (input.Length is < 2 or > MaxManifestBytes) throw new FormatException();
        var bytes = new byte[checked((int)input.Length)];
        input.ReadExactly(bytes);
        using var json = JsonDocument.Parse(bytes);
        var body = json.RootElement;
        if (body.ValueKind != JsonValueKind.Object) throw new FormatException();
        var keys = body.EnumerateObject().Select(p => p.Name).ToArray();
        if (keys.Length != Keys.Length || !keys.Order().SequenceEqual(Keys.Order()) ||
            body.GetProperty("schemaVersion").GetInt32() != 1 || body.GetProperty("channel").GetString() != "stable")
            throw new FormatException();
        var versionText = body.GetProperty("version").GetString();
        if (versionText is null || !Version.TryParse(versionText, out var version) || version.Build < 0 ||
            version.Revision >= 0 || version.ToString(3) != versionText) throw new FormatException();
        var file = body.GetProperty("file").GetString();
        var hash = body.GetProperty("sha256").GetString();
        var signer = body.GetProperty("signerThumbprint").GetString();
        var size = body.GetProperty("bytes").GetInt64();
        var notes = body.GetProperty("releaseNotes").GetString();
        if (file != $"KassaSetup-{versionText}.exe" || hash is null || !Sha256Pattern().IsMatch(hash) ||
            signer is null || !ThumbprintPattern().IsMatch(signer) || size is < 2 or > MaxInstallerBytes ||
            !body.GetProperty("publishedAtUtc").TryGetDateTimeOffset(out var published) ||
            published.Offset != TimeSpan.Zero || notes is null || notes.Length > 4000 ||
            notes.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t'))) throw new FormatException();
        return (root, bytes, new DesktopUpdateManifest(file, hash, size));
    }

    internal static FileStream OpenVerifiedInstaller(string root, DesktopUpdateManifest manifest)
    {
        var path = Path.Combine(root, manifest.File);
        EnsureRegularPath(path);
        var input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (input.Length != manifest.Bytes || input.ReadByte() != 'M' || input.ReadByte() != 'Z')
                throw new FormatException();
            input.Position = 0;
            if (!Convert.ToHexString(SHA256.HashData(input)).Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new FormatException();
            input.Position = 0;
            return input;
        }
        catch { input.Dispose(); throw; }
    }

    private static void EnsureRegularPath(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new FormatException();
    }

    [GeneratedRegex("\\A[0-9a-fA-F]{64}\\z")] private static partial Regex Sha256Pattern();
    [GeneratedRegex("\\A[0-9a-fA-F]{40}\\z")] private static partial Regex ThumbprintPattern();
}

internal sealed record DesktopUpdateManifest(string File, string Sha256, long Bytes);
