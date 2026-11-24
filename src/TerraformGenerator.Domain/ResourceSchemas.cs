using System.Text.Json;

namespace TerraformGenerator.Domain;

// What a Terraform provider says about each resource it can create, slimmed down to what the screen and the
// generator need: every field with its type, whether it is required, and the nested blocks.
public sealed record SchemaAttribute(string Name, string Type, bool Required, bool Optional, bool Computed, bool Sensitive, string? Description);

public sealed record SchemaBlock(string Name, string Nesting, int Min, int Max, List<SchemaAttribute> Attributes, List<SchemaBlock> Blocks, string? Description);

public sealed record ResourceSchema(string Type, string Platform, string? Description, List<SchemaAttribute> Attributes, List<SchemaBlock> Blocks)
{
    // Fields a person can set (not the ones Azure, AWS or Google fill in by themselves).
    public IEnumerable<SchemaAttribute> Settable => Attributes.Where(attribute => attribute.Required || attribute.Optional);
}

// A search hit: the type and a readable title made from it.
public sealed record ResourceSummary(string Type, string Title, string Short);

public interface IResourceSchemas
{
    ResourceSchema? Find(string platform, string type);
}

// The three clouds Terraform Generator draws, the Terraform provider of each, and their regions.
public sealed record Platform(string Id, string Label, string Provider, string Source, string Version, string Prefix, IReadOnlyList<PropertyOption> Regions);

public static class Platforms
{
    public const string Azure = "azure";
    public const string Aws = "aws";
    public const string Gcp = "gcp";

    private static PropertyOption O(string value, string label) => new(value, label);

    public static readonly IReadOnlyList<Platform> All =
    [
        new(Azure, "Microsoft Azure", "azurerm", "hashicorp/azurerm", "~> 4.0", "azurerm_", Catalog.Instance.Regions),
        new(Aws, "Amazon Web Services (AWS)", "aws", "hashicorp/aws", "~> 6.0", "aws_",
        [
            O("us-east-1", "US East (N. Virginia)"), O("us-east-2", "US East (Ohio)"), O("us-west-1", "US West (N. California)"),
            O("us-west-2", "US West (Oregon)"), O("ca-central-1", "Canada (Central)"), O("mx-central-1", "Mexico (Central)"),
            O("sa-east-1", "South America (São Paulo)"), O("eu-west-1", "Europe (Ireland)"), O("eu-central-1", "Europe (Frankfurt)"),
            O("eu-west-2", "Europe (London)"), O("ap-southeast-1", "Asia Pacific (Singapore)"), O("ap-northeast-1", "Asia Pacific (Tokyo)"),
        ]),
        new(Gcp, "Google Cloud (GCP)", "google", "hashicorp/google", "~> 7.0", "google_",
        [
            O("us-central1", "Iowa (us-central1)"), O("us-east1", "South Carolina (us-east1)"), O("us-east4", "Virginia (us-east4)"),
            O("us-west1", "Oregon (us-west1)"), O("northamerica-northeast1", "Montréal"), O("northamerica-south1", "Querétaro (Mexico)"),
            O("southamerica-east1", "São Paulo"), O("europe-west1", "Belgium"), O("europe-west3", "Frankfurt"), O("europe-west2", "London"),
            O("asia-southeast1", "Singapore"), O("asia-northeast1", "Tokyo"),
        ]),
    ];

    public static Platform Get(string id) => All.FirstOrDefault(platform => platform.Id == id) ?? All[0];

    // aws_s3_bucket => "S3 Bucket" and "S3"; google_cloud_run_v2_service => "Cloud Run V2 Service" and "CRV".
    public static ResourceSummary Summarize(string type)
    {
        var platform = All.FirstOrDefault(item => type.StartsWith(item.Prefix, StringComparison.Ordinal)) ?? All[0];
        var words = type[platform.Prefix.Length..].Split('_', StringSplitOptions.RemoveEmptyEntries);
        var title = string.Join(' ', words.Select(Word));
        var shortName = Special.TryGetValue(words[0], out var special)
            ? string.Concat(special.Where(char.IsUpper).Take(4))
            : words.Length == 1
            ? words[0][..Math.Min(3, words[0].Length)].ToUpperInvariant()
            : string.Concat(words.Take(3).Select(word => char.ToUpperInvariant(word[0])));
        return new ResourceSummary(type, title, shortName);
    }

