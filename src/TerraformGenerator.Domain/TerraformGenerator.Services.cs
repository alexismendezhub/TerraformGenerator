namespace TerraformGenerator.Domain;

// services.tf: the Azure services of AzureServices, each with Microsoft Entra sign-in only and, when private, its
// private endpoint.
public static partial class TerraformGenerator
{
    private static IEnumerable<SketchResource> ServiceResources(SketchModel model) =>
        model.Resources.Where(resource => AzureServices.Find(resource.Kind) is not null);

    private static string ServiceId(SketchResource service) => $"{AzureServices.Find(service.Kind)!.TerraformType}.{service.Label}.id";

    private static List<HclItem> Services(SketchModel model)
    {
        var items = new List<HclItem>();
        foreach (var service in ServiceResources(model))
        {
            items.AddRange(Service(model, service));
            var spec = AzureServices.Find(service.Kind)!;
            if (service.IsPrivate && spec.Subresource is not null)
            {
                items.Add(PrivateEndpoint(service.Label, service.Name, ServiceId(service), spec.Subresource, spec.ZoneLabel!));
                if (AzureServices.HasPrediction(service))
                {
                    items.Add(PrivateEndpoint($"{service.Label}_prediction", $"{service.Name}-predict", $"azurerm_cognitive_account.{service.Label}_prediction.id", spec.Subresource, spec.ZoneLabel!));
                }
            }
        }

        return items;
    }

