namespace TerraformGenerator.Domain;

// The Azure services of AzureServices in Bicep, the same as services.tf.
public static partial class BicepGenerator
{
    private static IEnumerable<SketchResource> ServiceResources(SketchModel model) =>
        model.Resources.Where(resource => AzureServices.Find(resource.Kind) is not null);

    private static void Services(SketchModel model, BicepFile file)
    {
        foreach (var service in ServiceResources(model))
        {
            Service(model, file, service);
            var spec = AzureServices.Find(service.Kind)!;
            if (service.IsPrivate && spec.Subresource is not null)
            {
                PrivateEndpoint(file, model, Symbol(service), service.Name, $"{Symbol(service)}.id", spec.Subresource, spec.ZoneLabel!);
                if (AzureServices.HasPrediction(service))
                {
                    PrivateEndpoint(file, model, Symbol(service) + "Prediction", $"{service.Name}-predict", $"{Symbol(service)}Prediction.id", spec.Subresource, spec.ZoneLabel!);
                }
            }
        }
    }

    private static void Service(SketchModel model, BicepFile file, SketchResource service)
    {
        var symbol = Symbol(service);
        var name = Prefixed($"{service.Name}-${{suffix}}");
        var access = PublicAccess(service.IsPrivate);
        switch (service.Kind)
        {
            case AzureServices.AiService:
                file.Resource(symbol, "Microsoft.CognitiveServices/accounts@2025-06-01", Located(name)
                    .Str("kind", AzureServices.AiKind(service))
                    .Obj("sku", sku => sku.Str("name", AzureServices.AiSku(service)))
                    .Obj("properties", properties => properties
                        .Add("customSubDomainName", name)
                        .Add("disableLocalAuth", "true")
                        .Add("publicNetworkAccess", access)),
                    $"{AzureServices.AiKind(service)} {service.Name}: Microsoft Entra sign-in only (the custom subdomain makes it possible).");
                if (AzureServices.HasPrediction(service))
                {
                    var prediction = Prefixed($"{service.Name}-predict-${{suffix}}");
                    file.Resource(symbol + "Prediction", "Microsoft.CognitiveServices/accounts@2025-06-01", Located(prediction)
                        .Str("kind", "CustomVision.Prediction")
                        .Obj("sku", sku => sku.Str("name", AzureServices.AiSku(service)))
                        .Obj("properties", properties => properties
                            .Add("customSubDomainName", prediction)
                            .Add("disableLocalAuth", "true")
                            .Add("publicNetworkAccess", access)),
                        $"The prediction account of {service.Name}: published models answer from here.");
                }

                break;
            case AzureServices.Search:
                file.Resource(symbol, "Microsoft.Search/searchServices@2023-11-01", Located(name)
                    .Obj("sku", sku => sku.Str("name", service.P["sku"]))
                    .Obj("properties", properties =>
                    {
                        if (service.P["sku"] != "free")
                        {
                            properties.Add("replicaCount", service.P["replicas"]).Add("partitionCount", "1");
                        }

                        properties.Str("hostingMode", "default")
                            .Add("disableLocalAuth", "true")
                            .Str("publicNetworkAccess", service.IsPrivate ? "disabled" : "enabled");
                    }),
                    $"AI Search {service.Name}: Microsoft Entra sign-in only.");
                break;
            case AzureServices.EventHubs:
                file.Resource(symbol, "Microsoft.EventHub/namespaces@2024-01-01", Located(name)
                    .Obj("sku", sku => sku.Str("name", service.P["sku"]).Str("tier", service.P["sku"]).Add("capacity", "1"))
                    .Obj("properties", properties => properties
                        .Str("minimumTlsVersion", "1.2")
                        .Add("disableLocalAuth", "true")
                        .Add("publicNetworkAccess", access)),
                    $"Event Hubs {service.Name}: Microsoft Entra sign-in only.");
                foreach (var hub in service.List("hubs"))
                {
                    file.Resource(symbol + Pascal(hub.Replace('.', '-').Replace('_', '-')), "Microsoft.EventHub/namespaces/eventhubs@2024-01-01", Bicep.Object(body => body
                        .Add("parent", symbol)
                        .Add("name", Bicep.Literal(hub))
                        .Obj("properties", properties => properties.Add("partitionCount", service.P["partitions"]).Add("messageRetentionInDays", "1"))));
                }

                break;
            case AzureServices.EventGrid:
                file.Resource(symbol, "Microsoft.EventGrid/topics@2022-06-15", Located(name)
                    .Obj("properties", properties => properties
                        .Str("inputSchema", "CloudEventSchemaV1_0")
                        .Add("disableLocalAuth", "true")
                        .Add("publicNetworkAccess", access)),
                    $"Event Grid topic {service.Name}: CloudEvents, Microsoft Entra sign-in only.");
                break;
            case AzureServices.AppConfig:
                file.Resource(symbol, "Microsoft.AppConfiguration/configurationStores@2024-05-01", Located(name)
                    .Obj("sku", sku => sku.Str("name", service.P["sku"]))
                    .Obj("properties", properties =>
                    {
                        properties.Add("disableLocalAuth", "true").Add("publicNetworkAccess", access);
                        if (service.P["sku"] == "standard")
                        {
                            properties.Add("enablePurgeProtection", Bicep.Bool(model.IsProduction));
                        }
                    }),
                    $"App Configuration {service.Name}: Microsoft Entra sign-in only.");
                break;
            case AzureServices.SignalR:
                file.Resource(symbol, "Microsoft.SignalRService/signalR@2024-03-01", Located(name)
                    .Str("kind", "SignalR")
                    .Obj("sku", sku => sku.Str("name", service.P["sku"]).Add("capacity", "1"))
                    .Obj("properties", properties => properties
                        .Add("features", Bicep.Array(Bicep.Object(feature => feature.Str("flag", "ServiceMode").Str("value", service.P["mode"]))))
                        .Add("disableLocalAuth", "true")
                        .Add("publicNetworkAccess", access)),
                    $"SignalR {service.Name}: Microsoft Entra sign-in only.");
                break;
            case AzureServices.WebPubSub:
                file.Resource(symbol, "Microsoft.SignalRService/webPubSub@2024-03-01", Located(name)
                    .Obj("sku", sku => sku.Str("name", service.P["sku"]).Add("capacity", "1"))
                    .Obj("properties", properties => properties
                        .Add("disableLocalAuth", "true")
                        .Add("publicNetworkAccess", access)),
                    $"Web PubSub {service.Name}: Microsoft Entra sign-in only.");
                break;
            case AzureServices.Maps:
                file.Resource(symbol, "Microsoft.Maps/accounts@2023-06-01", Bicep.Object(body => body
                    .Add("name", name)
                    .Str("location", AzureServices.MapsLocation(model))
                    .Add("tags", Tags)
                    .Str("kind", "Gen2")
                    .Obj("sku", sku => sku.Str("name", "G2"))
                    .Obj("properties", properties => properties.Add("disableLocalAuth", "true"))),
                    $"Azure Maps {service.Name}: Microsoft Entra sign-in only, in {AzureServices.MapsLocation(model)} (Maps runs in a few regions).");
                break;
            case AzureServices.Communication:
                var email = service.Flag("email");
                if (email)
                {
                    file.Resource(symbol + "Email", "Microsoft.Communication/emailServices@2023-04-01", Bicep.Object(body => body
                        .Add("name", Prefixed($"{service.Name}-email-${{suffix}}"))
                        .Str("location", "global")
                        .Add("tags", Tags)
                        .Obj("properties", properties => properties.Add("dataLocation", Bicep.Str(service.P["dataLocation"])))),
                        "Email from an Azure managed domain, linked to the Communication Services resource.");
                    file.Resource(symbol + "EmailDomain", "Microsoft.Communication/emailServices/domains@2023-04-01", Bicep.Object(body => body
                        .Add("parent", symbol + "Email")
                        .Str("name", "AzureManagedDomain")
                        .Str("location", "global")
                        .Add("tags", Tags)
                        .Obj("properties", properties => properties.Str("domainManagement", "AzureManaged"))));
                }

                file.Resource(symbol, "Microsoft.Communication/communicationServices@2023-04-01", Bicep.Object(body => body
                    .Add("name", name)
                    .Str("location", "global")
                    .Add("tags", Tags)
                    .Obj("properties", properties =>
                    {
                        properties.Add("dataLocation", Bicep.Str(service.P["dataLocation"]));
                        if (email)
                        {
                            properties.Add("linkedDomains", Bicep.Array($"{symbol}EmailDomain.id"));
                        }
                    })),
                    $"Communication Services {service.Name}, data kept in {service.P["dataLocation"]}.");
                break;
            case AzureServices.MachineLearning:
                MachineLearning(model, file, service);
                break;
            case AzureServices.Bot:
                var app = model.Links.First(link => link.From == service).To;
                file.Resource(symbol + "Identity", "Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31", Located(Prefixed($"{service.Name}-bot")),
                    $"Bot {service.Name}: it signs in as this identity, which {app.Name} also carries to answer as the bot.");
                file.Resource(symbol, "Microsoft.BotService/botServices@2022-09-15", Bicep.Object(body => body
                    .Add("name", name)
                    .Str("location", "global")
                    .Add("tags", Tags)
                    .Str("kind", "azurebot")
                    .Obj("sku", sku => sku.Str("name", service.P["sku"]))
                    .Obj("properties", properties => properties
                        .Add("displayName", name)
                        .Add("endpoint", $"'https://${{{HostOf(app)}}}/api/messages'")
                        .Str("msaAppType", "UserAssignedMSI")
                        .Add("msaAppId", $"{symbol}Identity.properties.clientId")
                        .Add("msaAppTenantId", $"{symbol}Identity.properties.tenantId")
                        .Add("msaAppMSIResourceId", $"{symbol}Identity.id"))));
                if (service.Flag("teams"))
                {
                    file.Resource(symbol + "Teams", "Microsoft.BotService/botServices/channels@2022-09-15", Bicep.Object(body => body
                        .Add("parent", symbol)
                        .Str("name", "MsTeamsChannel")
                        .Str("location", "global")
                        .Obj("properties", properties => properties
                            .Str("channelName", "MsTeamsChannel")
                            .Obj("properties", inner => inner.Add("isEnabled", "true")))));
                }

                break;
            case AzureServices.VideoIndexer:
                file.Resource(symbol + "Identity", "Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31", Located(Prefixed($"{service.Name}-videos")),
                    $"Video Indexer {service.Name}: it keeps the videos in its own storage account and reaches it with this identity.");
                file.Resource(symbol + "Videos", "Microsoft.Storage/storageAccounts@2023-05-01", Located(CompactName(service.Compact + "vi"))
                    .Str("kind", "StorageV2")
                    .Obj("sku", sku => sku.Str("name", "Standard_LRS"))
                    .Obj("properties", properties => properties
                        .Str("minimumTlsVersion", "TLS1_2")
                        .Add("supportsHttpsTrafficOnly", "true")
                        .Add("allowBlobPublicAccess", "false")));
                Role(file, symbol + "VideosRole", symbol + "Videos", "Storage Blob Data Contributor", $"{symbol}Identity.properties.principalId", $"{symbol}Identity.id");
                file.Resource(symbol, "Microsoft.VideoIndexer/accounts@2024-01-01", Located(name)
                    .Obj("identity", identity => identity
                        .Str("type", "UserAssigned")
                        .Obj("userAssignedIdentities", ids => ids.Add($"'${{{symbol}Identity.id}}'", "{}")))
                    .Obj("properties", properties => properties
                        .Obj("storageServices", storage => storage
                            .Add("resourceId", $"{symbol}Videos.id")
                            .Add("userAssignedIdentity", $"{symbol}Identity.id")))
                    .Add("dependsOn", Bicep.Array(symbol + "VideosRole")));
                break;
        }
    }

