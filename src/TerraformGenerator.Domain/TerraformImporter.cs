using System.Text.RegularExpressions;

namespace TerraformGenerator.Domain;

// Reads existing Terraform (azurerm) without AI and proposes the drawing: one box per resource Terraform Generator knows,
// arrows from references (app settings, role assignments, Front Door origins, custom domains), Private only from
// private endpoints. What it cannot draw goes to the review list. The result is a proposal the person approves.
public static partial class TerraformImporter
{
    // Body is the block as written; Refs adds the text of the locals it uses, so references through locals count.
    private sealed record Block(string Type, string Label, string Body, string Refs)
    {
        public string Address => $"{Type}.{Label}";
    }

    private static readonly Dictionary<string, string> BoxTypes = new(StringComparer.Ordinal)
    {
        ["azurerm_linux_web_app"] = Kinds.WebApp,
        ["azurerm_windows_web_app"] = Kinds.WebApp,
        ["azurerm_linux_function_app"] = Kinds.FunctionApp,
        ["azurerm_windows_function_app"] = Kinds.FunctionApp,
        ["azurerm_container_app"] = Kinds.ContainerApp,
        ["azurerm_static_web_app"] = Kinds.StaticWebApp,
        ["azurerm_mssql_database"] = Kinds.SqlDatabase,
        ["azurerm_postgresql_flexible_server"] = Kinds.Postgres,
        ["azurerm_cosmosdb_account"] = Kinds.CosmosDb,
        ["azurerm_storage_account"] = Kinds.Storage,
        ["azurerm_redis_cache"] = Kinds.Redis,
        ["azurerm_servicebus_namespace"] = Kinds.ServiceBus,
        ["azurerm_key_vault"] = Kinds.KeyVault,
        ["azurerm_container_registry"] = Kinds.ContainerRegistry,
        ["azurerm_cdn_frontdoor_profile"] = Kinds.FrontDoor,
        ["azurerm_application_gateway"] = Kinds.AppGateway,
        ["azurerm_dns_zone"] = Kinds.DnsZone,
        ["azurerm_search_service"] = AzureServices.Search,
        ["azurerm_eventhub_namespace"] = AzureServices.EventHubs,
        ["azurerm_eventgrid_topic"] = AzureServices.EventGrid,
        ["azurerm_app_configuration"] = AzureServices.AppConfig,
        ["azurerm_signalr_service"] = AzureServices.SignalR,
        ["azurerm_web_pubsub"] = AzureServices.WebPubSub,
        ["azurerm_maps_account"] = AzureServices.Maps,
        ["azurerm_communication_service"] = AzureServices.Communication,
        ["azurerm_machine_learning_workspace"] = AzureServices.MachineLearning,
        ["azurerm_bot_service_azure_bot"] = AzureServices.Bot,
        ["azurerm_video_indexer_account"] = AzureServices.VideoIndexer,
    };

    // Pieces Terraform Generator adds by itself: they become part of the boxes, not boxes.
    private static readonly HashSet<string> Supporting = new(StringComparer.Ordinal)
    {
        "azurerm_resource_group", "azurerm_service_plan", "azurerm_mssql_server", "azurerm_mssql_firewall_rule",
        "azurerm_virtual_network", "azurerm_subnet", "azurerm_private_dns_zone", "azurerm_private_dns_zone_virtual_network_link",
        "azurerm_private_endpoint", "azurerm_role_assignment", "azurerm_user_assigned_identity", "azurerm_log_analytics_workspace",
        "azurerm_application_insights", "azurerm_monitor_diagnostic_setting", "azurerm_storage_container", "azurerm_servicebus_queue",
        "azurerm_cosmosdb_sql_database", "azurerm_cosmosdb_sql_container", "azurerm_cosmosdb_sql_role_assignment",
        "azurerm_cdn_frontdoor_endpoint", "azurerm_cdn_frontdoor_origin_group", "azurerm_cdn_frontdoor_origin",
        "azurerm_cdn_frontdoor_route", "azurerm_cdn_frontdoor_custom_domain", "azurerm_cdn_frontdoor_custom_domain_association",
        "azurerm_dns_cname_record", "azurerm_dns_txt_record", "azurerm_dns_a_record", "azurerm_key_vault_secret",
        "azurerm_container_app_environment", "azurerm_container_app_environment_storage", "azurerm_postgresql_flexible_server_database",
        "azurerm_postgresql_flexible_server_firewall_rule", "azurerm_postgresql_flexible_server_active_directory_administrator",
        "azurerm_consumption_budget_resource_group", "azurerm_redis_cache_access_policy_assignment", "random_string",
        "random_password", "time_sleep", "tls_private_key", "azurerm_cognitive_account_project", "azurerm_cognitive_deployment",
        "azurerm_management_lock", "azurerm_monitor_action_group", "azurerm_monitor_metric_alert", "azurerm_network_security_group",
        "azurerm_subnet_network_security_group_association", "azurerm_eventhub", "azurerm_storage_queue", "azurerm_storage_table",
        "azurerm_email_communication_service", "azurerm_email_communication_service_domain", "azurerm_communication_service_email_domain_association",
        "azurerm_machine_learning_compute_cluster", "azurerm_bot_channel_ms_teams", "azurerm_bot_channel_web_chat",
        "azurerm_postgresql_flexible_server_configuration", "azurerm_cdn_frontdoor_firewall_policy", "azurerm_cdn_frontdoor_security_policy",
        "azurerm_security_center_subscription_pricing", "azurerm_security_center_contact",
        "azurerm_public_ip", "azurerm_web_application_firewall_policy",
    };

