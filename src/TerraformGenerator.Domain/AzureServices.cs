using static TerraformGenerator.Domain.Catalog;

namespace TerraformGenerator.Domain;

// More Azure smart boxes, described once for Terraform and Bicep: Azure AI services, AI Search, Event Hubs, Event Grid,
// App Configuration, SignalR, Web PubSub, Maps and Communication Services. Every one signs in with Microsoft Entra only
// (keys off); an arrow from an app gives the app's identity its roles and the address as a setting.
public static class AzureServices
{
    public const string AiService = "aiService";
    public const string Search = "aiSearch";
    public const string EventHubs = "eventHubs";
    public const string EventGrid = "eventGrid";
    public const string AppConfig = "appConfig";
    public const string SignalR = "signalR";
    public const string WebPubSub = "webPubSub";
    public const string Maps = "maps";
    public const string Communication = "communication";
    public const string MachineLearning = "machineLearning";
    public const string Bot = "bot";
    public const string VideoIndexer = "videoIndexer";

    // Kind, Terraform type, Bicep symbol prefix, roles for connected apps, the private DNS zone and the private endpoint's
    // subresource (null when it has no private access), and whether its logs go to Log Analytics.
    public sealed record Spec(string Kind, string TerraformType, string Symbol, string[] Roles, string? ZoneLabel, string? Zone, string? Subresource, bool Diagnostics = true);

    public static readonly IReadOnlyList<Spec> Specs =
    [
        new(AiService, "azurerm_cognitive_account", "cog", ["Cognitive Services User"], "cognitive", "privatelink.cognitiveservices.azure.com", "account"),
        new(Search, "azurerm_search_service", "search", ["Search Index Data Contributor", "Search Service Contributor"], "search", "privatelink.search.windows.net", "searchService"),
        new(EventHubs, "azurerm_eventhub_namespace", "hubs", ["Azure Event Hubs Data Sender", "Azure Event Hubs Data Receiver"], "servicebus", "privatelink.servicebus.windows.net", "namespace"),
        new(EventGrid, "azurerm_eventgrid_topic", "grid", ["EventGrid Data Sender"], "eventgrid", "privatelink.eventgrid.azure.net", "topic"),
        new(AppConfig, "azurerm_app_configuration", "config", ["App Configuration Data Reader"], "appconfig", "privatelink.azconfig.io", "configurationStores"),
        new(SignalR, "azurerm_signalr_service", "signalr", ["SignalR App Server"], "signalr", "privatelink.service.signalr.net", "signalr"),
        new(WebPubSub, "azurerm_web_pubsub", "pubsub", ["Web PubSub Service Owner"], "webpubsub", "privatelink.webpubsub.azure.com", "webpubsub"),
        new(Maps, "azurerm_maps_account", "maps", ["Azure Maps Data Reader"], null, null, null, Diagnostics: false),
        new(Communication, "azurerm_communication_service", "acs", ["Contributor"], null, null, null),
        new(MachineLearning, "azurerm_machine_learning_workspace", "ml", ["AzureML Data Scientist"], null, null, null),
        new(Bot, "azurerm_bot_service_azure_bot", "bot", [], null, null, null, Diagnostics: false),
        new(VideoIndexer, "azurerm_video_indexer_account", "vi", ["Contributor"], null, null, null, Diagnostics: false),
    ];

    public static Spec? Find(string kind) => Specs.FirstOrDefault(spec => spec.Kind == kind);

    // The Azure AI service kinds and the account kind and SKUs Azure uses for each (free, standard).
    public static readonly IReadOnlyDictionary<string, (string Kind, string Free, string Standard)> AiTypes = new Dictionary<string, (string, string, string)>(StringComparer.Ordinal)
    {
        ["documentIntelligence"] = ("FormRecognizer", "F0", "S0"),
        ["language"] = ("TextAnalytics", "F0", "S"),
        ["translator"] = ("TextTranslation", "F0", "S1"),
        ["vision"] = ("ComputerVision", "F0", "S1"),
        ["contentSafety"] = ("ContentSafety", "F0", "S0"),
        ["customVision"] = ("CustomVision.Training", "F0", "S0"),
        ["face"] = ("Face", "F0", "S0"),
    };

