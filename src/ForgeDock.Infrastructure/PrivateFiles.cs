namespace ForgeDock.Infrastructure;

public static class PrivateFiles
{
    /// <summary>
    /// Creates a new file exclusively with owner-only permissions on Unix. Existing files are never silently
    /// overwritten.
    /// </summary>
    public static FileStream Create(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }
}