    [GeneratedRegex("""(?m)^\s*resource\s+"([^"]+)"\s+"([^"]+)"\s*\{""")]
    private static partial Regex ResourceStart();

    [GeneratedRegex("""(?m)^\s*data\s+"azurerm_resource_group"\s+"([^"]+)"\s*\{""")]
    private static partial Regex ResourceGroupData();

    [GeneratedRegex("""(?m)^\s*variable\s+"([^"]+)"\s*\{""")]
    private static partial Regex VariableStart();

    [GeneratedRegex("""\blocal\.([A-Za-z_][A-Za-z0-9_]*)""")]
    private static partial Regex LocalReference();

    [GeneratedRegex("""^([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.+)$""")]
    private static partial Regex GenericField();

    [GeneratedRegex(@"""(?:[^""\\]|\\.)*""")]
    private static partial Regex QuotedText();

    [GeneratedRegex("""^((?:azurerm|aws|google)_[a-z0-9_]+\.[A-Za-z_][A-Za-z0-9_-]*)\.([A-Za-z_][A-Za-z0-9_]*)$""")]
    private static partial Regex GenericAttribute();

    [GeneratedRegex("""(?m)^\s*locals\s*\{""")]
    private static partial Regex LocalsStart();

    [GeneratedRegex("""^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=""")]
    private static partial Regex AttributeStart();

    [GeneratedRegex("""\b((?:azurerm|aws|google|random)_[a-z0-9_]+)\.([A-Za-z_][A-Za-z0-9_-]*)""")]
    private static partial Regex Reference();

