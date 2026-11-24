using System.Globalization;

namespace TerraformGenerator.Domain;

// The same design as the Terraform, in Bicep: one main.bicep deployed to the resource group, and its parameters.
public static partial class BicepGenerator
{
    private static readonly Dictionary<string, string> Roles = new(StringComparer.Ordinal)
    {
        ["Key Vault Secrets User"] = "4633458b-17de-408a-b874-0445c86b69e6",
        ["Key Vault Secrets Officer"] = "b86a8fe4-44ce-4948-aee5-eccb2c155cd7",
        ["Storage Blob Data Contributor"] = "ba92f5b4-2d11-453d-a403-e96b0029c9fe",
        ["Azure Service Bus Data Sender"] = "69a216fc-b8fb-44d8-bc22-1f3c2cd27a39",
        ["Azure Service Bus Data Receiver"] = "4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0",
        ["AcrPull"] = "7f951dda-4ed3-4680-a7ca-43fe172d538d",
        ["Azure AI User"] = "53ca6127-db72-4b80-b1b0-d745d6d5456d",
        ["Cognitive Services OpenAI User"] = "5e0bd9bd-7b93-4f28-af87-19fc36ad61bd",
        ["Cognitive Services Speech User"] = "f2dc8367-1007-4938-bd23-fe263f013447",
        ["Cognitive Services User"] = "a97b65f3-24c7-4388-baec-2e87135dc908",
        ["Search Index Data Contributor"] = "8ebe5a00-799e-43f5-93ac-243d3dce84a7",
        ["Search Service Contributor"] = "7ca78c08-252a-4471-8644-bb5ff32d4ba0",
        ["Azure Event Hubs Data Sender"] = "2b629674-e913-4c01-ae53-ef4638d8f975",
        ["Azure Event Hubs Data Receiver"] = "a638d3c7-ab3a-418d-83e6-5f17a39d4fde",
        ["EventGrid Data Sender"] = "d5a91429-5739-47e2-a06b-3470a27159e7",
        ["App Configuration Data Reader"] = "516239f1-63e1-4d78-a4de-a74fb236a071",
        ["SignalR App Server"] = "420fcaa2-552c-430f-98ca-3264be4806c7",
        ["Web PubSub Service Owner"] = "12cf5a90-567b-43ae-8102-96cf46c7d9b4",
        ["Azure Maps Data Reader"] = "423170ca-a8f6-4b0f-8487-9e4eb8f49bfa",
        ["Contributor"] = "b24988ac-6180-42a0-ab88-20f7382dd24c",
        ["AzureML Data Scientist"] = "f6c7c914-8db3-469d-8ca1-694a8f32e121",
        ["Key Vault Crypto Service Encryption User"] = "e147488a-f6f5-4113-8e2d-b22465e65bf6",
        ["Storage Queue Data Contributor"] = "974c5e8b-45b9-4653-ba55-5f855dd0fb88",
        ["Storage Table Data Contributor"] = "0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3",
    };

    private const string Tags = "tags";
    private const string Location = "location";

