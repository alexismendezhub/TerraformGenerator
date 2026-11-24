namespace TerraformGenerator.Domain;

public enum PropertyType
{
    Select,
    Toggle,
    Text,
}

public sealed record PropertyOption(string Value, string Label);

public sealed record PropertyDefinition(
    string Key,
    string Label,
    PropertyType Type,
    string Default,
    IReadOnlyList<PropertyOption> Options,
    string? Pattern = null,
    string? Help = null,
    string? Group = null,
    string? ShowWhen = null)
{
    // ShowWhen is "key=value" or "key!=value": the field matters only then (the screen hides it otherwise).
    // Several conditions are joined with "&" and must all hold.
    public bool Visible(Func<string, string> valueOf) =>
        ShowWhen is null || ShowWhen.Split('&').All(condition =>
        {
            var negate = condition.Contains("!=", StringComparison.Ordinal);
            var parts = condition.Split(negate ? "!=" : "=");
            return (valueOf(parts[0]) == parts[1]) != negate;
        });
}

public sealed record ResourceKind(
    string Kind,
    string Label,
    string Short,
    string Group,
    string Description,
    IReadOnlyList<PropertyDefinition> Properties,
    string Platform = Platforms.Azure);

public sealed record ConnectionDefinition(string From, string To, string Description);

public sealed record SketchCatalog(
    IReadOnlyList<ResourceKind> Kinds,
    IReadOnlyList<ConnectionDefinition> Connections,
    IReadOnlyList<PropertyOption> Regions,
    IReadOnlyList<PropertyOption> Environments,
    IReadOnlyList<PropertyDefinition> Settings,
    string NamePattern,
    string ProjectNamePattern);

public static class Kinds
{
    public const string WebApp = "webApp";
    public const string ContainerApp = "containerApp";
    public const string FunctionApp = "functionApp";
    public const string StaticWebApp = "staticWebApp";
    public const string SqlDatabase = "sqlDatabase";
    public const string Postgres = "postgres";
    public const string CosmosDb = "cosmosDb";
    public const string Storage = "storage";
    public const string Redis = "redis";
    public const string ServiceBus = "serviceBus";
    public const string KeyVault = "keyVault";
    public const string ContainerRegistry = "containerRegistry";
    public const string FrontDoor = "frontDoor";
    public const string AppGateway = "appGateway";
    public const string DnsZone = "dnsZone";
    public const string AiFoundry = "aiFoundry";
    public const string Speech = "speech";

    // Code that runs: it gets an identity, settings with the addresses it uses, and roles on what it points to.
    public static readonly IReadOnlyList<string> Compute = [WebApp, ContainerApp, FunctionApp];

    // What Front Door can serve.
    public static readonly IReadOnlyList<string> Origins = [WebApp, ContainerApp, FunctionApp, StaticWebApp];

    public static bool IsCompute(string kind) => Compute.Contains(kind);

    // A box for any resource of a Terraform provider: "tf:" followed by the resource type (tf:aws_s3_bucket).
    public const string GenericPrefix = "tf:";

    public static bool IsGeneric(string kind) => kind.StartsWith(GenericPrefix, StringComparison.Ordinal);

    public static string TypeOf(string kind) => kind[GenericPrefix.Length..];
}

// Global choices beyond name, environment, region and monitoring.
public static class SettingKeys
{
    public const string GcpProject = "gcpProject";
    public const string Subscription = "subscriptionId";
    public const string ResourceGroup = "resourceGroup";
    public const string ResourceGroupName = "resourceGroupName";
    public const string Owner = "owner";
    public const string CostCenter = "costCenter";
    public const string Network = "network";
    public const string NetworkRange = "networkRange";
    public const string NetworkName = "networkName";
    public const string NetworkResourceGroup = "networkResourceGroup";
    public const string PrivateDns = "privateDns";
    public const string PrivateDnsResourceGroup = "privateDnsResourceGroup";
    public const string Nsg = "nsg";
    public const string Logs = "logs";
    public const string LogWorkspaceId = "logWorkspaceId";
    public const string LogRetention = "logRetention";
    public const string Alerts = "alerts";
    public const string AlertEmail = "alertEmail";
    public const string DatabaseAdmin = "databaseAdmin";
    public const string DatabaseAdminGroupName = "databaseAdminGroupName";
    public const string DatabaseAdminGroupId = "databaseAdminGroupId";
    public const string Lock = "lock";
    public const string Output = "output";
    public const string State = "state";
    public const string StateResourceGroup = "stateResourceGroup";
    public const string Pipeline = "pipeline";
    public const string Budget = "budget";
    public const string BudgetEmail = "budgetEmail";
    public const string AwsNetwork = "awsNetwork";
    public const string Nat = "nat";
    public const string GcpNetwork = "gcpNetwork";
    public const string CloudNetworkRange = "cloudNetworkRange";
}