    private static IEnumerable<HclItem> Service(SketchModel model, SketchResource service)
    {
        var label = service.Label;
        var name = Prefixed($"{service.Name}-${{local.suffix}}");
        var open = Hcl.Bool(!service.IsPrivate);
        switch (service.Kind)
        {
            case AzureServices.AiService:
                yield return new HclComment($"{AzureServices.AiKind(service)} {service.Name}: Microsoft Entra sign-in only (the custom subdomain makes it possible).");
                yield return Resource("azurerm_cognitive_account", label)
                    .Attr("name", name)
                    .Attr("location", Location)
                    .Attr("resource_group_name", ResourceGroup)
                    .Str("kind", AzureServices.AiKind(service))
                    .Str("sku_name", AzureServices.AiSku(service))
                    .Attr("custom_subdomain_name", name)
                    .Attr("local_auth_enabled", "false")
                    .Attr("public_network_access_enabled", open)
                    .Attr("tags", Tags);
                if (AzureServices.HasPrediction(service))
                {
                    var prediction = Prefixed($"{service.Name}-predict-${{local.suffix}}");
                    yield return new HclComment($"The prediction account of {service.Name}: published models answer from here.");
                    yield return Resource("azurerm_cognitive_account", $"{label}_prediction")
                        .Attr("name", prediction)
                        .Attr("location", Location)
                        .Attr("resource_group_name", ResourceGroup)
                        .Str("kind", "CustomVision.Prediction")
                        .Str("sku_name", AzureServices.AiSku(service))
                        .Attr("custom_subdomain_name", prediction)
                        .Attr("local_auth_enabled", "false")
                        .Attr("public_network_access_enabled", open)
                        .Attr("tags", Tags);
                }

                break;
            case AzureServices.Search:
                yield return new HclComment($"AI Search {service.Name}: Microsoft Entra sign-in only.");
                var search = Resource("azurerm_search_service", label)
                    .Attr("name", name)
                    .Attr("location", Location)
                    .Attr("resource_group_name", ResourceGroup)
                    .Str("sku", service.P["sku"]);
                if (service.P["sku"] != "free")
                {
                    search.Attr("replica_count", service.P["replicas"]).Attr("partition_count", "1");
                }

                yield return search
                    .Attr("local_authentication_enabled", "false")
                    .Attr("public_network_access_enabled", open)
                    .Attr("tags", Tags);
                break;
            case AzureServices.EventHubs:
                yield return new HclComment($"Event Hubs {service.Name}: Microsoft Entra sign-in only.");
                yield return Resource("azurerm_eventhub_namespace", label)
                    .Attr("name", name)
                    .Attr("location", Location)
                    .Attr("resource_group_name", ResourceGroup)
                    .Str("sku", service.P["sku"])
                    .Attr("capacity", "1")
                    .Str("minimum_tls_version", "1.2")
                    .Attr("local_authentication_enabled", "false")
                    .Attr("public_network_access_enabled", open)
                    .Attr("tags", Tags);
                yield return Resource("azurerm_eventhub", label)
                    .Attr("for_each", $"toset({Hcl.List(service.List("hubs").Select(Hcl.Literal))})")
                    .Attr("name", "each.key")
                    .Attr("namespace_id", $"azurerm_eventhub_namespace.{label}.id")
                    .Attr("partition_count", service.P["partitions"])
                    .Attr("message_retention", "1");
                break;
            case AzureServices.EventGrid:
                yield return new HclComment($"Event Grid topic {service.Name}: CloudEvents, Microsoft Entra sign-in only.");
                yield return Resource("azurerm_eventgrid_topic", label)
                    .Attr("name", name)
                    .Attr("location", Location)
                    .Attr("resource_group_name", ResourceGroup)
                    .Str("input_schema", "CloudEventSchemaV1_0")
                    .Attr("local_auth_enabled", "false")
                    .Attr("public_network_access_enabled", open)
                    .Attr("tags", Tags);
                break;
            case AzureServices.AppConfig:
                yield return new HclComment($"App Configuration {service.Name}: Microsoft Entra sign-in only.");
                var config = Resource("azurerm_app_configuration", label)
                    .Attr("name", name)
                    .Attr("location", Location)
                    .Attr("resource_group_name", ResourceGroup)
                    .Str("sku", service.P["sku"])
                    .Attr("local_auth_enabled", "false")
                    .Str("public_network_access", service.IsPrivate ? "Disabled" : "Enabled");
                if (service.P["sku"] == "standard")
                {
                    config.Attr("purge_protection_enabled", Hcl.Bool(model.IsProduction));
                }

                yield return config.Attr("tags", Tags);
                break;
            case AzureServices.SignalR:
                yield return new HclComment($"SignalR {service.Name}: Microsoft Entra sign-in only.");
                yield return Resource("azurerm_signalr_service", label)
                    .Attr("name", name)
                    .Attr("location", Location)
                    .Attr("resource_group_name", ResourceGroup)
                    .Str("service_mode", service.P["mode"])
                    .Attr("local_auth_enabled", "false")
                    .Attr("public_network_access_enabled", open)
                    .Attr("tags", Tags)
                    .Block("sku", sku => sku.Str("name", service.P["sku"]).Attr("capacity", "1"));
                break;
            case AzureServices.WebPubSub:
                yield return new HclComment($"Web PubSub {service.Name}: Microsoft Entra sign-in only.");
                yield return Resource("azurerm_web_pubsub", label)
                    .Attr("name", name)
                    .Attr("location", Location)
                    .Attr("resource_group_name", ResourceGroup)
                    .Str("sku", service.P["sku"])
                    .Attr("capacity", "1")
                    .Attr("local_auth_enabled", "false")
                    .Attr("public_network_access_enabled", open)
                    .Attr("tags", Tags);
                break;
            case AzureServices.Maps:
                yield return new HclComment($"Azure Maps {service.Name}: Microsoft Entra sign-in only, in {AzureServices.MapsLocation(model)} (Maps runs in a few regions).");
                yield return Resource("azurerm_maps_account", label)
                    .Attr("name", name)
                    .Attr("resource_group_name", ResourceGroup)
                    .Str("location", AzureServices.MapsLocation(model))
                    .Str("sku_name", "G2")
                    .Attr("local_authentication_enabled", "false")
                    .Attr("tags", Tags);
                break;
            case AzureServices.Communication:
                yield return new HclComment($"Communication Services {service.Name}, data kept in {service.P["dataLocation"]}.");
                yield return Resource("azurerm_communication_service", label)
                    .Attr("name", name)
                    .Attr("resource_group_name", ResourceGroup)
                    .Str("data_location", service.P["dataLocation"])
                    .Attr("tags", Tags);
                if (service.Flag("email"))
                {
                    yield return new HclComment("Email from an Azure managed domain, linked to the Communication Services resource.");
                    yield return Resource("azurerm_email_communication_service", label)
                        .Attr("name", Prefixed($"{service.Name}-email-${{local.suffix}}"))
                        .Attr("resource_group_name", ResourceGroup)
                        .Str("data_location", service.P["dataLocation"])
                        .Attr("tags", Tags);
                    yield return Resource("azurerm_email_communication_service_domain", label)
                        .Str("name", "AzureManagedDomain")
                        .Attr("email_service_id", $"azurerm_email_communication_service.{label}.id")
                        .Str("domain_management", "AzureManaged")
                        .Attr("tags", Tags);
                    yield return Resource("azurerm_communication_service_email_domain_association", label)
                        .Attr("communication_service_id", $"azurerm_communication_service.{label}.id")
                        .Attr("email_service_domain_id", $"azurerm_email_communication_service_domain.{label}.id");
                }

                break;
            case AzureServices.MachineLearning:
                foreach (var item in MachineLearning(model, service))
                {
                    yield return item;
                }

                break;
            case AzureServices.Bot:
                var app = model.Links.First(link => link.From == service).To;
                yield return new HclComment($"Bot {service.Name}: it signs in as this identity, which {app.Name} also carries to answer as the bot.");
                yield return Resource("azurerm_user_assigned_identity", $"{label}_bot")
                    .Attr("name", Prefixed($"{service.Name}-bot"))
                    .Attr("location", Location)
                    .Attr("resource_group_name", ResourceGroup)
                    .Attr("tags", Tags);
                yield return Resource("azurerm_bot_service_azure_bot", label)
                    .Attr("name", name)
                    .Attr("resource_group_name", ResourceGroup)
                    .Str("location", "global")
                    .Str("sku", service.P["sku"])
                    .Str("microsoft_app_type", "UserAssignedMSI")
                    .Attr("microsoft_app_id", $"azurerm_user_assigned_identity.{label}_bot.client_id")
                    .Attr("microsoft_app_tenant_id", $"azurerm_user_assigned_identity.{label}_bot.tenant_id")
                    .Attr("microsoft_app_msi_id", $"azurerm_user_assigned_identity.{label}_bot.id")
                    .Attr("endpoint", Hcl.Str("https://" + Interpolate(HostOf(app)) + "/api/messages"))
                    .Attr("tags", Tags);
                if (service.Flag("teams"))
                {
                    yield return Resource("azurerm_bot_channel_ms_teams", label)
                        .Attr("bot_name", $"azurerm_bot_service_azure_bot.{label}.name")
                        .Str("location", "global")
                        .Attr("resource_group_name", ResourceGroup);
                }

                break;
            case AzureServices.VideoIndexer:
                yield return new HclComment($"Video Indexer {service.Name}: it keeps the videos in its own storage account and reaches it with its identity.");
                yield return Resource("azurerm_user_assigned_identity", $"{label}_videos")
                    .Attr("name", Prefixed($"{service.Name}-videos"))
                    .Attr("location", Location)
                    .Attr("resource_group_name", ResourceGroup)
                    .Attr("tags", Tags);
                yield return VideoStorage(service);
                yield return RoleAssignment($"{label}_videos", $"azurerm_storage_account.{label}_videos.id", "Storage Blob Data Contributor",
                    $"azurerm_user_assigned_identity.{label}_videos.principal_id", servicePrincipal: true);
                yield return Resource("azurerm_video_indexer_account", label)
                    .Attr("name", name)
                    .Attr("location", Location)
                    .Attr("resource_group_name", ResourceGroup)
                    .Attr("tags", Tags)
                    .Block("storage", storage => storage
                        .Attr("storage_account_id", $"azurerm_storage_account.{label}_videos.id")
                        .Attr("user_assigned_identity_id", $"azurerm_user_assigned_identity.{label}_videos.id"))
                    .Block("identity", identity => identity
                        .Str("type", "UserAssigned")
                        .Attr("identity_ids", $"[azurerm_user_assigned_identity.{label}_videos.id]"))
                    .Attr("depends_on", $"[azurerm_role_assignment.{label}_videos]");
                break;
        }
    }

