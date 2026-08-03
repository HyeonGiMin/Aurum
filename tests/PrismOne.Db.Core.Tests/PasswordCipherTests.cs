using PrismOne.Db.Core;
using Xunit;

namespace PrismOne.Db.Core.Tests;

public class PasswordCipherTests
{
    [Fact]
    public void ProtectUnprotect_Roundtrip()
    {
        var stored = PasswordCipher.Protect("s3cretpw!@#123");
        Assert.StartsWith("enc:v1:", stored);
        Assert.Equal("s3cretpw!@#123", PasswordCipher.Unprotect(stored));
    }

    [Fact]
    public void Protect_SamePlaintextDiffersEachTime()
    {
        // GCM nonce 가 매번 달라야 한다
        Assert.NotEqual(PasswordCipher.Protect("secret"), PasswordCipher.Protect("secret"));
    }

    [Fact]
    public void Unprotect_PlaintextLegacyPassesThrough()
    {
        Assert.Equal("oldplain", PasswordCipher.Unprotect("oldplain"));
        Assert.Null(PasswordCipher.Unprotect(null));
    }

    [Fact]
    public void Unprotect_CorruptedReturnsNull()
    {
        Assert.Null(PasswordCipher.Unprotect("enc:v1:AAAAAAAAAAAAAAAAAAAAAAAAAAAA"));
    }
}
