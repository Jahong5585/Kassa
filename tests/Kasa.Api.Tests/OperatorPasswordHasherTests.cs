using Kasa.Api;
using Xunit;

namespace Kasa.Api.Tests;

public sealed class OperatorPasswordHasherTests
{
    private readonly OperatorPasswordHasher hasher = new();

    [Fact]
    public async Task Hash_UsesArgon2idAndIndividualSalt()
    {
        var first = await hasher.Hash("Correct-Password-42", CancellationToken.None);
        var second = await hasher.Hash("Correct-Password-42", CancellationToken.None);

        Assert.StartsWith("$argon2id$v=19$m=65536,t=3,p=1$", first);
        Assert.NotEqual(first, second);
        Assert.True(await hasher.Verify("Correct-Password-42", first, CancellationToken.None));
        Assert.False(await hasher.Verify("Wrong-Password-42", first, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    public void Validate_RejectsInvalidPassword(string value)
    {
        var error = Assert.Throws<PosError>(() => OperatorPasswordHasher.Validate(value));
        Assert.Equal("invalid_employee_password", error.Code);
    }
}