    private static HclBlock VideoStorage(SketchResource service) =>
        Resource("azurerm_storage_account", $"{service.Label}_videos")
            .Attr("name", CompactName(service.Compact + "vi"))
            .Attr("location", Location)
            .Attr("resource_group_name", ResourceGroup)
            .Str("account_tier", "Standard")
            .Str("account_replication_type", "LRS")
            .Str("min_tls_version", "TLS1_2")
            .Attr("https_traffic_only_enabled", "true")
            .Attr("allow_nested_items_to_be_public", "false")
            .Attr("tags", Tags);

    // A Machine Learning workspace with the storage, Key Vault and Application Insights Azure asks for, and a
    // compute cluster that has no machines while nothing runs.
    private static IEnumerable<HclItem> MachineLearning(SketchModel model, SketchResource service)
    {
        var label = service.Label;
        var registry = model.Targets(service, Kinds.ContainerRegistry).FirstOrDefault();
        yield return new HclComment($"Machine Learning {service.Name}: the workspace and what Azure needs around it.");
        if (!Logs(model))
        {
            yield return Resource("azurerm_log_analytics_workspace", label)
                .Attr("name", Prefixed($"{service.Name}-logs"))
                .Attr("location", Location)
                .Attr("resource_group_name", ResourceGroup)
                .Str("sku", "PerGB2018")
                .Attr("retention_in_days", "30")
                .Attr("tags", Tags);
        }

        yield return Resource("azurerm_application_insights", label)
            .Attr("name", Prefixed($"{service.Name}-insights"))
            .Attr("location", Location)
            .Attr("resource_group_name", ResourceGroup)
            .Attr("workspace_id", Logs(model) ? Workspace(model) : $"azurerm_log_analytics_workspace.{label}.id")
            .Str("application_type", "web")
            .Attr("tags", Tags);
        yield return Resource("azurerm_key_vault", $"{label}_ml")
            .Attr("name", CompactName(service.Compact + "ml"))
            .Attr("location", Location)
            .Attr("resource_group_name", ResourceGroup)
            .Attr("tenant_id", Tenant)
            .Str("sku_name", "standard")
            .Attr("rbac_authorization_enabled", "true")
            .Attr("purge_protection_enabled", Hcl.Bool(model.IsProduction))
            .Attr("tags", Tags);
        yield return Resource("azurerm_storage_account", $"{label}_ml")
            .Attr("name", CompactName(service.Compact + "ml"))
            .Attr("location", Location)
            .Attr("resource_group_name", ResourceGroup)
            .Str("account_tier", "Standard")
            .Str("account_replication_type", "LRS")
            .Str("min_tls_version", "TLS1_2")
            .Attr("https_traffic_only_enabled", "true")
            .Attr("allow_nested_items_to_be_public", "false")
            .Attr("tags", Tags);
        var workspace = Resource("azurerm_machine_learning_workspace", label)
            .Attr("name", Prefixed($"{service.Name}-${{local.suffix}}"))
            .Attr("location", Location)
            .Attr("resource_group_name", ResourceGroup)
            .Attr("application_insights_id", $"azurerm_application_insights.{label}.id")
            .Attr("key_vault_id", $"azurerm_key_vault.{label}_ml.id")
            .Attr("storage_account_id", $"azurerm_storage_account.{label}_ml.id");
        if (registry is not null)
        {
            workspace.Attr("container_registry_id", $"azurerm_container_registry.{registry.Label}.id");
        }

        yield return workspace
            .Attr("public_network_access_enabled", "true")
            .Attr("tags", Tags)
            .Block("identity", identity => identity.Str("type", "SystemAssigned"));
        yield return new HclComment("Machines start only while a job runs and stop after 15 idle minutes.");
        yield return Resource("azurerm_machine_learning_compute_cluster", label)
            .Str("name", "training")
            .Attr("location", Location)
            .Attr("machine_learning_workspace_id", $"azurerm_machine_learning_workspace.{label}.id")
            .Str("vm_priority", service.P["priority"])
            .Str("vm_size", service.P["vmSize"])
            .Attr("local_auth_enabled", "false")
            .Attr("tags", Tags)
            .Block("scale_settings", scale => scale
                .Attr("min_node_count", "0")
                .Attr("max_node_count", service.P["maxNodes"])
                .Str("scale_down_nodes_after_idle_duration", "PT15M"))
            .Block("identity", identity => identity.Str("type", "SystemAssigned"));
    }

