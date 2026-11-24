using TerraformGenerator.Application.Ai;
using TerraformGenerator.Infrastructure.Ai;
using TerraformGenerator.Infrastructure.Terraform;

namespace TerraformGenerator.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Terraform (runner, formatter, provider schemas) and the AI proposer.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<TerraformRunner>();
        services.AddSingleton<ProviderSchemas>();
        services.AddSingleton<TerraformFormatter>();

        var ai = configuration.GetSection("Ai").Get<AiOptions>() ?? new AiOptions();
        services.AddSingleton<ISketchProposer>(provider => ai.Provider switch
        {
            "Anthropic" => new AnthropicSketchProposer(ai, provider.GetRequiredService<ILogger<AnthropicSketchProposer>>()),
            "None" => new DisabledSketchProposer("None", "The AI is turned off (Ai:Provider is None)."),
            _ => new DisabledSketchProposer(ai.Provider, $"Ai:Provider \"{ai.Provider}\" is not known: use Anthropic or None."),
        });
        return services;
    }
}