    // Custom Vision trains in one account and answers in a second one, its prediction account.
    public static bool HasPrediction(SketchResource service) => service.Kind == AiService && service.P["type"] == "customVision";

    public static string AiKind(SketchResource service) => AiTypes[service.P["type"]].Kind;

    public static string AiSku(SketchResource service) =>
        service.P["tier"] == "free" ? AiTypes[service.P["type"]].Free : AiTypes[service.P["type"]].Standard;

    // Azure Maps keeps its data in a geography; its account goes in one of the regions it supports.
    public static string MapsLocation(SketchModel model) => model.Settings.Region is "northeurope" or "westeurope" or "uksouth" ? "westeurope" : "eastus";

    private const string HubsPattern = "^([a-z0-9][a-z0-9._-]{0,48}[a-z0-9])(, *[a-z0-9][a-z0-9._-]{0,48}[a-z0-9])*$";

    public static readonly IReadOnlyList<ResourceKind> Boxes =
    [
        new(AiService, "Azure AI service", "AIS", "AI",
            "Document Intelligence, Language, Translator, Vision or Content Safety. Apps use it with their identity; keys are off.",
        [
            new("type", "Service", PropertyType.Select, "documentIntelligence",
            [
                O("documentIntelligence", "Document Intelligence (forms, invoices, receipts)"), O("language", "Language (sentiment, entities, summaries)"),
                O("translator", "Translator"), O("vision", "Vision (images, OCR)"), O("contentSafety", "Content Safety (harmful content)"),
                O("customVision", "Custom Vision (your own image classifier)"), O("face", "Face (detect faces; identifying people needs Microsoft's approval)"),
            ]),
            new("tier", "Tier", PropertyType.Select, "standard", [O("standard", "Standard"), O("free", "Free (one per kind of service in the subscription)")]),
            Private(),
        ]),
        new(Search, "AI Search", "SRH", "AI", "Azure AI Search for full-text and vector search. Apps manage indexes and query with their identity; keys are off.",
        [
            new("sku", "Tier", PropertyType.Select, "basic", [O("free", "Free (50 MB, testing)"), O("basic", "Basic"), O("standard", "Standard S1")]),
            new("replicas", "Copies", PropertyType.Select, "1", [O("1", "1"), O("2", "2 (reads keep working during updates)"), O("3", "3 (also writes)")],
                null, "Two copies keep queries working; three keep updates working too.", null, "sku!=free"),
            Private(),
        ]),
        new(EventHubs, "Event Hubs", "EH", "Messaging and cache", "An Event Hubs namespace with its event hubs, for streams of events. Apps send and read with their identity.",
        [
            new("sku", "Tier", PropertyType.Select, "Standard", [O("Basic", "Basic"), O("Standard", "Standard (consumer groups, private access, Kafka)")]),
            new("hubs", "Event hubs", PropertyType.Text, "events", [], HubsPattern, "Names separated by commas."),
            new("partitions", "Partitions per hub", PropertyType.Select, "2", [O("1", "1"), O("2", "2"), O("4", "4"), O("8", "8")], null, "How many readers can work in parallel."),
            Private(),
        ]),
        new(EventGrid, "Event Grid topic", "EG", "Messaging and cache", "An Event Grid topic that apps publish CloudEvents to with their identity; subscribe other services to it.",
        [
            Private(),
        ]),
        new(AppConfig, "App Configuration", "CFG", "Security", "Azure App Configuration for settings and feature flags. Connected apps read it with their identity.",
        [
            new("sku", "Tier", PropertyType.Select, "free", [O("free", "Free"), O("standard", "Standard (private access, recovery)")]),
            Private(),
        ]),
        new(SignalR, "SignalR", "SIG", "Messaging and cache", "Azure SignalR Service for real-time messages to browsers. The app connects with its identity.",
        [
            new("sku", "Tier", PropertyType.Select, "Standard_S1", [O("Free_F1", "Free (20 connections)"), O("Standard_S1", "Standard (1,000 connections per unit)")]),
            new("mode", "Mode", PropertyType.Select, "Default", [O("Default", "Default: an app server holds the hubs"), O("Serverless", "Serverless: functions send the messages")]),
            Private(),
        ]),
        new(WebPubSub, "Web PubSub", "WPS", "Messaging and cache", "Azure Web PubSub for real-time messages over WebSockets. The app manages it with its identity.",
        [
            new("sku", "Tier", PropertyType.Select, "Standard_S1", [O("Free_F1", "Free (20 connections)"), O("Standard_S1", "Standard (1,000 connections per unit)")]),
            Private(),
        ]),
        new(Maps, "Azure Maps", "MAP", "AI", "Azure Maps for maps, search and routes. Apps sign in with their identity; keys are off.", []),
        new(Communication, "Communication Services", "ACS", "Messaging and cache", "Azure Communication Services for email, SMS and calls. Apps use it with their identity.",
        [
            new("dataLocation", "Where its data is kept", PropertyType.Select, "United States",
            [
                O("United States", "United States"), O("Europe", "Europe"), O("UK", "United Kingdom"), O("Canada", "Canada"), O("Brazil", "Brazil"),
                O("Australia", "Australia"), O("Japan", "Japan"), O("Asia Pacific", "Asia Pacific"),
            ]),
            Toggle("email", "Send email", true, "Adds an email service with an Azure managed domain (DoNotReply@...azurecomm.net) linked to it."),
        ]),
        new(MachineLearning, "Machine Learning", "AML", "AI",
            "An Azure Machine Learning workspace to train and publish your own models. Its storage, Key Vault, Application Insights and a compute cluster that stops when idle are added for you.",
        [
            new("vmSize", "Training machines", PropertyType.Select, "Standard_DS3_v2",
            [
                O("Standard_DS3_v2", "Standard_DS3_v2 (4 cores, 14 GB)"), O("Standard_D8s_v3", "Standard_D8s_v3 (8 cores, 32 GB)"),
                O("Standard_NC4as_T4_v3", "Standard_NC4as_T4_v3 (GPU T4)"),
            ]),
            new("maxNodes", "Most machines at once", PropertyType.Select, "2", [O("1", "1"), O("2", "2"), O("4", "4"), O("8", "8")]),
            new("priority", "Machine price", PropertyType.Select, "Dedicated",
                [O("Dedicated", "Dedicated"), O("LowPriority", "Low priority (much cheaper, can be stopped by Azure)")]),
        ]),
        new(Bot, "Bot Service", "BOT", "AI",
            "An Azure Bot for Teams and web chat. Point it to the app that answers the messages; its identity is added to that app.",
        [
            new("sku", "Tier", PropertyType.Select, "F0", [O("F0", "Free (standard channels)"), O("S1", "Standard (premium channels)")]),
            Toggle("teams", "Microsoft Teams", true, "Adds the Teams channel. Web chat is always on."),
        ]),
        new(VideoIndexer, "Video Indexer", "VI", "AI",
            "Azure AI Video Indexer to find who speaks, what is said, text on screen and topics in videos. Its storage and identity are added for you.", []),
    ];

