namespace TerraformGenerator.Domain;

// One box with its choices resolved and the names the generators need for it.
public sealed class SketchResource(SketchNode node)
{
    public SketchNode Node { get; } = node;
    public string Kind => Node.Kind;
    public string Name => Node.Name;
    public Dictionary<string, string> P { get; } = Catalog.Resolve(node);

    // Code label (my-api => my_api), setting name part (MY_API), the name without hyphens (myapi) and camelCase (myApi).
    public string Label => Node.Name.Replace('-', '_');
    public string Upper => Label.ToUpperInvariant();
    public string Compact => Node.Name.Replace("-", "", StringComparison.Ordinal);
    public string Camel => string.Concat(Node.Name.Split('-').Select((part, index) => index == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));

    public bool Flag(string key) => P.TryGetValue(key, out var value) && value == "true";

    public bool IsPrivate => Flag("privateOnly");

    public List<string> List(string key) =>
        P[key].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToList();
}

// An App Service plan: web apps of one size, or function apps of one plan.
public sealed record PlanGroup(string Label, string Sku, bool ForFunctions, List<SketchResource> Apps);

// The drawing after the checks passed: boxes by kind and the arrows between them.
public sealed class SketchModel
{
    public SketchModel(Sketch sketch)
    {
        Settings = sketch.Settings;
        Resources = sketch.Nodes.Select(node => new SketchResource(node)).ToList();
        var byId = Resources.GroupBy(resource => resource.Node.Id, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        Links = sketch.Edges
            .Where(edge => byId.ContainsKey(edge.From) && byId.ContainsKey(edge.To))
            .Select(edge => (byId[edge.From], byId[edge.To]))
            .ToList();
    }

    public SketchSettings Settings { get; }
    public List<SketchResource> Resources { get; }
    public List<(SketchResource From, SketchResource To)> Links { get; }

    public bool IsProduction => Settings.Environment == "prod";

    public string Setting(string key) => Catalog.Setting(Settings, key);

    public string SubscriptionId => Setting(SettingKeys.Subscription);

    // Extra tags (owner, cost center) when they are filled in.
    public IEnumerable<(string Key, string Value)> ExtraTags
    {
        get
        {
            if (Setting(SettingKeys.Owner) is { Length: > 0 } owner)
            {
                yield return ("owner", owner);
            }

            if (Setting(SettingKeys.CostCenter) is { Length: > 0 } costCenter)
            {
                yield return ("cost-center", costCenter);
            }
        }
    }

    public bool ExistingNetwork => Setting(SettingKeys.Network) == "existing";
    public string NetworkRange => Setting(SettingKeys.NetworkRange);
    public string NetworkName => Setting(SettingKeys.NetworkName);
    public string NetworkResourceGroup => Setting(SettingKeys.NetworkResourceGroup);
    public bool ExistingDns => Setting(SettingKeys.PrivateDns) == "existing";
    public string DnsResourceGroup => Setting(SettingKeys.PrivateDnsResourceGroup);
    public bool Nsg => Setting(SettingKeys.Nsg) == "true";

    public bool ExistingLogs => Settings.Monitoring && Setting(SettingKeys.Logs) == "existing";
    public string LogWorkspaceId => Setting(SettingKeys.LogWorkspaceId);
    public int LogRetentionDays => Setting(SettingKeys.LogRetention) is "auto"
        ? IsProduction ? 90 : 30
        : int.Parse(Setting(SettingKeys.LogRetention), System.Globalization.CultureInfo.InvariantCulture);

    public bool Alerts => Setting(SettingKeys.Alerts) == "on";
    public string AlertEmail => Setting(SettingKeys.AlertEmail);

    public bool AdminGroup => Setting(SettingKeys.DatabaseAdmin) == "group";
    public string AdminGroupName => Setting(SettingKeys.DatabaseAdminGroupName);
    public string AdminGroupId => Setting(SettingKeys.DatabaseAdminGroupId);

    public bool Lock => IsAzure && Setting(SettingKeys.Lock) switch { "on" => true, "off" => false, _ => IsProduction };

    public string StateResourceGroup => Setting(SettingKeys.StateResourceGroup) is { Length: > 0 } name ? name : $"{Settings.Name}-tfstate-rg";

    // The resource group every resource goes in: created by the code, or one that already exists.
    public bool ExistingResourceGroup => Setting(SettingKeys.ResourceGroup) == "existing";

    public string ResourceGroupName => Setting(SettingKeys.ResourceGroupName) is { Length: > 0 } name
        ? name
        : $"{Settings.Name}-{Settings.Environment}-rg";

    public string Platform => Settings.Platform;

    // AWS and Google Cloud networks.
    public bool AwsOwnNetwork => Setting(SettingKeys.AwsNetwork) == "own";
    public int NatGateways => !AwsOwnNetwork ? 0 : Setting(SettingKeys.Nat) switch
    {
        "none" => 0,
        "one" => 1,
        "perZone" => 2,
        _ => IsProduction ? 2 : 1,
    };
    public bool GcpPrivateNetwork => Setting(SettingKeys.GcpNetwork) == "private";
    public string CloudNetworkRange => Setting(SettingKeys.CloudNetworkRange);
    public bool IsAzure => Platform == Platforms.Azure;
    public Platform Cloud => Platforms.Get(Platform);

    // Bicep is Azure only; every other cloud always gets Terraform.
    public bool Terraform => !IsAzure || Setting(SettingKeys.Output) is "terraform" or "both";
    public bool Bicep => IsAzure && Setting(SettingKeys.Output) is "bicep" or "both";
    public bool RemoteState => Setting(SettingKeys.State) == "azure";
    public bool GitHub => Setting(SettingKeys.Pipeline) == "github";
    public int Budget => int.Parse(Setting(SettingKeys.Budget), System.Globalization.CultureInfo.InvariantCulture);

    public IEnumerable<SketchResource> OfKind(string kind) => Resources.Where(resource => resource.Kind == kind);

    public IEnumerable<SketchResource> Targets(SketchResource from, string kind) =>
        Links.Where(link => link.From == from && link.To.Kind == kind).Select(link => link.To);

    public IEnumerable<SketchResource> Sources(SketchResource to, string kind) =>
        Links.Where(link => link.To == to && link.From.Kind == kind).Select(link => link.From);

    public IEnumerable<SketchResource> ComputeSources(SketchResource to) =>
        Links.Where(link => link.To == to && Kinds.IsCompute(link.From.Kind)).Select(link => link.From);

    public IEnumerable<SketchResource> ComputeTargets(SketchResource from) =>
        Links.Where(link => link.From == from && Kinds.IsCompute(link.To.Kind)).Select(link => link.To);

    public IEnumerable<SketchResource> Apps => OfKind(Kinds.WebApp);
    public IEnumerable<SketchResource> ContainerApps => OfKind(Kinds.ContainerApp);
    public IEnumerable<SketchResource> Functions => OfKind(Kinds.FunctionApp);
    public IEnumerable<SketchResource> StaticSites => OfKind(Kinds.StaticWebApp);
    public IEnumerable<SketchResource> Compute => Resources.Where(resource => Kinds.IsCompute(resource.Kind));
    public IEnumerable<SketchResource> Databases => OfKind(Kinds.SqlDatabase);
    public IEnumerable<SketchResource> PostgresServers => OfKind(Kinds.Postgres);
    public IEnumerable<SketchResource> CosmosAccounts => OfKind(Kinds.CosmosDb);
    public IEnumerable<SketchResource> StorageAccounts => OfKind(Kinds.Storage);
    public IEnumerable<SketchResource> Caches => OfKind(Kinds.Redis);
    public IEnumerable<SketchResource> Buses => OfKind(Kinds.ServiceBus);
    public IEnumerable<SketchResource> Vaults => OfKind(Kinds.KeyVault);
    public IEnumerable<SketchResource> Registries => OfKind(Kinds.ContainerRegistry);
    public IEnumerable<SketchResource> FrontDoors => OfKind(Kinds.FrontDoor);
    public IEnumerable<SketchResource> Zones => OfKind(Kinds.DnsZone);
    public IEnumerable<SketchResource> Foundries => OfKind(Kinds.AiFoundry);

    // Boxes for any provider resource (tf:...), written field by field.
    public IEnumerable<SketchResource> Generics => Resources.Where(resource => Kinds.IsGeneric(resource.Kind));

    public string GcpProject => Setting(SettingKeys.GcpProject);
    public IEnumerable<SketchResource> SpeechServices => OfKind(Kinds.Speech);

    // One SQL server holds every database, so it is private when any database asks for it.
    public bool SqlPrivate => Databases.Any(database => database.IsPrivate);

    public bool IsPrivateTarget(SketchResource target) => target.Kind == Kinds.SqlDatabase ? SqlPrivate : target.IsPrivate;

    public bool UsesPrivateResource(SketchResource app) =>
        Links.Any(link => link.From == app && IsPrivateTarget(link.To));

    public bool NeedsNetwork => Resources.Any(resource => resource.Kind != Kinds.SqlDatabase && resource.IsPrivate) || SqlPrivate || AppGateways.Any();

    public IEnumerable<SketchResource> AppGateways => OfKind(Kinds.AppGateway);

    public bool Monitoring => Settings.Monitoring && (Compute.Any() || StaticSites.Any());

    // Web apps of the same size share a plan; function apps of the same plan share another.
    public List<PlanGroup> Plans =>
    [
        .. Apps.GroupBy(app => app.P["sku"]).Select(group => new PlanGroup($"plan_{group.Key.ToLowerInvariant()}", group.Key, false, group.ToList())),
        .. Functions.GroupBy(app => app.P["plan"]).Select(group => new PlanGroup($"plan_functions_{group.Key.ToLowerInvariant()}", group.Key, true, group.ToList())),
    ];

    public PlanGroup PlanOf(SketchResource app) => Plans.First(plan => plan.Apps.Contains(app));

    // App Service plans that join the private network (each needs its own subnet), and whether the Container Apps environment does.
    public List<PlanGroup> IntegratedPlans => Plans.Where(plan => plan.Apps.Any(UsesPrivateResource)).ToList();

    public bool ContainerEnvironmentJoinsNetwork => ContainerApps.Any(UsesPrivateResource);

    // Private DNS zones for the private endpoints, by the label used in code.
    public List<(string Label, string Zone)> PrivateZones
    {
        get
        {
            var zones = new List<(string, string)>();
            if (SqlPrivate)
            {
                zones.Add(("sql", "privatelink.database.windows.net"));
            }

            void Add(IEnumerable<SketchResource> resources, string label, string zone)
            {
                if (resources.Any(resource => resource.IsPrivate) && zones.All(item => item.Item1 != label))
                {
                    zones.Add((label, zone));
                }
            }

            Add(PostgresServers, "postgres", "privatelink.postgres.database.azure.com");
            Add(CosmosAccounts, "cosmos", "privatelink.documents.azure.com");
            Add(StorageAccounts, "blob", "privatelink.blob.core.windows.net");
            Add(StorageAccounts.Where(storage => storage.List("queues").Count > 0), "queue", "privatelink.queue.core.windows.net");
            Add(StorageAccounts.Where(storage => storage.List("tables").Count > 0), "table", "privatelink.table.core.windows.net");
            Add(Caches, "redis", "privatelink.redis.cache.windows.net");
            Add(Buses, "servicebus", "privatelink.servicebus.windows.net");
            Add(Vaults, "vault", "privatelink.vaultcore.azure.net");
            Add(Foundries.Concat(SpeechServices), "cognitive", "privatelink.cognitiveservices.azure.com");
            Add(Foundries, "openai", "privatelink.openai.azure.com");
            Add(Foundries, "aiservices", "privatelink.services.ai.azure.com");
            foreach (var spec in AzureServices.Specs.Where(spec => spec.ZoneLabel is not null))
            {
                Add(OfKind(spec.Kind), spec.ZoneLabel!, spec.Zone!);
            }

            return zones;
        }
    }

    // Static Web Apps run only in a few regions; the nearest one is used.
    public string StaticSiteRegion => Settings.Region switch
    {
        "eastus2" or "centralus" or "westus2" or "westeurope" => Settings.Region,
        "northeurope" or "uksouth" => "westeurope",
        "westus3" => "westus2",
        "eastus" or "canadacentral" or "brazilsouth" => "eastus2",
        _ => "centralus",
    };
}
