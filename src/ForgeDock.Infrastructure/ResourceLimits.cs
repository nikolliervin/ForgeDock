using System.Globalization;
using System.Text.Json.Nodes;
using ForgeDock.Domain;

namespace ForgeDock.Infrastructure;

public static class ResourceLimits
{
    public static bool Valid(double cpu, int memory) =>
        double.IsFinite(cpu) && cpu is >= 0.1 and <= 32 && memory is >= 64 and <= 65536;

    /// <summary>
    /// Produces invariant-culture Docker quota flags after validating configured CPU and memory limits.
    /// </summary>
    public static string[] DockerArguments(double cpu, int memory)
    {
        if (!Valid(cpu, memory))
            throw new InvalidOperationException(
                "CPU must be 0.1–32 cores and memory 64–65536 MiB."
            );
        return
        [
            "--memory",
            memory.ToString(CultureInfo.InvariantCulture) + "m",
            "--memory-swap",
            memory.ToString(CultureInfo.InvariantCulture) + "m",
            "--cpus",
            cpu.ToString(CultureInfo.InvariantCulture),
            "--restart",
            "on-failure:5",
        ];
    }

    /// <summary>
    /// Applies project quotas and a bounded restart policy to the routed Compose service while preserving
    /// limits for its dependencies.
    /// </summary>
    public static void ApplyCompose(JsonObject model, DeploymentSnapshot snapshot)
    {
        if (!Valid(snapshot.CpuLimit, snapshot.MemoryLimitMiB))
            throw new InvalidOperationException("Invalid app resource limits.");
        var service = model["services"]![snapshot.ComposeService]!.AsObject();
        service["cpus"] = snapshot.CpuLimit;
        service["mem_limit"] = $"{snapshot.MemoryLimitMiB}m";
        service["memswap_limit"] = $"{snapshot.MemoryLimitMiB}m";
        service.Remove("mem_reservation");
        service["restart"] = "on-failure:5";
        if (service["deploy"] is JsonObject deploy)
        {
            deploy.Remove("restart_policy");
            if (deploy["resources"] is JsonObject resources)
            {
                resources.Remove("reservations");
                resources["limits"] = new JsonObject
                {
                    ["cpus"] = snapshot.CpuLimit.ToString(CultureInfo.InvariantCulture),
                    ["memory"] = $"{snapshot.MemoryLimitMiB}m",
                };
            }
        }
    }

    /// <summary>
    /// Requires three fresh samples above quota thresholds before emitting pressure, avoiding alerts from
    /// one spike or stale metrics.
    /// </summary>
    public static string? Saturation(
        IReadOnlyList<MetricSample> samples,
        double cpuLimit,
        DateTimeOffset now
    )
    {
        if (
            samples.Count < 3
            || samples.Any(s => now - s.Timestamp > TimeSpan.FromMinutes(2) || s.Timestamp > now)
        )
            return null;
        if (samples.All(s => s.MemoryLimitBytes > 0 && s.MemoryBytes >= s.MemoryLimitBytes * 0.9))
            return "MemoryPressure";
        if (samples.All(s => s.CpuPercent >= cpuLimit * 100 * 0.9))
            return "CpuPressure";
        return null;
    }
}
