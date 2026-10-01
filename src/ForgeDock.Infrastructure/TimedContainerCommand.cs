namespace ForgeDock.Infrastructure;

public static class TimedContainerCommand
{
    public static bool Valid(string? command, int seconds) =>
        command is not null
        && command.Length <= 4096
        && !command.Contains('\0')
        && seconds is >= 1 and <= 900;

    /// <summary>
    /// Quotes the operator command for an inner in-container shell and applies TERM followed by forced kill.
    /// Task containers require an init process so timeout commands do not become signal-ignoring PID 1.
    /// </summary>
    public static string ShellCommand(string command, int seconds)
    {
        if (!Valid(command, seconds) || string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("Invalid timed command.");
        // The shell runs only inside the owned container; quote commands as a single argument to its inner shell.
        return $"timeout -s TERM -k 5 {seconds} /bin/sh -c '{command.Replace("'", "'\\''")}'";
    }

    /// <summary>
    /// Masks complete secret values and multiline fragments longest-first. For bounded captures, also masks
    /// a trailing incomplete secret prefix; encoded/transformed secrets are outside this guarantee.
    /// </summary>
    public static string Redact(string text, IEnumerable<string> values, bool truncated = false)
    {
        foreach (
            var value in values
                .SelectMany(v =>
                    new[] { v }.Concat(v.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                )
                .Where(v => v.Length > 0)
                .Distinct()
                .OrderByDescending(v => v.Length)
        )
        {
            text = text.Replace(value, "[REDACTED]", StringComparison.Ordinal);
            // A bounded capture may end in the middle of a secret; hide its visible prefix too.
            if (truncated)
                for (var length = Math.Min(value.Length - 1, text.Length); length > 0; length--)
                    if (text.AsSpan(text.Length - length).SequenceEqual(value.AsSpan(0, length)))
                    {
                        text = text[..^length] + "[REDACTED]";
                        break;
                    }
        }
        return text;
    }
}
