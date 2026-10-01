using ForgeDock.Domain;
using Microsoft.EntityFrameworkCore;

namespace ForgeDock.Infrastructure;

public sealed class ForgeDockDbContext(DbContextOptions<ForgeDockDbContext> options) : DbContext(options)
{
    public DbSet<DeploymentHook> DeploymentHooks => Set<DeploymentHook>();
    public DbSet<StoragePolicy> StoragePolicies => Set<StoragePolicy>();
    public DbSet<StorageCleanup> StorageCleanups => Set<StorageCleanup>();
    public DbSet<ResourceAlert> ResourceAlerts => Set<ResourceAlert>();
    public DbSet<ResourceObservation> ResourceObservations => Set<ResourceObservation>();
    public DbSet<PreviewEnvironment> PreviewEnvironments => Set<PreviewEnvironment>();
    public DbSet<DatabaseBackup> DatabaseBackups => Set<DatabaseBackup>();
    public DbSet<DatabaseService> DatabaseServices => Set<DatabaseService>();
    public DbSet<NotificationSettings> NotificationSettings => Set<NotificationSettings>();
    public DbSet<NotificationDelivery> NotificationDeliveries => Set<NotificationDelivery>();
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
        model.Entity<DeploymentHook>().HasOne<Deployment>().WithMany().HasForeignKey(h => h.DeploymentId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<DeploymentHook>().HasIndex(h => new { h.DeploymentId, h.StartedAt });
        model.Entity<Project>().HasIndex(p => new { p.ApplicationName, p.EnvironmentName }).IsUnique();
        model.Entity<Project>().Property(p => p.ApplicationName).HasMaxLength(50);
        model.Entity<Project>().Property(p => p.EnvironmentName).HasMaxLength(50);
        model.Entity<Project>().Property(p => p.CpuLimit).HasDefaultValue(1.0);
        model.Entity<Project>().Property(p => p.MemoryLimitMiB).HasDefaultValue(512);
        model.Entity<Project>().Property(p => p.ResourceAlertsEnabled).HasDefaultValue(true);
        model.Entity<ResourceAlert>().HasOne<Project>().WithMany().HasForeignKey(a => a.ProjectId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<ResourceAlert>().HasIndex(a => new { a.ProjectId, a.CreatedAt });
        model.Entity<ResourceObservation>().HasKey(o => o.ProjectId);
        model.Entity<ResourceObservation>().HasOne<Project>().WithMany().HasForeignKey(o => o.ProjectId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<PreviewEnvironment>().HasKey(p => new { p.ParentProjectId, p.Number });
        model.Entity<PreviewEnvironment>().HasOne<Project>().WithMany().HasForeignKey(p => p.ParentProjectId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<DatabaseBackup>().HasOne<DatabaseService>().WithMany().HasForeignKey(b => b.ServiceId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<DatabaseBackup>().HasIndex(b => new { b.ServiceId, b.CreatedAt });
        model.Entity<DatabaseService>().Property(d => d.Kind).HasConversion<string>();
        model.Entity<DatabaseService>().HasIndex(d => new { d.ProjectId, d.Kind }).IsUnique();
        model.Entity<DatabaseService>().HasOne<Project>().WithMany().HasForeignKey(d => d.ProjectId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<NotificationSettings>().HasKey(n => n.ProjectId);
        model.Entity<NotificationSettings>().HasOne<Project>().WithMany().HasForeignKey(n => n.ProjectId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<NotificationDelivery>().HasIndex(n => new { n.DeploymentId, n.Event, n.Channel }).IsUnique();
        model.Entity<NotificationDelivery>().HasOne<Project>().WithMany().HasForeignKey(n => n.ProjectId).OnDelete(DeleteBehavior.Cascade);
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