// The one list of boxes, their choices and the arrows allowed between them; the screen and the AI prompt read it.
public static class Catalog
{
    public const string NamePattern = "^[a-z][a-z0-9-]{0,18}[a-z0-9]$";
    public const string ProjectNamePattern = "^[a-z][a-z0-9]{1,11}$";
    public const string ResourceGroupPattern = "^$|^[-\\w.()]{0,89}[-\\w()]$";
    public const string EmailPattern ="^$|^[^@\\s]+@[^@\\s]+\\.[^@\\s]+$";
    internal const string PathPattern = "^/([A-Za-z0-9_-]+/)*\\*$";
    internal const string ImagePattern = "^[a-z0-9][a-z0-9._/-]*(:[A-Za-z0-9._-]+)?$";
    public const string GuidPattern = "^$|^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$";
    public const string RangePattern = "^(10|172|192)\\.[0-9]{1,3}\\.0\\.0/16$";
    private const string TagPattern = "^[^<>%&\\\\?/]{0,256}$";
    internal const string ListPattern ="^([a-z0-9][a-z0-9-]{1,61}[a-z0-9])(, *[a-z0-9][a-z0-9-]{1,61}[a-z0-9])*$";

    internal static PropertyOption O(string value, string label) => new(value, label);

    internal static PropertyDefinition Toggle(string key, string label, bool value, string? help = null) =>
        new(key, label, PropertyType.Toggle, value ? "true" : "false", [], null, help);

    private static PropertyDefinition Private() =>
        Toggle("privateOnly", "Private only", false, "No public access: the apps that use it reach it over a private network.");

    private static PropertyDefinition FrontDoorPath() =>
        new("path", "Path at the entry", PropertyType.Text, "/*", [], PathPattern, "The path Front Door or Application Gateway sends here, for example /* or /api/*.");

    // Settings that exist only for Azure (resource groups, its network, Bicep...) show only with Azure; the tags and the
    // Terraform state apply to every cloud.
    private static readonly HashSet<string> EveryPlatform = new(
    [
        SettingKeys.Owner, SettingKeys.CostCenter, SettingKeys.State, SettingKeys.GcpProject, SettingKeys.Pipeline,
        SettingKeys.AwsNetwork, SettingKeys.Nat, SettingKeys.GcpNetwork, SettingKeys.CloudNetworkRange, .. Team.Keys,
        SettingKeys.Alerts, SettingKeys.AlertEmail, SettingKeys.Budget, SettingKeys.BudgetEmail, CloudAlerts.GcpBillingAccount, Protection.Key, Environments.Key, AppPipeline.Key, Guardrails.Key, Geo.Key, OwnKeys.Key,
    ], StringComparer.Ordinal);

    private static List<PropertyDefinition> WithPlatforms(List<PropertyDefinition> settings) =>
        settings.Select(setting => EveryPlatform.Contains(setting.Key)
            ? setting
            : setting with { ShowWhen = setting.ShowWhen is null ? "platform=azure" : "platform=azure&" + setting.ShowWhen }).ToList();

    private static IEnumerable<ConnectionDefinition> FromApps(string to, string description) =>
        Kinds.Compute.Select(app => new ConnectionDefinition(app, to, description));

