using static TerraformGenerator.Domain.Catalog;

namespace TerraformGenerator.Domain;

// The data encrypted with keys of your own (rotated every year or 90 days) instead of the cloud's: Key Vault on Azure
// (storage and SQL), KMS on AWS (S3, DynamoDB, SQS, RDS, Secrets Manager), Cloud KMS on Google Cloud (buckets,
// Pub/Sub, Cloud SQL). Services not listed keep the cloud's keys.
public static class OwnKeys
{
    public const string Key = "ownKeys";

    public static PropertyDefinition Setting() =>
        new(Key, "Encryption keys of your own", PropertyType.Select, "off",
            [O("off", "No: the cloud's keys"), O("on", "Yes: keys of this project, rotated automatically")], null,
            "Some rules (HIPAA, PCI) ask for them. Azure: storage and SQL. AWS: S3, DynamoDB, SQS, RDS, Secrets Manager. Google Cloud: buckets, Pub/Sub, Cloud SQL.",
            "Security");

    public static bool On(SketchModel model) => model.Setting(Key) == "on";

    public static bool Azure(SketchModel model) => On(model) && model.IsAzure && (model.StorageAccounts.Any() || model.Databases.Any());

    // AWS --------------------------------------------------------------------------------------------------------------

    public static bool Aws(SketchModel model) => On(model) && model.Platform == Platforms.Aws &&
        new[] { CloudKinds.S3, CloudKinds.DynamoDb, CloudKinds.Sqs, CloudKinds.Rds, CloudKinds.AwsSecret }.Any(kind => model.OfKind(kind).Any());

    public const string AwsKey = "aws_kms_key.main";
    public const string AwsSecondKey = "aws_kms_replica_key.second";

    // A bucket CloudFront reads keeps S3's keys: CloudFront cannot use the project's key without changing its policy.
    public static bool AwsBucket(SketchModel model, SketchResource bucket) => Aws(model) && !model.Sources(bucket, CloudKinds.CloudFront).Any();

    public static IEnumerable<HclItem> AwsKeys(SketchModel model)
    {
        if (!Aws(model))
        {
            yield break;
        }

        yield return new HclComment("The project's encryption key (KMS), rotated every year" + (Geo.On(model) ? ", with a copy in the second region for the copies of the data." : "."));
        var key = new HclBlock("resource", "aws_kms_key", "main")
            .Attr("description", Hcl.Str("${var.name_prefix} data"))
            .Attr("enable_key_rotation", "true")
            .Attr("deletion_window_in_days", "30");
        if (Geo.On(model))
        {
            key.Attr("multi_region", "true");
        }

        yield return key;
        yield return new HclBlock("resource", "aws_kms_alias", "main")
            .Attr("name", Hcl.Str("alias/${var.name_prefix}"))
            .Attr("target_key_id", $"{AwsKey}.key_id");
        if (Geo.On(model))
        {
            yield return new HclBlock("resource", "aws_kms_replica_key", "second")
                .Attr("provider", "aws.second_region")
                .Attr("primary_key_arn", $"{AwsKey}.arn")
                .Attr("deletion_window_in_days", "30");
        }
    }

    // Google Cloud -----------------------------------------------------------------------------------------------------

    public static bool Gcp(SketchModel model) => On(model) && model.Platform == Platforms.Gcp &&
        new[] { CloudKinds.Bucket, CloudKinds.PubSub, CloudKinds.CloudSql }.Any(kind => model.OfKind(kind).Any());

    // Cloud KMS keys live in a location; each resource needs one in its own: the region, the buckets' multi-region,
    // and the second region for the database copy.
    public static string GcpKey(string ring) => $"google_kms_crypto_key.{ring}.id";

    public static string BucketRing(SketchModel model) => Geo.On(model) ? "multi_region" : "main";

    private static string MultiRegionLocation(SketchModel model) => Geo.GcpMultiRegion(model) switch
    {
        "EU" => "europe",
        "ASIA" => "asia",
        _ => "us",
    };

    public static IEnumerable<HclItem> GcpKeys(SketchModel model)
    {
        if (!Gcp(model))
        {
            yield break;
        }

        var rings = new List<(string Label, string Location)> { ("main", "var.region") };
        if (Geo.On(model) && model.OfKind(CloudKinds.Bucket).Any())
        {
            rings.Add(("multi_region", Hcl.Str(MultiRegionLocation(model))));
        }

        if (Geo.On(model) && model.OfKind(CloudKinds.CloudSql).Any())
        {
            rings.Add(("second_region", Hcl.Str(Geo.Second(model))));
        }

        yield return new HclComment("The project's encryption keys (Cloud KMS), rotated every 90 days. Key rings cannot be deleted; a new one is made for each deployment.");
        foreach (var (label, location) in rings)
        {
            yield return new HclBlock("resource", "google_kms_key_ring", label)
                .Attr("name", Hcl.Str($"${{var.name_prefix}}-{label.Replace('_', '-')}-${{local.suffix}}"))
                .Attr("location", location);
            yield return new HclBlock("resource", "google_kms_crypto_key", label)
                .Str("name", "data")
                .Attr("key_ring", $"google_kms_key_ring.{label}.id")
                .Str("rotation_period", "7776000s");
        }

        // Each Google service encrypts with the key through its own service agent, which may use it.
        var agents = new List<(string Label, string Ring, string Member)>();
        if (model.OfKind(CloudKinds.Bucket).Any())
        {
            yield return new HclBlock("data", "google_storage_project_service_account", "current");
            agents.Add(("storage", BucketRing(model), "\"serviceAccount:${data.google_storage_project_service_account.current.email_address}\""));
        }

        if (model.OfKind(CloudKinds.PubSub).Any())
        {
            agents.Add(("pubsub", "main", "\"serviceAccount:service-${data.google_project.current.number}@gcp-sa-pubsub.iam.gserviceaccount.com\""));
        }

        if (model.OfKind(CloudKinds.CloudSql).Any())
        {
            agents.Add(("sql", "main", "\"serviceAccount:service-${data.google_project.current.number}@gcp-sa-cloud-sql.iam.gserviceaccount.com\""));
            if (Geo.On(model))
            {
                agents.Add(("sql_second", "second_region", "\"serviceAccount:service-${data.google_project.current.number}@gcp-sa-cloud-sql.iam.gserviceaccount.com\""));
            }
        }

        foreach (var (label, ring, member) in agents)
        {
            yield return new HclBlock("resource", "google_kms_crypto_key_iam_member", label)
                .Attr("crypto_key_id", GcpKey(ring))
                .Str("role", "roles/cloudkms.cryptoKeyEncrypterDecrypter")
                .Attr("member", member);
        }
    }

    public static bool NeedsProject(SketchModel model) =>
        Gcp(model) && (model.OfKind(CloudKinds.PubSub).Any() || model.OfKind(CloudKinds.CloudSql).Any());

    public static IEnumerable<SketchMessage> Check(SketchModel model)
    {
        if (!On(model))
        {
            yield break;
        }

        yield return new(MessageLevel.Info, model.Platform switch
        {
            Platforms.Azure => "Storage and SQL use the project's key in its own Key Vault (purge protection on); Cosmos DB, PostgreSQL and Service Bus keep Microsoft's keys.",
            Platforms.Aws => "S3, DynamoDB, SQS, RDS and Secrets Manager use the project's KMS key; the code that uses them may use the key. Buckets CloudFront serves keep S3's keys.",
            _ => "Buckets, Pub/Sub and Cloud SQL use the project's Cloud KMS keys; Firestore and Secret Manager keep Google's keys.",
        });
    }
}
