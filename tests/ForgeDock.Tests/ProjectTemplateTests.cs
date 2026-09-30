using ForgeDock.Application;
using ForgeDock.Infrastructure;
using System.IO.Compression;
namespace ForgeDock.Tests;
public class ProjectTemplateTests
{
    [Fact]
    public void EveryTemplateHasValidDefaultsAndDownloadableSource()
    {
        foreach (var template in ProjectTemplates.All)
        {
            Assert.Empty(ProjectConfiguration.Validate(template.Name, "https://github.com/example/app", "main", template.Dockerfile,
                template.ContainerPort, template.HealthPath, template.DeploymentMode, template.ComposeFile, template.ComposeService,
                template.BuildCommand, template.StartCommand, template.RootDirectory));
            using var stream = new MemoryStream(ProjectTemplates.Archive(template.Id)); using var zip = new ZipArchive(stream);
            Assert.Contains(zip.Entries, entry => entry.FullName == "README.md");
            Assert.True(zip.Entries.Count >= 3); Assert.All(zip.Entries, entry => Assert.True(ProjectConfiguration.IsRepositoryPath(entry.FullName)));
        }
    }
    [Fact]
    public void UnknownTemplatesCannotReadHostPaths() => Assert.Throws<ArgumentException>(() => ProjectTemplates.Archive("../../.env"));
}
