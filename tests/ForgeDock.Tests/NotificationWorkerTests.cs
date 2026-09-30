using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ForgeDock.Domain;
using ForgeDock.Infrastructure;
using ForgeDock.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
namespace ForgeDock.Tests;
public class NotificationWorkerTests
{
    [DockerFact]
    public async Task SmtpResultsAreDurableRetriedAndLinkToExactLogs()
    {
        var original = Environment.GetEnvironmentVariable("ConnectionStrings__ForgeDock");
        Assert.False(string.IsNullOrEmpty(original), "Run with scripts/with-env.sh.");
        var name = "forgedock_notify_test_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(original!) { Database = name };
        var admin = new NpgsqlConnectionStringBuilder(original!) { Database = "postgres" };
        await using var owner = new NpgsqlConnection(admin.ConnectionString); await owner.OpenAsync();
        await using (var command = new NpgsqlCommand($"CREATE DATABASE {name}", owner)) await command.ExecuteNonQueryAsync();
        await using var provider = new ServiceCollection().AddDbContext<ForgeDockDbContext>(o => o.UseNpgsql(connection.ConnectionString)).BuildServiceProvider();
        await using var smtp = new TestSmtp();
        try
        {
            using var scope = provider.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<ForgeDockDbContext>(); await db.Database.MigrateAsync();
            var project = new Project { Name = "Notification test", RepositoryUrl = "https://github.com/example/test" };
            var success = new Deployment { ProjectId = project.Id };
            foreach (var state in new[] { DeploymentState.Preparing, DeploymentState.Cloning, DeploymentState.Building, DeploymentState.Starting, DeploymentState.HealthChecking, DeploymentState.Routing, DeploymentState.Running }) success.TransitionTo(state);
            var failure = new Deployment { ProjectId = project.Id }; failure.TransitionTo(DeploymentState.Preparing); failure.TransitionTo(DeploymentState.Failed, "private-secret-error");
            db.Projects.Add(project); db.Deployments.AddRange(success, failure);
            db.NotificationSettings.Add(new NotificationSettings { ProjectId = project.Id, Email = "recipient@example.com", EnabledAt = DateTimeOffset.UtcNow.AddMinutes(-1) }); await db.SaveChangesAsync();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                ["ForgeDock:DashboardBaseUrl"] = "https://dashboard.example", ["ForgeDock:Smtp:Host"] = "127.0.0.1", ["ForgeDock:Smtp:Port"] = smtp.Port.ToString(),
                ["ForgeDock:Smtp:EnableSsl"] = "false", ["ForgeDock:Smtp:From"] = "sender@example.com" }).Build();
            var protector = new SecretProtector(Convert.ToBase64String(new byte[32]));
            NotificationWorker Sender() => new(provider.GetRequiredService<IServiceScopeFactory>(), config, protector, NullLogger<NotificationWorker>.Instance);
            await Sender().ProcessAsync(default);
            var deliveries = await db.NotificationDeliveries.ToListAsync(); Assert.Equal(2, deliveries.Count);
            var pending = Assert.Single(deliveries, d => d.SentAt is null); Assert.Equal(1, pending.Attempts); Assert.NotNull(pending.Error);
            pending.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1); await db.SaveChangesAsync();
            await Sender().ProcessAsync(default); await Sender().ProcessAsync(default); db.ChangeTracker.Clear();
            deliveries = await db.NotificationDeliveries.ToListAsync(); Assert.All(deliveries, delivery => Assert.NotNull(delivery.SentAt));
            Assert.Equal(2, smtp.Messages.Count);
            var messages = string.Join('\n', smtp.Messages).Replace("=\n", "");
            messages = System.Text.RegularExpressions.Regex.Replace(messages, "=([0-9A-Fa-f]{2})", match => ((char)Convert.ToInt32(match.Groups[1].Value, 16)).ToString()); Assert.Contains(success.Id.ToString(), messages); Assert.Contains(failure.Id.ToString(), messages);
            Assert.Contains("deployment=", messages); Assert.DoesNotContain("private-secret-error", messages);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var command = new NpgsqlCommand($"DROP DATABASE {name} WITH (FORCE)", owner); await command.ExecuteNonQueryAsync();
        }
    }
    private sealed class TestSmtp : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task loop;
        private bool rejectRecipient = true;
        public int Port { get; }
        public ConcurrentBag<string> Messages { get; } = [];
        public TestSmtp() { listener.Start(); Port = ((IPEndPoint)listener.LocalEndpoint).Port; loop = Serve(); }
        private async Task Serve()
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                    using var reader = new StreamReader(client.GetStream(), Encoding.ASCII);
                    using var writer = new StreamWriter(client.GetStream(), Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                    await writer.WriteLineAsync("220 localhost test SMTP");
                    while (await reader.ReadLineAsync(cancellation.Token) is { } line)
                    {
                        if (line.StartsWith("QUIT")) { await writer.WriteLineAsync("221 bye"); break; }
                        if (line.StartsWith("RCPT") && rejectRecipient) { rejectRecipient = false; await writer.WriteLineAsync("550 temporary test rejection"); continue; }
                        if (line == "DATA")
                        {
                            await writer.WriteLineAsync("354 send data"); var body = new StringBuilder();
                            while (await reader.ReadLineAsync(cancellation.Token) is { } data && data != ".") body.AppendLine(data);
                            Messages.Add(body.ToString()); await writer.WriteLineAsync("250 accepted");
                        }
                        else await writer.WriteLineAsync("250 OK");
                    }
                }
            }
            catch (Exception) when (cancellation.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync() { cancellation.Cancel(); listener.Stop(); await loop; cancellation.Dispose(); }
    }
}