    // A Machine Learning workspace with the storage, Key Vault and Application Insights Azure asks for, and a
    // compute cluster that has no machines while nothing runs.
    private static void MachineLearning(SketchModel model, BicepFile file, SketchResource service)
    {
        var symbol = Symbol(service);
        var registry = model.Targets(service, Kinds.ContainerRegistry).FirstOrDefault();
        var workspace = "logs.id";
        if (!TerraformGenerator.Logs(model))
        {
            file.Resource(symbol + "Logs", "Microsoft.OperationalInsights/workspaces@2023-09-01", Located(Prefixed($"{service.Name}-logs"))
                .Obj("properties", properties => properties.Obj("sku", sku => sku.Str("name", "PerGB2018")).Add("retentionInDays", "30")));
            workspace = symbol + "Logs.id";
        }

        file.Resource(symbol + "Insights", "Microsoft.Insights/components@2020-02-02", Located(Prefixed($"{service.Name}-insights"))
            .Str("kind", "web")
            .Obj("properties", properties => properties.Str("Application_Type", "web").Add("WorkspaceResourceId", workspace)),
            $"Machine Learning {service.Name}: the workspace and what Azure needs around it.");
        file.Resource(symbol + "Vault", "Microsoft.KeyVault/vaults@2023-07-01", Located(CompactName(service.Compact + "ml"))
            .Obj("properties", properties =>
            {
                properties.Add("tenantId", "tenant().tenantId")
                    .Obj("sku", sku => sku.Str("family", "A").Str("name", "standard"))
                    .Add("enableRbacAuthorization", "true")
                    .Add("enableSoftDelete", "true");
                if (model.IsProduction)
                {
                    properties.Add("enablePurgeProtection", "true");
                }
            }));
        file.Resource(symbol + "Storage", "Microsoft.Storage/storageAccounts@2023-05-01", Located(CompactName(service.Compact + "ml"))
            .Str("kind", "StorageV2")
            .Obj("sku", sku => sku.Str("name", "Standard_LRS"))
            .Obj("properties", properties => properties
                .Str("minimumTlsVersion", "TLS1_2")
                .Add("supportsHttpsTrafficOnly", "true")
                .Add("allowBlobPublicAccess", "false")));
        file.Resource(symbol, "Microsoft.MachineLearningServices/workspaces@2024-10-01", Located(Prefixed($"{service.Name}-${{suffix}}"))
            .Obj("identity", identity => identity.Str("type", "SystemAssigned"))
            .Obj("sku", sku => sku.Str("name", "Basic").Str("tier", "Basic"))
            .Obj("properties", properties =>
            {
                properties.Add("storageAccount", $"{symbol}Storage.id")
                    .Add("keyVault", $"{symbol}Vault.id")
                    .Add("applicationInsights", $"{symbol}Insights.id");
                if (registry is not null)
                {
                    properties.Add("containerRegistry", $"{Symbol(registry)}.id");
                }

                properties.Str("publicNetworkAccess", "Enabled");
            }));
        file.Resource(symbol + "Training", "Microsoft.MachineLearningServices/workspaces/computes@2024-10-01", Bicep.Object(body => body
            .Add("parent", symbol)
            .Str("name", "training")
            .Add("location", Location)
            .Obj("identity", identity => identity.Str("type", "SystemAssigned"))
            .Obj("properties", properties => properties
                .Str("computeType", "AmlCompute")
                .Add("disableLocalAuth", "true")
                .Obj("properties", compute => compute
                    .Str("vmSize", service.P["vmSize"])
                    .Str("vmPriority", service.P["priority"])
                    .Obj("scaleSettings", scale => scale
                        .Add("minNodeCount", "0")
                        .Add("maxNodeCount", service.P["maxNodes"])
                        .Str("nodeIdleTimeBeforeScaleDown", "PT15M"))))),
            "Machines start only while a job runs and stop after 15 idle minutes.");
    }

