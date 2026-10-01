using System.Text.RegularExpressions;

namespace ForgeDock.Application;

public static partial class EnvironmentFile
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,127}$")]
    private static partial Regex NamePattern();

    /// <summary>
    /// Parses a bounded, single-line .env subset without shell expansion. Validates the entire batch and
    /// rejects duplicates before any values can be persisted.
    /// </summary>
    public static Dictionary<string, string> Parse(string? content)
    {
        if (content is null || content.Length > 262144)
            throw new FormatException("Paste up to 256 KiB of environment variables.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = content.Replace("\r\n", "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line[7..].TrimStart();
            var separator = line.IndexOf('=');
            if (separator < 1)
                throw Error(index, "Use NAME=value, one variable per line.");
            var name = line[..separator].Trim();
            if (!NamePattern().IsMatch(name))
                throw Error(
                    index,
                    "Use a valid variable name with letters, numbers, and underscores."
                );
            var value = line[(separator + 1)..].Trim();
            if (value.StartsWith('\'') || value.StartsWith('"'))
            {
                var quote = value[0];
                var parsed = new System.Text.StringBuilder();
                var closed = false;
                var at = 1;
                for (; at < value.Length; at++)
                {
                    var c = value[at];
                    if (c == quote)
                    {
                        closed = true;
                        at++;
                        break;
                    }
                    if (
                        quote == '"'
                        && c == '\\'
                        && at + 1 < value.Length
                        && value[at + 1] is '"' or '\\'
                    )
                        c = value[++at];
                    parsed.Append(c);
                }
                if (!closed)
                    throw Error(
                        index,
                        "Close quoted values on the same line; multiline values are not supported."
                    );
                var suffix = value[at..].Trim();
                if (suffix.Length > 0 && !suffix.StartsWith('#'))
                    throw Error(index, "Only a comment can follow a quoted value.");
                value = parsed.ToString();
            }
            else
            {
                for (var at = 0; at < value.Length; at++)
                    if (value[at] == '#' && (at == 0 || char.IsWhiteSpace(value[at - 1])))
                    {
                        value = value[..at].TrimEnd();
                        break;
                    }
            }
            if (value.Length > 16384 || value.Any(c => c is '\r' or '\n' or '\0'))
                throw Error(index, "Use single-line values up to 16 KiB.");
            if (!result.TryAdd(name, value))
                throw Error(
                    index,
                    "A variable name is repeated. Keep one definition for each name."
                );
            if (result.Count > 100)
                throw new FormatException("Import up to 100 variables at a time.");
        }
        if (result.Count == 0)
            throw new FormatException("Paste at least one NAME=value entry.");
        return result;
    }

    private static FormatException Error(int index, string message) =>
        new($"Line {index + 1}: {message}");
}