    public static readonly SketchCatalog Instance = new(
        [
            new(Kinds.WebApp, "Web App", "APP", "Apps", "An App Service web app or API on Linux. Its App Service plan is added for you.",
            [
                new("runtime", "Runtime", PropertyType.Select, "dotnet8",
                [
                    O("dotnet8", ".NET 8"), O("dotnet9", ".NET 9"), O("node20", "Node.js 20"), O("node22", "Node.js 22"),
                    O("python312", "Python 3.12"), O("docker", "Docker image"),
                ]),
                new("dockerImage", "Docker image", PropertyType.Text, "nginx:latest", [], ImagePattern,
                    "With a registry arrow, the image name inside that registry.", null, "runtime=docker"),
                new("sku", "Size (plan)", PropertyType.Select, "B1",
                [
                    O("B1", "B1 Basic (1 core, 1.75 GB)"), O("B2", "B2 Basic (2 cores, 3.5 GB)"), O("S1", "S1 Standard"),
                    O("P0v3", "P0v3 Premium"), O("P1v3", "P1v3 Premium"),
                ], null, "Apps with the same size share one App Service plan."),
                Toggle("alwaysOn", "Always on", true, "Keeps the app loaded so the first request is not slow."),
                FrontDoorPath(),
            ]),
            new(Kinds.ContainerApp, "Container App", "CA", "Apps", "A container on Azure Container Apps that scales by itself. The Container Apps environment is added for you.",
            [
                new("image", "Image", PropertyType.Text, "mcr.microsoft.com/k8se/quickstart:latest", [], ImagePattern,
                    "With a registry arrow, the image name inside that registry (for example shop/api:1.0)."),
                new("size", "Size", PropertyType.Select, "0.5",
                    [O("0.25", "0.25 CPU, 0.5 GB"), O("0.5", "0.5 CPU, 1 GB"), O("1", "1 CPU, 2 GB"), O("2", "2 CPU, 4 GB")]),
                new("minReplicas", "Always running", PropertyType.Select, "0",
                    [O("0", "0: stops when idle (cheapest)"), O("1", "1 copy"), O("2", "2 copies"), O("3", "3 copies")]),
                new("maxReplicas", "Most copies", PropertyType.Select, "3", [O("1", "1"), O("3", "3"), O("5", "5"), O("10", "10")]),
                new("port", "Port the container listens on", PropertyType.Select, "80",
                    [O("80", "80"), O("8080", "8080"), O("3000", "3000"), O("5000", "5000"), O("8000", "8000")]),
                Toggle("external", "Reachable from the internet", true, "Off keeps it inside the Container Apps environment."),
                FrontDoorPath(),
            ]),
            new(Kinds.FunctionApp, "Function App", "FN", "Apps", "Azure Functions on Linux for background jobs and small APIs. Its plan and its own storage are added for you.",
            [
                new("runtime", "Runtime", PropertyType.Select, "dotnet8",
                    [O("dotnet8", ".NET 8 (isolated)"), O("node20", "Node.js 20"), O("python311", "Python 3.11")]),
                new("plan", "Plan", PropertyType.Select, "Y1",
                    [O("Y1", "Consumption: pay per run"), O("EP1", "Elastic Premium EP1: always warm, private network")]),
                FrontDoorPath(),
            ]),
            new(Kinds.StaticWebApp, "Static Web App", "SWA", "Apps", "A static site (Angular, React, plain HTML) with free HTTPS. Point it to an API to give the site its address.",
            [
                new("sku", "Tier", PropertyType.Select, "Free", [O("Free", "Free"), O("Standard", "Standard (custom auth, more sites)")]),
                FrontDoorPath(),
            ]),
            new(Kinds.SqlDatabase, "SQL Database", "SQL", "Data", "An Azure SQL database. The SQL server is added for you and apps sign in with their identity (no passwords).",
            [
                new("sku", "Size", PropertyType.Select, "S0",
                [
                    O("Basic", "Basic (5 DTU, 2 GB)"), O("S0", "Standard S0 (10 DTU)"), O("S1", "Standard S1 (20 DTU)"),
                    O("GP_S_Gen5_1", "Serverless (pauses when idle)"), O("GP_Gen5_2", "General Purpose 2 vCores"),
                ]),
                new("backupDays", "Point-in-time backup", PropertyType.Select, "7",
                    [O("7", "7 days"), O("14", "14 days"), O("35", "35 days")]),
                Private(),
            ]),
            new(Kinds.Postgres, "PostgreSQL", "PG", "Data", "An Azure Database for PostgreSQL flexible server with one database. Apps sign in with their identity (no passwords).",
            [
                new("version", "Version", PropertyType.Select, "16", [O("14", "14"), O("15", "15"), O("16", "16")]),
                new("sku", "Size", PropertyType.Select, "B_Standard_B1ms",
                [
                    O("B_Standard_B1ms", "Burstable B1ms (1 core, 2 GB)"), O("B_Standard_B2s", "Burstable B2s (2 cores, 4 GB)"),
                    O("GP_Standard_D2ds_v5", "General Purpose D2ds v5 (2 cores, 8 GB)"),
                ]),
                new("storageGb", "Storage", PropertyType.Select, "32", [O("32", "32 GB"), O("64", "64 GB"), O("128", "128 GB"), O("256", "256 GB")]),
                new("backupDays", "Backup", PropertyType.Select, "7", [O("7", "7 days"), O("14", "14 days"), O("35", "35 days")]),
                Toggle("vector", "Vector search (pgvector)", false, "Lets the database store embeddings and search by meaning (CREATE EXTENSION vector)."),
                Private(),
            ]),
            new(Kinds.CosmosDb, "Cosmos DB", "COS", "Data", "Azure Cosmos DB (NoSQL) with one database and its containers. Apps use their identity; keys are turned off.",
            [
                new("capacity", "Capacity", PropertyType.Select, "serverless",
                    [O("serverless", "Serverless: pay per request"), O("provisioned", "Provisioned: 400 RU/s per container")]),
                new("containers", "Containers", PropertyType.Text, "items", [], ListPattern, "Names separated by commas; each uses /id as its partition key."),
                new("backup", "Backup", PropertyType.Select, "Periodic",
                [
                    O("Periodic", "Periodic (every 4 hours, 8 hours kept)"), O("Continuous7Days", "Continuous: any point in 7 days"),
                    O("Continuous30Days", "Continuous: any point in 30 days"),
                ]),
                Toggle("vectorSearch", "Vector search", false, "Lets the containers store embeddings and search by meaning."),
                Private(),
            ]),
            new(Kinds.Storage, "Storage", "STO", "Data", "A storage account for files (blob containers).",
            [
                new("replication", "Copies", PropertyType.Select, "LRS",
                [
                    O("LRS", "LRS: 3 copies in one datacenter"), O("ZRS", "ZRS: 3 zones in the region"), O("GRS", "GRS: copied to a second region"),
                ]),
                new("containers", "Containers", PropertyType.Text, "files", [], ListPattern, "Names separated by commas."),
                new("softDeleteDays", "Keep deleted files", PropertyType.Select, "7",
                    [O("0", "No: deleted is gone"), O("7", "7 days"), O("30", "30 days"), O("90", "90 days")], null,
                    "Deleted or overwritten files and containers can be brought back during this time."),
                Toggle("versioning", "Keep every version", false, "Each change keeps the previous version of the file."),
                new("queues", "Queues", PropertyType.Text, "", [], "^$|" + ListPattern[1..], "Optional: message queues in this account, names separated by commas."),
                new("tables", "Tables", PropertyType.Text, "", [], "^$|^([A-Za-z][A-Za-z0-9]{2,62})(, *[A-Za-z][A-Za-z0-9]{2,62})*$",
                    "Optional: tables (letters and digits) in this account, names separated by commas."),
                Private(),
            ]),
            new(Kinds.Redis, "Redis Cache", "RED", "Messaging and cache", "Azure Cache for Redis. Apps sign in with their identity; access keys are off.",
            [
                new("sku", "Size", PropertyType.Select, "Basic_C0",
                [
                    O("Basic_C0", "Basic C0 (250 MB, no copy)"), O("Standard_C1", "Standard C1 (1 GB, with a copy)"),
                    O("Premium_P1", "Premium P1 (6 GB)"),
                ]),
                Private(),
            ]),
            new(Kinds.ServiceBus, "Service Bus", "SB", "Messaging and cache", "A Service Bus namespace with queues for work between apps. Apps send and receive with their identity.",
            [
                new("sku", "Tier", PropertyType.Select, "Standard",
                    [O("Basic", "Basic (queues only)"), O("Standard", "Standard"), O("Premium", "Premium (private network)")]),
                new("queues", "Queues", PropertyType.Text, "jobs", [], ListPattern, "Names separated by commas."),
                Private(),
            ]),
            new(Kinds.KeyVault, "Key Vault", "KV", "Security", "A Key Vault for secrets. Access by role (RBAC); connected apps can read secrets.",
            [
                new("retentionDays", "Keep deleted secrets", PropertyType.Select, "7",
                    [O("7", "7 days"), O("30", "30 days"), O("90", "90 days")]),
                Toggle("purgeProtection", "Purge protection", false, "Deleted secrets cannot be erased before the retention ends. Cannot be turned off later."),
                Private(),
            ]),
            new(Kinds.ContainerRegistry, "Container Registry", "ACR", "Security", "A private registry for your container images. Connected apps pull with their identity.",
            [
                new("sku", "Tier", PropertyType.Select, "Basic", [O("Basic", "Basic"), O("Standard", "Standard"), O("Premium", "Premium")]),
            ]),
            new(Kinds.FrontDoor, "Front Door", "FD", "Edge", "Azure Front Door: one public entry with HTTPS. Web and function apps behind it accept traffic only from it.",
            [
                new("sku", "Tier", PropertyType.Select, "Standard_AzureFrontDoor",
                    [O("Standard_AzureFrontDoor", "Standard"), O("Premium_AzureFrontDoor", "Premium (adds private links and WAF rules)")]),
                new("subdomain", "Subdomain", PropertyType.Text, "www", [], "^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$",
                    "Used when a DNS zone points to this Front Door: www gives www.<zone>."),
                Firewall.Option("Filters attacks before they reach the apps. The rules Microsoft keeps up to date need Premium; Standard limits the requests."),
                Firewall.Rate(),
            ]),
            new(Kinds.AppGateway, "Application Gateway", "AGW", "Edge",
                "Azure Application Gateway in the region, instead of Front Door: one public entry with a firewall, routing by path to the apps. Its subnet is added for you.",
            [
                Firewall.Option("The Web Application Firewall with Microsoft's rules (DRS 2.1, bots) and a request limit per address."),
                Firewall.Rate(),
                new("https", "HTTPS", PropertyType.Select, "none",
                    [O("none", "HTTP only (testing)"), O("keyVault", "HTTPS with a certificate in a Key Vault (draw an arrow to it)")], null,
                    "Application Gateway does not issue certificates: put yours in a Key Vault and name it below."),
                new("certificate", "Certificate name in the Key Vault", PropertyType.Text, "site", [], "^[a-zA-Z0-9-]{1,127}$", null, null, "https=keyVault"),
                new("minCapacity", "Always running", PropertyType.Select, "1", [O("0", "0 (starts when needed)"), O("1", "1 unit"), O("2", "2 units")]),
                new("maxCapacity", "Most units", PropertyType.Select, "5", [O("2", "2"), O("5", "5"), O("10", "10")]),
                new("subdomain", "Subdomain", PropertyType.Text, "www", [], "^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$",
                    "Used when a DNS zone points to this gateway: www gives www.<zone>."),
            ]),
            new(Kinds.AiFoundry, "AI Foundry", "AI", "AI", "Microsoft Foundry (Azure AI Foundry): AI models with a project. Apps use the models with their identity; keys are off.",
            [
                new("model", "Chat model", PropertyType.Select, "gpt-4.1-mini",
                [
                    O("gpt-4.1-mini", "GPT-4.1 mini (fast, low cost)"), O("gpt-4.1", "GPT-4.1"), O("gpt-4o-mini", "GPT-4o mini"),
                    O("o4-mini", "o4-mini (reasoning)"), O("none", "None"),
                ]),
                new("embeddings", "Embeddings model", PropertyType.Select, "none",
                    [O("none", "None"), O("text-embedding-3-small", "text-embedding-3-small"), O("text-embedding-3-large", "text-embedding-3-large")], null,
                    "For search over your own documents."),
                new("capacity", "Capacity", PropertyType.Select, "10",
                    [O("10", "10,000 tokens per minute"), O("50", "50,000 tokens per minute"), O("100", "100,000 tokens per minute"), O("250", "250,000 tokens per minute")], null,
                    "How much each model can process per minute; you pay for what you use."),
                Private(),
            ]),
            new(Kinds.Speech, "Speech", "SPK", "AI", "Azure AI Speech: speech to text, text to speech and translation. Apps use it with their identity; keys are off.",
            [
                new("sku", "Tier", PropertyType.Select, "S0", [O("S0", "Standard S0"), O("F0", "Free F0 (limited, one per subscription)")]),
                Private(),
            ]),
            new(Kinds.DnsZone, "DNS Zone", "DNS", "Edge", "A public DNS zone for your domain. Point it to a Front Door to get a custom domain with a free certificate.",
            [
                new("zone", "Domain", PropertyType.Text, "example.com", [],
                    "^([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z]{2,63}$", "The domain you own, for example example.com."),
            ]),
            .. AzureServices.Boxes,
            .. CloudKinds.Aws,
            .. CloudKinds.Gcp,
        ],
        [
            .. FromApps(Kinds.SqlDatabase, "The app uses the database: it signs in with its managed identity and gets the connection string as a setting."),
            .. FromApps(Kinds.Postgres, "The app uses PostgreSQL: it signs in with its managed identity and gets the host and database as settings."),
            .. FromApps(Kinds.CosmosDb, "The app uses Cosmos DB: it gets the data contributor role and the endpoint as a setting."),
            .. FromApps(Kinds.Storage, "The app reads and writes the files: it gets the Storage Blob Data Contributor role."),
            .. FromApps(Kinds.Redis, "The app uses the cache: it gets a Redis access policy for its identity and the host as a setting."),
            .. FromApps(Kinds.ServiceBus, "The app sends and receives messages: it gets the Service Bus sender and receiver roles."),
            .. FromApps(Kinds.KeyVault, "The app reads secrets: it gets the Key Vault Secrets User role and the vault URI as a setting."),
            .. FromApps(Kinds.AiFoundry, "The app uses the AI models: it gets the Azure AI User and Cognitive Services OpenAI User roles and the endpoint and model names as settings."),
            .. FromApps(Kinds.Speech, "The app uses Speech: it gets the Cognitive Services Speech User role and the endpoint, region and resource id as settings."),
            .. Kinds.Compute.SelectMany(from => Kinds.Compute.Select(to => new ConnectionDefinition(from, to, "The app calls the other app: it gets the other app's URL as a setting."))),
            .. Kinds.Compute.Select(to => new ConnectionDefinition(Kinds.StaticWebApp, to, "The site calls this API: it gets the API's URL as a setting.")),
            new(Kinds.WebApp, Kinds.ContainerRegistry, "The app runs its Docker image from this registry: it gets the AcrPull role."),
            new(Kinds.ContainerApp, Kinds.ContainerRegistry, "The container comes from this registry: the app gets the AcrPull role."),
            .. Kinds.Origins.Select(to => new ConnectionDefinition(Kinds.FrontDoor, to, "Front Door serves it on its path.")),
            new(Kinds.DnsZone, Kinds.FrontDoor, "The domain points to Front Door: a custom domain with a managed certificate and its DNS records."),
            .. Kinds.Origins.Select(to => new ConnectionDefinition(Kinds.AppGateway, to, "The gateway serves it on its path; web and function apps then accept traffic only from the gateway.")),
            new(Kinds.DnsZone, Kinds.AppGateway, "The domain points to the gateway's public address."),
            new(Kinds.AppGateway, Kinds.KeyVault, "The gateway reads its HTTPS certificate from this Key Vault."),
            .. AzureServices.Connections,
            .. CloudKinds.Connections,
        ],
        [
            O("eastus", "East US"), O("eastus2", "East US 2"), O("centralus", "Central US"), O("southcentralus", "South Central US"),
            O("westus2", "West US 2"), O("westus3", "West US 3"), O("canadacentral", "Canada Central"), O("mexicocentral", "Mexico Central"),
            O("brazilsouth", "Brazil South"), O("northeurope", "North Europe"), O("westeurope", "West Europe"), O("uksouth", "UK South"),
        ],
        [O("dev", "Development"), O("test", "Test"), O("staging", "Staging"), O("prod", "Production")],
        WithPlatforms([
            // Google Cloud: the project everything goes in.
            new(SettingKeys.GcpProject, "Project id", PropertyType.Text, "", [], "^$|^[a-z][a-z0-9-]{4,28}[a-z0-9]$",
                "The Google Cloud project every resource goes in (gcloud projects list shows yours).", "Google Cloud", "platform=gcp"),

            // Azure: where everything goes.
            new(SettingKeys.Subscription, "Subscription id", PropertyType.Text, "", [], GuidPattern,
                "Empty means the subscription you are signed in to with az login.", "Account and tags"),
            new(SettingKeys.ResourceGroup, "Resource group", PropertyType.Select, "new",
                [O("new", "Create a new resource group"), O("existing", "Use a resource group that already exists")], null,
                "Every resource goes in it. A new one is created (and deleted with everything); an existing one is only used.", "Account and tags"),
            new(SettingKeys.ResourceGroupName, "Resource group name", PropertyType.Text, "", [], ResourceGroupPattern,
                "Empty means <project>-<environment>-rg. Letters, digits, - _ . ( ), up to 90.", "Account and tags"),
            new(SettingKeys.Owner, "Owner (tag)", PropertyType.Text, "", [], TagPattern, "Who is responsible, as a tag on every resource. Empty leaves it out.", "Account and tags"),
            new(SettingKeys.CostCenter, "Cost center (tag)", PropertyType.Text, "", [], TagPattern, "For billing reports, as a tag on every resource. Empty leaves it out.", "Account and tags"),

            // Network: used only when something is Private only.
            new(SettingKeys.AwsNetwork, "Network", PropertyType.Select, "own",
                [O("own", "A VPC of its own: private subnets for code and databases (recommended)"), O("default", "The account's default VPC (public subnets, testing only)")],
                null, "Used by containers, databases, SageMaker and functions that reach a database.", "Network", "platform=aws"),
            new(SettingKeys.Nat, "Internet access from the private subnets", PropertyType.Select, "auto",
            [
                O("auto", "One NAT gateway, one per zone in production"), O("one", "One NAT gateway (cheaper)"), O("perZone", "One NAT gateway per zone (keeps working if a zone fails)"),
                O("none", "None (code cannot reach the internet)"),
            ], null, "How code in the private subnets reaches the internet (to pull images, call APIs). Each NAT gateway costs about 35 dollars a month.", "Network",
                "platform=aws&awsNetwork=own"),
            new(SettingKeys.GcpNetwork, "Network", PropertyType.Select, "private",
                [O("private", "Private: Cloud SQL without a public address, reached through a VPC (recommended)"), O("public", "Public address, reached only with the Cloud SQL connector")],
                null, "How code reaches the Cloud SQL databases.", "Network", "platform=gcp"),
            new(SettingKeys.CloudNetworkRange, "Address range", PropertyType.Text, "10.0.0.0/16", [], RangePattern,
                "A /16 that does not overlap your other networks or VPN.", "Network", "platform!=azure"),
            new(SettingKeys.Network, "Private network", PropertyType.Select, "new",
                [O("new", "Create a new virtual network"), O("existing", "Add subnets to an existing virtual network")], null,
                "Used only when something is Private only.", "Network"),
            new(SettingKeys.NetworkRange, "Address range", PropertyType.Text, "10.50.0.0/16", [], RangePattern,
                "A /16 that does not overlap your other networks or VPN. With an existing network, a free /16 inside it.", "Network"),
            new(SettingKeys.NetworkName, "Existing network name", PropertyType.Text, "", [], ResourceGroupPattern, null, "Network", "network=existing"),
            new(SettingKeys.NetworkResourceGroup, "Its resource group", PropertyType.Text, "", [], ResourceGroupPattern, null, "Network", "network=existing"),
            new(SettingKeys.PrivateDns, "Private DNS zones", PropertyType.Select, "new",
                [O("new", "Create them in the resource group"), O("existing", "Use central zones that already exist")], null,
                "Companies often keep them in one place; the network is linked to them.", "Network"),
            new(SettingKeys.PrivateDnsResourceGroup, "Resource group of the zones", PropertyType.Text, "", [], ResourceGroupPattern, null, "Network", "privateDns=existing"),
            new(SettingKeys.Nsg, "Network security groups", PropertyType.Toggle, "true", [], null,
                "A network security group on every subnet, ready for your own rules.", "Network"),

            // Monitoring.
            new(SettingKeys.Logs, "Log Analytics workspace", PropertyType.Select, "new",
                [O("new", "Create one for this environment"), O("existing", "Use one that already exists")], null, null, "Monitoring", "monitoring=true"),
            new(SettingKeys.LogWorkspaceId, "Workspace resource id", PropertyType.Text, "", [],
                "^$|^/subscriptions/[^/]+/resourceGroups/[^/]+/providers/Microsoft\\.OperationalInsights/workspaces/[^/]+$",
                "Copy it from the workspace's JSON view in the Azure portal.", "Monitoring", "logs=existing"),
            new(SettingKeys.LogRetention, "Keep logs", PropertyType.Select, "auto",
                [O("auto", "30 days (90 in production)"), O("30", "30 days"), O("90", "90 days"), O("180", "180 days"), O("365", "365 days")], null, null, "Monitoring", "logs=new"),
            new(SettingKeys.Alerts, "Alerts", PropertyType.Select, "off",
                [O("off", "None"), O("on", "Email when apps fail or databases are overloaded")], null,
                "Server errors on web and function apps, and high CPU on databases.", "Monitoring"),
            new(SettingKeys.AlertEmail, "Alert email", PropertyType.Text, "", [], EmailPattern, null, "Monitoring", "alerts=on"),

            // Security.
            new(SettingKeys.DatabaseAdmin, "Database administrator", PropertyType.Select, "me",
                [O("me", "Me (whoever deploys)"), O("group", "A Microsoft Entra group")], null,
                "Who administers SQL and PostgreSQL. A group survives people leaving.", "Security"),
            new(SettingKeys.DatabaseAdminGroupName, "Group name", PropertyType.Text, "", [], TagPattern, null, "Security", "databaseAdmin=group"),
            new(SettingKeys.DatabaseAdminGroupId, "Group object id", PropertyType.Text, "", [], GuidPattern, "In the Azure portal: Microsoft Entra ID > Groups.", "Security", "databaseAdmin=group"),
            new(SettingKeys.Lock, "Delete lock", PropertyType.Select, "auto",
                [O("auto", "In production only"), O("on", "Always"), O("off", "Never")], null,
                "Stops anyone from deleting the resource group by mistake. deploy.ps1 -Destroy removes it first.", "Security"),

            // Code and deployment.
            new(SettingKeys.Output, "Code to generate", PropertyType.Select, "terraform",
                [O("terraform", "Terraform"), O("bicep", "Bicep"), O("both", "Terraform and Bicep")], null,
                "Bicep is Azure's own language; Terraform also works with other clouds.", "Code and deployment"),
            new(SettingKeys.State, "Where Terraform keeps its state", PropertyType.Select, "azure",
                [O("azure", "In the cloud: a storage account, S3 bucket or Cloud Storage bucket (recommended)"), O("local", "On this computer")], null,
                "Terraform remembers what it created in a state file. In the cloud it is shared and backed up; a script creates its storage.",
                "Code and deployment", "output!=bicep"),
            new(SettingKeys.StateResourceGroup, "Resource group of the state", PropertyType.Text, "", [], ResourceGroupPattern,
                "Empty means <project>-tfstate-rg. Keep it apart from the environment.", "Code and deployment", "state=azure"),
            new(SettingKeys.Pipeline, "Deploy from GitHub", PropertyType.Select, "none",
                [O("none", "No, I run it from my computer"), O("github", "Yes, GitHub Actions: plan on pull requests, apply on main")], null, null,
                "Code and deployment"),

            // Budget.
            Protection.Setting(),
            Guardrails.Setting(),
            OwnKeys.Setting(),
            Geo.Setting(),
            Environments.Setting(),
            AppPipeline.Setting(),
            .. Team.Settings,
            new(SettingKeys.Budget, "Monthly budget alert", PropertyType.Select, "0",
            [
                O("0", "None"), O("50", "US$50"), O("100", "US$100"), O("250", "US$250"), O("500", "US$500"), O("1000", "US$1,000"),
                O("5000", "US$5,000"),
            ], null, "An email at 80% of the budget and when the month is forecast to go over.", "Budget"),
            new(SettingKeys.BudgetEmail, "Budget email", PropertyType.Text, "", [], EmailPattern, "Who gets the budget emails.", "Budget", "budget!=0"),
            new(CloudAlerts.GcpBillingAccount, "Billing account id", PropertyType.Text, "", [], "^$|^[0-9A-F]{6}-[0-9A-F]{6}-[0-9A-F]{6}$",
                "The billing account the project uses (gcloud billing accounts list), for example 01ABCD-23EF45-6789AB.", "Budget", "platform=gcp&budget!=0"),
        ]),
        NamePattern,
        ProjectNamePattern);