    // The identities of the bots an app answers for: the app carries them besides its own.
    private static List<string> BotIdentities(SketchModel model, SketchResource app) =>
        model.Sources(app, AzureServices.Bot).Select(bot => Symbol(bot) + "Identity").ToList();

    private static void AppIdentity(BicepObject identity, List<string> bots)
    {
        if (bots.Count == 0)
        {
            identity.Str("type", "SystemAssigned");
            return;
        }

        identity.Str("type", "SystemAssigned, UserAssigned").Obj("userAssignedIdentities", ids =>
        {
            foreach (var bot in bots)
            {
                ids.Add($"'${{{bot}.id}}'", "{}");
            }
        });
    }

    private static IEnumerable<(string Name, string Value)> ServiceSettings(SketchModel model, SketchResource app) =>
        model.Links.Where(link => link.From == app && AzureServices.Find(link.To.Kind) is not null)
            .SelectMany(link => AzureServices.Settings(link.To, Symbol(link.To)))
            .Select(setting => (setting.Name, setting.Bicep))
            .Concat(model.Sources(app, AzureServices.Bot).SelectMany(bot => new[]
            {
                ("MicrosoftAppType", "'UserAssignedMSI'"),
                ("MicrosoftAppId", $"{Symbol(bot)}Identity.properties.clientId"),
                ("MicrosoftAppTenantId", $"{Symbol(bot)}Identity.properties.tenantId"),
            }));

