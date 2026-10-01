using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ForgeDock.Infrastructure;

// Independently authenticated bounded frames; the authenticated final frame prevents truncation.
public static class BackupEncryption
{
    private const int Chunk = 65536;

    /// <summary>
    /// Streams independently authenticated 64 KiB AES-GCM frames with a per-file random nonce prefix and an
    /// authenticated zero-length final frame.
    /// </summary>
    public static async Task Encrypt(
        string source,
        string destination,
        string encodedKey,
        CancellationToken ct
    )
    {
        await using var input = File.OpenRead(source);
        await using var output = PrivateFiles.Create(destination);
        var prefix = RandomNumberGenerator.GetBytes(8);
        await output.WriteAsync("FGDB1"u8.ToArray(), ct);
        await output.WriteAsync(prefix, ct);
        using var cipher = new AesGcm(Convert.FromBase64String(encodedKey), 16);
        var buffer = new byte[Chunk];
        uint index = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct);
            var nonce = Nonce(prefix, index++);
            var length = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, read);
            var encrypted = new byte[read];
            var tag = new byte[16];
            cipher.Encrypt(nonce, buffer.AsSpan(0, read), encrypted, tag, length);
            await output.WriteAsync(length, ct);
            await output.WriteAsync(encrypted, ct);
            await output.WriteAsync(tag, ct);
            if (read == 0)
                break;
        }
    }

    /// <summary>
    /// Authenticates every bounded frame and requires the final marker before restore can use the file.
    /// Deletes only plaintext created by this invocation on failure.
    /// </summary>
    public static async Task Decrypt(
        string source,
        string destination,
        string encodedKey,
        CancellationToken ct
    )
    {
        var created = false;
        try
        {
            await using var input = File.OpenRead(source);
            await using var output = PrivateFiles.Create(destination);
            created = true;
            var header = new byte[5];
            await input.ReadExactlyAsync(header, ct);
            if (!header.AsSpan().SequenceEqual("FGDB1"u8))
                throw new CryptographicException("Unsupported backup format.");
            var prefix = new byte[8];
            await input.ReadExactlyAsync(prefix, ct);
            uint index = 0;
            using var cipher = new AesGcm(Convert.FromBase64String(encodedKey), 16);
            while (true)
            {
                var length = new byte[4];
                await input.ReadExactlyAsync(length, ct);
                var count = BinaryPrimitives.ReadInt32LittleEndian(length);
                if (count < 0 || count > Chunk)
                    throw new CryptographicException("Invalid backup frame.");
                var encrypted = new byte[count];
                var plain = new byte[count];
                var tag = new byte[16];
                await input.ReadExactlyAsync(encrypted, ct);
                await input.ReadExactlyAsync(tag, ct);
                cipher.Decrypt(Nonce(prefix, index++), encrypted, tag, plain, length);
                if (count == 0)
                {
                    if (input.Position != input.Length)
                        throw new CryptographicException("Unexpected backup data.");
                    break;
                }
                await output.WriteAsync(plain, ct);
            }
        }
        catch
        {
            if (created)
                File.Delete(destination);
            throw;
        }
    }

    /// <summary>
    /// Combines the per-file random prefix and monotonically increasing frame index to obtain a distinct
    /// 96-bit GCM nonce.
    /// </summary>
    private static byte[] Nonce(byte[] prefix, uint index)
    {
        var nonce = new byte[12];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(8), index);
        return nonce;
    }
}
