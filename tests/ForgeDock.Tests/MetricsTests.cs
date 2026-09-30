using ForgeDock.Domain;
using ForgeDock.Infrastructure;

namespace ForgeDock.Tests;

public class MetricsTests
{
    [Theory]
    [InlineData("1.5MiB", 1572864)]
    [InlineData("2.1kB", 2100)]
    [InlineData("0B", 0)]
    [InlineData("1GiB", 1073741824)]
    public void DockerUnitsAreConvertedWithoutMixingDecimalAndBinary(string text, long expected) => Assert.Equal(expected, DockerMetrics.ParseBytes(text));

    [Theory]
    [InlineData("NaNMB")]
    [InlineData("-1B")]
    [InlineData("1unknown")]
    public void InvalidDockerMeasurementsAreRejected(string text) => Assert.Throws<FormatException>(() => DockerMetrics.ParseBytes(text));

    [Fact]
    public void DockerStatsPopulateCpuMemoryNetworkAndIo()
    {
        var sample = DockerMetrics.Parse("""{"CPUPerc":"125.50%","MemUsage":"1MiB / 512MiB","NetIO":"2kB / 1kB","BlockIO":"1MB / 0B","PIDs":"3"}""",
            Guid.NewGuid(), Guid.NewGuid(), "container", "app", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        Assert.Equal(125.5, sample.CpuPercent); Assert.Equal(1048576, sample.MemoryBytes);
        Assert.Equal(536870912, sample.MemoryLimitBytes); Assert.Equal(2000, sample.NetworkReceivedBytes);
        Assert.Equal(1000000, sample.BlockReadBytes); Assert.Equal(3, sample.Pids);
    }

    private static readonly DateTimeOffset Start = DateTimeOffset.FromUnixTimeSeconds(1800000000);
    private static MetricSample Sample(int seconds, string container, double cpu, long received, int restarted = 0) =>
        new() { ContainerId = container, Timestamp = Start.AddSeconds(seconds), StartedAt = Start.AddSeconds(restarted),
            CpuPercent = cpu, MemoryBytes = 100, NetworkReceivedBytes = received, NetworkSentBytes = received / 2 };

    [Fact]
    public void RatesUseActualTimeAndTotalsSumServicesBeforeAveraging()
    {
        var points = MetricSeries.Build([Sample(0, "web", 10, 0), Sample(0, "db", 20, 0),
            Sample(30, "web", 30, 3000), Sample(30, "db", 40, 6000)], Start, 60);
        var point = Assert.Single(points);
        Assert.Equal(50, point.CpuPercent); Assert.Equal(200, point.MemoryBytes);
        Assert.Equal(300, point.ReceivedBytesPerSecond); Assert.Equal(150, point.SentBytesPerSecond);
    }

    [Fact]
    public void RestartsCounterResetsAndGapsDoNotCreateFalseTrafficSpikes()
    {
        var points = MetricSeries.Build([Sample(0,"a",0,1000),Sample(30,"a",0,10),Sample(60,"a",0,1000,60),Sample(180,"a",0,5000,60)], Start, 30);
        Assert.All(points, p => Assert.Null(p.ReceivedBytesPerSecond));
    }

    [Fact]
    public void PriorSampleSeedsRateButIsNotShownOutsideRequestedRange()
    {
        var points = MetricSeries.Build([Sample(-30,"a",0,0),Sample(0,"a",0,3000)], Start, 30);
        Assert.Equal(100, Assert.Single(points).ReceivedBytesPerSecond);
    }
}