    public static ProposalDraft Read(string terraform)
    {
        var text = StripComments(terraform.ReplaceLineEndings("\n"));
        var variables = Variables(text);
        var locals = Locals(text);
        var blocks = Blocks(text, ResourceStart()).Select(item => new Block(item.Type, item.Label, item.Body, WithLocals(item.Body, locals))).ToList();
        var byAddress = blocks.GroupBy(block => block.Address).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var draft = new ProposalDraft { Summary = $"Read {blocks.Count} resources from the Terraform." };

        // Storage accounts that only hold a function app's own files are part of that function app.
        var functionStorage = blocks
            .Where(block => BoxTypes.TryGetValue(block.Type, out var kind) && kind == Kinds.FunctionApp)
            .SelectMany(block => References(Attribute(block.Body, "storage_account_name") ?? ""))
            .Concat(blocks.Where(block => block.Type is "azurerm_machine_learning_workspace" or "azurerm_video_indexer_account")
                .SelectMany(block => References(block.Body)
                    .Where(address => address.StartsWith("azurerm_storage_account.", StringComparison.Ordinal) || address.StartsWith("azurerm_key_vault.", StringComparison.Ordinal))))
            // A Custom Vision prediction account is part of its training account's box.
            .Concat(blocks.Where(block => block.Type == "azurerm_cognitive_account" && Attribute(block.Body, "kind")?.Trim('"') == "CustomVision.Prediction").Select(block => block.Address))
            .ToHashSet(StringComparer.Ordinal);

        var boxes = new Dictionary<string, ProposalNode>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var generics = new List<Block>();
        foreach (var block in blocks)
        {
            var kind = KindOf(block);
            if (kind is null || functionStorage.Contains(block.Address))
            {
                if (kind is null && !Supporting.Contains(block.Type) && !functionStorage.Contains(block.Address))
                {
                    if (Platforms.All.Any(platform => block.Type.StartsWith(platform.Prefix, StringComparison.Ordinal)))
                    {
                        // Any other resource of a cloud provider becomes a box of the full list, field by field.
                        generics.Add(block);
                    }
                    else
                    {
                        draft.Questions.Add($"Not drawn: {block.Address} ({block.Type}); Terraform Generator draws only Azure, AWS and Google Cloud resources.");
                    }
                }

                continue;
            }

            var node = new ProposalNode { Id = block.Address, Kind = kind, Name = BoxName(block.Label, names) };
            var review = new List<string>();
            Properties(node, block, byAddress, variables, review);
            if (block.Type.StartsWith("azurerm_windows_", StringComparison.Ordinal))
            {
                review.Add("it runs on Windows; Terraform Generator draws Linux apps");
            }

            if (block.Type == "azurerm_mssql_database" && Reference().Match(Attribute(block.Body, "server_id") ?? "") is { Success: true } server
                && byAddress.TryGetValue(server.Value, out var serverBlock) && Attribute(serverBlock.Body, "public_network_access_enabled") == "false")
            {
                node.Props.Add(new() { Key = "privateOnly", Value = "true" });
            }

            node.Review = review.Count == 0 ? "" : string.Join("; ", review) + ".";
            boxes[block.Address] = node;
        }

        foreach (var endpoint in blocks.Where(block => block.Type == "azurerm_private_endpoint"))
        {
            var target = References(endpoint.Refs).Select(address => Owner(address, byAddress, boxes)).FirstOrDefault(owner => owner is not null);
            if (target is not null && Catalog.Find(target.Kind)!.Properties.Any(property => property.Key == "privateOnly") && target.Props.All(prop => prop.Key != "privateOnly"))
            {
                target.Props.Add(new() { Key = "privateOnly", Value = "true" });
            }
        }

        // A resource whose label an earlier one already has (files and its versioning) adds the last word of its type.
        var genericNodes = generics.Select(block => new ProposalNode
        {
            Id = block.Address,
            Kind = Kinds.GenericPrefix + block.Type,
            Name = BoxName(
                blocks.TakeWhile(other => other != block).Any(other => other.Label == block.Label && (boxes.ContainsKey(other.Address) || generics.Contains(other)))
                    ? $"{block.Label}_{block.Type[(block.Type.LastIndexOf('_') + 1)..]}"
                    : block.Label,
                names),
        }).ToList();
        var known = boxes.Keys.Concat(genericNodes.Select(node => node.Id)).ToHashSet(StringComparer.Ordinal);
        foreach (var (node, block) in genericNodes.Zip(generics))
        {
            node.Props = GenericProps(block.Body, variables, known);
        }

        draft.Nodes = [.. boxes.Values, .. genericNodes];
        draft.Edges = Edges(blocks, byAddress, boxes);
        foreach (var node in genericNodes)
        {
            foreach (var target in node.Props.Select(prop => GenericGenerator.ParseReference(prop.Value)?.NodeId).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                draft.Edges.Add(new ProposalEdge { From = node.Id, To = target });
            }
        }

        draft.Settings = Settings(blocks, variables);
        draft.Settings.Platform = DetectPlatform(text) ?? Platforms.Azure;
        if (blocks.Any(block => block.Type is "azurerm_security_center_subscription_pricing" or "aws_guardduty_detector"))
        {
            draft.Settings.Set(Protection.Key, "on");
        }
        ResourceGroup(draft.Settings, text, blocks, variables);
        if (draft.Nodes.Count == 0)
        {
            draft.Questions.Add("No resource Terraform Generator can draw was found. Is this Terraform for Azure, AWS or Google Cloud?");
        }

        return draft;
    }

    // Arrows: a box (or a piece that belongs to it) that refers to another box. Role assignments and access policies
    // point from the identity to what it may use; a pair drawn the wrong way round is turned when the other way exists.
    private static List<ProposalEdge> Edges(List<Block> blocks, Dictionary<string, Block> byAddress, Dictionary<string, ProposalNode> boxes)
    {
        var pairs = new List<(ProposalNode From, ProposalNode To)>();
        foreach (var block in blocks)
        {
            if (block.Type is "azurerm_role_assignment" or "azurerm_cosmosdb_sql_role_assignment" or "azurerm_redis_cache_access_policy_assignment")
            {
                var scope = References(Attribute(block.Body, "scope") ?? Attribute(block.Body, "redis_cache_id") ?? Attribute(block.Body, "account_name") ?? "")
                    .Select(address => Owner(address, byAddress, boxes)).FirstOrDefault(owner => owner is not null);
                var principal = References(Attribute(block.Body, "principal_id") ?? Attribute(block.Body, "object_id") ?? "")
                    .Select(address => Owner(address, byAddress, boxes)).FirstOrDefault(owner => owner is not null);
                if (scope is not null && principal is not null)
                {
                    pairs.Add((principal, scope));
                }

                continue;
            }

            if (block.Type is "azurerm_monitor_diagnostic_setting" or "azurerm_private_endpoint")
            {
                continue;
            }

            var owner = Owner(block.Address, byAddress, boxes);
            if (owner is null)
            {
                continue;
            }

            foreach (var target in References(block.Refs).Select(address => Owner(address, byAddress, boxes)).Where(target => target is not null && target != owner))
            {
                pairs.Add((owner, target!));
            }
        }

        var edges = new List<ProposalEdge>();
        foreach (var (from, to) in pairs)
        {
            var (source, target) = Catalog.FindConnection(from.Kind, to.Kind) is not null ? (from, to)
                : Catalog.FindConnection(to.Kind, from.Kind) is not null ? (to, from) : (null, null);
            if (source is not null && target is not null && !edges.Any(edge => edge.From == source.Id && edge.To == target.Id))
            {
                edges.Add(new ProposalEdge { From = source.Id, To = target.Id });
            }
        }

        return edges;
    }

