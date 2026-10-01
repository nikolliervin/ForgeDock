using ForgeDock.Domain;

namespace ForgeDock.Infrastructure;

public sealed record MetricPoint(
    DateTimeOffset Timestamp,
    double CpuPercent,
    double MemoryBytes,
    double? ReceivedBytesPerSecond,
    double? SentBytesPerSecond
);

public static class MetricSeries
{
    /// <summary>
    /// Aggregates service samples into time buckets. Network rates are absent across restarts, counter
    /// resets, missing baselines, or gaps over 90 seconds rather than manufacturing misleading rates.
    /// </summary>
    public static IReadOnlyList<MetricPoint> Build(
        IEnumerable<MetricSample> samples,
        DateTimeOffset start,
        int bucketSeconds
    )
    {
        if (bucketSeconds < 1)
            throw new ArgumentOutOfRangeException(nameof(bucketSeconds));
        var previous = new Dictionary<string, MetricSample>();
        var rows = new List<MetricPoint>();
        foreach (
            var group in samples
                .OrderBy(s => s.Timestamp)
                .ThenBy(s => s.Id)
                .GroupBy(s => s.Timestamp)
        )
        {
            double received = 0,
                sent = 0;
            var validRate = true;
            foreach (var sample in group)
            {
                if (
                    previous.TryGetValue(sample.ContainerId, out var before)
                    && before.StartedAt == sample.StartedAt
                    && (sample.Timestamp - before.Timestamp).TotalSeconds is > 0 and <= 90
                    && sample.NetworkReceivedBytes >= before.NetworkReceivedBytes
                    && sample.NetworkSentBytes >= before.NetworkSentBytes
                )
                {
                    var seconds = (sample.Timestamp - before.Timestamp).TotalSeconds;
                    received +=
                        (sample.NetworkReceivedBytes - before.NetworkReceivedBytes) / seconds;
                    sent += (sample.NetworkSentBytes - before.NetworkSentBytes) / seconds;
                }
                else
                    validRate = false;
                previous[sample.ContainerId] = sample;
            }
            if (group.Key >= start)
                rows.Add(
                    new(
                        group.Key,
                        group.Sum(s => s.CpuPercent),
                        group.Sum(s => (double)s.MemoryBytes),
                        validRate ? received : null,
                        validRate ? sent : null
                    )
                );
        }
        return rows.GroupBy(row => row.Timestamp.ToUnixTimeSeconds() / bucketSeconds)
            .Select(bucket => new MetricPoint(
                DateTimeOffset.FromUnixTimeSeconds(bucket.Key * bucketSeconds),
                bucket.Average(r => r.CpuPercent),
                bucket.Average(r => r.MemoryBytes),
                bucket.Any(r => r.ReceivedBytesPerSecond.HasValue)
                    ? bucket
                        .Where(r => r.ReceivedBytesPerSecond.HasValue)
                        .Average(r => r.ReceivedBytesPerSecond)
                    : null,
                bucket.Any(r => r.SentBytesPerSecond.HasValue)
                    ? bucket
                        .Where(r => r.SentBytesPerSecond.HasValue)
                        .Average(r => r.SentBytesPerSecond)
                    : null
            ))
            .ToArray();
    }
}
