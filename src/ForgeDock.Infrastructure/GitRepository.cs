using System.Text;

namespace ForgeDock.Infrastructure;

public static class GitRepository
{
    public static Dictionary<string, string> AuthenticationEnvironment(string repositoryUrl, string? token)
    {
        var environment = new Dictionary<string, string>
        {
            ["GIT_CONFIG_COUNT"] = "0",
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = "/dev/null"
        };
        if (string.IsNullOrWhiteSpace(token) || !Uri.TryCreate(repositoryUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" || uri.Host != "github.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            return environment;
        var authorization = Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + token));
        environment["GIT_CONFIG_COUNT"] = "1";
        environment["GIT_CONFIG_KEY_0"] = $"http.{uri.GetLeftPart(UriPartial.Path).TrimEnd('/')}/.extraHeader";
        environment["GIT_CONFIG_VALUE_0"] = "Authorization: Basic " + authorization;
        return environment;
    }

    public static async Task<string> RunAsync(ProcessRunner runner, string repositoryUrl, string? token,
        IEnumerable<string> arguments, Func<string, Task> log, CancellationToken ct)
    {
        string Redact(string value)
        {
            if (string.IsNullOrEmpty(token)) return value;
            return value.Replace(token, "[REDACTED]", StringComparison.Ordinal)
                .Replace(Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + token)),
                    "[REDACTED]", StringComparison.Ordinal);
        }
        return Redact(await runner.RunAsync("git", arguments, null, line => log(Redact(line)), ct,
            inheritEnvironment: false, environment: AuthenticationEnvironment(repositoryUrl, token)));
    }
}