    // The box a resource belongs to: itself, the app whose identity it is, or the first box it refers to.
    private static ProposalNode? Owner(string address, Dictionary<string, Block> byAddress, Dictionary<string, ProposalNode> boxes, int depth = 0)
    {
        if (boxes.TryGetValue(address, out var box))
        {
            return box;
        }

        if (depth > 4 || !byAddress.TryGetValue(address, out var block) || block.Type is "azurerm_resource_group" or "azurerm_log_analytics_workspace"
            or "azurerm_application_insights" or "azurerm_subnet" or "azurerm_virtual_network" or "azurerm_service_plan" or "random_string")
        {
            return null;
        }

        if (block.Type == "azurerm_user_assigned_identity")
        {
            var user = byAddress.Values.FirstOrDefault(other => boxes.ContainsKey(other.Address) && other.Refs.Contains(address + ".id", StringComparison.Ordinal));
            return user is null ? null : boxes[user.Address];
        }

        if (block.Type == "azurerm_mssql_server")
        {
            // Using the server's address means using its database when it has only one.
            var databases = byAddress.Values
                .Where(other => boxes.ContainsKey(other.Address) && other.Type == "azurerm_mssql_database" && other.Body.Contains(address + ".", StringComparison.Ordinal))
                .ToList();
            return databases.Count == 1 ? boxes[databases[0].Address] : null;
        }

        return References(block.Refs).Select(reference => Owner(reference, byAddress, boxes, depth + 1)).FirstOrDefault(owner => owner is not null);
    }