    public static Dictionary<string, string> Files(SketchModel model)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["main.bicep"] = Main(model),
            ["main.bicepparam"] = Parameters(model),
        };
        if (model.NeedsNetwork && model.ExistingNetwork)
        {
            files["subnets.bicep"] = SubnetsModule;
        }

        if (model.NeedsNetwork && model.ExistingDns)
        {
            files["dns-links.bicep"] = DnsLinksModule;
        }

        if (Protection.On(model))
        {
            files[Protection.BicepModule] = Protection.AzureBicepModule;
        }

        return files;
    }

    private static string Pascal(SketchResource resource) => char.ToUpperInvariant(resource.Camel[0]) + resource.Camel[1..];

    private static string Pascal(string name)
    {
        var camel = string.Concat(name.Split('-').Select((part, index) => index == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));
        return char.ToUpperInvariant(camel[0]) + camel[1..];
    }

    // Symbols: a prefix by kind plus the box name (web app api => appApi).
    public static string Symbol(SketchResource resource) => resource.Kind switch
    {
        Kinds.WebApp => "app",
        Kinds.FunctionApp => "fn",
        Kinds.ContainerApp => "ca",
        Kinds.StaticWebApp => "swa",
        Kinds.SqlDatabase => "sqlDb",
        Kinds.Postgres => "pg",
        Kinds.CosmosDb => "cosmos",
        Kinds.Storage => "stg",
        Kinds.Redis => "redis",
        Kinds.ServiceBus => "bus",
        Kinds.KeyVault => "kv",
        Kinds.ContainerRegistry => "acr",
        Kinds.FrontDoor => "fd",
        Kinds.AppGateway => "agw",
        Kinds.AiFoundry => "ai",
        Kinds.Speech => "speech",
        _ when AzureServices.Find(resource.Kind) is { } spec => spec.Symbol,
        _ => "dns",
    } + Pascal(resource);

    private static string Prefixed(string suffix) => Bicep.Str("${namePrefix}-" + suffix);

    // Bicep allows quoted strings inside an interpolation, so this is written out rather than escaped.
    private static string CompactName(string compact) => "'${take('${compactPrefix}" + compact + "', 18)}${suffix}'";

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string HostOf(SketchResource app) => app.Kind switch
    {
        Kinds.ContainerApp => $"{Symbol(app)}.properties.configuration.ingress.fqdn",
        Kinds.StaticWebApp => $"{Symbol(app)}.properties.defaultHostname",
        _ => $"{Symbol(app)}.properties.defaultHostName",
    };

    private static string PrincipalOf(SketchResource app) =>
        app.Kind == Kinds.ContainerApp ? $"{Symbol(app)}Identity.properties.principalId" : $"{Symbol(app)}.identity.principalId";

    // Written as the name itself for an App Service app, whose own settings cannot refer to it.
    private static string IdentityNameOf(SketchResource app) =>
        app.Kind == Kinds.ContainerApp ? $"{Symbol(app)}Identity.name" : Prefixed($"{app.Name}-${{suffix}}");

    private static string Main(SketchModel model)
    {
        var file = new BicepFile();
        file.Comment($"{model.Settings.Name} ({model.Settings.Environment}) on Azure, drawn in Terraform Generator. Deploy it with deploy-bicep.ps1.");
        Parameters(model, file);
        file.Var("compactPrefix", "take(replace(namePrefix, '-', ''), 12)");
        file.Var("suffix", "take(uniqueString(resourceGroup().id), 6)");
        if (TerraformGenerator.NeedsAdmin(model))
        {
            file.Var("adminId", "empty(adminObjectId) ? deployer().objectId : adminObjectId");
        }

        Monitoring(model, file);
        Keys(model, file);
        Network(model, file);
        Apps(model, file);
        Data(model, file);
        Security(model, file);
        Edge(model, file);
        Ai(model, file);
        Services(model, file);
        Team.AzureBicep(model, file);
        Protection.AzureBicep(model, file);
        Guardrails.AzureBicep(model, file);
        Diagnostics(model, file);
        Alerts(model, file);
        Outputs(model, file);
        return file.Render();
    }

    private static void Parameters(SketchModel model, BicepFile file)
    {
        file.Param("location", "string", "The Azure region.", Bicep.Str(model.Settings.Region));
        file.Param("namePrefix", "string", "Starts the name of every resource.", Bicep.Str($"{model.Settings.Name}-{model.Settings.Environment}"));
        file.Param("tags", "object", "Tags on every resource.",
            Bicep.Render(Bicep.Object(tags =>
            {
                tags.Add("project", Bicep.Literal(model.Settings.Name))
                    .Add("environment", Bicep.Literal(model.Settings.Environment))
                    .Str("managed-by", "bicep");
                foreach (var (key, value) in model.ExtraTags)
                {
                    tags.Add(key, Bicep.Literal(value));
                }
            }), 0));
        if (TerraformGenerator.NeedsAdmin(model))
        {
            file.Param("adminObjectId", "string", "Object id of the Microsoft Entra user or group that administers the databases; empty means whoever deploys.",
                Bicep.Literal(model.AdminGroup ? model.AdminGroupId : ""));
            file.Param("adminName", "string", "Sign-in name (user) or display name (group) of that administrator.", Bicep.Literal(model.AdminGroup ? model.AdminGroupName : ""));
            file.Param("adminType", "string", "User or Group.", model.AdminGroup ? "'Group'" : "'User'");
        }

        if (model.NeedsNetwork)
        {
            file.Param("networkRange", "string", "The /16 the subnets are cut from.", Bicep.Literal(model.NetworkRange));
            if (model.ExistingNetwork)
            {
                file.Param("networkName", "string", "The existing virtual network the subnets are added to.", Bicep.Literal(model.NetworkName));
                file.Param("networkResourceGroup", "string", "Its resource group.", Bicep.Literal(model.NetworkResourceGroup));
            }

            if (model.ExistingDns)
            {
                file.Param("privateDnsResourceGroup", "string", "The resource group of the existing private DNS zones.", Bicep.Literal(model.DnsResourceGroup));
            }
        }

        if (model.ExistingLogs)
        {
            file.Param("logAnalyticsWorkspaceId", "string", "The existing Log Analytics workspace every resource sends its logs to.", Bicep.Literal(model.LogWorkspaceId));
        }

        if (model.Alerts && TerraformGenerator.AlertTargets(model).Any())
        {
            file.Param("alertEmail", "string", "Who gets the alert emails.", Bicep.Literal(model.AlertEmail));
        }

        if (model.Budget > 0)
        {
            file.Param("monthlyBudget", "int", "Monthly budget in US dollars.", Number(model.Budget));
            file.Param("budgetEmail", "string", "Who gets the budget emails.", Bicep.Literal(model.Setting(SettingKeys.BudgetEmail)));
            file.Param("budgetStartDate", "string", "First day of the budget (this month).", "'${utcNow('yyyy-MM')}-01'");
        }
    }

    private static string Parameters(SketchModel model)
    {
        var lines = new List<string>
        {
            "// Values for main.bicep. deploy-bicep.ps1 adds you as the database administrator.",
            "using 'main.bicep'",
            "",
            $"param location = {Bicep.Str(model.Settings.Region)}",
            $"param namePrefix = {Bicep.Str($"{model.Settings.Name}-{model.Settings.Environment}")}",
        };
        if (model.Budget > 0)
        {
            lines.Add($"param monthlyBudget = {Number(model.Budget)}");
            lines.Add($"param budgetEmail = {Bicep.Literal(model.Setting(SettingKeys.BudgetEmail))}");
        }

        return string.Join('\n', lines) + "\n";
    }

    private static BicepObject Located(string name) => Bicep.Object(body => body.Add("name", name).Add("location", Location).Add("tags", Tags));

    // Monitoring and budget -----------------------------------------------------------------------------------------

    private static void Monitoring(SketchModel model, BicepFile file)
    {
        if (TerraformGenerator.Logs(model) && model.ExistingLogs)
        {
            file.Resource("logs", "Microsoft.OperationalInsights/workspaces@2023-09-01 existing", Bicep.Object(body => body
                .Add("name", "last(split(logAnalyticsWorkspaceId, '/'))")
                .Add("scope", "resourceGroup(split(logAnalyticsWorkspaceId, '/')[2], split(logAnalyticsWorkspaceId, '/')[4])")),
                "The existing Log Analytics workspace every resource sends its logs to.");
        }
        else if (TerraformGenerator.Logs(model))
        {
            file.Resource("logs", "Microsoft.OperationalInsights/workspaces@2023-09-01", Located(Prefixed("logs"))
                .Obj("properties", properties => properties
                    .Obj("sku", sku => sku.Str("name", "PerGB2018"))
                    .Add("retentionInDays", Number(model.LogRetentionDays))),
                "Logs of every resource, and Application Insights for the apps.");
        }

        if (model.Monitoring)
        {
            file.Resource("appInsights", "Microsoft.Insights/components@2020-02-02", Located(Prefixed("appinsights"))
                .Str("kind", "web")
                .Obj("properties", properties => properties
                    .Str("Application_Type", "web")
                    .Add("WorkspaceResourceId", "logs.id")));
        }

        if (model.Lock)
        {
            file.Resource("lock", "Microsoft.Authorization/locks@2020-05-01", Bicep.Object(body => body
                .Str("name", "do-not-delete")
                .Obj("properties", properties => properties
                    .Str("level", "CanNotDelete")
                    .Str("notes", "Created by Terraform Generator. Remove it only to delete the environment on purpose."))),
                "Nobody can delete the resource group by mistake.");
        }

        if (model.Budget > 0)
        {
            BicepObject Notification(int threshold, string type) => Bicep.Object(notification => notification
                .Add("enabled", "true")
                .Str("operator", "GreaterThan")
                .Add("threshold", Number(threshold))
                .Str("thresholdType", type)
                .Add("contactEmails", Bicep.Array("budgetEmail")));
            file.Resource("budget", "Microsoft.Consumption/budgets@2023-11-01", Bicep.Object(body => body
                .Add("name", Prefixed("budget"))
                .Obj("properties", properties => properties
                    .Str("category", "Cost")
                    .Add("amount", "monthlyBudget")
                    .Str("timeGrain", "Monthly")
                    .Obj("timePeriod", period => period.Add("startDate", "budgetStartDate"))
                    .Obj("notifications", notifications => notifications
                        .Add("actual80", Notification(80, "Actual"))
                        .Add("forecast100", Notification(100, "Forecasted"))))),
                "An email at 80% of the monthly budget and when the month is forecast to go over it.");
        }
    }

    // Network ---------------------------------------------------------------------------------------------------------

    private static string PlanSymbol(PlanGroup plan) => "plan" + Pascal(plan.Label.Replace("plan_", "", StringComparison.Ordinal).Replace('_', '-'));

    private static List<(PlanGroup Plan, int Index)> IntegratedPlans(SketchModel model) =>
        model.IntegratedPlans.Select((plan, index) => (plan, index)).ToList();

    // The subnets Terraform Generator adds, in order: name, address, delegation ("" for none).
    private static List<(string Name, string Prefix, string Delegation)> Subnets(SketchModel model)
    {
        var subnets = new List<(string, string, string)> { ("private-endpoints", "cidrSubnet(networkRange, 24, 1)", "") };
        foreach (var (plan, index) in IntegratedPlans(model))
        {
            subnets.Add((plan.Label.Replace('_', '-'), $"cidrSubnet(networkRange, 26, {64 + index})", "Microsoft.Web/serverFarms"));
        }

        if (model.ContainerEnvironmentJoinsNetwork)
        {
            subnets.Add(("container-apps", "cidrSubnet(networkRange, 23, 4)", "Microsoft.App/environments"));
        }

        if (model.AppGateways.Any())
        {
            subnets.Add((GatewaySubnet, "cidrSubnet(networkRange, 24, 2)", ""));
        }

        return subnets;
    }

    // A subnet's id: from the new network, or from the module that adds subnets to an existing one.
    private static string SubnetId(SketchModel model, string name)
    {
        if (!model.ExistingNetwork)
        {
            return $"'${{vnet.id}}/subnets/${{namePrefix}}-{name}'";
        }

        var index = Subnets(model).FindIndex(subnet => subnet.Name == name);
        return $"networkSubnets.outputs.ids[{index}]";
    }

    private static string NetworkId(SketchModel model) =>
        model.ExistingNetwork ? "resourceId(networkResourceGroup, 'Microsoft.Network/virtualNetworks', networkName)" : "vnet.id";

    private static void Network(SketchModel model, BicepFile file)
    {
        if (!model.NeedsNetwork)
        {
            return;
        }

        var subnets = Subnets(model);
        if (model.Nsg)
        {
            foreach (var (name, _, _) in subnets.Where(subnet => subnet.Name != GatewaySubnet))
            {
                file.Resource("nsg" + Pascal(name), "Microsoft.Network/networkSecurityGroups@2024-05-01", Located(Prefixed($"{name}-nsg"))
                    .Obj("properties", _ => { }),
                    name == subnets[0].Name ? "A network security group on every subnet: the Azure default rules, ready for your own." : null);
            }
        }

        if (model.ExistingNetwork)
        {
            file.Module("networkSubnets", "subnets.bicep", Bicep.Object(body => body
                .Add("scope", "resourceGroup(networkResourceGroup)")
                .Obj("params", parameters => parameters
                    .Add("networkName", "networkName")
                    .Add("subnets", Bicep.Array(subnets.Select(subnet => (BicepValue)Bicep.Object(item =>
                    {
                        item.Add("name", Prefixed(subnet.Name)).Add("addressPrefix", subnet.Prefix).Str("delegation", subnet.Delegation);
                        item.Add("nsgId", model.Nsg && subnet.Name != GatewaySubnet ? $"nsg{Pascal(subnet.Name)}.id" : "''");
                    })))))),
                "The existing network: the subnets are added to it.");
        }
        else
        {
            file.Resource("vnet", "Microsoft.Network/virtualNetworks@2024-05-01", Located(Prefixed("vnet"))
                .Obj("properties", properties => properties
                    .Obj("addressSpace", space => space.Add("addressPrefixes", Bicep.Array("networkRange")))
                    .Add("subnets", Bicep.Array(subnets.Select(subnet => (BicepValue)Bicep.Object(item => item
                        .Add("name", Prefixed(subnet.Name))
                        .Obj("properties", inner =>
                        {
                            inner.Add("addressPrefix", subnet.Prefix);
                            if (subnet.Delegation.Length > 0)
                            {
                                inner.Add("delegations", Bicep.Array(Bicep.Object(delegation => delegation
                                    .Str("name", "delegation")
                                    .Obj("properties", properties => properties.Str("serviceName", subnet.Delegation)))));
                            }

                            if (model.Nsg && subnet.Name != GatewaySubnet)
                            {
                                inner.Obj("networkSecurityGroup", nsg => nsg.Add("id", $"nsg{Pascal(subnet.Name)}.id"));
                            }
                        })))))),
                "Private network: private endpoints in one subnet, one subnet per App Service plan that joins it, one for Container Apps.");
        }

        var ids = new BicepObject();
        if (model.ExistingDns)
        {
            file.Module("dnsLinks", "dns-links.bicep", Bicep.Object(body => body
                .Add("scope", "resourceGroup(privateDnsResourceGroup)")
                .Obj("params", parameters => parameters
                    .Add("zoneNames", Bicep.Array(model.PrivateZones.Select(zone => (BicepValue)Bicep.Raw(ZoneName(zone.Label, zone.Zone)))))
                    .Add("networkId", NetworkId(model))
                    .Add("namePrefix", "namePrefix"))),
                "The central private DNS zones: the network is linked to them.");
            foreach (var (label, zone) in model.PrivateZones)
            {
                ids.Add(label, $"resourceId(privateDnsResourceGroup, 'Microsoft.Network/privateDnsZones', {ZoneName(label, zone)})");
            }
        }
        else
        {
            foreach (var (label, zone) in model.PrivateZones)
            {
                var symbol = "zone" + Pascal(label);
                file.Resource(symbol, "Microsoft.Network/privateDnsZones@2024-06-01", Bicep.Object(body => body
                    .Add("name", ZoneName(label, zone))
                    .Str("location", "global")
                    .Add("tags", Tags)));
                file.Resource(symbol + "Link", "Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01", Bicep.Object(body => body
                    .Add("parent", symbol)
                    .Add("name", Prefixed($"{label}-link"))
                    .Str("location", "global")
                    .Add("tags", Tags)
                    .Obj("properties", properties => properties
                        .Add("registrationEnabled", "false")
                        .Obj("virtualNetwork", network => network.Add("id", NetworkId(model))))));
                ids.Add(label, $"{symbol}.id");
            }
        }

        if (model.PrivateZones.Count > 0)
        {
            file.Var("privateDnsZoneIds", Bicep.Render(ids, 0));
        }
    }

    // Zones on the Azure hosts the linter checks come from environment(), so they work in every Azure cloud.
    private static string ZoneName(string label, string zone) => label switch
    {
        "sql" => "'privatelink${environment().suffixes.sqlServerHostname}'",
        "blob" => "'privatelink.blob.${environment().suffixes.storage}'",
        "queue" => "'privatelink.queue.${environment().suffixes.storage}'",
        "table" => "'privatelink.table.${environment().suffixes.storage}'",
        _ => Bicep.Str(zone),
    };

    // Adds Terraform Generator's subnets to an existing network, one at a time (Azure changes one subnet of a network at a time).
    public const string SubnetsModule = """
        // Adds the Terraform Generator subnets to an existing virtual network. main.bicep uses it when the design chooses an existing network.
        @description('The existing virtual network, in the resource group this module is deployed to.')
        param networkName string

        @description('The subnets: name, addressPrefix, delegation (empty for none) and nsgId (empty for none).')
        param subnets array

        resource network 'Microsoft.Network/virtualNetworks@2024-05-01' existing = {
          name: networkName
        }

        @batchSize(1)
        resource subnet 'Microsoft.Network/virtualNetworks/subnets@2024-05-01' = [for item in subnets: {
          parent: network
          name: item.name
          properties: {
            addressPrefix: item.addressPrefix
            delegations: empty(item.delegation) ? [] : [
              {
                name: 'delegation'
                properties: {
                  serviceName: item.delegation
                }
              }
            ]
            networkSecurityGroup: empty(item.nsgId) ? null : {
              id: item.nsgId
            }
          }
        }]

        output ids array = [for (item, index) in subnets: subnet[index].id]

        """;

    // Links the network to central private DNS zones that already exist.
    public const string DnsLinksModule = """
        // Links the network to private DNS zones that already exist. main.bicep uses it when the design chooses central zones.
        @description('The names of the existing zones, in the resource group this module is deployed to.')
        param zoneNames array

        @description('The virtual network to link.')
        param networkId string

        @description('Starts the name of every link.')
        param namePrefix string

        resource zone 'Microsoft.Network/privateDnsZones@2024-06-01' existing = [for name in zoneNames: {
          name: name
        }]

        resource link 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = [for (name, index) in zoneNames: {
          parent: zone[index]
          name: '${namePrefix}-link'
          location: 'global'
          properties: {
            registrationEnabled: false
            virtualNetwork: {
              id: networkId
            }
          }
        }]

        """;

    private static void PrivateEndpoint(BicepFile file, SketchModel model, string symbol, string name, string resourceId, string group, params string[] zoneLabels)
    {
        file.Resource(symbol + "Endpoint", "Microsoft.Network/privateEndpoints@2024-05-01", Located(Prefixed($"{name}-pe"))
            .Obj("properties", properties => properties
                .Obj("subnet", subnet => subnet.Add("id", SubnetId(model, "private-endpoints")))
                .Add("privateLinkServiceConnections", Bicep.Array(Bicep.Object(connection => connection
                    .Str("name", $"{name}-connection")
                    .Obj("properties", inner => inner
                        .Add("privateLinkServiceId", resourceId)
                        .Add("groupIds", Bicep.Array(Bicep.Str(group)))))))));
        file.Resource(symbol + "EndpointDns", "Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-05-01", Bicep.Object(body => body
            .Add("parent", symbol + "Endpoint")
            .Str("name", "default")
            .Obj("properties", properties => properties
                .Add("privateDnsZoneConfigs", Bicep.Array(zoneLabels.Select(label => (BicepValue)Bicep.Object(config => config
                    .Str("name", label)
                    .Obj("properties", inner => inner.Add("privateDnsZoneId", $"privateDnsZoneIds.{label}")))))))));
    }

    // Apps ------------------------------------------------------------------------------------------------------------

    private static void Apps(SketchModel model, BicepFile file)
    {
        var integrated = IntegratedPlans(model).Select(item => item.Plan.Label).ToHashSet(StringComparer.Ordinal);
        foreach (var plan in model.Plans)
        {
            file.Resource(PlanSymbol(plan), "Microsoft.Web/serverfarms@2024-04-01", Located(Prefixed(plan.Label.Replace('_', '-')))
                .Str("kind", plan.ForFunctions ? "functionapp" : "linux")
                .Obj("sku", sku => sku.Str("name", plan.Sku))
                .Obj("properties", properties => properties.Add("reserved", "true")),
                $"App Service plan {plan.Sku}: {string.Join(", ", plan.Apps.Select(app => app.Name))}.");
            foreach (var app in plan.Apps)
            {
                Site(model, file, app, plan, integrated.Contains(plan.Label) && model.UsesPrivateResource(app));
            }
        }

        if (model.ContainerApps.Any())
        {
            ContainerApps(model, file);
        }

        foreach (var site in model.StaticSites)
        {
            var symbol = Symbol(site);
            file.Resource(symbol, "Microsoft.Web/staticSites@2024-04-01", Bicep.Object(body => body
                .Add("name", Prefixed(site.Name))
                .Str("location", model.StaticSiteRegion)
                .Add("tags", Tags)
                .Obj("sku", sku => sku.Str("name", site.P["sku"]).Str("tier", site.P["sku"]))
                .Obj("properties", _ => { })),
                $"Static Web App {site.Name}.");
            var settings = Settings(model, site);
            if (settings.Count > 0)
            {
                file.Resource(symbol + "Settings", "Microsoft.Web/staticSites/config@2024-04-01", Bicep.Object(body => body
                    .Add("parent", symbol)
                    .Str("name", "appsettings")
                    .Add("properties", SettingsObject(settings))));
            }
        }
    }

    private static BicepObject SettingsObject(List<(string Name, string Value)> settings)
    {
        var value = new BicepObject();
        foreach (var (name, expression) in settings)
        {
            value.Add(name, expression);
        }

        return value;
    }

    private static BicepArray NameValues(IEnumerable<(string Name, string Value)> settings) =>
        Bicep.Array(settings.Select(setting => (BicepValue)Bicep.Object(item => item.Str("name", setting.Name).Add("value", setting.Value))));

    private static void Site(SketchModel model, BicepFile file, SketchResource app, PlanGroup plan, bool joinsNetwork)
    {
        var symbol = Symbol(app);
        var function = app.Kind == Kinds.FunctionApp;
        var registry = model.Targets(app, Kinds.ContainerRegistry).FirstOrDefault();
        var settings = Settings(model, app);
        if (function)
        {
            file.Resource(symbol + "Storage", "Microsoft.Storage/storageAccounts@2023-05-01", Located(CompactName(app.Compact + "fn"))
                .Str("kind", "StorageV2")
                .Obj("sku", sku => sku.Str("name", "Standard_LRS"))
                .Obj("properties", properties => properties
                    .Str("minimumTlsVersion", "TLS1_2")
                    .Add("allowBlobPublicAccess", "false")),
                $"Function App {app.Name} and the storage Azure Functions needs for itself.");
            var connection = $"'DefaultEndpointsProtocol=https;AccountName=${{{symbol}Storage.name}};AccountKey=${{{symbol}Storage.listKeys().keys[0].value}};EndpointSuffix=${{environment().suffixes.storage}}'";
            var worker = app.P["runtime"] switch { "dotnet8" => "dotnet-isolated", "node20" => "node", _ => "python" };
            settings.InsertRange(0,
            [
                ("AzureWebJobsStorage", connection),
                ("FUNCTIONS_EXTENSION_VERSION", "'~4'"),
                ("FUNCTIONS_WORKER_RUNTIME", Bicep.Str(worker)),
            ]);
            if (plan.Sku == "EP1")
            {
                settings.Add(("WEBSITE_CONTENTAZUREFILECONNECTIONSTRING", connection));
                settings.Add(("WEBSITE_CONTENTSHARE", $"toLower('{app.Name}')"));
            }
        }

        var stack = app.P["runtime"] switch
        {
            "dotnet8" => function ? "DOTNET-ISOLATED|8.0" : "DOTNETCORE|8.0",
            "dotnet9" => "DOTNETCORE|9.0",
            "node20" => function ? "NODE|20" : "NODE|20-lts",
            "node22" => "NODE|22-lts",
            "python312" => "PYTHON|3.12",
            "python311" => "PYTHON|3.11",
            _ => "DOCKER|",
        };
        var linuxFx = stack == "DOCKER|"
            ? registry is null
                ? Bicep.Literal("DOCKER|" + app.P["dockerImage"])
                : "'DOCKER|${" + Symbol(registry) + ".properties.loginServer}/" + app.P["dockerImage"].Replace("${", "\\${", StringComparison.Ordinal) + "'"
            : Bicep.Str(stack);
        var frontDoors = model.Sources(app, Kinds.FrontDoor).ToList();

        var siteConfig = Bicep.Object(config =>
        {
            config.Add("linuxFxVersion", linuxFx);
            if (!function)
            {
                config.Add("alwaysOn", Bicep.Bool(app.Flag("alwaysOn")));
            }

            config.Str("ftpsState", "Disabled").Str("minTlsVersion", "1.2");
            if (registry is not null)
            {
                config.Add("acrUseManagedIdentityCreds", "true");
            }

            if (joinsNetwork)
            {
                config.Add("vnetRouteAllEnabled", "true");
            }

            if (frontDoors.Count > 0 || model.Sources(app, Kinds.AppGateway).Any())
            {
                config.Str("ipSecurityRestrictionsDefaultAction", "Deny");
                config.Add("ipSecurityRestrictions", Bicep.Array([.. frontDoors.Select((frontDoor, index) => (BicepValue)Bicep.Object(rule => rule
                    .Str("name", $"frontdoor-{frontDoor.Name}")
                    .Add("priority", Number(100 + index))
                    .Str("action", "Allow")
                    .Str("tag", "ServiceTag")
                    .Str("ipAddress", "AzureFrontDoor.Backend")
                    .Obj("headers", headers => headers.Add("x-azure-fdid", Bicep.Array($"{Symbol(frontDoor)}.properties.frontDoorId"))))),
                    .. GatewayRestrictions(model, app)]));
            }

            config.Add("appSettings", NameValues(settings));
        });

        file.Resource(symbol, "Microsoft.Web/sites@2024-04-01", Located(Prefixed($"{app.Name}-${{suffix}}"))
            .Str("kind", function ? "functionapp,linux" : "app,linux")
            .Obj("identity", identity => AppIdentity(identity, BotIdentities(model, app)))
            .Obj("properties", properties =>
            {
                properties.Add("serverFarmId", $"{PlanSymbol(plan)}.id").Add("httpsOnly", "true");
                if (joinsNetwork)
                {
                    properties.Add("virtualNetworkSubnetId", SubnetId(model, plan.Label.Replace('_', '-')));
                }

                properties.Add("siteConfig", siteConfig);
            }),
            function ? null : $"Web App {app.Name}.");
    }

    private static void ContainerApps(SketchModel model, BicepFile file)
    {
        var joins = model.ContainerEnvironmentJoinsNetwork;
        file.Resource("containerEnvironment", "Microsoft.App/managedEnvironments@2024-03-01", Located(Prefixed("containers"))
            .Obj("properties", properties =>
            {
                if (TerraformGenerator.Logs(model))
                {
                    properties.Obj("appLogsConfiguration", logs => logs
                        .Str("destination", "log-analytics")
                        .Obj("logAnalyticsConfiguration", analytics => analytics
                            .Add("customerId", "logs.properties.customerId")
                            .Add("sharedKey", "logs.listKeys().primarySharedKey")));
                }

                if (joins)
                {
                    properties.Obj("vnetConfiguration", network => network.Add("infrastructureSubnetId", SubnetId(model, "container-apps")));
                    properties.Add("workloadProfiles", Bicep.Array(Bicep.Object(profile => profile.Str("name", "Consumption").Str("workloadProfileType", "Consumption"))));
                }
            }),
            "Container Apps environment: where every container app runs.");

        foreach (var app in model.ContainerApps)
        {
            var symbol = Symbol(app);
            var registry = model.Targets(app, Kinds.ContainerRegistry).FirstOrDefault();
            file.Resource(symbol + "Identity", "Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31", Located(Prefixed($"{app.Name}-id")),
                $"Container App {app.Name} with its own identity (so its roles exist before it starts).");
            var size = app.P["size"];
            var memory = size switch { "0.25" => "0.5Gi", "0.5" => "1Gi", "1" => "2Gi", _ => "4Gi" };
            var image = registry is null
                ? Bicep.Literal(app.P["image"])
                : "'${" + Symbol(registry) + ".properties.loginServer}/" + app.P["image"].Replace("${", "\\${", StringComparison.Ordinal) + "'";
            var body = Bicep.Object(resource =>
            {
                resource.Str("name", app.Name).Add("location", Location).Add("tags", Tags)
                    .Obj("identity", identity => identity
                        .Str("type", "UserAssigned")
                        .Obj("userAssignedIdentities", ids =>
                        {
                            ids.Add($"'${{{symbol}Identity.id}}'", "{}");
                            foreach (var bot in BotIdentities(model, app))
                            {
                                ids.Add($"'${{{bot}.id}}'", "{}");
                            }
                        }))
                    .Obj("properties", properties =>
                    {
                        properties.Add("managedEnvironmentId", "containerEnvironment.id");
                        if (joins)
                        {
                            properties.Str("workloadProfileName", "Consumption");
                        }

                        properties.Obj("configuration", configuration =>
                        {
                            configuration.Obj("ingress", ingress => ingress
                                .Add("external", Bicep.Bool(app.Flag("external")))
                                .Add("targetPort", app.P["port"])
                                .Str("transport", "auto"));
                            if (registry is not null)
                            {
                                configuration.Add("registries", Bicep.Array(Bicep.Object(entry => entry
                                    .Add("server", $"{Symbol(registry)}.properties.loginServer")
                                    .Add("identity", $"{symbol}Identity.id"))));
                            }
                        });
                        properties.Obj("template", template => template
                            .Add("containers", Bicep.Array(Bicep.Object(container => container
                                .Str("name", "main")
                                .Add("image", image)
                                .Obj("resources", resources => resources.Add("cpu", $"json('{size}')").Str("memory", memory))
                                .Add("env", NameValues(Settings(model, app))))))
                            .Obj("scale", scale => scale.Add("minReplicas", app.P["minReplicas"]).Add("maxReplicas", app.P["maxReplicas"])));
                    });
                if (registry is not null)
                {
                    resource.Add("dependsOn", Bicep.Array($"{symbol}{Symbol(registry)}Role"));
                }
            });
            file.Resource(symbol, "Microsoft.App/containerApps@2024-03-01", body);
        }
    }

    // Settings: the same names as in Terraform.
    private static List<(string Name, string Value)> Settings(SketchModel model, SketchResource app)
    {
        var settings = new List<(string, string)>();
        if (model.Monitoring && app.Kind != Kinds.StaticWebApp)
        {
            settings.Add(("APPLICATIONINSIGHTS_CONNECTION_STRING", "appInsights.properties.ConnectionString"));
        }

        if (app.Kind == Kinds.ContainerApp)
        {
            settings.Add(("AZURE_CLIENT_ID", $"{Symbol(app)}Identity.properties.clientId"));
        }

        var dotnet = app.P.TryGetValue("runtime", out var runtime) && runtime.StartsWith("dotnet", StringComparison.Ordinal);
        var userId = app.Kind == Kinds.ContainerApp ? $"User Id=${{{Symbol(app)}Identity.properties.clientId}};" : "";
        foreach (var database in model.Targets(app, Kinds.SqlDatabase))
        {
            settings.Add((dotnet ? $"ConnectionStrings__{database.Name}" : $"SQL_{database.Upper}_CONNECTION_STRING",
                $"'Server=tcp:${{sqlServer.properties.fullyQualifiedDomainName}},1433;Database=${{{Symbol(database)}.name}};Authentication=Active Directory Managed Identity;{userId}Encrypt=True;'"));
        }

        foreach (var server in model.Targets(app, Kinds.Postgres))
        {
            settings.Add(($"POSTGRES_{server.Upper}_HOST", $"{Symbol(server)}.properties.fullyQualifiedDomainName"));
            settings.Add(($"POSTGRES_{server.Upper}_DATABASE", $"{Symbol(server)}Database.name"));
            settings.Add(($"POSTGRES_{server.Upper}_USER", IdentityNameOf(app)));
        }

        foreach (var account in model.Targets(app, Kinds.CosmosDb))
        {
            settings.Add(($"COSMOS_{account.Upper}_ENDPOINT", $"{Symbol(account)}.properties.documentEndpoint"));
            settings.Add(($"COSMOS_{account.Upper}_DATABASE", $"{Symbol(account)}Database.name"));
        }

        foreach (var storage in model.Targets(app, Kinds.Storage))
        {
            settings.Add(($"STORAGE_{storage.Upper}_BLOB_ENDPOINT", $"{Symbol(storage)}.properties.primaryEndpoints.blob"));
            foreach (var part in new[] { "queue", "table" }.Where(part => storage.List(part + "s").Count > 0))
            {
                settings.Add(($"STORAGE_{storage.Upper}_{part.ToUpperInvariant()}_ENDPOINT", $"{Symbol(storage)}.properties.primaryEndpoints.{part}"));
            }
        }

        foreach (var cache in model.Targets(app, Kinds.Redis))
        {
            settings.Add(($"REDIS_{cache.Upper}_HOST", $"{Symbol(cache)}.properties.hostName"));
            settings.Add(($"REDIS_{cache.Upper}_PORT", $"string({Symbol(cache)}.properties.sslPort)"));
        }

        foreach (var bus in model.Targets(app, Kinds.ServiceBus))
        {
            settings.Add(($"SERVICEBUS_{bus.Upper}_NAMESPACE", $"'${{{Symbol(bus)}.name}}.servicebus.windows.net'"));
        }

        foreach (var vault in model.Targets(app, Kinds.KeyVault))
        {
            settings.Add(($"KEYVAULT_{vault.Upper}_URI", $"{Symbol(vault)}.properties.vaultUri"));
        }

        foreach (var foundry in model.Targets(app, Kinds.AiFoundry))
        {
            settings.Add(($"FOUNDRY_{foundry.Upper}_ENDPOINT", $"{Symbol(foundry)}.properties.endpoint"));
            settings.Add(($"FOUNDRY_{foundry.Upper}_PROJECT", $"{Symbol(foundry)}Project.name"));
            if (foundry.P["model"] != "none")
            {
                settings.Add(($"FOUNDRY_{foundry.Upper}_CHAT_MODEL", $"{Symbol(foundry)}Chat.name"));
            }

            if (foundry.P["embeddings"] != "none")
            {
                settings.Add(($"FOUNDRY_{foundry.Upper}_EMBEDDINGS_MODEL", $"{Symbol(foundry)}Embeddings.name"));
            }
        }

        foreach (var speech in model.Targets(app, Kinds.Speech))
        {
            settings.Add(($"SPEECH_{speech.Upper}_ENDPOINT", $"{Symbol(speech)}.properties.endpoint"));
            settings.Add(($"SPEECH_{speech.Upper}_REGION", $"{Symbol(speech)}.location"));
            settings.Add(($"SPEECH_{speech.Upper}_RESOURCE_ID", $"{Symbol(speech)}.id"));
        }

        settings.AddRange(ServiceSettings(model, app));
        if (AppPipeline.BuildsOnArrival(model, app))
        {
            settings.Add(("SCM_DO_BUILD_DURING_DEPLOYMENT", "'true'"));
        }

        foreach (var other in model.ComputeTargets(app))
        {
            settings.Add(($"{other.Upper}_URL", $"'https://${{{HostOf(other)}}}'"));
        }

        return settings;
    }

    // Data ------------------------------------------------------------------------------------------------------------

    private static string PublicAccess(bool isPrivate) => isPrivate ? "'Disabled'" : "'Enabled'";

    private static void Data(SketchModel model, BicepFile file)
    {
        if (model.Databases.Any())
        {
            Sql(model, file);
        }

        foreach (var server in model.PostgresServers)
        {
            Postgres(model, file, server);
        }

        foreach (var account in model.CosmosAccounts)
        {
            Cosmos(model, file, account);
        }

        foreach (var storage in model.StorageAccounts)
        {
            var symbol = Symbol(storage);
            file.Resource(symbol, "Microsoft.Storage/storageAccounts@2023-05-01", Located(CompactName(storage.Compact))
                .Str("kind", "StorageV2")
                .Obj("sku", sku => sku.Str("name", "Standard_" + Geo.AzureReplication(model, storage.P["replication"])))
                .Obj("properties", properties => properties
                    .Str("minimumTlsVersion", "TLS1_2")
                    .Add("supportsHttpsTrafficOnly", "true")
                    .Add("allowBlobPublicAccess", "false")
                    .Add("publicNetworkAccess", PublicAccess(storage.IsPrivate))
                    .With(properties => StorageEncryption(model, properties)))
                .With(account => StorageIdentity(model, account)),
                $"Storage {storage.Name}.");
            var days = storage.P["softDeleteDays"];
            file.Resource(symbol + "Blobs", "Microsoft.Storage/storageAccounts/blobServices@2023-05-01", Bicep.Object(body => body
                .Add("parent", symbol)
                .Str("name", "default")
                .Obj("properties", properties =>
                {
                    properties.Add("isVersioningEnabled", Bicep.Bool(storage.Flag("versioning")));
                    if (days != "0")
                    {
                        properties.Obj("deleteRetentionPolicy", policy => policy.Add("enabled", "true").Add("days", days));
                        properties.Obj("containerDeleteRetentionPolicy", policy => policy.Add("enabled", "true").Add("days", days));
                    }
                })));
            foreach (var container in storage.List("containers"))
            {
                file.Resource(symbol + Pascal(container), "Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01", Bicep.Object(body => body
                    .Add("parent", symbol + "Blobs")
                    .Add("name", Bicep.Literal(container))
                    .Obj("properties", properties => properties.Str("publicAccess", "None"))));
            }

            foreach (var (part, service, child) in new[] { ("queues", "queueServices", "queues"), ("tables", "tableServices", "tables") }.Where(item => storage.List(item.Item1).Count > 0))
            {
                var parent = symbol + Pascal(part);
                file.Resource(parent, $"Microsoft.Storage/storageAccounts/{service}@2023-05-01", Bicep.Object(body => body.Add("parent", symbol).Str("name", "default")));
                foreach (var name in storage.List(part))
                {
                    file.Resource(parent + Pascal(name.ToLowerInvariant()), $"Microsoft.Storage/storageAccounts/{service}/{child}@2023-05-01", Bicep.Object(body => body
                        .Add("parent", parent)
                        .Add("name", Bicep.Literal(name))));
                }
            }

            if (storage.IsPrivate)
            {
                PrivateEndpoint(file, model, symbol, $"{storage.Name}-blob", $"{symbol}.id", "blob", "blob");
                foreach (var part in new[] { "queue", "table" }.Where(part => storage.List(part + "s").Count > 0))
                {
                    PrivateEndpoint(file, model, symbol + Pascal(part), $"{storage.Name}-{part}", $"{symbol}.id", part, part);
                }
            }
        }

        foreach (var cache in model.Caches)
        {
            var symbol = Symbol(cache);
            var (family, capacity, sku) = cache.P["sku"] switch
            {
                "Basic_C0" => ("C", 0, "Basic"),
                "Standard_C1" => ("C", 1, "Standard"),
                _ => ("P", 1, "Premium"),
            };
            file.Resource(symbol, "Microsoft.Cache/redis@2024-11-01", Located(Prefixed($"{cache.Name}-${{suffix}}"))
                .Obj("properties", properties => properties
                    .Obj("sku", skuValue => skuValue.Str("name", sku).Str("family", family).Add("capacity", Number(capacity)))
                    .Add("enableNonSslPort", "false")
                    .Str("minimumTlsVersion", "1.2")
                    .Add("publicNetworkAccess", PublicAccess(cache.IsPrivate))
                    .Add("disableAccessKeyAuthentication", "true")
                    .Obj("redisConfiguration", configuration => configuration.Str("aad-enabled", "true"))),
                $"Redis {cache.Name}: Microsoft Entra sign-in only, no access keys.");
            if (cache.IsPrivate)
            {
                PrivateEndpoint(file, model, symbol, cache.Name, $"{symbol}.id", "redisCache", "redis");
            }
        }

        foreach (var bus in model.Buses)
        {
            var symbol = Symbol(bus);
            var premium = bus.P["sku"] == "Premium";
            file.Resource(symbol, "Microsoft.ServiceBus/namespaces@2024-01-01", Located(Prefixed($"{bus.Name}-${{suffix}}"))
                .Obj("sku", sku =>
                {
                    sku.Str("name", bus.P["sku"]).Str("tier", bus.P["sku"]);
                    if (premium)
                    {
                        sku.Add("capacity", "1");
                    }
                })
                .Obj("properties", properties =>
                {
                    properties.Add("disableLocalAuth", "true").Str("minimumTlsVersion", "1.2").Add("publicNetworkAccess", PublicAccess(bus.IsPrivate));
                    if (premium)
                    {
                        properties.Add("premiumMessagingPartitions", "1");
                    }
                }),
                $"Service Bus {bus.Name}: Microsoft Entra sign-in only, no connection strings.");
            foreach (var queue in bus.List("queues"))
            {
                file.Resource(symbol + Pascal(queue), "Microsoft.ServiceBus/namespaces/queues@2024-01-01", Bicep.Object(body => body
                    .Add("parent", symbol)
                    .Add("name", Bicep.Literal(queue))));
            }

            if (bus.IsPrivate)
            {
                PrivateEndpoint(file, model, symbol, bus.Name, $"{symbol}.id", "namespace", "servicebus");
            }
        }

        foreach (var registry in model.Registries)
        {
            file.Resource(Symbol(registry), "Microsoft.ContainerRegistry/registries@2023-07-01", Located(CompactName(registry.Compact))
                .Obj("sku", sku => sku.Str("name", registry.P["sku"]))
                .Obj("properties", properties => properties.Add("adminUserEnabled", "false")),
                $"Container registry {registry.Name}: apps pull with their identity, no admin user.");
        }
    }

    private static void Sql(SketchModel model, BicepFile file)
    {
        file.Resource("sqlServer", "Microsoft.Sql/servers@2023-08-01", Located(Prefixed("sql-${suffix}"))
            .Obj("properties", properties => properties
                .Str("version", "12.0")
                .Str("minimalTlsVersion", "1.2")
                .Add("publicNetworkAccess", PublicAccess(model.SqlPrivate))
                .Obj("administrators", admin => admin
                    .Str("administratorType", "ActiveDirectory")
                    .Add("login", "empty(adminName) ? 'sql-admins' : adminName")
                    .Add("sid", "adminId")
                    .Add("tenantId", "tenant().tenantId")
                    .Add("principalType", "adminType")
                    .Add("azureADOnlyAuthentication", "true"))
                .With(properties => SqlEncryption(model, properties)))
            .With(server => SqlIdentity(model, server)),
            "Azure SQL: one server for every database, Microsoft Entra sign-in only (no SQL passwords).");

        foreach (var database in model.Databases)
        {
            var symbol = Symbol(database);
            var sku = database.P["sku"] switch
            {
                "Basic" => Bicep.Object(value => value.Str("name", "Basic").Str("tier", "Basic")),
                "S0" or "S1" => Bicep.Object(value => value.Str("name", database.P["sku"]).Str("tier", "Standard")),
                "GP_S_Gen5_1" => Bicep.Object(value => value.Str("name", "GP_S_Gen5").Str("tier", "GeneralPurpose").Str("family", "Gen5").Add("capacity", "1")),
                _ => Bicep.Object(value => value.Str("name", "GP_Gen5").Str("tier", "GeneralPurpose").Str("family", "Gen5").Add("capacity", "2")),
            };
            file.Resource(symbol, "Microsoft.Sql/servers/databases@2023-08-01", Bicep.Object(body => body
                .Add("parent", "sqlServer")
                .Str("name", database.Name)
                .Add("location", Location)
                .Add("tags", Tags)
                .Add("sku", sku)
                .Obj("properties", properties =>
                {
                    properties.Str("requestedBackupStorageRedundancy", model.IsProduction || Geo.On(model) ? "Geo" : "Local");
                    if (database.P["sku"] == "GP_S_Gen5_1")
                    {
                        properties.Add("minCapacity", "json('0.5')").Add("autoPauseDelay", "60");
                    }
                })));
            file.Resource(symbol + "Backup", "Microsoft.Sql/servers/databases/backupShortTermRetentionPolicies@2023-08-01", Bicep.Object(body => body
                .Add("parent", symbol)
                .Str("name", "default")
                .Obj("properties", properties => properties.Add("retentionDays", database.P["backupDays"]))));
        }

        if (model.SqlPrivate)
        {
            PrivateEndpoint(file, model, "sqlServer", "sql", "sqlServer.id", "sqlServer", "sql");
        }
        else
        {
            file.Resource("sqlAzureServices", "Microsoft.Sql/servers/firewallRules@2023-08-01", Bicep.Object(body => body
                .Add("parent", "sqlServer")
                .Str("name", "AllowAzureServices")
                .Obj("properties", properties => properties.Str("startIpAddress", "0.0.0.0").Str("endIpAddress", "0.0.0.0"))),
                "Azure services (the apps) reach the server; nothing else does.");
        }
    }

    private static void Postgres(SketchModel model, BicepFile file, SketchResource server)
    {
        var symbol = Symbol(server);
        file.Resource(symbol, "Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01", Located(Prefixed($"{server.Name}-${{suffix}}"))
            .Obj("sku", sku => sku.Str("name", server.P["sku"][(server.P["sku"].IndexOf('_', StringComparison.Ordinal) + 1)..]).Str("tier", server.P["sku"].StartsWith("B_", StringComparison.Ordinal) ? "Burstable" : "GeneralPurpose"))
            .Obj("properties", properties => properties
                .Str("version", server.P["version"])
                .Obj("storage", storage => storage.Add("storageSizeGB", server.P["storageGb"]))
                .Obj("backup", backup => backup.Add("backupRetentionDays", server.P["backupDays"]).Str("geoRedundantBackup", Geo.On(model) ? "Enabled" : "Disabled"))
                .Obj("network", network => network.Add("publicNetworkAccess", PublicAccess(server.IsPrivate)))
                .Obj("authConfig", auth => auth
                    .Str("activeDirectoryAuth", "Enabled")
                    .Str("passwordAuth", "Disabled")
                    .Add("tenantId", "tenant().tenantId"))),
            $"PostgreSQL {server.Name}: Microsoft Entra sign-in only (no passwords).");
        file.Resource(symbol + "Admin", "Microsoft.DBforPostgreSQL/flexibleServers/administrators@2024-08-01", Bicep.Object(body => body
            .Add("parent", symbol)
            .Add("name", "adminId")
            .Obj("properties", properties => properties
                .Add("principalName", "adminName")
                .Add("principalType", "adminType")
                .Add("tenantId", "tenant().tenantId"))));
        file.Resource(symbol + "Database", "Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01", Bicep.Object(body => body
            .Add("parent", symbol)
            .Str("name", server.Label)
            .Obj("properties", properties => properties.Str("charset", "UTF8").Str("collation", "en_US.utf8"))));
        if (server.Flag("vector"))
        {
            file.Resource(symbol + "Extensions", "Microsoft.DBforPostgreSQL/flexibleServers/configurations@2024-08-01", Bicep.Object(body => body
                .Add("parent", symbol)
                .Str("name", "azure.extensions")
                .Obj("properties", properties => properties.Str("value", "VECTOR").Str("source", "user-override"))),
                "Allows the pgvector extension: run CREATE EXTENSION vector; in the database.");
        }

        if (server.IsPrivate)
        {
            PrivateEndpoint(file, model, symbol, server.Name, $"{symbol}.id", "postgresqlServer", "postgres");
        }
        else
        {
            file.Resource(symbol + "AzureServices", "Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01", Bicep.Object(body => body
                .Add("parent", symbol)
                .Str("name", "AllowAzureServices")
                .Obj("properties", properties => properties.Str("startIpAddress", "0.0.0.0").Str("endIpAddress", "0.0.0.0"))));
        }
    }

    private static void Cosmos(SketchModel model, BicepFile file, SketchResource account)
    {
        var symbol = Symbol(account);
        var serverless = account.P["capacity"] == "serverless";
        file.Resource(symbol, "Microsoft.DocumentDB/databaseAccounts@2024-11-15", Located(CompactName(account.Compact))
            .Str("kind", "GlobalDocumentDB")
            .Obj("properties", properties =>
            {
                properties.Str("databaseAccountOfferType", "Standard")
                    .Add("locations", Geo.On(model)
                        ? Bicep.Array(Bicep.Object(location => location.Add("locationName", Location).Add("failoverPriority", "0")),
                            Bicep.Object(location => location.Str("locationName", Geo.Second(model)).Add("failoverPriority", "1")))
                        : Bicep.Array(Bicep.Object(location => location.Add("locationName", Location).Add("failoverPriority", "0"))))
                    .Add("enableAutomaticFailover", Bicep.Bool(Geo.On(model)))
                    .Obj("consistencyPolicy", policy => policy.Str("defaultConsistencyLevel", "Session"))
                    .Add("disableLocalAuth", "true")
                    .Add("publicNetworkAccess", PublicAccess(account.IsPrivate));
                var capabilities = new[] { (serverless, "EnableServerless"), (account.Flag("vectorSearch"), "EnableNoSQLVectorSearch") }
                    .Where(item => item.Item1).Select(item => (BicepValue)Bicep.Object(capability => capability.Str("name", item.Item2))).ToArray();
                if (capabilities.Length > 0)
                {
                    properties.Add("capabilities", Bicep.Array(capabilities));
                }

                properties.Add("backupPolicy", account.P["backup"] == "Periodic"
                    ? Bicep.Object(backup => backup.Str("type", "Periodic").Obj("periodicModeProperties", periodic => periodic
                        .Add("backupIntervalInMinutes", "240")
                        .Add("backupRetentionIntervalInHours", "8")
                        .Str("backupStorageRedundancy", model.IsProduction ? "Geo" : "Local")))
                    : Bicep.Object(backup => backup.Str("type", "Continuous").Obj("continuousModeProperties", continuous => continuous.Str("tier", account.P["backup"]))));
            }),
            $"Cosmos DB {account.Name}: Microsoft Entra sign-in only, keys turned off.");
        file.Resource(symbol + "Database", "Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-11-15", Bicep.Object(body => body
            .Add("parent", symbol)
            .Str("name", account.Label)
            .Obj("properties", properties => properties.Obj("resource", resource => resource.Str("id", account.Label)))));
        foreach (var container in account.List("containers"))
        {
            file.Resource(symbol + Pascal(container), "Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15", Bicep.Object(body => body
                .Add("parent", symbol + "Database")
                .Add("name", Bicep.Literal(container))
                .Obj("properties", properties =>
                {
                    properties.Obj("resource", resource => resource
                        .Add("id", Bicep.Literal(container))
                        .Obj("partitionKey", key => key.Add("paths", Bicep.Array("'/id'")).Str("kind", "Hash")));
                    if (!serverless)
                    {
                        properties.Obj("options", options => options.Add("throughput", "400"));
                    }
                })));
        }

        if (account.IsPrivate)
        {
            PrivateEndpoint(file, model, symbol, account.Name, $"{symbol}.id", "Sql", "cosmos");
        }
    }

    // Security --------------------------------------------------------------------------------------------------------

    private static void Role(BicepFile file, string symbol, string scope, string role, string principal, string principalKey, bool servicePrincipal = true)
    {
        file.Resource(symbol, "Microsoft.Authorization/roleAssignments@2022-04-01", Bicep.Object(body => body
            .Add("scope", scope)
            .Add("name", $"guid({scope}.id, {principalKey}, '{Roles[role]}')")
            .Obj("properties", properties => properties
                .Add("roleDefinitionId", $"subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '{Roles[role]}')")
                .Add("principalId", principal)
                .Add("principalType", servicePrincipal ? "'ServicePrincipal'" : "null"))),
            $"{role}.");
    }

    // The resource id used in a role assignment's name: known when the deployment starts.
    private static string KeyOf(SketchResource app) => app.Kind == Kinds.ContainerApp ? $"{Symbol(app)}Identity.id" : $"{Symbol(app)}.id";

    private static void Security(SketchModel model, BicepFile file)
    {
        foreach (var vault in model.Vaults)
        {
            var symbol = Symbol(vault);
            file.Resource(symbol, "Microsoft.KeyVault/vaults@2023-07-01", Located(CompactName(vault.Compact))
                .Obj("properties", properties =>
                {
                    properties.Add("tenantId", "tenant().tenantId")
                        .Obj("sku", sku => sku.Str("family", "A").Str("name", "standard"))
                        .Add("enableRbacAuthorization", "true")
                        .Add("enableSoftDelete", "true")
                        .Add("softDeleteRetentionInDays", vault.P["retentionDays"])
                        .Add("publicNetworkAccess", PublicAccess(vault.IsPrivate));
                    if (vault.Flag("purgeProtection"))
                    {
                        properties.Add("enablePurgeProtection", "true");
                    }
                }),
                $"Key Vault {vault.Name}: access by role, never by access policy.");
            Role(file, symbol + "Deployer", symbol, "Key Vault Secrets Officer", "deployer().objectId", "deployer().objectId", servicePrincipal: false);
            if (vault.IsPrivate)
            {
                PrivateEndpoint(file, model, symbol, vault.Name, $"{symbol}.id", "vault", "vault");
            }
        }

        foreach (var app in model.Compute)
        {
            var principal = PrincipalOf(app);
            var key = KeyOf(app);
            var symbol = Symbol(app);
            foreach (var vault in model.Targets(app, Kinds.KeyVault))
            {
                Role(file, symbol + Symbol(vault) + "Role", Symbol(vault), "Key Vault Secrets User", principal, key);
            }

            foreach (var storage in model.Targets(app, Kinds.Storage))
            {
                Role(file, symbol + Symbol(storage) + "Role", Symbol(storage), "Storage Blob Data Contributor", principal, key);
                foreach (var (part, role) in new[] { ("queues", "Storage Queue Data Contributor"), ("tables", "Storage Table Data Contributor") }.Where(item => storage.List(item.Item1).Count > 0))
                {
                    Role(file, symbol + Symbol(storage) + Pascal(part) + "Role", Symbol(storage), role, principal, key);
                }
            }

            foreach (var bus in model.Targets(app, Kinds.ServiceBus))
            {
                Role(file, symbol + Symbol(bus) + "Send", Symbol(bus), "Azure Service Bus Data Sender", principal, key);
                Role(file, symbol + Symbol(bus) + "Receive", Symbol(bus), "Azure Service Bus Data Receiver", principal, key);
            }

            foreach (var registry in model.Targets(app, Kinds.ContainerRegistry))
            {
                Role(file, symbol + Symbol(registry) + "Role", Symbol(registry), "AcrPull", principal, key);
            }

            foreach (var foundry in model.Targets(app, Kinds.AiFoundry))
            {
                Role(file, symbol + Symbol(foundry) + "AiUser", Symbol(foundry), "Azure AI User", principal, key);
                Role(file, symbol + Symbol(foundry) + "OpenAiUser", Symbol(foundry), "Cognitive Services OpenAI User", principal, key);
            }

            foreach (var speech in model.Targets(app, Kinds.Speech))
            {
                Role(file, symbol + Symbol(speech) + "Role", Symbol(speech), "Cognitive Services Speech User", principal, key);
            }

            ServiceRoles(model, file, app, symbol, principal, key);

            foreach (var account in model.Targets(app, Kinds.CosmosDb))
            {
                var cosmos = Symbol(account);
                file.Resource(symbol + cosmos + "Data", "Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15", Bicep.Object(body => body
                    .Add("parent", cosmos)
                    .Add("name", $"guid({cosmos}.id, {key}, 'data-contributor')")
                    .Obj("properties", properties => properties
                        .Add("roleDefinitionId", $"'${{{cosmos}.id}}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'")
                        .Add("principalId", principal)
                        .Add("scope", $"{cosmos}.id"))),
                    $"{app.Name} reads and writes the data of {account.Name} (built-in Cosmos DB Data Contributor).");
            }

            foreach (var cache in model.Targets(app, Kinds.Redis))
            {
                file.Resource(symbol + Symbol(cache) + "Access", "Microsoft.Cache/redis/accessPolicyAssignments@2024-11-01", Bicep.Object(body => body
                    .Add("parent", Symbol(cache))
                    .Str("name", app.Name)
                    .Obj("properties", properties => properties
                        .Str("accessPolicyName", "Data Contributor")
                        .Add("objectId", principal)
                        .Str("objectIdAlias", app.Name))));
            }
        }
    }

    // Edge ------------------------------------------------------------------------------------------------------------

    private static void Edge(SketchModel model, BicepFile file)
    {
        foreach (var zone in model.Zones)
        {
            file.Resource(Symbol(zone), "Microsoft.Network/dnsZones@2018-05-01", Bicep.Object(body => body
                .Add("name", Bicep.Literal(zone.P["zone"]))
                .Str("location", "global")
                .Add("tags", Tags)),
                $"DNS zone {zone.P["zone"]}: point your registrar to its name servers (see the outputs).");
        }

        foreach (var frontDoor in model.FrontDoors)
        {
            var fd = Symbol(frontDoor);
            file.Resource(fd, "Microsoft.Cdn/profiles@2024-02-01", Bicep.Object(body => body
                .Add("name", Prefixed(frontDoor.Name))
                .Str("location", "global")
                .Add("tags", Tags)
                .Obj("sku", sku => sku.Str("name", frontDoor.P["sku"]))),
                $"Front Door {frontDoor.Name}.");
            file.Resource(fd + "Endpoint", "Microsoft.Cdn/profiles/afdEndpoints@2024-02-01", Bicep.Object(body => body
                .Add("parent", fd)
                .Add("name", Prefixed($"{frontDoor.Name}-${{suffix}}"))
                .Str("location", "global")
                .Add("tags", Tags)
                .Obj("properties", properties => properties.Str("enabledState", "Enabled"))));

            var domains = new List<string>();
            foreach (var zone in model.Sources(frontDoor, Kinds.DnsZone))
            {
                var subdomain = frontDoor.P["subdomain"];
                var host = $"{subdomain}.{zone.P["zone"]}";
                var domain = fd + Symbol(zone) + "Domain";
                domains.Add(domain);
                file.Resource(domain, "Microsoft.Cdn/profiles/customDomains@2024-02-01", Bicep.Object(body => body
                    .Add("parent", fd)
                    .Str("name", host.Replace('.', '-'))
                    .Obj("properties", properties => properties
                        .Add("hostName", Bicep.Literal(host))
                        .Obj("azureDnsZone", dns => dns.Add("id", $"{Symbol(zone)}.id"))
                        .Obj("tlsSettings", tls => tls.Str("certificateType", "ManagedCertificate").Str("minimumTlsVersion", "TLS12")))),
                    $"{host} with a certificate managed by Front Door.");
                file.Resource(domain + "Validation", "Microsoft.Network/dnsZones/TXT@2018-05-01", Bicep.Object(body => body
                    .Add("parent", Symbol(zone))
                    .Add("name", Bicep.Literal($"_dnsauth.{subdomain}"))
                    .Obj("properties", properties => properties
                        .Add("TTL", "3600")
                        .Add("TXTRecords", Bicep.Array(Bicep.Object(record => record.Add("value", Bicep.Array($"{domain}.properties.validationProperties.validationToken"))))))));
                file.Resource(domain + "Cname", "Microsoft.Network/dnsZones/CNAME@2018-05-01", Bicep.Object(body => body
                    .Add("parent", Symbol(zone))
                    .Add("name", Bicep.Literal(subdomain))
                    .Obj("properties", properties => properties
                        .Add("TTL", "3600")
                        .Obj("CNAMERecord", record => record.Add("cname", $"{fd}Endpoint.properties.hostName")))));
            }

            foreach (var origin in Kinds.Origins.SelectMany(kind => model.Targets(frontDoor, kind)))
            {
                var group = fd + Symbol(origin) + "Group";
                file.Resource(group, "Microsoft.Cdn/profiles/originGroups@2024-02-01", Bicep.Object(body => body
                    .Add("parent", fd)
                    .Str("name", origin.Name)
                    .Obj("properties", properties => properties
                        .Obj("loadBalancingSettings", balancing => balancing.Add("sampleSize", "4").Add("successfulSamplesRequired", "3"))
                        .Obj("healthProbeSettings", probe => probe
                            .Str("probePath", "/")
                            .Str("probeProtocol", "Https")
                            .Str("probeRequestType", "HEAD")
                            .Add("probeIntervalInSeconds", "100"))
                        .Str("sessionAffinityState", "Disabled"))),
                    $"{origin.P["path"]} goes to {origin.Name}.");
                file.Resource(group + "Origin", "Microsoft.Cdn/profiles/originGroups/origins@2024-02-01", Bicep.Object(body => body
                    .Add("parent", group)
                    .Str("name", origin.Name)
                    .Obj("properties", properties => properties
                        .Add("hostName", HostOf(origin))
                        .Add("originHostHeader", HostOf(origin))
                        .Add("httpPort", "80")
                        .Add("httpsPort", "443")
                        .Add("priority", "1")
                        .Add("weight", "1000")
                        .Str("enabledState", "Enabled")
                        .Add("enforceCertificateNameCheck", "true"))));
                file.Resource(group + "Route", "Microsoft.Cdn/profiles/afdEndpoints/routes@2024-02-01", Bicep.Object(body => body
                    .Add("parent", fd + "Endpoint")
                    .Str("name", origin.Name)
                    .Obj("properties", properties =>
                    {
                        properties.Obj("originGroup", reference => reference.Add("id", $"{group}.id"))
                            .Add("supportedProtocols", Bicep.Array("'Http'", "'Https'"))
                            .Add("patternsToMatch", Bicep.Array(Bicep.Literal(origin.P["path"])))
                            .Str("forwardingProtocol", "HttpsOnly")
                            .Str("httpsRedirect", "Enabled")
                            .Str("linkToDefaultDomain", "Enabled");
                        if (domains.Count > 0)
                        {
                            properties.Add("customDomains", Bicep.Array(domains.Select(domain => (BicepValue)Bicep.Object(reference => reference.Add("id", $"{domain}.id")))));
                        }
                    })
                    .Add("dependsOn", Bicep.Array($"{group}Origin"))));
            }

            if (Firewall.On(frontDoor))
            {
                FrontDoorFirewall(model, file, frontDoor, domains);
            }
        }

        Gateways(model, file);
    }

    private static void FrontDoorFirewall(SketchModel model, BicepFile file, SketchResource frontDoor, List<string> domains)
    {
        var fd = Symbol(frontDoor);
        var premium = frontDoor.P["sku"] == "Premium_AzureFrontDoor";
        file.Resource(fd + "Waf", "Microsoft.Network/FrontDoorWebApplicationFirewallPolicies@2024-02-01", Bicep.Object(body => body
            .Add("name", $"'${{compactPrefix}}{frontDoor.Compact}waf'")
            .Str("location", "global")
            .Add("tags", Tags)
            .Obj("sku", sku => sku.Str("name", frontDoor.P["sku"]))
            .Obj("properties", properties =>
            {
                properties.Obj("policySettings", settings => settings
                        .Str("enabledState", "Enabled")
                        .Str("mode", Firewall.Blocks(model, frontDoor) ? "Prevention" : "Detection"))
                    .Obj("customRules", custom => custom.Add("rules", Bicep.Array(Bicep.Object(rule => rule
                        .Str("name", "RequestLimit")
                        .Str("enabledState", "Enabled")
                        .Add("priority", "100")
                        .Str("ruleType", "RateLimitRule")
                        .Add("rateLimitDurationInMinutes", "1")
                        .Add("rateLimitThreshold", Firewall.Rate(frontDoor))
                        .Str("action", "Block")
                        .Add("matchConditions", Bicep.Array(Bicep.Object(condition => condition
                            .Str("matchVariable", "RemoteAddr")
                            .Str("operator", "IPMatch")
                            .Add("negateCondition", "true")
                            .Add("matchValue", Bicep.Array("'127.0.0.1'")))))))));
                if (premium)
                {
                    properties.Obj("managedRules", managed => managed.Add("managedRuleSets", Bicep.Array(
                        Bicep.Object(set => set.Str("ruleSetType", "Microsoft_DefaultRuleSet").Str("ruleSetVersion", "2.1").Str("ruleSetAction", "Block")),
                        Bicep.Object(set => set.Str("ruleSetType", "Microsoft_BotManagerRuleSet").Str("ruleSetVersion", "1.1").Str("ruleSetAction", "Block")))));
                }
            })),
            $"Firewall (WAF) of {frontDoor.Name}: " + (Firewall.Blocks(model, frontDoor) ? "blocks attacks" : "only logs attacks") + $" and limits each address to {Firewall.Rate(frontDoor)} requests a minute.");
        file.Resource(fd + "Security", "Microsoft.Cdn/profiles/securityPolicies@2024-02-01", Bicep.Object(body => body
            .Add("parent", fd)
            .Str("name", "firewall")
            .Obj("properties", properties => properties.Obj("parameters", parameters => parameters
                .Str("type", "WebApplicationFirewall")
                .Obj("wafPolicy", policy => policy.Add("id", $"{fd}Waf.id"))
                .Add("associations", Bicep.Array(Bicep.Object(association => association
                    .Add("domains", Bicep.Array([(BicepValue)Bicep.Object(domain => domain.Add("id", $"{fd}Endpoint.id")),
                        .. domains.Select(custom => (BicepValue)Bicep.Object(domain => domain.Add("id", $"{custom}.id")))]))
                    .Add("patternsToMatch", Bicep.Array("'/*'")))))))));
    }

    // Diagnostics and outputs ---------------------------------------------------------------------------------------

    private static void Diagnostics(SketchModel model, BicepFile file)
    {
        if (!TerraformGenerator.Logs(model))
        {
            return;
        }

        var targets = model.Apps.Concat(model.Functions).Concat(model.Databases).Concat(model.PostgresServers).Concat(model.CosmosAccounts)
            .Concat(model.Caches).Concat(model.Buses).Concat(model.Vaults).Concat(model.Registries).Concat(model.FrontDoors)
            .Concat(model.Foundries).Concat(model.SpeechServices).Concat(ServiceDiagnostics(model)).Concat(model.AppGateways);
        foreach (var target in targets)
        {
            file.Resource(Symbol(target) + "Diagnostics", "Microsoft.Insights/diagnosticSettings@2021-05-01-preview", Bicep.Object(body => body
                .Add("scope", Symbol(target))
                .Str("name", "to-log-analytics")
                .Obj("properties", properties => properties
                    .Add("workspaceId", "logs.id")
                    .Add("logs", Bicep.Array(Bicep.Object(log => log.Str("categoryGroup", "allLogs").Add("enabled", "true"))))
                    .Add("metrics", Bicep.Array(Bicep.Object(metric => metric.Str("category", "AllMetrics").Add("enabled", "true")))))));
        }
    }

    private static void Outputs(SketchModel model, BicepFile file)
    {
        foreach (var app in model.Compute.Concat(model.StaticSites))
        {
            if (app.Kind != Kinds.StaticWebApp)
            {
                file.Output($"{app.Camel}Identity", "string", IdentityNameOf(app));
            }

            file.Output($"{app.Camel}Url", "string", $"'https://${{{HostOf(app)}}}'");
        }

        if (model.Databases.Any())
        {
            file.Output("sqlServerFqdn", "string", "sqlServer.properties.fullyQualifiedDomainName");
        }

        foreach (var server in model.PostgresServers)
        {
            file.Output($"{server.Camel}Fqdn", "string", $"{Symbol(server)}.properties.fullyQualifiedDomainName");
        }

        foreach (var registry in model.Registries)
        {
            file.Output($"{registry.Camel}LoginServer", "string", $"{Symbol(registry)}.properties.loginServer");
        }

        foreach (var vault in model.Vaults)
        {
            file.Output($"{vault.Camel}Uri", "string", $"{Symbol(vault)}.properties.vaultUri");
        }

        foreach (var frontDoor in model.FrontDoors)
        {
            file.Output($"{frontDoor.Camel}Endpoint", "string", $"'https://${{{Symbol(frontDoor)}Endpoint.properties.hostName}}'");
        }

        foreach (var zone in model.Zones)
        {
            file.Output($"{zone.Camel}NameServers", "array", $"{Symbol(zone)}.properties.nameServers");
        }

        foreach (var gateway in model.AppGateways)
        {
            file.Output($"{gateway.Camel}Address", "string", $"{Symbol(gateway)}Ip.properties.ipAddress");
        }

        foreach (var account in model.Foundries.Concat(model.SpeechServices))
        {
            file.Output($"{account.Camel}Endpoint", "string", $"{Symbol(account)}.properties.endpoint");
        }

        ServiceOutputs(model, file);
    }

    // AI --------------------------------------------------------------------------------------------------------------

    private static void Ai(SketchModel model, BicepFile file)
    {
        foreach (var foundry in model.Foundries)
        {
            var symbol = Symbol(foundry);
            file.Resource(symbol, "Microsoft.CognitiveServices/accounts@2025-06-01", Located(Prefixed($"{foundry.Name}-${{suffix}}"))
                .Str("kind", "AIServices")
                .Obj("sku", sku => sku.Str("name", "S0"))
                .Obj("identity", identity => identity.Str("type", "SystemAssigned"))
                .Obj("properties", properties => properties
                    .Add("customSubDomainName", Prefixed($"{foundry.Name}-${{suffix}}"))
                    .Add("allowProjectManagement", "true")
                    .Add("disableLocalAuth", "true")
                    .Add("publicNetworkAccess", foundry.IsPrivate ? "'Disabled'" : "'Enabled'")),
                $"AI Foundry {foundry.Name}: the AI resource, its project and its model deployments. Microsoft Entra sign-in only.");
            file.Resource(symbol + "Project", "Microsoft.CognitiveServices/accounts/projects@2025-06-01", Bicep.Object(body => body
                .Add("parent", symbol)
                .Str("name", $"{foundry.Name}-project")
                .Add("location", Location)
                .Add("tags", Tags)
                .Obj("identity", identity => identity.Str("type", "SystemAssigned"))
                .Obj("properties", _ => { })));
            string? previous = null;
            foreach (var (suffix, name) in new[] { ("Chat", foundry.P["model"]), ("Embeddings", foundry.P["embeddings"]) }.Where(item => item.Item2 != "none"))
            {
                var after = previous;
                file.Resource(symbol + suffix, "Microsoft.CognitiveServices/accounts/deployments@2025-06-01", Bicep.Object(body =>
                {
                    body.Add("parent", symbol)
                        .Str("name", name)
                        .Obj("sku", sku => sku.Str("name", "GlobalStandard").Add("capacity", foundry.P["capacity"]))
                        .Obj("properties", properties => properties
                            .Obj("model", definition => definition.Str("format", "OpenAI").Str("name", name).Str("version", TerraformGenerator.ModelVersions[name])));
                    if (after is not null)
                    {
                        body.Add("dependsOn", Bicep.Array(after));
                    }
                }));
                previous = symbol + suffix;
            }

            if (foundry.IsPrivate)
            {
                PrivateEndpoint(file, model, symbol, foundry.Name, $"{symbol}.id", "account", "cognitive", "openai", "aiservices");
            }
        }

        foreach (var speech in model.SpeechServices)
        {
            var symbol = Symbol(speech);
            file.Resource(symbol, "Microsoft.CognitiveServices/accounts@2025-06-01", Located(Prefixed($"{speech.Name}-${{suffix}}"))
                .Str("kind", "SpeechServices")
                .Obj("sku", sku => sku.Str("name", speech.P["sku"]))
                .Obj("properties", properties => properties
                    .Add("customSubDomainName", Prefixed($"{speech.Name}-${{suffix}}"))
                    .Add("disableLocalAuth", "true")
                    .Add("publicNetworkAccess", speech.IsPrivate ? "'Disabled'" : "'Enabled'")),
                $"Speech {speech.Name}: Microsoft Entra sign-in only (the custom subdomain makes it possible).");
            if (speech.IsPrivate)
            {
                PrivateEndpoint(file, model, symbol, speech.Name, $"{symbol}.id", "account", "cognitive");
            }
        }
    }

    // Alerts ----------------------------------------------------------------------------------------------------------

    private static void Alerts(SketchModel model, BicepFile file)
    {
        var targets = TerraformGenerator.AlertTargets(model).ToList();
        if (!model.Alerts || targets.Count == 0)
        {
            return;
        }

        file.Resource("alertGroup", "Microsoft.Insights/actionGroups@2023-01-01", Bicep.Object(body => body
            .Add("name", Prefixed("alerts"))
            .Str("location", "global")
            .Add("tags", Tags)
            .Obj("properties", properties => properties
                .Str("groupShortName", "alerts")
                .Add("enabled", "true")
                .Add("emailReceivers", Bicep.Array(Bicep.Object(email => email
                    .Str("name", "email")
                    .Add("emailAddress", "alertEmail")
                    .Add("useCommonAlertSchema", "true")))))),
            "Alerts: an email when an app returns server errors or a database is overloaded.");

        var symbols = model.Resources.ToDictionary(resource => resource.Label, Symbol, StringComparer.Ordinal);
        foreach (var alert in targets)
        {
            var target = symbols[alert.Label[..alert.Label.LastIndexOf('_')]];
            file.Resource("alert" + Pascal(alert.Label.Replace('_', '-')), "Microsoft.Insights/metricAlerts@2018-03-01", Bicep.Object(body => body
                .Add("name", Prefixed(alert.Label.Replace('_', '-')))
                .Str("location", "global")
                .Add("tags", Tags)
                .Obj("properties", properties => properties
                    .Str("description", alert.Description)
                    .Add("severity", "2")
                    .Add("enabled", "true")
                    .Add("scopes", Bicep.Array($"{target}.id"))
                    .Str("evaluationFrequency", "PT5M")
                    .Str("windowSize", "PT15M")
                    .Obj("criteria", criteria => criteria
                        .Str("odata.type", "Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria")
                        .Add("allOf", Bicep.Array(Bicep.Object(criterion =>
                        {
                            criterion.Str("name", "condition")
                                .Str("criterionType", "StaticThresholdCriterion")
                                .Str("metricNamespace", alert.Namespace)
                                .Str("metricName", alert.Metric)
                                .Str("operator", "GreaterThan")
                                .Add("threshold", alert.Threshold)
                                .Str("timeAggregation", alert.Aggregation);
                            if (alert.Dimension is { } dimension)
                            {
                                criterion.Add("dimensions", Bicep.Array(Bicep.Object(filter => filter
                                    .Str("name", dimension.Name)
                                    .Str("operator", "Include")
                                    .Add("values", Bicep.Array(Bicep.Str(dimension.Value))))));
                            }
                        }))))
                    .Add("actions", Bicep.Array(Bicep.Object(action => action.Add("actionGroupId", "alertGroup.id")))))));
        }
    }
}
