namespace TerraformGenerator.Domain;

// What the person drew: global settings, the boxes and the arrows between them.
public sealed class Sketch
{
    public SketchSettings Settings { get; set; } = new();
    public List<SketchNode> Nodes { get; set; } = [];
    public List<SketchEdge> Edges { get; set; } = [];
}

public sealed class SketchSettings
{
    public string Platform { get; set; } = "azure";
    public string Name { get; set; } = "myapp";
    public string Environment { get; set; } = "dev";
    public string Region { get; set; } = "eastus2";
    public bool Monitoring { get; set; } = true;

    // Output, state, pipeline and budget choices (SettingKeys); missing ones take the catalog default.
    public Dictionary<string, string> Options { get; set; } = [];
}

public sealed class SketchNode
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public Dictionary<string, string> Props { get; set; } = [];
}

// An arrow: "From uses To" (an app uses a database, Front Door serves an app, a DNS zone points to Front Door).
public sealed class SketchEdge
{
    public string Id { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

public enum MessageLevel
{
    Info,
    Warning,
    Error,
}

public sealed record SketchMessage(MessageLevel Level, string Text, string? NodeId = null, string? EdgeId = null);

public sealed record GenerationResult(IReadOnlyDictionary<string, string> Files, IReadOnlyList<SketchMessage> Messages)
{
    public bool HasErrors => Messages.Any(message => message.Level == MessageLevel.Error);
}