    private static void Properties(ProposalNode node, Block block, Dictionary<string, Block> byAddress, Dictionary<string, string> variables, List<string> review)
    {
        string? Value(string key) => Resolve(Attribute(block.Body, key), variables);

        void Set(string key, string? value)
        {
            if (value is null)
            {
                return;
            }

            var property = Catalog.Find(node.Kind)!.Properties.First(item => item.Key == key);
            if (SketchRules.IsValid(property, value))
            {
                node.Props.Add(new() { Key = key, Value = value });
            }
            else
            {
                review.Add($"{property.Label} \"{value}\" is not a Terraform Generator choice, the default is used");
            }
        }

        switch (node.Kind)
        {
            case Kinds.WebApp:
            case Kinds.FunctionApp:
                var plan = References(Attribute(block.Body, "service_plan_id") ?? "").Select(address => byAddress.GetValueOrDefault(address)).FirstOrDefault();
                var sku = plan is null ? null : Resolve(Attribute(plan.Body, "sku_name"), variables);
                Set(node.Kind == Kinds.WebApp ? "sku" : "plan", sku);
                Set("runtime", Runtime(block.Body, node.Kind == Kinds.FunctionApp, variables));
                if (node.Kind == Kinds.WebApp && Resolve(InnerAttribute(block.Body, "docker_image_name"), variables) is { } image)
                {
                    Set("dockerImage", image);
                }

                break;
            case Kinds.ContainerApp:
                Set("image", Resolve(InnerAttribute(block.Body, "image"), variables));
                Set("minReplicas", Resolve(InnerAttribute(block.Body, "min_replicas"), variables));
                Set("maxReplicas", Resolve(InnerAttribute(block.Body, "max_replicas"), variables));
                Set("port", Resolve(InnerAttribute(block.Body, "target_port"), variables));
                Set("external", Resolve(InnerAttribute(block.Body, "external_enabled"), variables));
                break;
            case Kinds.StaticWebApp:
                Set("sku", Value("sku_tier"));
                break;
            case Kinds.SqlDatabase:
                Set("sku", Value("sku_name"));
                Set("backupDays", Resolve(InnerAttribute(block.Body, "retention_days"), variables));
                break;
            case Kinds.Postgres:
                Set("version", Value("version"));
                Set("sku", Value("sku_name"));
                Set("backupDays", Value("backup_retention_days"));
                if (Value("storage_mb") is { } megabytes && int.TryParse(megabytes, out var mb))
                {
                    Set("storageGb", (mb / 1024).ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                Private(node, Value("public_network_access_enabled"));
                break;
            case Kinds.CosmosDb:
                Set("capacity", block.Body.Contains("EnableServerless", StringComparison.Ordinal) ? "serverless" : "provisioned");
                Private(node, Value("public_network_access_enabled"));
                Containers(node, block, byAddress, "azurerm_cosmosdb_sql_container", "containers");
                break;
            case Kinds.Storage:
                Set("replication", Value("account_replication_type"));
                Private(node, Value("public_network_access_enabled"));
                Containers(node, block, byAddress, "azurerm_storage_container", "containers");
                Containers(node, block, byAddress, "azurerm_storage_queue", "queues");
                Containers(node, block, byAddress, "azurerm_storage_table", "tables", "^[A-Za-z][A-Za-z0-9]{2,62}$");
                break;
            case Kinds.Redis:
                var redisSku = $"{Value("sku_name")}_{Value("family")}{Value("capacity")}";
                Set("sku", redisSku);
                Private(node, Value("public_network_access_enabled"));
                break;
            case Kinds.ServiceBus:
                Set("sku", Value("sku"));
                Private(node, Value("public_network_access_enabled"));
                Containers(node, block, byAddress, "azurerm_servicebus_queue", "queues");
                break;
            case Kinds.KeyVault:
                Set("retentionDays", Value("soft_delete_retention_days"));
                Set("purgeProtection", Value("purge_protection_enabled"));
                Private(node, Value("public_network_access_enabled"));
                break;
            case Kinds.ContainerRegistry:
                Set("sku", Value("sku"));
                break;
            case Kinds.FrontDoor:
                Set("sku", Value("sku_name"));
                // The firewall: its mode when a security policy puts one on this Front Door, off when none does.
                var security = byAddress.Values.FirstOrDefault(other => other.Type == "azurerm_cdn_frontdoor_security_policy" && References(other.Body).Contains(block.Address));
                var firewall = security is null ? null : References(security.Body)
                    .Select(address => byAddress.GetValueOrDefault(address)).FirstOrDefault(other => other?.Type == "azurerm_cdn_frontdoor_firewall_policy");
                Set(Firewall.Key, firewall is null ? "off" : Resolve(Attribute(firewall.Body, "mode"), variables) == "Detection" ? "detection" : "prevention");
                if (firewall is not null && Regex.Match(firewall.Body, "rate_limit_threshold\\s*=\\s*([0-9]+)") is { Success: true } rate)
                {
                    Set(Firewall.RateKey, rate.Groups[1].Value);
                }

                var domain = byAddress.Values.FirstOrDefault(other => other.Type == "azurerm_cdn_frontdoor_custom_domain" && other.Body.Contains(block.Address + ".", StringComparison.Ordinal));
                if (domain is not null && Resolve(Attribute(domain.Body, "host_name"), variables) is { } host && host.Contains('.', StringComparison.Ordinal))
                {
                    Set("subdomain", host[..host.IndexOf('.', StringComparison.Ordinal)]);
                }

                break;
            case Kinds.DnsZone:
                Set("zone", Value("name"));
                break;
            case Kinds.AiFoundry:
                Private(node, Value("public_network_access_enabled"));
                var deployed = byAddress.Values
                    .Where(other => other.Type == "azurerm_cognitive_deployment" && other.Body.Contains(block.Address + ".", StringComparison.Ordinal))
                    .Select(other => ModelName(other.Body, variables))
                    .Where(name => name is not null)
                    .ToList();
                Set("model", deployed.FirstOrDefault(name => !name!.StartsWith("text-embedding", StringComparison.Ordinal)) ?? (deployed.Count > 0 ? "none" : null));
                Set("embeddings", deployed.FirstOrDefault(name => name!.StartsWith("text-embedding", StringComparison.Ordinal)));
                break;
            case Kinds.Speech:
                Set("sku", Value("sku_name"));
                Private(node, Value("public_network_access_enabled"));
                break;
            case AzureServices.AiService:
                var aiKind = Value("kind");
                Set("type", AzureServices.AiTypes.FirstOrDefault(type => type.Value.Kind == aiKind).Key);
                Set("tier", Value("sku_name")?.StartsWith('F') == true ? "free" : "standard");
                Private(node, Value("public_network_access_enabled"));
                break;
            case AzureServices.Search:
                Set("sku", Value("sku"));
                Set("replicas", Value("replica_count"));
                Private(node, Value("public_network_access_enabled"));
                break;
            case AzureServices.EventHubs:
                Set("sku", Value("sku"));
                Private(node, Value("public_network_access_enabled"));
                Containers(node, block, byAddress, "azurerm_eventhub", "hubs", "^[a-z0-9][a-z0-9._-]{0,48}[a-z0-9]$");
                var hub = byAddress.Values.FirstOrDefault(item => item.Type == "azurerm_eventhub" && References(item.Body).Contains(block.Address));
                Set("partitions", hub is null ? null : Resolve(Attribute(hub.Body, "partition_count"), variables));
                break;
            case AzureServices.EventGrid:
            case AzureServices.WebPubSub:
                if (node.Kind == AzureServices.WebPubSub)
                {
                    Set("sku", Value("sku"));
                }

                Private(node, Value("public_network_access_enabled"));
                break;
            case AzureServices.AppConfig:
                Set("sku", Value("sku"));
                Private(node, Value("public_network_access") == "Disabled" ? "false" : null);
                break;
            case AzureServices.SignalR:
                var signalSku = Regex.Match(block.Body, "sku\\s*\\{([^}]*)\\}");
                Set("sku", signalSku.Success ? Resolve(InnerAttribute(signalSku.Groups[1].Value, "name"), variables) : null);
                Set("mode", Value("service_mode"));
                Private(node, Value("public_network_access_enabled"));
                break;
            case AzureServices.MachineLearning:
                var cluster = byAddress.Values.FirstOrDefault(item => item.Type == "azurerm_machine_learning_compute_cluster" && References(item.Body).Contains(block.Address));
                if (cluster is not null)
                {
                    Set("vmSize", Resolve(Attribute(cluster.Body, "vm_size"), variables));
                    Set("priority", Resolve(Attribute(cluster.Body, "vm_priority"), variables));
                    Set("maxNodes", Resolve(InnerAttribute(cluster.Body, "max_node_count"), variables));
                }

                break;
            case AzureServices.Bot:
                Set("sku", Value("sku"));
                if (!byAddress.Values.Any(item => item.Type == "azurerm_bot_channel_ms_teams" && References(item.Body).Contains(block.Address)))
                {
                    Set("teams", "false");
                }

                break;
            case AzureServices.Communication:
                Set("dataLocation", Value("data_location"));
                if (!byAddress.Values.Any(item => item.Type == "azurerm_communication_service_email_domain_association" && References(item.Body).Contains(block.Address)))
                {
                    Set("email", "false");
                }

                break;
        }
    }

    // The model a deployment deploys: the name inside its model block.
    private static string? ModelName(string body, Dictionary<string, string> variables)
    {
        var model = System.Text.RegularExpressions.Regex.Match(body, "model\\s*\\{([^}]*)\\}");
        return model.Success ? Resolve(InnerAttribute(model.Groups[1].Value, "name"), variables) : null;
    }

    private static void Private(ProposalNode node, string? publicAccess)
    {
        if (publicAccess == "false" && node.Props.All(prop => prop.Key != "privateOnly"))
        {
            node.Props.Add(new() { Key = "privateOnly", Value = "true" });
        }
    }

    // Containers, queues: child resources that name this one; a for_each over a list gives every name.
    private static void Containers(ProposalNode node, Block parent, Dictionary<string, Block> byAddress, string childType, string key,
        string pattern = "^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$")
    {
        var names = new List<string>();
        foreach (var child in byAddress.Values.Where(block => block.Type == childType && References(block.Body).Contains(parent.Address)))
        {
            var forEach = Attribute(child.Body, "for_each");
            if (forEach is not null)
            {
                names.AddRange(Regex.Matches(forEach, "\"([A-Za-z0-9._-]+)\"").Select(match => match.Groups[1].Value));
            }
            else if (Attribute(child.Body, "name") is { } name && name.StartsWith('"'))
            {
                names.Add(name.Trim('"'));
            }
        }

        var valid = names.Where(name => Regex.IsMatch(name, pattern)).Distinct().ToList();
        if (valid.Count > 0)
        {
            node.Props.Add(new() { Key = key, Value = string.Join(", ", valid) });
        }
    }

    private static string? Runtime(string body, bool function, Dictionary<string, string> variables)
    {
        var dotnet = Resolve(InnerAttribute(body, "dotnet_version"), variables);
        var node = Resolve(InnerAttribute(body, "node_version"), variables);
        var python = Resolve(InnerAttribute(body, "python_version"), variables);
        if (dotnet is not null)
        {
            return dotnet.StartsWith('9') && !function ? "dotnet9" : "dotnet8";
        }

        if (node is not null)
        {
            return function ? "node20" : node.StartsWith("22", StringComparison.Ordinal) ? "node22" : "node20";
        }

        if (python is not null)
        {
            return function ? "python311" : "python312";
        }

        return !function && InnerAttribute(body, "docker_image_name") is not null ? "docker" : null;
    }

    private static ProposalSettings Settings(List<Block> blocks, Dictionary<string, string> variables)
    {
        var settings = new ProposalSettings { Monitoring = blocks.Any(block => block.Type == "azurerm_log_analytics_workspace") };
        var group = blocks.FirstOrDefault(block => block.Type == "azurerm_resource_group");
        var location = group is null ? null : Resolve(Attribute(group.Body, "location"), variables);
        if (location is not null && Catalog.Instance.Regions.Any(region => region.Value == location))
        {
            settings.Region = location;
        }

        var name = Resolve(Attribute(group?.Body ?? "", "name"), variables) ?? variables.GetValueOrDefault("name_prefix") ?? "";
        var lower = name.ToLowerInvariant();
        settings.Environment = lower.Contains("prod", StringComparison.Ordinal) ? "prod"
            : lower.Contains("stag", StringComparison.Ordinal) ? "staging"
            : lower.Contains("test", StringComparison.Ordinal) || lower.Contains("qa", StringComparison.Ordinal) ? "test" : "dev";
        var project = new string(lower.Split('-', '_')[0].Where(char.IsAsciiLetterOrDigit).Take(12).ToArray());
        settings.Name = Regex.IsMatch(project, Catalog.ProjectNamePattern) ? project : "";
        return settings;
    }

    // The cloud most resources belong to.
    public static string? DetectPlatform(string terraform)
    {
        var counts = Platforms.All
            .Select(platform => (platform.Id, Count: Regex.Count(terraform, $"""resource\s+"{platform.Prefix}""")))
            .Where(item => item.Count > 0)
            .OrderByDescending(item => item.Count)
            .ToList();
        return counts.Count > 0 ? counts[0].Id : null;
    }

    // A resource's fields as box props: one-line plain values as they are, references to other boxes as ref:,
    // other one-line expressions with "="; nested blocks and multi-line values as HCL text.
    private static List<ProposalProp> GenericProps(string body, Dictionary<string, string> variables, HashSet<string> boxes)
    {
        var props = new List<ProposalProp>();
        var extra = new List<string>();
        var depth = 0;
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            var code = QuotedText().Replace(trimmed, "\"\"");
            var opens = code.Count(character => character is '{' or '[' or '(');
            var closes = code.Count(character => character is '}' or ']' or ')');
            var single = depth == 0 && opens == closes ? GenericField().Match(trimmed) : Match.Empty;
            if (single.Success && single.Groups[1].Value is not ("depends_on" or "provider" or "count" or "for_each"))
            {
                var raw = single.Groups[2].Value.Trim();
                var reference = GenericAttribute().Match(raw);
                var value = reference.Success && boxes.Contains(reference.Groups[1].Value)
                    ? $"{GenericGenerator.ReferencePrefix}{reference.Groups[1].Value}.{reference.Groups[2].Value}"
                    : Resolve(raw, variables) ?? "=" + raw;
                props.Add(new ProposalProp { Key = single.Groups[1].Value, Value = value });
            }
            else if (trimmed.Length > 0 || depth > 0)
            {
                extra.Add(line.StartsWith("  ", StringComparison.Ordinal) ? line[2..] : line);
            }

            depth += opens - closes;
        }

        var hcl = string.Join('\n', extra).Trim();
        if (hcl.Length > 0)
        {
            props.Add(new ProposalProp { Key = GenericGenerator.ExtraHcl, Value = hcl });
        }

        return props;
    }

    // The box a resource becomes: by type, and for an Azure AI account by its kind.
    private static string? KindOf(Block block)
    {
        if (BoxTypes.TryGetValue(block.Type, out var kind))
        {
            return kind;
        }

        return block.Type != "azurerm_cognitive_account" ? null : Attribute(block.Body, "kind")?.Trim('"') switch
        {
            "AIServices" or "OpenAI" => Kinds.AiFoundry,
            "SpeechServices" => Kinds.Speech,
            "CustomVision.Prediction" => null,
            var other when AzureServices.AiTypes.Values.Any(type => type.Kind == other) => AzureServices.AiService,
            _ => null,
        };
    }

    // A resource group read with a data block already exists; a created one keeps its name when it is a plain value.
    private static void ResourceGroup(ProposalSettings settings, string text, List<Block> blocks, Dictionary<string, string> variables)
    {
        var existing = Blocks(text, ResourceGroupData()).FirstOrDefault();
        var created = blocks.FirstOrDefault(block => block.Type == "azurerm_resource_group");
        var name = existing.Body is not null ? Resolve(Attribute(existing.Body, "name"), variables)
            : created is not null ? Resolve(Attribute(created.Body, "name"), variables) : null;
        if (existing.Body is not null)
        {
            settings.Set(SettingKeys.ResourceGroup, "existing");
        }

        if (name is not null && Regex.IsMatch(name, Catalog.ResourceGroupPattern))
        {
            settings.Set(SettingKeys.ResourceGroupName, name);
        }
    }

    private static string BoxName(string label, HashSet<string> used)
    {
        var name = Regex.Replace(label.ToLowerInvariant().Replace('_', '-'), "[^a-z0-9-]", "");
        name = Regex.Replace(name, "-+", "-").Trim('-');
        if (name.Length == 0 || !char.IsAsciiLetter(name[0]))
        {
            name = "r" + name;
        }

        name = name.Length > 20 ? name[..20].TrimEnd('-') : name;
        if (name.Length < 2)
        {
            name += "x";
        }

        var unique = name;
        for (var index = 2; !used.Add(unique.Replace('-', '_')); index++)
        {
            var tail = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            unique = (name.Length + tail.Length > 20 ? name[..(20 - tail.Length)] : name).TrimEnd('-') + tail;
        }

        return unique;
    }

    // Parsing ---------------------------------------------------------------------------------------------------------

    // Each block that starts with the pattern: its first and second quoted labels and its body.
    private static IEnumerable<(string Type, string Label, string Body)> Blocks(string text, Regex start) =>
        start.Matches(text).Select(match => (
            match.Groups[1].Value,
            match.Groups.Count > 2 ? match.Groups[2].Value : "",
            Body(text, match.Index + match.Length - 1)));

    // The text between the brace at open and its matching brace, skipping braces inside strings and heredocs.
    private static string Body(string text, int open)
    {
        var depth = 0;
        var inString = false;
        for (var index = open; index < text.Length; index++)
        {
            var character = text[index];
            if (inString)
            {
                if (character == '\\')
                {
                    index++;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
            }
            else if (character == '<' && index + 1 < text.Length && text[index + 1] == '<')
            {
                var marker = Regex.Match(text[index..], "^<<-?([A-Z_]+)\\r?\\n");
                if (marker.Success)
                {
                    var end = Regex.Match(text[(index + marker.Length)..], $"(?m)^\\s*{marker.Groups[1].Value}\\s*$");
                    index = end.Success ? index + marker.Length + end.Index + end.Length : text.Length;
                }
            }
            else if (character == '{')
            {
                depth++;
            }
            else if (character == '}' && --depth == 0)
            {
                return text[(open + 1)..index];
            }
        }

        return text[(open + 1)..];
    }

    private static string StripComments(string text)
    {
        var result = new System.Text.StringBuilder(text.Length);
        var inString = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (inString)
            {
                result.Append(character);
                if (character == '\\' && index + 1 < text.Length)
                {
                    result.Append(text[++index]);
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
                result.Append(character);
            }
            else if (character == '#' || (character == '/' && index + 1 < text.Length && text[index + 1] == '/'))
            {
                while (index < text.Length && text[index] != '\n')
                {
                    index++;
                }

                result.Append('\n');
            }
            else if (character == '/' && index + 1 < text.Length && text[index + 1] == '*')
            {
                var end = text.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = end < 0 ? text.Length : end + 1;
            }
            else
            {
                result.Append(character);
            }
        }

        return result.ToString();
    }

    // A top-level attribute of a block body (not inside a nested block), as written: "text", true, 5, var.x...
    private static string? Attribute(string body, string key)
    {
        var depth = 0;
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (depth == 0)
            {
                var match = Regex.Match(trimmed, $"^{Regex.Escape(key)}\\s*=\\s*(.+)$");
                if (match.Success)
                {
                    return match.Groups[1].Value.Trim();
                }
            }

            depth += trimmed.Count(character => character == '{') - trimmed.Count(character => character == '}');
        }

        return null;
    }

    // The first attribute with that name at any depth (inside site_config, template, ingress...).
    private static string? InnerAttribute(string body, string key)
    {
        var match = Regex.Match(body, $"(?m)^\\s*{Regex.Escape(key)}\\s*=\\s*(.+)$");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    // A plain value: a quoted string without interpolation, a number or a bool, or a variable with such a default.
    private static string? Resolve(string? raw, Dictionary<string, string> variables)
    {
        if (raw is null)
        {
            return null;
        }

        if (raw.StartsWith("var.", StringComparison.Ordinal))
        {
            return variables.GetValueOrDefault(raw[4..]);
        }

        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"' && !raw.Contains("${", StringComparison.Ordinal))
        {
            return raw[1..^1];
        }

        return Regex.IsMatch(raw, "^(true|false|[0-9.]+)$") ? raw : null;
    }

    // Every attribute of every locals block, with its whole (possibly multi-line) value.
    private static Dictionary<string, string> Locals(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in LocalsStart().Matches(text))
        {
            string? name = null;
            var value = new System.Text.StringBuilder();
            var depth = 0;
            foreach (var line in Body(text, match.Index + match.Length - 1).Split('\n'))
            {
                var start = depth == 0 ? AttributeStart().Match(line) : Match.Empty;
                if (start.Success)
                {
                    if (name is not null)
                    {
                        values[name] = value.ToString();
                    }

                    name = start.Groups[1].Value;
                    value.Clear();
                }

                value.Append(line).Append('\n');
                depth += line.Count(character => character is '{' or '[' or '(') - line.Count(character => character is '}' or ']' or ')');
            }

            if (name is not null)
            {
                values[name] = value.ToString();
            }
        }

        return values;
    }

    // The body plus the locals it uses, three levels deep.
    private static string WithLocals(string body, Dictionary<string, string> locals)
    {
        var text = new System.Text.StringBuilder(body);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = LocalReference().Matches(body).Select(match => match.Groups[1].Value).ToList();
        for (var level = 0; level < 3 && pending.Count > 0; level++)
        {
            var next = new List<string>();
            foreach (var name in pending.Where(seen.Add))
            {
                if (locals.TryGetValue(name, out var value))
                {
                    text.Append('\n').Append(value);
                    next.AddRange(LocalReference().Matches(value).Select(match => match.Groups[1].Value));
                }
            }

            pending = next;
        }

        return text.ToString();
    }

    private static Dictionary<string, string> Variables(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, _, body) in Blocks(text, VariableStart()))
        {
            if (Resolve(Attribute(body, "default"), values) is { } value)
            {
                values[name] = value;
            }
        }

        return values;
    }

    private static IEnumerable<string> References(string text) =>
        Reference().Matches(text).Select(match => match.Value).Distinct(StringComparer.Ordinal);
}
