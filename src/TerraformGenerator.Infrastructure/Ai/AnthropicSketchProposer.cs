using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Messages;
using TerraformGenerator.Application.Ai;

namespace TerraformGenerator.Infrastructure.Ai;

public sealed class AnthropicSketchProposer(AiOptions options, ILogger<AnthropicSketchProposer> logger) : ISketchProposer
{
    // With no Ai:ApiKey the SDK finds ANTHROPIC_API_KEY or a signed-in profile by itself.
    private readonly IAnthropicClient _client = CreateClient(options);

    private static IAnthropicClient CreateClient(AiOptions options)
    {
        var client = string.IsNullOrWhiteSpace(options.ApiKey) ? new AnthropicClient() : new AnthropicClient { ApiKey = options.ApiKey };
        return string.IsNullOrWhiteSpace(options.BaseUrl) ? client : client.WithOptions(current => current with { BaseUrl = options.BaseUrl });
    }

    public AiStatus Status => new(true, "Anthropic", options.Model, HasCredentials(options) ? null
        : "No ANTHROPIC_API_KEY is set: Claude answers only if this computer is signed in with ant auth login.");

    private static bool HasCredentials(AiOptions options) =>
        !string.IsNullOrWhiteSpace(options.ApiKey)
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"))
        || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN"));

    public async Task<T> AskAsync<T>(string system, string user, JsonObject schema, Func<string, T> parse, CancellationToken cancellationToken)
    {
        var outputSchema = JsonSerializer.SerializeToElement(schema).EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
        BetaMessage response;
        try
        {
            response = await _client.Beta.Messages.Create(new MessageCreateParams
            {
                Model = options.Model,
                MaxTokens = 16000,
                // A declined request is answered by the fallback model inside the same call.
                Betas = ["server-side-fallback-2026-07-01"],
                Fallbacks = new Default(),
                OutputConfig = new BetaOutputConfig
                {
                    Effort = options.Effort,
                    Format = new BetaJsonOutputFormat { Schema = outputSchema },
                },
                System = system,
                Messages = [new() { Role = Role.User, Content = user }],
            }, cancellationToken);
        }
        catch (AnthropicUnauthorizedException)
        {
            throw new ProposalException("Claude did not accept the API key. Set ANTHROPIC_API_KEY (or Ai:ApiKey) and restart Terraform Generator.");
        }
        catch (AnthropicRateLimitException)
        {
            throw new ProposalException("Claude is busy (rate limit). Try again in a minute.");
        }
        catch (AnthropicIOException exception)
        {
            logger.LogWarning(exception, "Claude could not be reached");
            throw new ProposalException("Claude could not be reached. Check the internet connection and try again.");
        }
        catch (AnthropicApiException exception)
        {
            logger.LogWarning(exception, "Claude call failed");
            throw new ProposalException($"Claude could not answer: {exception.Message}");
        }

        if (response.StopReason == "refusal")
        {
            throw new ProposalException("Claude declined this request. Describe the infrastructure in other words.");
        }

        if (response.StopReason == "max_tokens")
        {
            throw new ProposalException("The answer was too long. Ask for a smaller piece and add the rest after.");
        }

        var text = string.Concat(response.Content.Select(block => block.TryPickText(out var part) ? part.Text : ""));
        try
        {
            return parse(text);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Claude returned JSON that does not match the schema");
            throw new ProposalException("Claude's answer could not be read. Try again.");
        }
    }
}