    private static PropertyDefinition Private() =>
        Toggle("privateOnly", "Private only", false, "No public access: the apps that use it reach it over a private network.");

    public static IEnumerable<ConnectionDefinition> Connections =>
        Boxes.Select(kind => kind.Kind).Where(target => target != Bot).SelectMany(target => Kinds.Compute.Select(app => new ConnectionDefinition(app, target, target switch
        {
            AiService => "The app uses the AI service: it gets the Cognitive Services User role and the endpoint as a setting.",
            Search => "The app manages indexes and searches: it gets the Search Index Data Contributor and Search Service Contributor roles and the endpoint as a setting.",
            EventHubs => "The app sends and reads events: it gets the Event Hubs Data Sender and Receiver roles and the namespace as a setting.",
            EventGrid => "The app publishes events: it gets the EventGrid Data Sender role and the topic endpoint as a setting.",
            AppConfig => "The app reads its settings: it gets the App Configuration Data Reader role and the endpoint as a setting.",
            SignalR => "The app sends real-time messages: it gets the SignalR App Server role and the endpoint as a setting.",
            WebPubSub => "The app sends real-time messages: it gets the Web PubSub Service Owner role and the endpoint as a setting.",
            Maps => "The app uses the maps: it gets the Azure Maps Data Reader role and the account's client id as a setting.",
            MachineLearning => "The app calls the published models: it gets the AzureML Data Scientist role and the workspace as a setting.",
            VideoIndexer => "The app indexes videos: it gets the Contributor role on the account and its id as a setting.",
            _ => "The app sends email, SMS or calls: it gets the Contributor role on the resource and the endpoint as a setting.",
        })))
        .Concat(Kinds.Compute.Select(app => new ConnectionDefinition(Bot, app,
            "The bot sends its messages to this app (/api/messages); the app gets the bot's identity and its settings.")))
        .Append(new ConnectionDefinition(MachineLearning, Kinds.ContainerRegistry, "The workspace keeps its model images in this registry."));

