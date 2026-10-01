using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using ForgeDock.Domain;

namespace ForgeDock.Infrastructure;

public sealed record S3BackupOptions(string Endpoint, string Region, string Bucket, string Prefix, string AccessKey, string SecretKey, bool AllowHttp = false)
{
    public void Validate()
    {
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/"
            || (uri.Scheme != "https" && !(AllowHttp && uri.Scheme == "http")))
            throw new InvalidOperationException("Backup S3 endpoint must be an HTTPS origin (HTTP requires explicit AllowHttp for local testing).");
        if (string.IsNullOrWhiteSpace(Region) || string.IsNullOrWhiteSpace(Bucket) || Bucket.Contains('/')
            || string.IsNullOrWhiteSpace(AccessKey) || string.IsNullOrWhiteSpace(SecretKey)
            || Prefix.Split('/').Any(part => part is "." or ".."))
            throw new InvalidOperationException("Configure the backup S3 region, bucket, credentials, and a valid prefix.");
    }
}

public sealed class S3BackupStore : IDisposable
{
    private readonly IAmazonS3 client;
    private readonly S3BackupOptions options;
    public S3BackupStore(S3BackupOptions options, IAmazonS3? client = null)
    {
        options.Validate(); this.options = options;
        this.client = client ?? new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), new AmazonS3Config
        {
            ServiceURL = options.Endpoint, AuthenticationRegion = options.Region, ForcePathStyle = true,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        });
    }
    public void SetLocation(DatabaseBackup backup)
    {
        // Persist before upload so retries use the same object after a worker crash.
        backup.RemoteEndpoint = options.Endpoint.TrimEnd('/'); backup.RemoteRegion = options.Region;
        backup.RemoteBucket = options.Bucket;
        backup.RemoteKey = $"{options.Prefix.Trim('/')}/{backup.ProjectId:N}/{backup.ServiceId:N}/{backup.Id:N}.fgbackup".TrimStart('/');
        backup.RemoteState = "Pending";
    }
    public bool CanAccess(DatabaseBackup backup) => backup.RemoteEndpoint == options.Endpoint.TrimEnd('/') && backup.RemoteRegion == options.Region
        && backup.RemoteBucket == options.Bucket && !string.IsNullOrWhiteSpace(backup.RemoteKey);
    private void AssertLocation(DatabaseBackup backup)
    {
        if (!CanAccess(backup))
            throw new InvalidOperationException("The configured backup store does not match this backup's recorded location.");
    }
    public async Task Upload(DatabaseBackup backup, string file, CancellationToken ct)
    {
        AssertLocation(backup);
        await client.PutObjectAsync(new PutObjectRequest { BucketName = backup.RemoteBucket, Key = backup.RemoteKey,
            FilePath = file, ContentType = "application/octet-stream" }, ct);
    }
    public async Task Download(DatabaseBackup backup, string file, CancellationToken ct)
    {
        AssertLocation(backup);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".download";
        try
        {
            using var response = await client.GetObjectAsync(new GetObjectRequest { BucketName = backup.RemoteBucket, Key = backup.RemoteKey }, ct);
            var fileOptions = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var output = new FileStream(temporary, fileOptions))
                await response.ResponseStream.CopyToAsync(output, ct);
            if (new FileInfo(temporary).Length != backup.SizeBytes) throw new InvalidDataException("Remote backup size does not match the saved snapshot.");
            File.Move(temporary, file, true);
        }
        finally { File.Delete(temporary); }
    }
    public async Task Delete(DatabaseBackup backup, CancellationToken ct)
    {
        AssertLocation(backup);
        await client.DeleteObjectAsync(new DeleteObjectRequest { BucketName = backup.RemoteBucket, Key = backup.RemoteKey }, ct);
    }
    public void Dispose() => client.Dispose();
}
