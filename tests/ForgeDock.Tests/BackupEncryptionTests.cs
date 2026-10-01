using System.Security.Cryptography;
using ForgeDock.Infrastructure;

namespace ForgeDock.Tests;

public class BackupEncryptionTests
{
    [Fact]
    public async Task RoundTripsLargeFilesAndRejectsTamperingAndTruncation()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "input");
        var archive = Path.Combine(root, "archive");
        var output = Path.Combine(root, "output");
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var data = RandomNumberGenerator.GetBytes(300000);
        try
        {
            await File.WriteAllBytesAsync(source, data);
            await BackupEncryption.Encrypt(source, archive, key, default);
            await BackupEncryption.Decrypt(archive, output, key, default);
            Assert.Equal(data, await File.ReadAllBytesAsync(output));
            File.Delete(output);
            var encrypted = await File.ReadAllBytesAsync(archive);
            encrypted[100] ^= 1;
            await File.WriteAllBytesAsync(archive, encrypted);
            await Assert.ThrowsAnyAsync<CryptographicException>(() =>
                BackupEncryption.Decrypt(archive, output, key, default)
            );
            Assert.False(File.Exists(output));
            encrypted[100] ^= 1;
            await File.WriteAllBytesAsync(archive, encrypted[..^20]);
            await Assert.ThrowsAnyAsync<Exception>(() =>
                BackupEncryption.Decrypt(archive, output, key, default)
            );
            Assert.False(File.Exists(output));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RefusingAnExistingPlaintextDestinationDoesNotDeleteIt()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "input");
        var archive = Path.Combine(root, "archive");
        var output = Path.Combine(root, "output");
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        try
        {
            await File.WriteAllTextAsync(source, "backup data");
            await BackupEncryption.Encrypt(source, archive, key, default);
            await File.WriteAllTextAsync(output, "existing data");
            await Assert.ThrowsAsync<IOException>(() =>
                BackupEncryption.Decrypt(archive, output, key, default)
            );
            Assert.Equal("existing data", await File.ReadAllTextAsync(output));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