    public static ResourceKind? Find(string kind) => Instance.Kinds.FirstOrDefault(item => item.Kind == kind);

    public static ConnectionDefinition? FindConnection(string fromKind, string toKind) =>
        Instance.Connections.FirstOrDefault(item => item.From == fromKind && item.To == toKind);

    // The node's choices with the defaults filled in for anything it does not set.
    public static Dictionary<string, string> Resolve(SketchNode node)
    {
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in Find(node.Kind)?.Properties ?? [])
        {
            resolved[property.Key] = node.Props.TryGetValue(property.Key, out var value) ? value : property.Default;
        }

        return resolved;
    }

    // The settings beyond the four fixed ones, with defaults filled in.
    public static string Setting(SketchSettings settings, string key) =>
        settings.Options.TryGetValue(key, out var value) ? value : Instance.Settings.First(item => item.Key == key).Default;

    // Any project value by key, the four fixed ones included (for ShowWhen).
    public static string SettingValue(SketchSettings settings, string key) => key switch
    {
        "name" => settings.Name,
        "environment" => settings.Environment,
        "platform" => settings.Platform,
        "region" => settings.Region,
        "monitoring" => settings.Monitoring ? "true" : "false",
        _ => Setting(settings, key),
    };

    // A setting counts only when it is shown (an existing network's name means nothing with a new network).
    public static bool SettingApplies(SketchSettings settings, string key) =>
        Instance.Settings.First(item => item.Key == key).Visible(other => SettingValue(settings, other));
}
