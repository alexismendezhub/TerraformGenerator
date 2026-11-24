using System.Text.Json;
using System.Text.Json.Nodes;
using TerraformGenerator.Domain;

namespace TerraformGenerator.Application.Ai;

// Ai:Provider is "Anthropic" (Claude) or "None" (the Describe button is off). Ai:Model and Ai:Effort tune the call.
public sealed class AiOptions
{
    public string Provider { get; set; } = "Anthropic";
    public string Model { get; set; } = "claude-opus-5-5";
    public string Effort { get; set; } = "medium";
    public string? ApiKey { get; set; }

    // Only to send the calls somewhere else (a proxy or a local test double); empty means Anthropic.
    public string? BaseUrl { get; set; }
}

public sealed record AiStatus(bool Enabled, string Provider, string Model, string? Problem);

public sealed class ProposalException(string message) : Exception(message);

// One structured question to the AI: instructions, the question and the JSON schema of the answer, which parse reads.
public interface ISketchProposer
{
    AiStatus Status { get; }

    Task<T> AskAsync<T>(string system, string user, JsonObject schema, Func<string, T> parse, CancellationToken cancellationToken);
}

public static class SketchProposerExtensions
{
    public static Task<ProposalDraft> ProposeAsync(this ISketchProposer proposer, string description, Sketch current, CancellationToken cancellationToken) =>
        proposer.AskAsync(ProposalPrompt.System(current.Settings.Platform), ProposalPrompt.User(description, current), ProposalPrompt.Schema(), ProposalPrompt.Parse, cancellationToken);

    public static Task<ProposalDraft> ImportAsync(this ISketchProposer proposer, string terraform, Sketch current, CancellationToken cancellationToken) =>
        proposer.AskAsync(ProposalPrompt.System(PlatformOf(terraform, current)), ProposalPrompt.ImportUser(terraform, current), ProposalPrompt.Schema(), ProposalPrompt.Parse, cancellationToken);

    private static string PlatformOf(string terraform, Sketch current) => TerraformImporter.DetectPlatform(terraform) ?? current.Settings.Platform;

    public static Task<RecommendationDraft> RecommendAsync(this ISketchProposer proposer, Sketch sketch, IEnumerable<SketchMessage> checks, string language, CancellationToken cancellationToken) =>
        proposer.AskAsync(RecommendationPrompt.System(), RecommendationPrompt.User(sketch, checks, language), RecommendationPrompt.Schema(), RecommendationPrompt.Parse, cancellationToken);
}

public sealed class DisabledSketchProposer(string provider, string reason) : ISketchProposer
{
    public AiStatus Status { get; } = new(false, provider, "", reason);

    public Task<T> AskAsync<T>(string system, string user, JsonObject schema, Func<string, T> parse, CancellationToken cancellationToken) =>
        throw new ProposalException(Status.Problem!);
}
