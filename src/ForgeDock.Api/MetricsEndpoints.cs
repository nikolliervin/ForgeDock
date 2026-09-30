using ForgeDock.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ForgeDock.Api;

public static class MetricsEndpoints
{
    public static void MapMetricsEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/projects/{id:guid}/metrics", async (Guid id, string? range, string? service, ForgeDockDbContext db, CancellationToken ct) =>
        {
            var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, ct);
            if (project is null) return Results.NotFound();
            range ??= "1h";
            var hours = range switch { "1h" => 1, "6h" => 6, "24h" => 24, _ => 0 };
            if (hours == 0) return Results.Problem("Choose a metrics range of 1h, 6h, or 24h.", statusCode: 400);
            if (service?.Length > 200) return Results.Problem("Invalid service name.", statusCode: 400);
            var now = DateTimeOffset.UtcNow; var start = now.AddHours(-hours); var from = start.AddSeconds(-90);
            var query = db.Metrics.AsNoTracking().Where(m => m.ProjectId == id && m.Timestamp >= from && m.Timestamp <= now);
            var services = await query.Select(m => m.Service).Distinct().OrderBy(s => s).ToListAsync(ct);
            if (!string.IsNullOrEmpty(service)) query = query.Where(m => m.Service == service);
            var samples = await query.OrderBy(m => m.Timestamp).ThenBy(m => m.Id).ToListAsync(ct);
            var current = samples.Where(m => m.DeploymentId == project.ActiveDeploymentId).ToList();
            var latestAt = current.Count > 0 ? current.Max(m => m.Timestamp) : (DateTimeOffset?)null;
            var latest = current.Where(m => m.Timestamp == latestAt).ToArray();
            var fresh = latestAt > now.AddSeconds(-75) && project.HealthStatus != "Stopped";
            var bucket = hours switch { 1 => 30, 6 => 120, _ => 300 };
            return Results.Ok(new { range, service, start, end = now, sampleIntervalSeconds = 30, bucketSeconds = bucket,
                retentionHours = 24, services, fresh, latestAt,
                latest = latest.Select(m => new { m.Service, m.CpuPercent, m.MemoryBytes, m.MemoryLimitBytes,
                    m.NetworkReceivedBytes, m.NetworkSentBytes, m.BlockReadBytes, m.BlockWrittenBytes, m.Pids,
                    uptimeSeconds = Math.Max(0, (m.Timestamp - m.StartedAt).TotalSeconds) }),
                points = MetricSeries.Build(samples, start, bucket) });
        });
    }
}
