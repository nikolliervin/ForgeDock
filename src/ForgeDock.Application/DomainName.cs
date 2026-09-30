using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace ForgeDock.Application;

public static partial class DomainName
{
    public static bool TryNormalize(string? input, out string hostname)
    {
        hostname = "";
        if (string.IsNullOrWhiteSpace(input) || input.Any(char.IsControl)) return false;
        try { hostname = new IdnMapping().GetAscii(input.Trim().TrimEnd('.')).ToLowerInvariant(); }
        catch (ArgumentException) { return false; }
        var normalized = hostname;
        var labels = hostname.Split('.');
        if (hostname.Length > 253 || labels.Length < 2 || IPAddress.TryParse(hostname, out _) ||
            labels.Any(label => label.Length is < 1 or > 63 || !LabelPattern().IsMatch(label)) ||
            labels[^1].All(char.IsDigit) || new[] { "localhost", "local", "internal", "test", "invalid", "example", "onion", "home.arpa", "ts.net" }
                .Any(suffix => normalized == suffix || normalized.EndsWith("." + suffix, StringComparison.Ordinal)))
        { hostname = ""; return false; }
        return true;
    }

    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9-]*[a-z0-9])?$")]
    private static partial Regex LabelPattern();
}