    private static readonly HashSet<string> Upper = new(StringComparer.Ordinal)
    {
        "s3", "ec2", "ecs", "eks", "rds", "sqs", "sns", "iam", "vpc", "kms", "acm", "dns", "ip", "ssl", "api", "sql", "id", "db", "ai", "ml",
        "vm", "nat", "cdn", "waf", "emr", "efs", "ebs", "elb", "lb", "ses", "ssm", "gke", "url", "http", "https", "tls", "v2",
    };

    // Service names written their own way.
    private static readonly Dictionary<string, string> Special = new(StringComparer.Ordinal)
    {
        ["dynamodb"] = "DynamoDB", ["cloudfront"] = "CloudFront", ["cloudwatch"] = "CloudWatch", ["elasticache"] = "ElastiCache", ["opensearch"] = "OpenSearch",
        ["bigquery"] = "BigQuery", ["pubsub"] = "PubSub", ["cosmosdb"] = "CosmosDB", ["apigatewayv2"] = "APIGatewayV2",
    };

    private static string Word(string word) =>
        Special.TryGetValue(word, out var special) ? special : Upper.Contains(word) ? word.ToUpperInvariant() : char.ToUpperInvariant(word[0]) + word[1..];
}

// Reads the output of "terraform providers schema -json" into the slim schema.
public static class SchemaReader
{
    public static Dictionary<string, ResourceSchema> Read(string platformId, JsonDocument document)
    {
        var platform = Platforms.Get(platformId);
        var result = new Dictionary<string, ResourceSchema>(StringComparer.Ordinal);
        foreach (var provider in document.RootElement.GetProperty("provider_schemas").EnumerateObject())
        {
            if (!provider.Name.EndsWith("/" + platform.Source, StringComparison.Ordinal)
                || !provider.Value.TryGetProperty("resource_schemas", out var resources))
            {
                continue;
            }

            foreach (var resource in resources.EnumerateObject())
            {
                var block = ReadBlock(resource.Name, "single", 1, 1, resource.Value.GetProperty("block"));
                result[resource.Name] = new ResourceSchema(resource.Name, platformId, block.Description, block.Attributes, block.Blocks);
            }
        }

        return result;
    }

    private static SchemaBlock ReadBlock(string name, string nesting, int min, int max, JsonElement block)
    {
        var attributes = new List<SchemaAttribute>();
        if (block.TryGetProperty("attributes", out var attributeList))
        {
            foreach (var attribute in attributeList.EnumerateObject())
            {
                var value = attribute.Value;
                attributes.Add(new SchemaAttribute(
                    attribute.Name,
                    TypeOf(value.TryGetProperty("type", out var type) ? type : default),
                    Flag(value, "required"),
                    Flag(value, "optional"),
                    Flag(value, "computed"),
                    Flag(value, "sensitive"),
                    Text(value, "description")));
            }
        }

        var blocks = new List<SchemaBlock>();
        if (block.TryGetProperty("block_types", out var blockList))
        {
            foreach (var nested in blockList.EnumerateObject())
            {
                // Timeouts are how long Terraform waits; never something to design.
                if (nested.Name == "timeouts")
                {
                    continue;
                }

                var value = nested.Value;
                blocks.Add(ReadBlock(
                    nested.Name,
                    Text(value, "nesting_mode") ?? "list",
                    value.TryGetProperty("min_items", out var minItems) ? minItems.GetInt32() : 0,
                    value.TryGetProperty("max_items", out var maxItems) ? maxItems.GetInt32() : 0,
                    value.GetProperty("block")));
            }
        }

        return new SchemaBlock(name, nesting, min, max, attributes.OrderBy(attribute => attribute.Name, StringComparer.Ordinal).ToList(),
            blocks.OrderBy(item => item.Name, StringComparer.Ordinal).ToList(), Text(block, "description"));
    }

    private static bool Flag(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text ? text : null;

    // "string", "number", "bool", ["list","string"], ["map","string"], ["object",{...}] => string, number, bool, list(string), map(string), object.
    private static string TypeOf(JsonElement type) => type.ValueKind switch
    {
        JsonValueKind.String => type.GetString()!,
        JsonValueKind.Array when type.GetArrayLength() == 2 && type[0].GetString() is "list" or "set" or "map" =>
            $"{type[0].GetString()}({TypeOf(type[1])})",
        JsonValueKind.Array => type[0].GetString() ?? "dynamic",
        _ => "dynamic",
    };
}
