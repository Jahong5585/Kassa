using System.Security.Cryptography;
using System.Text.Json;
using Kasa.Api;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Kasa.Api.Tests;

public sealed class DesktopUpdatesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "kassa-update-test-" + Guid.NewGuid());
    private readonly byte[] payload = "MZtest-installer"u8.ToArray();
    private const string FileName = "KassaSetup-0.8.16.exe";
    public DesktopUpdatesTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true);

    private IConfiguration WriteManifest(Action<Dictionary<string, object>>? change = null)
    {
        File.WriteAllBytes(Path.Combine(root, FileName), payload);
        var manifest = new Dictionary<string, object>
        {
            ["schemaVersion"] = 1, ["channel"] = "stable", ["version"] = "0.8.16", ["file"] = FileName,
            ["bytes"] = payload.Length, ["sha256"] = Convert.ToHexString(SHA256.HashData(payload)),
            ["signerThumbprint"] = new string('A', 40), ["publishedAtUtc"] = "2026-11-20T13:00:00.000Z",
            ["releaseNotes"] = "Исправление добавления товара"
        };
        change?.Invoke(manifest);
        File.WriteAllText(Path.Combine(root, "latest.json"), JsonSerializer.Serialize(manifest));
        return new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
            { ["DesktopUpdates:Root"] = root }).Build();
    }

    [Fact]
    public void ValidManifestAndPayloadAreVerified()
    {
        var (directory, _, manifest) = DesktopUpdates.ReadManifest(WriteManifest());
        using var file = DesktopUpdates.OpenVerifiedInstaller(directory, manifest);
        Assert.Equal(payload.Length, file.Length);
        Assert.Equal(0, file.Position);
    }

    [Theory]
    [InlineData("file", "../other.exe")]
    [InlineData("version", "0.8.16.0")]
    [InlineData("signerThumbprint", "")]
    [InlineData("sha256", "invalid")]
    [InlineData("channel", "other")]
    public void InvalidManifestIsRejected(string key, string value) =>
        Assert.Throws<FormatException>(() => DesktopUpdates.ReadManifest(WriteManifest(m => m[key] = value)));

    [Fact]
    public void PayloadTamperingIsRejected()
    {
        var (directory, _, manifest) = DesktopUpdates.ReadManifest(WriteManifest());
        var changed = payload.ToArray();
        changed[^1] ^= 1;
        File.WriteAllBytes(Path.Combine(root, FileName), changed);
        Assert.Throws<FormatException>(() => DesktopUpdates.OpenVerifiedInstaller(directory, manifest));
    }

    [Fact]
    public void ExtraFieldsAreRejected() => Assert.Throws<FormatException>(() =>
        DesktopUpdates.ReadManifest(WriteManifest(m => m["downloadUrl"] = "https://untrusted.invalid/")));
}
