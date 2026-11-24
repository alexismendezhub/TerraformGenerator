using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraformGenerator.Domain;

// The instructions and the output schema for the AI that proposes a drawing. Built from the catalog, so a new box or
// option reaches the AI without editing the prompt.
public static class ProposalPrompt
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // Box kinds from the full provider list, written the same way for every cloud.
    private const string GenericRules = """
        Boxes from the full Terraform provider list:
        - "kind" is "tf:" followed by the Terraform resource type of the provider (for example tf:aws_s3_bucket, tf:google_pubsub_topic, tf:azurerm_kubernetes_cluster). Use only real resource types of that provider.
        - "props" keys are the resource's own argument names; a field inside a nested block is "block.field" (one level only, for example versioning_configuration.status). Values: plain text, numbers and true/false are written by their type; lists as comma-separated values; maps as key=value, key2=value2; anything else as an HCL expression starting with "=".
        - A field that takes another box's value is "ref:<that box id>.<attribute>" (for example ref:new-1.id or ref:new-2.arn); draw an arrow from the box with the field to the other box.
        - Leave out the fields Terraform Generator fills in by itself: required "name" fields, and on Azure resource_group_name, location and tags; on Google Cloud project, region and labels; on AWS tags.
        - Nested blocks deeper than one level, and blocks that repeat, go in the "_hcl" prop as HCL text for the inside of the resource.
        - Fill every argument the resource requires, and the ones that make it safe (encryption, versioning, private access, backups).
        """;

    // The smart boxes of one cloud and the arrows allowed between them, as JSON for the prompt.
    private static (string Kinds, string Connections) SmartBoxes(string platform)
    {
        var catalog = Catalog.Instance;
        var kinds = catalog.Kinds.Where(kind => kind.Platform == platform).Select(kind => new
        {
            kind.Kind,
            kind.Label,
            kind.Description,
            Properties = kind.Properties.Select(property => new
            {
                property.Key,
                property.Label,
                Type = property.Type.ToString().ToLowerInvariant(),
                property.Default,
                Options = property.Options.Select(option => option.Value),
                property.Pattern,
                property.Help,
            }),
        });
        var names = catalog.Kinds.Where(kind => kind.Platform == platform).Select(kind => kind.Kind).ToHashSet(StringComparer.Ordinal);
        var connections = catalog.Connections.Where(connection => names.Contains(connection.From)).Select(connection => new { connection.From, connection.To, connection.Description });
        return (JsonSerializer.Serialize(kinds, Json), JsonSerializer.Serialize(connections, Json));
    }

    public static string System(string platform = Platforms.Azure)
    {
        if (platform != Platforms.Azure)
        {
            var cloud = Platforms.Get(platform);
            var (smartKinds, smartConnections) = SmartBoxes(platform);
            return $$"""
                You design {{cloud.Label}} architectures in Terraform Generator, a drawing tool that turns boxes and arrows into Terraform ({{cloud.Source}} provider).
                The person describes what they need; you return the whole drawing as JSON in the required schema. They review it and approve or correct it before anything changes, so mark what you assumed instead of guessing silently.

                Smart boxes (kind, options with their default; "toggle" values are "true" or "false"). Each adds its roles, logs, encryption and network by itself:
                {{smartKinds}}

                Arrows between smart boxes (from kind -> to kind, and what each adds):
                {{smartConnections}}

                Use the smart boxes whenever they fit; list only the options that differ from the default. For any other service, use a box from the full provider list:

                {{GenericRules}}

                Settings: project name matches {{Catalog.ProjectNamePattern}}; environment is one of {{string.Join(", ", Catalog.Instance.Environments.Select(item => item.Value))}}; region is one of {{string.Join(", ", cloud.Regions.Select(item => item.Value))}}.
                Other project settings, in "options" as key and value; list only the ones to change: owner, costCenter (tags), state ("azure" means a cloud bucket, "local" this computer){{(platform == Platforms.Gcp ? ", gcpProject (the project id)" : "")}}.

                Rules:
                - Box names match {{Catalog.NamePattern}} and are unique. Use short names that say the job.
                - When a current drawing is given, keep its boxes, ids and arrows unless the person asks to change or remove them, and return the complete drawing. Give new boxes ids like "new-1", "new-2".
                - Prefer managed, serverless services and safe defaults; in production, backups, encryption and private access.
                - Anything the description does not name that you chose anyway goes in that box's "review" as one short sentence; questions for the person go in "questions".
                - Write "summary", "review" and "questions" in the language the person used.
                """;
        }

        var catalog = Catalog.Instance;
        var (kinds, connections) = SmartBoxes(Platforms.Azure);

        return $$"""
            You design Azure architectures in Terraform Generator, a drawing tool that turns boxes and arrows into Terraform and Bicep.
            The person describes what they need; you return the whole drawing as JSON in the required schema. They review it and approve or correct it before anything changes, so mark what you assumed instead of guessing silently.

            Boxes you can use (kind, options with their default; "toggle" values are "true" or "false"):
            {{kinds}}

            Arrows you can draw (from kind -> to kind, and what each adds):
            {{connections}}

            Settings: project name matches {{Catalog.ProjectNamePattern}}; environment is one of {{string.Join(", ", catalog.Environments.Select(item => item.Value))}}; region is one of {{string.Join(", ", catalog.Regions.Select(item => item.Value))}}.
            Other project settings, in "options" as key and value; list only the ones to change: {{JsonSerializer.Serialize(catalog.Settings.Select(setting => new { setting.Key, setting.Label, Values = setting.Options.Select(option => option.Value), setting.Default, setting.Pattern }), Json)}}

            Rules:
            - Box names match {{Catalog.NamePattern}} and are unique. Use short names that say the job: web, api, orders, files, secrets.
            - Use only the kinds, option keys and option values listed. List only the options that differ from the default.
            - The SQL server, App Service plans, the Container Apps environment, function storage, network, identities, roles and certificates are added automatically: never draw them.
            - When a current drawing is given, keep its boxes, ids and arrows unless the person asks to change or remove them, and return the complete drawing, not only the changes. Give new boxes ids like "new-1", "new-2".
            - Arrows from an app to the things it uses; Front Door to the apps it serves (each app behind one Front Door needs its own path, for example /* and /api/*); a DNS zone to its Front Door. Apps must not call each other in a circle.
            - Production: at least 14 days of database backups, ZRS or GRS storage, Key Vault purge protection, S1 or Premium app plans, Standard or Premium Redis, monitoring on, a budget alert, the state in Azure.
            - Anything the description does not name that you chose anyway (a runtime, a size, private access, a domain, a setting) goes in that box's "review" (or "questions" for project settings) as one short sentence. Leave "review" empty when the person asked for it.
            - Use these smart boxes whenever they fit. For any other Azure service (virtual machines, AKS, Event Hubs, API Management...), use a box from the full azurerm list as below.
            - Write "summary", "review" and "questions" in the language the person used.

            {{GenericRules}}
            """;
    }

    public static string User(string description, Sketch current) =>
        current.Nodes.Count == 0
            ? $"Description:\n{description.Trim()}\n\nThere is no current drawing: start a new one."
            : $"Description:\n{description.Trim()}\n\nCurrent drawing (keep what still fits):\n{JsonSerializer.Serialize(Current(current), Json)}";

    // Existing Terraform to draw: the AI maps resources to boxes and lists what it cannot draw.
    public static string ImportUser(string terraform, Sketch current) =>
        "Draw this existing Terraform as a new Terraform Generator drawing. Make a box for each app, database, storage, cache, queue, vault, registry, Front Door and DNS zone, " +
        "and arrows for what each app uses (app settings, role assignments, Front Door origins, custom domains). Resources Terraform Generator adds by itself " +
        "(plans, servers, identities, roles, networks, private endpoints, DNS records, diagnostic settings) are not boxes: a private endpoint means Private only. " +
        "Keep the resource names, shortened to the name rules. Put every resource you could not draw in \"questions\", and every value you guessed in that box's \"review\"." +
        (current.Nodes.Count == 0 ? "" : " Ignore the current drawing.") +
        $"\n\nTerraform:\n{terraform}";

    private static object Current(Sketch sketch) => new
    {
        sketch.Settings,
        Nodes = sketch.Nodes.Select(node => new { node.Id, node.Kind, node.Name, Props = node.Props.Select(prop => new { prop.Key, prop.Value }) }),
        Edges = sketch.Edges.Select(edge => new { edge.From, edge.To }),
    };

    // The JSON schema of ProposalDraft for structured outputs: every object closed and every field required.
    public static JsonObject Schema()
    {
        static JsonObject Obj(JsonObject properties) => new()
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(properties.Select(pair => (JsonNode)pair.Key).ToArray()),
            ["additionalProperties"] = false,
        };
        static JsonObject Str() => new() { ["type"] = "string" };
        static JsonObject Enum(IEnumerable<string> values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(value => (JsonNode)value).ToArray()) };
        static JsonObject Array(JsonObject items) => new() { ["type"] = "array", ["items"] = items };

        var catalog = Catalog.Instance;
        return Obj(new JsonObject
        {
            ["summary"] = Str(),
            ["settings"] = Obj(new JsonObject
            {
                ["name"] = Str(),
                ["environment"] = Enum(catalog.Environments.Select(item => item.Value)),
                ["region"] = Str(),
                ["platform"] = Enum(Platforms.All.Select(item => item.Id)),
                ["monitoring"] = new JsonObject { ["type"] = "boolean" },
                ["options"] = Array(Obj(new JsonObject { ["key"] = Enum(catalog.Settings.Select(setting => setting.Key)), ["value"] = Str() })),
            }),
            ["nodes"] = Array(Obj(new JsonObject
            {
                ["id"] = Str(),
                ["kind"] = Str(),
                ["name"] = Str(),
                ["props"] = Array(Obj(new JsonObject { ["key"] = Str(), ["value"] = Str() })),
                ["review"] = Str(),
            })),
            ["edges"] = Array(Obj(new JsonObject { ["from"] = Str(), ["to"] = Str() })),
            ["questions"] = Array(Str()),
        });
    }

    public static ProposalDraft Parse(string json) =>
        JsonSerializer.Deserialize<ProposalDraft>(json, Json) ?? throw new JsonException("The AI returned an empty proposal.");
}
