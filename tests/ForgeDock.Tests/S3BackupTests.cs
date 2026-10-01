using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using System.Security.Cryptography;

namespace ForgeDock.Tests;
public class S3BackupTests
{
    private static S3BackupOptions Options => new("https://s3.example.com", "us-east-1", "backups", "instance", "test", "secret");
    [Theory]
    [InlineData("http://s3.example.com")]
    [InlineData("https://user:secret@s3.example.com")]
    [InlineData("https://s3.example.com/path")]
    [InlineData("https://s3.example.com?token=secret")]
    [InlineData("file:///tmp")]
    public void RejectsUnsafeEndpointConfiguration(string endpoint) => Assert.Throws<InvalidOperationException>(() => (Options with { Endpoint = endpoint }).Validate());

    [Theory]
    [InlineData("Pending", 10, true, false, BackupRetentionAction.Keep)]
    [InlineData("Failed", 10, true, false, BackupRetentionAction.Keep)]
    [InlineData("Uploaded", 10, false, false, BackupRetentionAction.Keep)]
    [InlineData("Uploaded", 10, true, true, BackupRetentionAction.Keep)]
    [InlineData("Uploaded", 10, true, false, BackupRetentionAction.Expire)]
    [InlineData("Uploaded", 1, true, false, BackupRetentionAction.RemoveLocal)]
    [InlineData("Disabled", 1, true, false, BackupRetentionAction.Keep)]
    [InlineData("Disabled", 10, false, false, BackupRetentionAction.Expire)]
    public void RetentionPreservesPendingUploadsAndRestoreSources(string state, int position, bool enabled, bool restoring, BackupRetentionAction expected)
        => Assert.Equal(expected, BackupRetention.Decide(new DatabaseBackup { RemoteState = state }, position, 7, 1, enabled, restoring));

    [Fact]
    public async Task RejectsChangedLocationBeforeContactingStorage()
    {
        using var store = new S3BackupStore(Options);
        var backup = new DatabaseBackup(); store.SetLocation(backup);
        Assert.EndsWith($"/{backup.Id:N}.fgbackup", backup.RemoteKey);
        backup.RemoteBucket = "another-bucket";
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.Upload(backup, "missing", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.Delete(backup, default));
    }

    [Fact]
    public async Task TruncatedDownloadPreservesExistingLocalFileAndRemovesTemporaryFile()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var store = new S3BackupStore(Options, new TruncatedClient());
            var backup = new DatabaseBackup { SizeBytes = 100 }; store.SetLocation(backup);
            var file = Path.Combine(root, "backup"); await File.WriteAllTextAsync(file, "preserved");
            await Assert.ThrowsAsync<InvalidDataException>(() => store.Download(backup, file, default));
            Assert.Equal("preserved", await File.ReadAllTextAsync(file)); Assert.Single(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, true); }
    }
    private sealed class TruncatedClient() : AmazonS3Client(new BasicAWSCredentials("test", "secret"), RegionEndpoint.USEast1)
    {
        public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken ct = default)
            => Task.FromResult(new GetObjectResponse { ResponseStream = new MemoryStream([1, 2, 3]) });
    }

    // Opt in with a local S3-compatible endpoint; this test creates and deletes a unique bucket.
    [S3Fact]
    public async Task S3RoundTripEncryptedArchiveAfterRemovingLocalCopy()
    {
        var endpoint = Environment.GetEnvironmentVariable("FORGEDOCK_TEST_S3_ENDPOINT");
        if (endpoint is null) return;
        var options = new S3BackupOptions(endpoint, "us-east-1", "fd-test-" + Guid.NewGuid().ToString("N"), "instance", "test", "test-secret", true);
        using var client = new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), new AmazonS3Config
        { ServiceURL = endpoint, AuthenticationRegion = options.Region, ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED, ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED });
        using var store = new S3BackupStore(options, client);
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var backup = new DatabaseBackup { ProjectId = Guid.NewGuid(), ServiceId = Guid.NewGuid() }; store.SetLocation(backup);
        var input = Path.Combine(root, "input"); var file = Path.Combine(root, "backup"); var output = Path.Combine(root, "output");
        var data = RandomNumberGenerator.GetBytes(300000); var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await client.PutBucketAsync(options.Bucket);
        try
        {
            await File.WriteAllBytesAsync(input, data); await BackupEncryption.Encrypt(input, file, key, default);
            backup.SizeBytes = new FileInfo(file).Length;
            await store.Upload(backup, file, default); await store.Upload(backup, file, default); // Retry is idempotent.
            File.Delete(file); await store.Download(backup, file, default);
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            await BackupEncryption.Decrypt(file, output, key, default); Assert.Equal(data, await File.ReadAllBytesAsync(output));
            await store.Delete(backup, default);
            await Assert.ThrowsAnyAsync<AmazonS3Exception>(() => store.Download(backup, file + ".missing", default));
            Assert.False(File.Exists(file + ".missing")); Assert.Empty(Directory.GetFiles(root, "*.download"));
        }
        finally { await store.Delete(backup, default); await client.DeleteBucketAsync(options.Bucket); Directory.Delete(root, true); }
    }
}

public sealed class S3FactAttribute : FactAttribute
{
    public S3FactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FORGEDOCK_TEST_S3_ENDPOINT") is null) Skip = "Set FORGEDOCK_TEST_S3_ENDPOINT to an isolated local S3 test server.";
    }
}