    // The settings an app gets for a service: the name, the Terraform value and the Bicep value.
    public static IEnumerable<(string Name, string Terraform, string Bicep)> Settings(SketchResource service, string symbol)
    {
        var label = service.Label;
        var upper = service.Upper;
        switch (service.Kind)
        {
            case AiService:
                yield return ($"AI_{upper}_ENDPOINT", $"azurerm_cognitive_account.{label}.endpoint", $"{symbol}.properties.endpoint");
                if (HasPrediction(service))
                {
                    yield return ($"AI_{upper}_PREDICTION_ENDPOINT", $"azurerm_cognitive_account.{label}_prediction.endpoint", $"{symbol}Prediction.properties.endpoint");
                }

                break;
            case MachineLearning:
                yield return ($"ML_{upper}_WORKSPACE", $"azurerm_machine_learning_workspace.{label}.name", $"{symbol}.name");
                break;
            case VideoIndexer:
                yield return ($"VIDEO_INDEXER_{upper}_RESOURCE_ID", $"azurerm_video_indexer_account.{label}.id", $"{symbol}.id");
                break;
            case Search:
                yield return ($"SEARCH_{upper}_ENDPOINT", $"\"https://${{azurerm_search_service.{label}.name}}.search.windows.net\"", $"'https://${{{symbol}.name}}.search.windows.net'");
                break;
            case EventHubs:
                yield return ($"EVENTHUBS_{upper}_NAMESPACE", $"\"${{azurerm_eventhub_namespace.{label}.name}}.servicebus.windows.net\"",
                    $"split(replace({symbol}.properties.serviceBusEndpoint, 'https://', ''), ':')[0]");
                break;
            case EventGrid:
                yield return ($"EVENTGRID_{upper}_ENDPOINT", $"azurerm_eventgrid_topic.{label}.endpoint", $"{symbol}.properties.endpoint");
                break;
            case AppConfig:
                yield return ($"APPCONFIG_{upper}_ENDPOINT", $"azurerm_app_configuration.{label}.endpoint", $"{symbol}.properties.endpoint");
                break;
            case SignalR:
                yield return ($"SIGNALR_{upper}_ENDPOINT", $"\"https://${{azurerm_signalr_service.{label}.hostname}}\"", $"'https://${{{symbol}.properties.hostName}}'");
                break;
            case WebPubSub:
                yield return ($"WEBPUBSUB_{upper}_ENDPOINT", $"\"https://${{azurerm_web_pubsub.{label}.hostname}}\"", $"'https://${{{symbol}.properties.hostName}}'");
                break;
            case Maps:
                yield return ($"MAPS_{upper}_CLIENT_ID", $"azurerm_maps_account.{label}.x_ms_client_id", $"{symbol}.properties.uniqueId");
                break;
            case Communication:
                yield return ($"COMMUNICATION_{upper}_ENDPOINT", $"\"https://${{azurerm_communication_service.{label}.hostname}}\"", $"'https://${{{symbol}.properties.hostName}}'");
                if (service.Flag("email"))
                {
                    yield return ($"COMMUNICATION_{upper}_SENDER", $"\"DoNotReply@${{azurerm_email_communication_service_domain.{label}.mail_from_sender_domain}}\"",
                        $"'DoNotReply@${{{symbol}EmailDomain.properties.mailFromSenderDomain}}'");
                }

                break;
        }
    }

