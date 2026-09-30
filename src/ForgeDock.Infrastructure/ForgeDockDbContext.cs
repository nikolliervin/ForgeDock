using ForgeDock.Domain;
using Microsoft.EntityFrameworkCore;

namespace ForgeDock.Infrastructure;

public sealed class ForgeDockDbContext(DbContextOptions<ForgeDockDbContext> options) : DbContext(options)
{
    public DbSet<ProjectWebhook> ProjectWebhooks => Set<ProjectWebhook>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<MetricSample> Metrics => Set<MetricSample>();
    public DbSet<CustomDomain> CustomDomains => Set<CustomDomain>();
    public DbSet<ProjectOperation> Operations => Set<ProjectOperation>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<ProjectEnvironment> EnvironmentVariables => Set<ProjectEnvironment>();
    public DbSet<DeploymentLog> Logs => Set<DeploymentLog>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<ProjectWebhook>().HasKey(w => w.ProjectId);
        model.Entity<ProjectWebhook>().HasOne<Project>().WithMany().HasForeignKey(w => w.ProjectId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<WebhookDelivery>().HasKey(w => new { w.ProjectId, w.DeliveryId });
        model.Entity<WebhookDelivery>().HasIndex(w => new { w.ProjectId, w.ReceivedAt });
        model.Entity<WebhookDelivery>().Property(w => w.Event).HasMaxLength(100);
        model.Entity<WebhookDelivery>().Property(w => w.Status).HasMaxLength(100);
        model.Entity<WebhookDelivery>().HasOne<Project>().WithMany().HasForeignKey(w => w.ProjectId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<MetricSample>().HasIndex(m => new { m.ProjectId, m.Timestamp });
        model.Entity<MetricSample>().HasIndex(m => m.Timestamp);
        model.Entity<MetricSample>().Property(m => m.ContainerId).HasMaxLength(64);
        model.Entity<MetricSample>().Property(m => m.Service).HasMaxLength(200);
        model.Entity<MetricSample>().HasOne<Project>().WithMany().HasForeignKey(m => m.ProjectId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<CustomDomain>().Property(d => d.State).HasConversion<string>();
        model.Entity<CustomDomain>().Property(d => d.Hostname).HasMaxLength(253);
        model.Entity<CustomDomain>().HasIndex(d => d.Hostname).IsUnique();
        model.Entity<CustomDomain>().HasOne<Project>().WithMany().HasForeignKey(d => d.ProjectId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<ProjectOperation>().Property(o => o.Kind).HasConversion<string>();
        model.Entity<ProjectOperation>().Property(o => o.State).HasConversion<string>();
        model.Entity<ProjectOperation>().HasIndex(o => o.State);
        model.Entity<ProjectEnvironment>().HasKey(e => new { e.ProjectId, e.Name });
        model.Entity<ProjectEnvironment>().HasOne<Project>().WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<Project>().Property(p => p.DeploymentMode).HasConversion<string>();
        model.Entity<Project>().Property(p => p.RootDirectory).HasDefaultValue(".");
        model.Entity<Project>().Property(p => p.Name).HasMaxLength(100);
        model.Entity<Deployment>().Property(d => d.Trigger).HasDefaultValue("Manual");
        model.Entity<Deployment>().Property(d => d.ServiceStatusJson).HasDefaultValue("[]");
        model.Entity<Deployment>().Property(d => d.State).HasConversion<string>().IsConcurrencyToken();
        model.Entity<Deployment>().Property(d => d.LastStage).HasConversion<string>().HasDefaultValue(DeploymentState.Queued);
        model.Entity<DeploymentLog>().Property(l => l.Phase).HasDefaultValue("Build");
        model.Entity<Deployment>().HasOne<Project>().WithMany().HasForeignKey(d => d.ProjectId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<Deployment>().HasIndex(d => new { d.ProjectId, d.CreatedAt });
        model.Entity<Deployment>().HasIndex(d => d.State);
        model.Entity<DeploymentLog>().HasOne<Deployment>().WithMany().HasForeignKey(l => l.DeploymentId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<DeploymentLog>().HasIndex(l => new { l.DeploymentId, l.Id });
    }
}

public sealed class DeploymentLog
{
    public long Id { get; set; }
    public Guid DeploymentId { get; set; }
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public string Message { get; set; } = "";
    public string Phase { get; set; } = "Build";
}
