using static TerraformGenerator.Domain.Catalog;

namespace TerraformGenerator.Domain;

// Copies in a second region, so the data survives a whole region going down: each service keeps a copy where its
// cloud offers one (storage replication, database backups or replicas in the paired region, multi-region locations).
public static class Geo
{
    public const string Key = "geoCopies";

    public static PropertyDefinition Setting() =>
        new(Key, "Copies in a second region", PropertyType.Select, "off",
            [O("off", "No"), O("on", "Yes: the data is copied to the paired region")], null,
            "Storage, databases and tables keep a copy in another region; it costs about twice for those services.", "Reliability");

    public static bool On(SketchModel model) => model.Setting(Key) == "on";

    private static readonly Dictionary<string, string> Pairs = new(StringComparer.Ordinal)
    {
        // Azure.
        ["eastus"] = "westus", ["eastus2"] = "centralus", ["centralus"] = "eastus2", ["southcentralus"] = "northcentralus", ["westus2"] = "westcentralus",
        ["westus3"] = "eastus", ["canadacentral"] = "canadaeast", ["mexicocentral"] = "southcentralus", ["brazilsouth"] = "southcentralus",
        ["northeurope"] = "westeurope", ["westeurope"] = "northeurope", ["uksouth"] = "ukwest",
        // AWS.
        ["us-east-1"] = "us-west-2", ["us-east-2"] = "us-west-2", ["us-west-1"] = "us-east-1", ["us-west-2"] = "us-east-1", ["ca-central-1"] = "ca-west-1",
        ["mx-central-1"] = "us-east-1", ["sa-east-1"] = "us-east-1", ["eu-west-1"] = "eu-central-1", ["eu-central-1"] = "eu-west-1", ["eu-west-2"] = "eu-west-1",
        ["ap-southeast-1"] = "ap-northeast-1", ["ap-northeast-1"] = "ap-southeast-1",
        // Google Cloud.
        ["us-central1"] = "us-east1", ["us-east1"] = "us-central1", ["us-east4"] = "us-central1", ["us-west1"] = "us-central1",
        ["northamerica-northeast1"] = "northamerica-northeast2", ["northamerica-south1"] = "us-central1", ["southamerica-east1"] = "southamerica-west1",
        ["europe-west1"] = "europe-west4", ["europe-west3"] = "europe-west1", ["europe-west2"] = "europe-west1", ["asia-southeast1"] = "asia-east1",
        ["asia-northeast1"] = "asia-northeast2",
    };

    public static string Second(SketchModel model) => Pairs.GetValueOrDefault(model.Settings.Region, model.Settings.Region);

    // Google Cloud's multi-regions for buckets and backups, and Firestore's.
    public static string GcpMultiRegion(SketchModel model) =>
        model.Settings.Region.StartsWith("europe-", StringComparison.Ordinal) ? "EU" : model.Settings.Region.StartsWith("asia-", StringComparison.Ordinal) ? "ASIA" : "US";

    public static string? FirestoreMultiRegion(SketchModel model) => GcpMultiRegion(model) switch
    {
        "US" => "nam5",
        "EU" => "eur3",
        _ => null,
    };

    // Azure storage: a copy in the paired region.
    public static string AzureReplication(SketchModel model, string replication) =>
        !On(model) ? replication : replication switch
        {
            "LRS" => "GRS",
            "ZRS" => "GZRS",
            _ => replication,
        };

    public static IEnumerable<SketchMessage> Check(SketchModel model)
    {
        if (!On(model))
        {
            yield break;
        }

        yield return new(MessageLevel.Info, $"The data is copied to {Second(model)}: storage, database backups or replicas, and tables. The apps themselves run in {model.Settings.Region} only.");
        if (model.Platform == Platforms.Aws && model.OfKind(CloudKinds.S3).Any(bucket => !bucket.Flag("versioning")))
        {
            yield return new(MessageLevel.Info, "Buckets copied to the second region keep every version (S3 replication needs it).");
        }
    }
}
