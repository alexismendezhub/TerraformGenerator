using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraformGenerator.Domain;

// The AI reviews a drawing and suggests improvements. A recommendation with a change can be applied: the change goes
// through the same proposal (review, approve or discard) as a description.
public sealed class RecommendationDraft
{
    public string Summary { get; set; } = "";
    public List<Recommendation> Recommendations { get; set; } = [];
}

public sealed class Recommendation
{
    public string Title { get; set; } = "";
    public string Category { get; set; } = "";
    public string Priority { get; set; } = "";
    public string Why { get; set; } = "";

    // An instruction for the drawing, in the words a person would use; empty when it is advice only.
    public string Change { get; set; } = "";
}

public static class RecommendationPrompt
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static readonly string[] Categories = ["security", "reliability", "cost", "performance", "operations"];
    public static readonly string[] Priorities = ["high", "medium", "low"];

    public static string System() => $$"""
        You review Azure architectures drawn in Terraform Generator, a tool that turns boxes and arrows into Terraform and Bicep, for people who do not know infrastructure well.
        Give the most useful recommendations first, between 3 and 8, each one short and concrete: what to do and why it matters for this design (not general advice).

        What Terraform Generator adds by itself, so never recommend it: App Service plans, the SQL server, identities without passwords, roles for every arrow, the private network for Private only resources, private DNS, HTTPS, TLS 1.2, logs and metrics to Log Analytics when monitoring is on, budget alerts when set, Front Door restrictions on web and function apps.

        Boxes, options and arrows it can draw:
        {{JsonSerializer.Serialize(Catalog.Instance.Kinds.Select(kind => new { kind.Kind, kind.Label, Options = kind.Properties.Select(property => new { property.Key, Values = property.Options.Select(option => option.Value), property.Default }) }), Json)}}
        {{JsonSerializer.Serialize(Catalog.Instance.Connections.Select(connection => new { connection.From, connection.To }), Json)}}
        Project settings: {{JsonSerializer.Serialize(Catalog.Instance.Settings.Select(setting => new { setting.Key, Values = setting.Options.Select(option => option.Value), setting.Default }), Json)}}

        Rules:
        - "category" is one of {{string.Join(", ", Categories)}}; "priority" is one of {{string.Join(", ", Priorities)}}.
        - "change" is an instruction that Terraform Generator can draw with the boxes, options and settings above, written as the person would ask for it (for example: "Make the orders database private and keep 14 days of backups"). Leave it empty when the advice needs something Terraform Generator cannot draw (application code, a WAF policy, a second region); say what to do in "why".
        - Respect the environment: development values low cost; production values reliability and security.
        - Do not repeat what the checks below already say unless you add what to do about it.
        - Write "summary", "title", "why" and "change" in the language given.
        """;

    public static string User(Sketch sketch, IEnumerable<SketchMessage> checks, string language) =>
        $"Language: {language}\n\nDrawing:\n{JsonSerializer.Serialize(new { sketch.Settings, Nodes = sketch.Nodes.Select(node => new { node.Kind, node.Name, node.Props }), Edges = sketch.Edges.Select(edge => new { From = sketch.Nodes.FirstOrDefault(node => node.Id == edge.From)?.Name, To = sketch.Nodes.FirstOrDefault(node => node.Id == edge.To)?.Name }) }, Json)}\n\n" +
        $"Checks Terraform Generator already shows:\n{string.Join("\n", checks.Where(check => check.Level != MessageLevel.Info).Select(check => $"- {check.Level}: {check.Text}").DefaultIfEmpty("- none"))}";

    public static JsonObject Schema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["summary"] = new JsonObject { ["type"] = "string" },
            ["recommendations"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["title"] = new JsonObject { ["type"] = "string" },
                        ["category"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Categories.Select(value => (JsonNode)value).ToArray()) },
                        ["priority"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray(Priorities.Select(value => (JsonNode)value).ToArray()) },
                        ["why"] = new JsonObject { ["type"] = "string" },
                        ["change"] = new JsonObject { ["type"] = "string" },
                    },
                    ["required"] = new JsonArray("title", "category", "priority", "why", "change"),
                    ["additionalProperties"] = false,
                },
            },
        },
        ["required"] = new JsonArray("summary", "recommendations"),
        ["additionalProperties"] = false,
    };

    public static RecommendationDraft Parse(string json)
    {
        var draft = JsonSerializer.Deserialize<RecommendationDraft>(json, Json) ?? throw new JsonException("The AI returned no recommendations.");
        draft.Recommendations = draft.Recommendations
            .Where(item => item.Title.Trim().Length > 0)
            .OrderBy(item => global::System.Array.IndexOf(Priorities, item.Priority) is var index and >= 0 ? index : Priorities.Length)
            .ToList();
        return draft;
    }
}