    // The identities of the bots an app answers for: the app carries them besides its own.
    private static List<string> BotIdentities(SketchModel model, SketchResource app) =>
        model.Sources(app, AzureServices.Bot).Select(bot => $"azurerm_user_assigned_identity.{bot.Label}_bot.id").ToList();

    private static IEnumerable<KeyValuePair<string, string>> BotSettings(SketchModel model, SketchResource app)
    {
        foreach (var bot in model.Sources(app, AzureServices.Bot))
        {
            yield return new("MicrosoftAppType", Hcl.Str("UserAssignedMSI"));
            yield return new("MicrosoftAppId", $"azurerm_user_assigned_identity.{bot.Label}_bot.client_id");
            yield return new("MicrosoftAppTenantId", $"azurerm_user_assigned_identity.{bot.Label}_bot.tenant_id");
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> ServiceSettings(SketchModel model, SketchResource app) =>
        model.Links.Where(link => link.From == app && AzureServices.Find(link.To.Kind) is not null)
            .SelectMany(link => AzureServices.Settings(link.To, BicepGenerator.Symbol(link.To)))
            .Select(setting => new KeyValuePair<string, string>(setting.Name, setting.Terraform))
            .Concat(BotSettings(model, app));

    private static IEnumerable<HclItem> ServiceRoles(SketchModel model, SketchResource app, string principal)
    {
        foreach (var service in model.Links.Where(link => link.From == app).Select(link => link.To).Where(target => AzureServices.Find(target.Kind) is not null))
        {
            var roles = AzureServices.Find(service.Kind)!.Roles;
            for (var index = 0; index < roles.Length; index++)
            {
                yield return RoleAssignment($"{app.Label}__{service.Label}{(index == 0 ? "" : $"_{index + 1}")}", ServiceId(service), roles[index], principal, servicePrincipal: true);
                if (AzureServices.HasPrediction(service))
                {
                    yield return RoleAssignment($"{app.Label}__{service.Label}_prediction", $"azurerm_cognitive_account.{service.Label}_prediction.id", roles[index], principal, servicePrincipal: true);
                }
            }
        }
    }

    private static IEnumerable<(string Label, string ResourceId)> ServiceDiagnostics(SketchModel model) =>
        ServiceResources(model).Where(service => AzureServices.Find(service.Kind)!.Diagnostics).SelectMany(service => AzureServices.HasPrediction(service)
            ? new[] { (service.Label, ServiceId(service)), ($"{service.Label}_prediction", $"azurerm_cognitive_account.{service.Label}_prediction.id") }
            : [(service.Label, ServiceId(service))]);

    private static IEnumerable<HclItem> ServiceOutputs(SketchModel model) =>
        ServiceResources(model).SelectMany(service => AzureServices.Settings(service, BicepGenerator.Symbol(service))
            .Select(setting => (HclItem)Output(setting.Name.ToLowerInvariant(), setting.Terraform, $"{setting.Name} of {service.Name}.")));
}
