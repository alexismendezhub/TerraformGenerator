using System.Text.RegularExpressions;

namespace TerraformGenerator.Domain;

// Terraform for boxes of any provider resource (tf:aws_s3_bucket...). Each field is written from what the person set:
// plain text and numbers by the field's type, "=..." as an expression, "ref:<box id>.<attribute>" as a reference to
// another box. The names, regions, groups and tags every resource needs are filled in when left empty.
public static partial class GenericGenerator
{
    public const string ExtraHcl = "_hcl";
    public const string ReferencePrefix = "ref:";

    [GeneratedRegex("^-?[0-9]+(\\.[0-9]+)?$")]
    private static partial Regex NumberPattern();

    // The main Terraform resource of a smart box, so a generic box can refer to it.
    public static string? SmartAddress(SketchResource resource) => resource.Kind switch
    {
        Kinds.WebApp => $"azurerm_linux_web_app.{resource.Label}",
        Kinds.FunctionApp => $"azurerm_linux_function_app.{resource.Label}",
        Kinds.ContainerApp => $"azurerm_container_app.{resource.Label}",
        Kinds.StaticWebApp => $"azurerm_static_web_app.{resource.Label}",
        Kinds.SqlDatabase => $"azurerm_mssql_database.{resource.Label}",
        Kinds.Postgres => $"azurerm_postgresql_flexible_server.{resource.Label}",
        Kinds.CosmosDb => $"azurerm_cosmosdb_account.{resource.Label}",
        Kinds.Storage => $"azurerm_storage_account.{resource.Label}",
        Kinds.Redis => $"azurerm_redis_cache.{resource.Label}",
        Kinds.ServiceBus => $"azurerm_servicebus_namespace.{resource.Label}",
        Kinds.KeyVault => $"azurerm_key_vault.{resource.Label}",
        Kinds.ContainerRegistry => $"azurerm_container_registry.{resource.Label}",
        Kinds.FrontDoor => $"azurerm_cdn_frontdoor_profile.{resource.Label}",
        Kinds.AppGateway => $"azurerm_application_gateway.{resource.Label}",
        Kinds.DnsZone => $"azurerm_dns_zone.{resource.Label}",
        Kinds.AiFoundry or Kinds.Speech => $"azurerm_cognitive_account.{resource.Label}",
        _ when AzureServices.Find(resource.Kind) is { } service => $"{service.TerraformType}.{resource.Label}",
        CloudKinds.Lambda => $"aws_lambda_function.{resource.Label}",
        CloudKinds.Ecr => $"aws_ecr_repository.{resource.Label}",
        CloudKinds.ArtifactRegistry => $"google_artifact_registry_repository.{resource.Label}",
        CloudKinds.Fargate => $"aws_ecs_service.{resource.Label}",
        CloudKinds.S3 => $"aws_s3_bucket.{resource.Label}",
        CloudKinds.DynamoDb => $"aws_dynamodb_table.{resource.Label}",
        CloudKinds.Sqs => $"aws_sqs_queue.{resource.Label}",
        CloudKinds.Rds => $"aws_db_instance.{resource.Label}",
        CloudKinds.AwsSecret => $"aws_secretsmanager_secret.{resource.Label}",
        CloudKinds.CloudFront => $"aws_cloudfront_distribution.{resource.Label}",
        CloudKinds.Route53 => $"aws_route53_zone.{resource.Label}",
        CloudKinds.CloudRun => $"google_cloud_run_v2_service.{resource.Label}",
        CloudKinds.CloudFunction => $"google_cloudfunctions2_function.{resource.Label}",
        CloudKinds.CloudSql => $"google_sql_database_instance.{resource.Label}",
        CloudKinds.Firestore => $"google_firestore_database.{resource.Label}",
        CloudKinds.Bucket => $"google_storage_bucket.{resource.Label}",
        CloudKinds.PubSub => $"google_pubsub_topic.{resource.Label}",
        CloudKinds.GcpSecret => $"google_secret_manager_secret.{resource.Label}",
        CloudKinds.LoadBalancer => $"google_compute_global_address.{resource.Label}",
        CloudKinds.CloudDns => $"google_dns_managed_zone.{resource.Label}",
        _ when Kinds.IsGeneric(resource.Kind) => $"{Kinds.TypeOf(resource.Kind)}.{resource.Label}",
        _ => null,
    };

