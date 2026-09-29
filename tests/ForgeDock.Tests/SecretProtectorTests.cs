using ForgeDock.Infrastructure;
using System.Security.Cryptography;

namespace ForgeDock.Tests;
public class SecretProtectorTests
{
    [Fact]
    public void EncryptionUsesUniqueNoncesAndAuthenticatesCiphertext()
    {
        var protector = new SecretProtector(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var first = protector.Protect("secret-value");
        var second = protector.Protect("secret-value");
        Assert.NotEqual(first, second);
        Assert.Equal("secret-value", protector.Unprotect(first));
        var bytes = Convert.FromBase64String(first); bytes[^1] ^= 1;
        Assert.Throws<AuthenticationTagMismatchException>(() => protector.Unprotect(Convert.ToBase64String(bytes)));
    }
    [Fact]
    public void RejectsInvalidKeys() => Assert.Throws<InvalidOperationException>(() => new SecretProtector("invalid"));
}
