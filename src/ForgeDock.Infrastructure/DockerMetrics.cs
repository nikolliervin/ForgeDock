using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ForgeDock.Domain;

namespace ForgeDock.Infrastructure;

public static partial class DockerMetrics
{
    [GeneratedRegex(@"^([0-9]+(?:\.[0-9]+)?)\s*(B|kB|KB|MB|GB|TB|KiB|MiB|GiB|TiB)$")]
    private static partial Regex ByteValue();

    public static long ParseBytes(string value)
    {
        var match = ByteValue().Match(value.Trim());
        if (!match.Success) throw new FormatException("Invalid Docker byte quantity.");
        var units = new Dictionary<string, double> { ["B"] = 1, ["kB"] = 1e3, ["KB"] = 1e3, ["MB"] = 1e6, ["GB"] = 1e9, ["TB"] = 1e12,
            ["KiB"] = 1024, ["MiB"] = 1048576, ["GiB"] = 1073741824, ["TiB"] = 1099511627776 };
        return checked((long)Math.Round(double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * units[match.Groups[2].Value]));
    }

    public static MetricSample Parse(string json, Guid projectId, Guid deploymentId, string containerId, string service,
        DateTimeOffset timestamp, DateTimeOffset startedAt)
    {
        using var document = JsonDocument.Parse(json);
        var data = document.RootElement;
        string Get(string key) => data.GetProperty(key).GetString() ?? throw new FormatException("Missing Docker metric.");
        long[] Pair(string key) => Get(key).Split('/').Select(ParseBytes).ToArray() is { Length: 2 } pair ? pair : throw new FormatException("Invalid Docker metric pair.");
        var memory = Pair("MemUsage"); var network = Pair("NetIO"); var block = Pair("BlockIO");
        var cpu = double.Parse(Get("CPUPerc").TrimEnd('%'), CultureInfo.InvariantCulture);
        if (!double.IsFinite(cpu) || cpu < 0) throw new FormatException("Invalid CPU metric.");
        return new() { ProjectId = projectId, DeploymentId = deploymentId, ContainerId = containerId, Service = service,
            Timestamp = timestamp, StartedAt = startedAt, CpuPercent = cpu, MemoryBytes = memory[0], MemoryLimitBytes = memory[1],
            NetworkReceivedBytes = network[0], NetworkSentBytes = network[1], BlockReadBytes = block[0], BlockWrittenBytes = block[1],
            Pids = int.Parse(Get("PIDs"), CultureInfo.InvariantCulture) };
    }
}
