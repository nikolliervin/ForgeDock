using System.Text.Json.Nodes;

namespace ForgeDock.Infrastructure;

// Only allowlisted structural metadata crosses the API boundary: never environment,
// commands, external network names, or host bind paths from the retained manifest.
public sealed record ComposeTopology(string EntryService, IReadOnlyList<TopologyService> Services)
{
    public static ComposeTopology Read(string? manifest, string entryService)
    {
        if (manifest is null) return new(entryService, []);
        var services = JsonNode.Parse(manifest)?["services"] as JsonObject;
        if (services is null) return new(entryService, []);
        string[] Keys(JsonNode? node) => node switch
        {
            JsonObject obj => obj.Select(p => p.Key).Order().ToArray(),
            JsonArray array => array.OfType<JsonValue>().Select(v => v.GetValue<string>()).Order().ToArray(),
            _ => [],
        };
        return new(entryService, services.Select(pair => new TopologyService(
            pair.Key,
            Keys(pair.Value?["depends_on"]).Where(name => name != pair.Key && services.ContainsKey(name)).ToArray(),
            Keys(pair.Value?["networks"]),
            (pair.Value?["volumes"] as JsonArray ?? []).OfType<JsonObject>()
                .Where(v => v["type"]?.GetValue<string>() == "volume")
                .Select(v => v["source"]?.GetValue<string>()).OfType<string>().Distinct().Order().ToArray()
        )).OrderBy(s => s.Name).ToArray());
    }
}

public sealed record TopologyService(string Name, string[] Dependencies, string[] Networks, string[] Volumes);
