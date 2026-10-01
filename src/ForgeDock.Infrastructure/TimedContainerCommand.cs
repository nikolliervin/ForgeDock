namespace ForgeDock.Infrastructure;
public static class TimedContainerCommand
{
    public static bool Valid(string? command, int seconds) => command is not null && command.Length <= 4096 && !command.Contains('\0') && seconds is >= 1 and <= 900;
    public static string ShellCommand(string command, int seconds)
    {
        if (!Valid(command, seconds) || string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Invalid timed command.");
        // The shell runs only inside the owned container; quote commands as a single argument to its inner shell.
        return $"timeout -s TERM -k 5 {seconds} /bin/sh -c '{command.Replace("'", "'\\''")}'";
    }
    public static string Redact(string text, IEnumerable<string> values)
    {
        foreach (var value in values.SelectMany(v => new[] { v }.Concat(v.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))).Where(v => v.Length > 0).Distinct().OrderByDescending(v => v.Length))
            text = text.Replace(value, "[REDACTED]", StringComparison.Ordinal);
        return text;
    }
}
