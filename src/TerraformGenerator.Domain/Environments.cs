using System.Text.Json;
using static TerraformGenerator.Domain.Catalog;

namespace TerraformGenerator.Domain;

// Several environments from one drawing: the same boxes in dev, test, staging and production, each in its own folder
// with its own state and pipeline. A box's choice can differ by environment: "sku@prod" holds the value production
// uses instead of "sku".
public static class Environments
{
    public const string Key = "environments";
    public const char Separator = '@';

    public static PropertyDefinition Setting() =>
        new(Key, "Environments", PropertyType.Select, "one",
        [
            O("one", "Only the environment above"), O("dev,prod", "Development and production"), O("dev,test,prod", "Development, test and production"),
            O("dev,test,staging,prod", "Development, test, staging and production"),
        ], null,
        "Each one gets its own folder, resources and state from this drawing. A box's choices can differ by environment (Values for, in its properties).",
        "Code and deployment");

    public static IReadOnlyList<string> Of(Sketch sketch)
    {
        var chosen = sketch.Settings.Options.GetValueOrDefault(Key, "one");
        return chosen == "one" ? [sketch.Settings.Environment] : chosen.Split(',');
    }

    public static bool Several(Sketch sketch) => Of(sketch).Count > 1;

    // "sku@prod" => ("sku", "prod"); a key without an environment => (key, null).
    public static (string Key, string? Environment) Split(string key)
    {
        var at = key.IndexOf(Separator, StringComparison.Ordinal);
        return at < 0 ? (key, null) : (key[..at], key[(at + 1)..]);
    }

    public static bool IsEnvironment(string value) => Catalog.Instance.Environments.Any(item => item.Value == value);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // The drawing as one environment sees it: that environment, and each box's values for it.
    public static Sketch For(Sketch sketch, string environment)
    {
        var copy = JsonSerializer.Deserialize<Sketch>(JsonSerializer.Serialize(sketch, Json), Json)!;
        copy.Settings.Environment = environment;
        copy.Settings.Options.Remove(Key);
        copy.Settings.Options = Resolve(copy.Settings.Options, environment);
        foreach (var node in copy.Nodes)
        {
            node.Props = Resolve(node.Props, environment);
        }

        return copy;
    }

    private static Dictionary<string, string> Resolve(Dictionary<string, string> values, string environment)
    {
        var resolved = values.Where(pair => !pair.Key.Contains(Separator, StringComparison.Ordinal))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            var (name, scope) = Split(key);
            if (scope == environment)
            {
                resolved[name] = value;
            }
        }

        return resolved;
    }

    // One folder per environment; the pipelines go to the repository's workflows, one per environment.
    public static GenerationResult Generate(Sketch sketch, Func<Sketch, GenerationResult> generateOne)
    {
        var environments = Of(sketch);
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var byEnvironment = new List<(string Environment, GenerationResult Result)>();
        foreach (var environment in environments)
        {
            byEnvironment.Add((environment, generateOne(For(sketch, environment))));
        }

        // A message every environment shares is said once; the others say which environment they are about.
        var messages = byEnvironment
            .SelectMany(item => item.Result.Messages.Select(message => (item.Environment, Message: message)))
            .GroupBy(item => item.Message)
            .Select(group => group.Count() == environments.Count
                ? group.Key
                : group.Key with { Text = $"{string.Join(", ", group.Select(item => item.Environment))}: {group.Key.Text}" })
            .ToList();
        if (messages.Any(message => message.Level == MessageLevel.Error))
        {
            return new GenerationResult(files, messages);
        }

        foreach (var (environment, result) in byEnvironment)
        {
            foreach (var (path, content) in result.Files)
            {
                if (path.StartsWith(".github/workflows/", StringComparison.Ordinal))
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    files[$".github/workflows/{name}-{environment}.yml"] = content.Replace("infra", $"infra/environments/{environment}", StringComparison.Ordinal);
                }
                else
                {
                    files[$"environments/{environment}/{path}"] = content;
                }
            }
        }

        files["README.md"] = Readme(sketch, environments);
        return new GenerationResult(files, messages);
    }

    private static string Readme(Sketch sketch, IReadOnlyList<string> environments) => string.Join('\n',
    [
        $"# {sketch.Settings.Name}: {string.Join(", ", environments)}",
        "",
        "Generated by Terraform Generator from one drawing (`sketch.json`). Each environment has its own folder, with its own resources, Terraform state and README:",
        "",
        .. environments.Select(environment => $"- `environments/{environment}`: open its README.md and run `./deploy.ps1` there."),
        "",
        "Each environment folder also has a copy of `sketch.json`: to bring an environment back into Terraform Generator with **Import Terraform**, choose its `.tf` files and that `sketch.json`, and the boxes keep their places.",
        "",
        "Deploy them in order (" + string.Join(", then ", environments) + ") so a change is tried before it reaches production.",
        "",
        "To deploy from GitHub, put this folder in an `infra` folder of your repository: each environment has its own workflow in `.github/workflows`, and its own GitHub environment where you can require an approval.",
        "",
    ]);
}