    private static void ServiceRoles(SketchModel model, BicepFile file, SketchResource app, string symbol, string principal, string key)
    {
        foreach (var service in model.Links.Where(link => link.From == app).Select(link => link.To).Where(target => AzureServices.Find(target.Kind) is not null))
        {
            var roles = AzureServices.Find(service.Kind)!.Roles;
            for (var index = 0; index < roles.Length; index++)
            {
                Role(file, symbol + Symbol(service) + "Role" + (index == 0 ? "" : Number(index + 1)), Symbol(service), roles[index], principal, key);
                if (AzureServices.HasPrediction(service))
                {
                    Role(file, symbol + Symbol(service) + "PredictionRole", Symbol(service) + "Prediction", roles[index], principal, key);
                }
            }
        }
    }

    private static IEnumerable<SketchResource> ServiceDiagnostics(SketchModel model) =>
        ServiceResources(model).Where(service => AzureServices.Find(service.Kind)!.Diagnostics);

    private static void ServiceOutputs(SketchModel model, BicepFile file)
    {
        foreach (var service in ServiceResources(model))
        {
            foreach (var (name, _, value) in AzureServices.Settings(service, Symbol(service)))
            {
                file.Output(Camel(name), "string", value);
            }
        }
    }

    // FILES_ENDPOINT => filesEndpoint.
    private static string Camel(string upper)
    {
        var parts = upper.ToLowerInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries);
        return parts[0] + string.Concat(parts.Skip(1).Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }
}
