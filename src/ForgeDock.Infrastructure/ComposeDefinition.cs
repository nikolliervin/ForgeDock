using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ForgeDock.Infrastructure;

public static partial class ComposeDefinition
{
    public static string StackName(Guid projectId) => $"forgedock-{projectId:N}";
    public static string ContainerName(Guid projectId, string service) => $"{StackName(projectId)}-{service}";
    public static string ImageName(Guid projectId, Guid deploymentId, string service) =>
        $"forgedock/{projectId:N}/{service.ToLowerInvariant()}:{deploymentId:N}";

    public static JsonObject Normalize(string json, string source, Guid projectId, Guid deploymentId,
        string routedService, string platformNetwork)
    {
        var model = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidOperationException("Compose configuration is empty.");
        var services = model["services"]?.AsObject() ?? throw new InvalidOperationException("Compose file must define services.");
        if (services.Count == 0 || !services.ContainsKey(routedService))
            throw new InvalidOperationException($"Compose service '{routedService}' does not exist.");
        model["name"] = StackName(projectId);
        foreach (var (name, node) in services)
        {
            RequireName(name);
            var service = node?.AsObject() ?? throw new InvalidOperationException($"Service '{name}' is empty.");
            foreach (var key in new[] { "network_mode", "devices", "device_cgroup_rules", "volumes_from", "external_links", "cap_add", "use_api_socket", "post_start", "pre_stop", "credential_spec" })
                if (service.ContainsKey(key)) throw new InvalidOperationException($"Compose service '{name}' uses unsupported host access: {key}.");
            if (service["privileged"]?.GetValue<bool>() == true || service["pid"]?.GetValue<string>() == "host" ||
                service["ipc"]?.GetValue<string>() == "host") throw new InvalidOperationException("Privileged containers and host namespaces are not supported.");
            if (service["profiles"] is JsonArray { Count: > 0 } || service["deploy"]?["replicas"]?.GetValue<int>() is > 1 ||
                service["scale"]?.GetValue<int>() is > 1) throw new InvalidOperationException("Compose profiles and multiple replicas are not supported yet.");
            foreach (var key in new[] { "pid", "ipc", "ports", "develop", "pull_policy", "attach", "provider" }) service.Remove(key);
            service["container_name"] = ContainerName(projectId, name);
            service["labels"] = Labels(service["labels"], projectId);
            service["cap_drop"] ??= new JsonArray("NET_RAW", "MKNOD", "AUDIT_WRITE", "SETFCAP");
            service["security_opt"] = new JsonArray("no-new-privileges:true");
            service["mem_limit"] ??= "2g";
            service["cpus"] ??= 1.0;
            service["pids_limit"] ??= 256;
            if (service["build"] is JsonObject build)
            {
                var context = build["context"]?.GetValue<string>() ?? source;
                build["context"] = RepositoryPath(source, context);
                if (build.ContainsKey("dockerfile")) RepositoryPath(source,
                    Path.Combine(build["context"]!.GetValue<string>(), build["dockerfile"]!.GetValue<string>()));
                foreach (var key in new[] { "ssh", "entitlements", "additional_contexts", "outputs", "cache_to" })
                    if (build.ContainsKey(key)) throw new InvalidOperationException($"Unsupported Compose build feature: {key}.");
                if (build["network"]?.GetValue<string>() == "host") throw new InvalidOperationException("Host networking during builds is not supported.");
                build["labels"] = Labels(build["labels"], projectId);
                service["image"] = ImageName(projectId, deploymentId, name);
            }
            else if (service["image"] is null) throw new InvalidOperationException($"Service '{name}' needs an image or build configuration.");
            if (service["volumes"] is JsonArray mounts)
                foreach (var mount in mounts.OfType<JsonObject>())
                {
                    var type = mount["type"]?.GetValue<string>();
                    if (type == "bind")
                    {
                        mount["source"] = RepositoryPath(source, mount["source"]!.GetValue<string>());
                        mount["bind"] = new JsonObject { ["create_host_path"] = false, ["selinux"] = "z" };
                    }
                    else if (type != "volume" || string.IsNullOrEmpty(mount["source"]?.GetValue<string>()))
                        throw new InvalidOperationException("Only repository bind mounts and named volumes are supported.");
                }
            if (service["env_file"] is JsonArray files)
                foreach (var file in files)
                {
                    var path = file is JsonObject item ? item["path"]!.GetValue<string>() : file!.GetValue<string>();
                    RepositoryPath(source, path);
                }
            service["networks"] ??= new JsonObject { ["default"] = null };
        }
        NormalizeResources(model, "volumes", projectId);
        model["networks"] ??= new JsonObject { ["default"] = new JsonObject() };
        NormalizeResources(model, "networks", projectId);
        var networks = model["networks"]!.AsObject();
        if (networks.ContainsKey("forgedock_ingress")) throw new InvalidOperationException("The network key forgedock_ingress is reserved.");
        networks["forgedock_ingress"] = new JsonObject { ["external"] = true, ["name"] = platformNetwork };
        services[routedService]!["networks"]!.AsObject()["forgedock_ingress"] = new JsonObject();
        foreach (var type in new[] { "secrets", "configs" })
            if (model[type] is JsonObject resources)
                foreach (var (_, node) in resources)
                {
                    var resource = node!.AsObject();
                    if (resource["external"]?.GetValue<bool>() == true || resource.ContainsKey("environment") || resource.ContainsKey("content"))
                        throw new InvalidOperationException("Compose secrets/configs must reference repository files.");
                    resource["file"] = RepositoryPath(source, resource["file"]!.GetValue<string>());
                }
        return model;
    }

    private static void NormalizeResources(JsonObject model, string type, Guid projectId)
    {
        if (model[type] is not JsonObject resources) return;
        foreach (var (name, node) in resources.ToList())
        {
            RequireName(name);
            var resource = node?.AsObject() ?? new JsonObject();
            if (resource["external"]?.GetValue<bool>() == true || resource.ContainsKey("driver_opts") ||
                (resource["driver"] is JsonValue driver && driver.GetValue<string>() != (type == "volumes" ? "local" : "bridge")))
                throw new InvalidOperationException("External resources and custom volume/network drivers are not supported.");
            resource["name"] = $"{StackName(projectId)}-{type}-{name}";
            resource["labels"] = Labels(resource["labels"], projectId);
            resources[name] = resource.DeepClone();
        }
    }

    private static JsonObject Labels(JsonNode? existing, Guid projectId)
    {
        var labels = existing is JsonObject value ? (JsonObject)value.DeepClone() : new JsonObject();
        labels["io.forgedock.managed"] = "true";
        labels["io.forgedock.project"] = projectId.ToString();
        return labels;
    }

    public static string RepositoryPath(string root, string value)
    {
        root = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(root, value));
        if (path != root && !path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Compose paths must remain inside the repository.");
        FileSystemInfo? current = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        while (current is not null)
        {
            if (current.LinkTarget is not null) throw new InvalidOperationException("Compose paths must not traverse symbolic links.");
            if (current.FullName == root) break;
            current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent;
        }
        return path;
    }

    private static void RequireName(string name)
    {
        if (!ResourceName().IsMatch(name) || name.Length > 100) throw new InvalidOperationException("Compose resource name is invalid.");
    }
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]*$")]
    private static partial Regex ResourceName();
}
