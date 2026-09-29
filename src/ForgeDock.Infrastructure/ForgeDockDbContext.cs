using ForgeDock.Domain;
using Microsoft.EntityFrameworkCore;

namespace ForgeDock.Infrastructure;

public sealed class ForgeDockDbContext(DbContextOptions<ForgeDockDbContext> options) : DbContext(options)
{
    public DbSet<ProjectOperation> Operations => Set<ProjectOperation>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<ProjectEnvironment> EnvironmentVariables => Set<ProjectEnvironment>();
    public DbSet<DeploymentLog> Logs => Set<DeploymentLog>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<ProjectOperation>().Property(o => o.Kind).HasConversion<string>();
        model.Entity<ProjectOperation>().Property(o => o.State).HasConversion<string>();
        model.Entity<ProjectOperation>().HasIndex(o => o.State);
        model.Entity<ProjectEnvironment>().HasKey(e => new { e.ProjectId, e.Name });
        model.Entity<ProjectEnvironment>().HasOne<Project>().WithMany().HasForeignKey(e => e.ProjectId).OnDelete(DeleteBehavior.Cascade);
        model.Entity<Project>().Property(p => p.DeploymentMode).HasConversion<string>();
        model.Entity<Project>().Property(p => p.Name).HasMaxLength(100);
        model.Entity<Deployment>().Property(d => d.ServiceStatusJson).HasDefaultValue("[]");
        model.Entity<Deployment>().Property(d => d.State).HasConversion<string>();
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
}