    // What the cloud would refuse: tiers without private access.
    public static IEnumerable<SketchMessage> Check(SketchModel model)
    {
        foreach (var resource in model.Resources.Where(resource => Find(resource.Kind) is not null || Kinds.IsCompute(resource.Kind)))
        {
            var noPrivate = resource.Kind switch
            {
                Search => resource.P["sku"] == "free",
                EventHubs => resource.P["sku"] == "Basic",
                AppConfig => resource.P["sku"] == "free",
                SignalR or WebPubSub => resource.P["sku"] == "Free_F1",
                _ => false,
            };
            if (noPrivate && resource.IsPrivate)
            {
                yield return new(MessageLevel.Error, $"{resource.Name}: this tier has no private access. Choose a higher tier or turn off \"Private only\".", resource.Node.Id);
            }

            if (resource.Kind == AppConfig && resource.P["sku"] == "free" && model.IsProduction)
            {
                yield return new(MessageLevel.Warning, $"{resource.Name}: the Free tier has no recovery of deleted settings and a daily request limit; production usually needs Standard.", resource.Node.Id);
            }

            if (resource.Kind == Search && resource.P["sku"] != "free" && model.IsProduction && resource.P["replicas"] == "1")
            {
                yield return new(MessageLevel.Warning, $"{resource.Name}: production with one copy stops answering while the service updates.", resource.Node.Id);
            }

            if (resource.Kind == AiService && resource.P["type"] == "face")
            {
                yield return new(MessageLevel.Info, $"{resource.Name}: detecting faces works at once; identifying or verifying people needs Microsoft's approval (Limited Access form).", resource.Node.Id);
            }

            if (resource.Kind == Bot)
            {
                var apps = model.Links.Count(link => link.From == resource);
                if (apps != 1)
                {
                    yield return new(MessageLevel.Error, $"{resource.Name}: draw one arrow from the bot to the app that answers its messages.", resource.Node.Id);
                }
            }

            if (Kinds.IsCompute(resource.Kind) && model.Links.Count(link => link.To == resource && link.From.Kind == Bot) > 1)
            {
                yield return new(MessageLevel.Error, $"{resource.Name}: an app answers one bot. Give each bot its own app.", resource.Node.Id);
            }

            if (resource.Kind == MachineLearning)
            {
                yield return new(MessageLevel.Info, $"{resource.Name}: the compute cluster starts machines only while a job runs and stops them after 15 idle minutes.", resource.Node.Id);
            }

            if (resource.Kind == Maps)
            {
                yield return new(MessageLevel.Info, $"{resource.Name}: Azure Maps runs in {MapsLocation(model)} (it is offered in a few regions only).", resource.Node.Id);
            }

            if (resource.Kind == Communication && resource.Flag("email"))
            {
                yield return new(MessageLevel.Info, $"{resource.Name}: email goes out from an Azure managed domain; add your own domain in the portal when you are ready.", resource.Node.Id);
            }
        }
    }
}
