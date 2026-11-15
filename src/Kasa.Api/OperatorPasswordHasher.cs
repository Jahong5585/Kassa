using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace Kasa.Api;

internal sealed class OperatorPasswordHasher
{
    private const int MemoryKiB = 65_536;
    private const int Iterations = 3;
    private const int Parallelism = 1;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public async Task<string> Hash(string password, CancellationToken ct)
    {
        Validate(password);
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var digest = await DeriveSafe(password, salt, MemoryKiB, Iterations, Parallelism, HashBytes, ct);
        return $"$argon2id$v=19$m={MemoryKiB},t={Iterations},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(digest)}";
    }

    public async Task<bool> Verify(string? password, string encoded, CancellationToken ct)
    {
        if (password is null || password.Length is < 8 or > 128 || !TryParse(encoded, out var memory, out var iterations,
                out var parallelism, out var salt, out var expected))
            return false;
        var actual = await DeriveSafe(password, salt, memory, iterations, parallelism, expected.Length, ct);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    internal static void Validate(string? password)
    {
        if (password is null || password.Length is < 8 or > 128 || password.Contains('\0'))
            throw new PosError(400, "invalid_employee_password");
    }

    private static async Task<byte[]> Derive(string password, byte[] salt, int memory, int iterations,
        int parallelism, int bytes, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memory,
            Iterations = iterations,
            DegreeOfParallelism = parallelism
        };
        var result = await argon.GetBytesAsync(bytes);
        ct.ThrowIfCancellationRequested();
        return result;
    }

    private static async Task<byte[]> DeriveSafe(string password, byte[] salt, int memory, int iterations,
        int parallelism, int bytes, CancellationToken ct)
    {
        try
        {
            return await Derive(password, salt, memory, iterations, parallelism, bytes, ct);
        }
        catch (Exception error) when (error is CryptographicException or InvalidOperationException or ArgumentException)
        {
            throw new PosError(503, "password_service_unavailable");
        }
    }

    private static bool TryParse(string value, out int memory, out int iterations, out int parallelism,
        out byte[] salt, out byte[] digest)
    {
        memory = iterations = parallelism = 0;
        salt = digest = [];
        try
        {
            var parts = value.Split('$', StringSplitOptions.None);
            if (parts.Length != 6 || parts[1] != "argon2id" || parts[2] != "v=19") return false;
            foreach (var setting in parts[3].Split(','))
            {
                var pair = setting.Split('=', 2);
                if (pair.Length != 2 || !int.TryParse(pair[1], out var parsed)) return false;
                switch (pair[0])
                {
                    case "m": memory = parsed; break;
                    case "t": iterations = parsed; break;
                    case "p": parallelism = parsed; break;
                    default: return false;
                }
            }
            salt = Convert.FromBase64String(parts[4]);
            digest = Convert.FromBase64String(parts[5]);
            return memory is >= 32_768 and <= 262_144 && iterations is >= 2 and <= 10 &&
                   parallelism is >= 1 and <= 4 && salt.Length is >= 16 and <= 64 && digest.Length is 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