    // A reference value: the box id and the attribute after the last dot.
    public static (string NodeId, string Attribute)? ParseReference(string value)
    {
        if (!value.StartsWith(ReferencePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var target = value[ReferencePrefix.Length..];
        var dot = target.LastIndexOf('.');
        return dot <= 0 ? null : (target[..dot], target[(dot + 1)..]);
    }

    // Fields filled in by Terraform Generator when the person leaves them empty, by cloud.
    public static Dictionary<string, string> AutoFields(SketchModel model, ResourceSchema schema, SketchResource resource)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        bool Has(string name) => schema.Settable.Any(attribute => attribute.Name == name);
        bool Required(string name) => schema.Attributes.Any(attribute => attribute.Name == name && attribute.Required);

        if (Required("name") && schema.Attributes.First(attribute => attribute.Name == "name").Type == "string")
        {
            fields["name"] = Hcl.Str("${var.name_prefix}-" + resource.Name);
        }

        switch (model.Platform)
        {
            case Platforms.Azure:
                if (Has("resource_group_name")) { fields["resource_group_name"] = "local.resource_group_name"; }
                if (Has("location")) { fields["location"] = "local.location"; }
                if (Has("tags")) { fields["tags"] = "var.tags"; }
                break;
            case Platforms.Gcp:
                if (Has("project")) { fields["project"] = "var.project_id"; }
                if (Has("region")) { fields["region"] = "var.region"; }
                if (Required("location")) { fields["location"] = "var.region"; }
                if (Has("labels")) { fields["labels"] = "var.labels"; }
                break;
        }

        return fields;
    }

    public static List<HclItem> Resources(SketchModel model, IResourceSchemas? schemas)
    {
        var items = new List<HclItem>();
        var byId = model.Resources.ToDictionary(resource => resource.Node.Id, StringComparer.Ordinal);
        foreach (var resource in model.Generics)
        {
            var type = Kinds.TypeOf(resource.Kind);
            var schema = schemas?.Find(model.Platform, type);
            var summary = Platforms.Summarize(type);
            items.Add(new HclComment($"{summary.Title} {resource.Name}."));
            var block = new HclBlock("resource", type, resource.Label);
            var values = resource.Node.Props.Where(prop => prop.Key != ExtraHcl && prop.Value.Length > 0)
                .ToDictionary(prop => prop.Key, prop => prop.Value, StringComparer.Ordinal);

            // Top-level fields: the person's first, then the ones filled in for them.
            var auto = schema is null ? [] : AutoFields(model, schema, resource);
            foreach (var (name, expression) in auto.Where(field => !values.ContainsKey(field.Key)))
            {
                block.Attr(name, expression);
            }

            foreach (var (key, value) in values.Where(pair => !pair.Key.Contains('.', StringComparison.Ordinal)).OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var attribute = schema?.Attributes.FirstOrDefault(item => item.Name == key);
                block.Attr(key, Expression(value, attribute?.Type ?? "string", byId));
            }

            // Nested blocks: "versioning.enabled" becomes a versioning block with that field.
            foreach (var group in values.Where(pair => pair.Key.Contains('.', StringComparison.Ordinal)).GroupBy(pair => pair.Key[..pair.Key.IndexOf('.')]).OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                var nested = schema?.Blocks.FirstOrDefault(item => item.Name == group.Key);
                block.Block(group.Key, inner =>
                {
                    foreach (var (key, value) in group.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    {
                        var name = key[(key.IndexOf('.') + 1)..];
                        var attribute = nested?.Attributes.FirstOrDefault(item => item.Name == name);
                        inner.Attr(name, Expression(value, attribute?.Type ?? "string", byId));
                    }
                });
            }

            if (resource.Node.Props.TryGetValue(ExtraHcl, out var extra) && extra.Trim().Length > 0)
            {
                block.Comment("Written by hand in Terraform Generator:");
                block.Body.Add(new HclRawText(extra.Trim()));
            }

            items.Add(block);
        }

        return items;
    }

    // A field's value as HCL, by the field's type.
    public static string Expression(string value, string type, IReadOnlyDictionary<string, SketchResource> byId)
    {
        if (ParseReference(value) is { } reference)
        {
            return byId.TryGetValue(reference.NodeId, out var target) && SmartAddress(target) is { } address
                ? $"{address}.{reference.Attribute}"
                : Hcl.Literal(value);
        }

        if (value.StartsWith('='))
        {
            return value[1..].Trim();
        }

        if (type == "number" && NumberPattern().IsMatch(value))
        {
            return value;
        }

        if (type == "bool" && value is "true" or "false")
        {
            return value;
        }

        if (type.StartsWith("list(", StringComparison.Ordinal) || type.StartsWith("set(", StringComparison.Ordinal))
        {
            var inner = type[(type.IndexOf('(') + 1)..^1];
            return Hcl.List(value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(item => Expression(item, inner, byId)));
        }

        if (type.StartsWith("map(", StringComparison.Ordinal))
        {
            var entries = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(entry => entry.Split('=', 2, StringSplitOptions.TrimEntries))
                .Where(parts => parts.Length == 2)
                .Select(parts => $"{Hcl.Literal(parts[0])} = {Hcl.Literal(parts[1])}");
            return "{ " + string.Join(", ", entries) + " }";
        }

        return Hcl.Literal(value);
    }

    // Checks for generic boxes: the resource exists in the provider, its required fields have a value, the fields exist.
    public static IEnumerable<SketchMessage> Check(SketchModel model, IResourceSchemas schemas)
    {
        var byId = model.Resources.ToDictionary(resource => resource.Node.Id, StringComparer.Ordinal);
        foreach (var resource in model.Generics)
        {
            var type = Kinds.TypeOf(resource.Kind);
            if (!type.StartsWith(model.Cloud.Prefix, StringComparison.Ordinal))
            {
                yield return new(MessageLevel.Error, $"{resource.Name}: {type} belongs to another cloud than {model.Cloud.Label}.", resource.Node.Id);
                continue;
            }

            var schema = schemas.Find(model.Platform, type);
            if (schema is null)
            {
                yield return new(MessageLevel.Error, $"{resource.Name}: {type} is not a resource of the {model.Cloud.Provider} provider.", resource.Node.Id);
                continue;
            }

            var auto = AutoFields(model, schema, resource);
            foreach (var attribute in schema.Attributes.Where(attribute => attribute.Required && !auto.ContainsKey(attribute.Name)))
            {
                if (!resource.Node.Props.TryGetValue(attribute.Name, out var value) || value.Length == 0)
                {
                    yield return new(MessageLevel.Error, $"{resource.Name}: \"{attribute.Name}\" is required.", resource.Node.Id);
                }
            }

            foreach (var block in schema.Blocks.Where(block => block.Min > 0))
            {
                var hasField = resource.Node.Props.Keys.Any(key => key.StartsWith(block.Name + ".", StringComparison.Ordinal));
                var hasHcl = resource.Node.Props.TryGetValue(ExtraHcl, out var extra) && extra.Contains(block.Name, StringComparison.Ordinal);
                if (!hasField && !hasHcl)
                {
                    yield return new(MessageLevel.Error, $"{resource.Name}: the \"{block.Name}\" block is required; fill its fields or write it under Extra HCL.", resource.Node.Id);
                }
            }

            foreach (var (key, value) in resource.Node.Props.Where(prop => prop.Key != ExtraHcl && !prop.Key.Contains(Environments.Separator, StringComparison.Ordinal)))
            {
                var known = key.Contains('.', StringComparison.Ordinal)
                    ? schema.Blocks.Any(block => block.Name == key[..key.IndexOf('.')] && block.Attributes.Any(attribute => attribute.Name == key[(key.IndexOf('.') + 1)..]))
                    : schema.Attributes.Any(attribute => attribute.Name == key);
                if (!known)
                {
                    yield return new(MessageLevel.Error, $"{resource.Name}: {type} has no field \"{key}\".", resource.Node.Id);
                }
                else if (ParseReference(value) is { } reference && !byId.ContainsKey(reference.NodeId))
                {
                    yield return new(MessageLevel.Error, $"{resource.Name}: \"{key}\" points to a box that no longer exists.", resource.Node.Id);
                }
            }
        }
    }

    // The arrows a generic box should show: one per field that refers to another box.
    public static IEnumerable<(string From, string To)> ReferenceEdges(Sketch sketch) =>
        sketch.Nodes.Where(node => Kinds.IsGeneric(node.Kind))
            .SelectMany(node => node.Props.Values.Select(ParseReference).Where(reference => reference is not null).Select(reference => (node.Id, reference!.Value.NodeId)))
            .Distinct();
}
