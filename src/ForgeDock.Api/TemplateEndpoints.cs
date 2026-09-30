using ForgeDock.Infrastructure;
namespace ForgeDock.Api;
public static class TemplateEndpoints
{
    public static void MapTemplateEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/templates", () => ProjectTemplates.All);
        api.MapGet("/templates/{id}/archive", (string id) => ProjectTemplates.Find(id) is null ? Results.NotFound() : Results.File(ProjectTemplates.Archive(id), "application/zip", $"forgedock-{id}-starter.zip"));
    }
}
