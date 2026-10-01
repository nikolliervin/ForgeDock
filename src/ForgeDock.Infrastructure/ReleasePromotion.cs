using ForgeDock.Domain;
namespace ForgeDock.Infrastructure;
public static class ReleasePromotion
{
    public static Deployment Create(Project sourceProject, Deployment source, Project target, IEnumerable<ProjectEnvironment> environment)
    {
        if (sourceProject.Id == target.Id || source.ProjectId != sourceProject.Id || string.IsNullOrEmpty(target.ApplicationName)
            || target.ApplicationName != sourceProject.ApplicationName) throw new InvalidOperationException("Choose a different environment in the same application group.");
        if (source.State is not (DeploymentState.Running or DeploymentState.Stopped) || source.ImageTag is null)
            throw new InvalidOperationException("Choose a successful deployment with a retained image.");
        var original = DeploymentSnapshot.Deserialize(source.ConfigurationJson);
        if (original.DeploymentMode == DeploymentMode.Compose || target.DeploymentMode == DeploymentMode.Compose)
            throw new InvalidOperationException("Image promotion currently supports Auto and Dockerfile applications. Compose stacks must deploy independently.");
        static string Repository(string url) => url.TrimEnd('/').EndsWith(".git", StringComparison.Ordinal) ? url.TrimEnd('/')[..^4] : url.TrimEnd('/');
        if (Repository(original.RepositoryUrl) != Repository(target.RepositoryUrl)) throw new InvalidOperationException("Environment repositories must match for promotion.");
        return new Deployment { ProjectId = target.Id, RollbackSourceId = source.Id, ImageTag = source.ImageTag, Trigger = "Promotion",
            CommitSha = source.CommitSha, CommitMessage = source.CommitMessage, CommitAuthor = source.CommitAuthor,
            ConfigurationJson = DeploymentSnapshot.Create(target, environment).Serialize() };
    }
}
