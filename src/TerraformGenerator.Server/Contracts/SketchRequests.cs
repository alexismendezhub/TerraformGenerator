using System.Text.Json;
using TerraformGenerator.Domain;

namespace TerraformGenerator.Server.Contracts;

// Current is what the AI starts from (the drawing, or the proposal being corrected); Baseline is the drawing before any proposal.
public sealed record ProposalRequest(string? Description, Sketch Current, bool StartFresh, Sketch? Baseline)
{
    public const int MaxDescription = 4000;
}

public sealed record RecommendRequest(Sketch Sketch, string? Language);

// Terraform is every chosen .tf file joined; Current is the drawing it replaces once approved; Layout is the sketch.json
// chosen with them, whose boxes give the positions.
public sealed record ImportRequest(string? Terraform, bool UseAi, Sketch Current, Sketch? Layout = null)
{
    public const int MaxLength = 2_000_000;
}

public static class SketchJson
{
    // The saved drawing: the same shape the screen sends, readable in a text editor.
    public static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}
