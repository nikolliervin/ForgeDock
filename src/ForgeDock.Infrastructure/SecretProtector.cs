using System.Security.Cryptography;
using System.Text;

namespace ForgeDock.Infrastructure;

public sealed class SecretProtector
{
    private readonly byte[] key;
    public SecretProtector(string encodedKey)
    {
        try { key = Convert.FromBase64String(encodedKey); }
        catch (FormatException) { throw new InvalidOperationException("ForgeDock__SecretKey must be a base64-encoded 32-byte key."); }
        if (key.Length != 32) throw new InvalidOperationException("ForgeDock__SecretKey must encode exactly 32 bytes.");
    }
    public string Protect(string value)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes(value);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var cipher = new AesGcm(key, 16);
        cipher.Encrypt(nonce, plaintext, ciphertext, tag);
        return Convert.ToBase64String(nonce.Concat(tag).Concat(ciphertext).ToArray());
    }
    public string Unprotect(string value)
    {
        var bytes = Convert.FromBase64String(value);
        if (bytes.Length < 28) throw new CryptographicException("Invalid encrypted secret.");
        var plaintext = new byte[bytes.Length - 28];
        using var cipher = new AesGcm(key, 16);
        cipher.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }
}
