using System.Globalization;

namespace TerraformGenerator.Domain;

// Turns a checked drawing into the Terraform files of one Azure environment (azurerm 4).
public static partial class TerraformGenerator
{
    // Every resource goes in the resource group of main.tf (created or existing) and in the chosen region.
    private const string Location = "local.location";
    private const string ResourceGroup = "local.resource_group_name";
    private const string Tags = "var.tags";
    private const string Tenant = "data.azurerm_client_config.current.tenant_id";
    private const string AdminObjectId = "var.admin_object_id != \"\" ? var.admin_object_id : data.azurerm_client_config.current.object_id";

    public static Dictionary<string, string> Files(SketchModel model, IResourceSchemas? schemas = null)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["versions.tf"] = Hcl.Render(Versions(model)),
            ["main.tf"] = Hcl.Render(Main(model)),
            ["variables.tf"] = Hcl.Render(Variables(model)),
        };
        AddIfAny(files, "network.tf", Network(model));
        AddIfAny(files, "apps.tf", Apps(model));
        AddIfAny(files, "data.tf", Data(model));
        AddIfAny(files, "ai.tf", Ai(model).ToList());
        AddIfAny(files, "services.tf", Services(model));
        AddIfAny(files, "team.tf", Team.AzureTerraform(model));
        AddIfAny(files, "policies.tf", Guardrails.AzureTerraform(model));
        AddIfAny(files, "keys.tf", Keys(model));
        AddIfAny(files, "audit.tf", Protection.AzureTerraform(model, Logs(model) ? Workspace(model) : ""));
        AddIfAny(files, "security.tf", Security(model));
        AddIfAny(files, "edge.tf", Edge(model));
        AddIfAny(files, "monitoring.tf", Diagnostics(model));
        AddIfAny(files, "resources.tf", GenericGenerator.Resources(model, schemas));
        files["outputs.tf"] = Hcl.Render(Outputs(model));
        files["terraform.tfvars.example"] = TfvarsExample(model);
        return files;
    }

    private static void AddIfAny(Dictionary<string, string> files, string name, List<HclItem> items)
    {
        if (items.Count > 0)
        {
            files[name] = Hcl.Render(items);
        }
    }

    private static HclBlock Resource(string type, string label) => new("resource", type, label);

    private static string Interpolate(string expression) => "${" + expression + "}";

    private static string Prefixed(string suffix) => Hcl.Str("${var.name_prefix}-" + suffix);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    // Globally unique names without hyphens (storage, Key Vault, registry, Cosmos DB): up to 18 characters plus the suffix.
    private static string CompactName(string compact) =>
        "\"${substr(\"${local.compact_prefix}" + compact + "\", 0, 18)}${local.suffix}\"";

    // References to compute, whatever its kind.
    public static string ResourceType(SketchResource app) => app.Kind switch
    {
        Kinds.WebApp => "azurerm_linux_web_app",
        Kinds.FunctionApp => "azurerm_linux_function_app",
        Kinds.ContainerApp => "azurerm_container_app",
        Kinds.StaticWebApp => "azurerm_static_web_app",
        _ => throw new ArgumentException(app.Kind),
    };

    private static string Ref(SketchResource app) => $"{ResourceType(app)}.{app.Label}";

    private static string HostOf(SketchResource app) => app.Kind switch
    {
        Kinds.ContainerApp => $"{Ref(app)}.ingress[0].fqdn",
        Kinds.StaticWebApp => $"{Ref(app)}.default_host_name",
        _ => $"{Ref(app)}.default_hostname",
    };

    // An App Service app's own identity, plus the identities of the bots it answers for.
    private static void AppIdentity(HclBlock identity, List<string> bots)
    {
        if (bots.Count == 0)
        {
            identity.Str("type", "SystemAssigned");
        }
        else
        {
            identity.Str("type", "SystemAssigned, UserAssigned").Attr("identity_ids", Hcl.List(bots));
        }
    }

    private static string PrincipalOf(SketchResource app) =>
        app.Kind == Kinds.ContainerApp ? $"azurerm_user_assigned_identity.{app.Label}.principal_id" : $"{Ref(app)}.identity[0].principal_id";

    // The name the identity has in the databases (an App Service app's identity is named after the app). Written as
    // the name itself, not a reference, because an app's own settings cannot refer to the app.
    private static string IdentityNameOf(SketchResource app) =>
        app.Kind == Kinds.ContainerApp ? $"azurerm_user_assigned_identity.{app.Label}.name" : Prefixed($"{app.Name}-${{local.suffix}}");

    // versions.tf, backend --------------------------------------------------------------------------------------------

    private static List<HclItem> Versions(SketchModel model)
    {
        var terraform = new HclBlock("terraform")
            .Str("required_version", ">= 1.6.0")
            .Block("required_providers", providers => providers
                .Map("azurerm", [new("source", Hcl.Str("hashicorp/azurerm")), new("version", Hcl.Str("~> 4.0"))])
                .Map("random", [new("source", Hcl.Str("hashicorp/random")), new("version", Hcl.Str("~> 3.6"))]));
        if (model.RemoteState)
        {
            terraform.Comment("The state lives in Azure; bootstrap-state.ps1 creates its storage and writes backend.hcl.");
            terraform.Block("backend", _ => { }, "azurerm");
        }

        return [terraform];
    }

    // main.tf ---------------------------------------------------------------------------------------------------------

    public static bool NeedsClientConfig(SketchModel model) =>
        model.Databases.Any() || model.PostgresServers.Any() || model.Vaults.Any() || model.OfKind(AzureServices.MachineLearning).Any() || OwnKeys.Azure(model);

    private static List<HclItem> Main(SketchModel model)
    {
        var items = new List<HclItem>
        {
            new HclComment($"{model.Settings.Name} ({model.Settings.Environment}) on Azure, drawn in Terraform Generator. See README.md for the steps."),
            new HclBlock("provider", "azurerm")
                .Attr("subscription_id", "var.subscription_id")
                .Attr("storage_use_azuread", "true")
                .Block("features", features =>
                {
                    if (model.Vaults.Any())
                    {
                        features.Block("key_vault", vault => vault
                            .Comment("A deleted vault stays recoverable for its retention period; Terraform never purges it.")
                            .Attr("purge_soft_delete_on_destroy", "false"));
                    }

                    features.Block("resource_group", group => group.Attr("prevent_deletion_if_contains_resources", "true"));
                }),
        };

        if (NeedsClientConfig(model))
        {
            items.Add(new HclBlock("data", "azurerm_client_config", "current"));
        }

        items.Add(new HclComment("Six characters that make the globally unique names unique."));
        items.Add(Resource("random_string", "suffix")
            .Attr("length", "6")
            .Attr("lower", "true")
            .Attr("upper", "false")
            .Attr("numeric", "true")
            .Attr("special", "false"));
        items.Add(new HclBlock("locals")
            .Attr("compact_prefix", "substr(replace(var.name_prefix, \"-\", \"\"), 0, 12)")
            .Attr("suffix", "random_string.suffix.result")
            .Attr("location", "var.location")
            .Attr("resource_group_name", $"{ResourceGroupSource(model)}.name")
            .Attr("resource_group_id", $"{ResourceGroupSource(model)}.id"));
        if (model.ExistingResourceGroup)
        {
            items.Add(new HclComment("The resource group already exists: Terraform uses it and never deletes it."));
            items.Add(new HclBlock("data", "azurerm_resource_group", "main").Attr("name", "var.resource_group_name"));
        }
        else
        {
            items.Add(new HclComment("The resource group of the environment: every resource goes in it."));
            items.Add(Resource("azurerm_resource_group", "main")
                .Attr("name", "var.resource_group_name")
                .Attr("location", "var.location")
                .Attr("tags", Tags));
        }

        if (Logs(model) && !model.ExistingLogs)
        {
            items.Add(new HclComment("Logs of every resource, and Application Insights for the apps."));
            items.Add(Resource("azurerm_log_analytics_workspace", "main")
                .Attr("name", Prefixed("logs"))
                .Attr("location", Location)
                .Attr("resource_group_name", ResourceGroup)
                .Str("sku", "PerGB2018")
                .Attr("retention_in_days", Number(model.LogRetentionDays))
                .Attr("tags", Tags));
        }

        if (model.Lock)
        {
            items.Add(new HclComment("Nobody can delete the resource group by mistake. deploy.ps1 -Destroy removes this lock first."));
            items.Add(Resource("azurerm_management_lock", "main")
                .Str("name", "do-not-delete")
                .Attr("scope", "local.resource_group_id")
                .Str("lock_level", "CanNotDelete")
                .Str("notes", "Created by Terraform Generator. Remove it only to delete the environment on purpose."));
        }

        if (model.Monitoring)
        {
            items.Add(Resource("azurerm_application_insights", "main")
                .Attr("name", Prefixed("appinsights"))
                .Attr("location", Location)
                .Attr("resource_group_name", ResourceGroup)
                .Attr("workspace_id", Workspace(model))
                .Str("application_type", "web")
                .Attr("tags", Tags));
        }

        if (model.Budget > 0)
        {
            items.Add(new HclComment("An email at 80% of the monthly budget and when the month is forecast to go over it."));
            items.Add(Resource("azurerm_consumption_budget_resource_group", "main")
                .Attr("name", Prefixed("budget"))
                .Attr("resource_group_id", "local.resource_group_id")
                .Attr("amount", "var.monthly_budget")
                .Str("time_grain", "Monthly")
                .Block("time_period", period => period.Attr("start_date", "formatdate(\"YYYY-MM-01'T'00:00:00Z\", timestamp())"))
                .Block("notification", notification => notification
                    .Attr("enabled", "true")
                    .Attr("threshold", "80")
                    .Str("threshold_type", "Actual")
                    .Str("operator", "GreaterThan")
                    .Attr("contact_emails", "[var.budget_email]"))
                .Block("notification", notification => notification
                    .Attr("enabled", "true")
                    .Attr("threshold", "100")
                    .Str("threshold_type", "Forecasted")
                    .Str("operator", "GreaterThan")
                    .Attr("contact_emails", "[var.budget_email]"))
                .Block("lifecycle", lifecycle => lifecycle
                    .Comment("The start month is set once; later plans leave it alone.")
                    .Attr("ignore_changes", "[time_period]")));
        }

        return items;
    }

    // The Log Analytics workspace: this environment's, or the existing one named in the settings.
    private static string Workspace(SketchModel model) =>
        model.ExistingLogs ? "var.log_analytics_workspace_id" : "azurerm_log_analytics_workspace.main.id";

    private static string ResourceGroupSource(SketchModel model) =>
        model.ExistingResourceGroup ? "data.azurerm_resource_group.main" : "azurerm_resource_group.main";

    public static bool Logs(SketchModel model) => model.IsAzure && model.Settings.Monitoring && model.Resources.Count > 0;

    // variables.tf ----------------------------------------------------------------------------------------------------

    private static HclBlock Variable(string name, string type, string description, string? defaultValue = null)
    {
        var block = new HclBlock("variable", name).Str("description", description).Attr("type", type);
        if (defaultValue is not null)
        {
            block.Attr("default", defaultValue);
        }

        return block;
    }

    public static bool NeedsAdmin(SketchModel model) => model.Databases.Any() || model.PostgresServers.Any();

    private static List<HclItem> Variables(SketchModel model)
    {
        var tags = new List<KeyValuePair<string, string>>
        {
            new("project", Hcl.Literal(model.Settings.Name)),
            new("environment", Hcl.Literal(model.Settings.Environment)),
            new("managed-by", Hcl.Str("terraform")),
        };
        tags.AddRange(model.ExtraTags.Select(tag => new KeyValuePair<string, string>(tag.Key, Hcl.Literal(tag.Value))));

        var items = new List<HclItem>
        {
            model.SubscriptionId.Length > 0
                ? Variable("subscription_id", "string", "The Azure subscription to deploy to.", Hcl.Literal(model.SubscriptionId))
                : Variable("subscription_id", "string", "The Azure subscription to deploy to (deploy.ps1 writes it to terraform.tfvars)."),
            Variable("location", "string", "The Azure region.", Hcl.Str(model.Settings.Region)),
            Variable("name_prefix", "string", "Starts the name of every resource.", Hcl.Str($"{model.Settings.Name}-{model.Settings.Environment}")),
            Variable("resource_group_name", "string",
                model.ExistingResourceGroup ? "The existing resource group every resource goes in." : "The resource group to create; every resource goes in it.",
                Hcl.Literal(model.ResourceGroupName)),
            new HclBlock("variable", "tags")
                .Str("description", "Tags on every resource.")
                .Attr("type", "map(string)")
                .Map("default", tags),
        };

        if (NeedsAdmin(model))
        {
            items.Add(Variable("admin_object_id", "string", "Object id of the Microsoft Entra user or group that administers the databases; empty means whoever runs Terraform.",
                Hcl.Literal(model.AdminGroup ? model.AdminGroupId : "")));
            items.Add(Variable("admin_name", "string", "Sign-in name (user) or display name (group) of that administrator." + (model.AdminGroup ? "" : " deploy.ps1 fills it with yours."),
                Hcl.Literal(model.AdminGroup ? model.AdminGroupName : "")));
            items.Add(Variable("admin_type", "string", "User or Group.", Hcl.Str(model.AdminGroup ? "Group" : "User")));
        }

        if (model.NeedsNetwork)
        {
            items.Add(Variable("network_range", "string", "The /16 the subnets are cut from.", Hcl.Literal(model.NetworkRange)));
            if (model.ExistingNetwork)
            {
                items.Add(Variable("network_name", "string", "The existing virtual network the subnets are added to.", Hcl.Literal(model.NetworkName)));
                items.Add(Variable("network_resource_group", "string", "Its resource group.", Hcl.Literal(model.NetworkResourceGroup)));
            }

            if (model.ExistingDns)
            {
                items.Add(Variable("private_dns_resource_group", "string", "The resource group of the existing private DNS zones.", Hcl.Literal(model.DnsResourceGroup)));
            }
        }

        if (model.ExistingLogs)
        {
            items.Add(Variable("log_analytics_workspace_id", "string", "The existing Log Analytics workspace every resource sends its logs to.", Hcl.Literal(model.LogWorkspaceId)));
        }

        if (model.Alerts)
        {
            items.Add(Variable("alert_email", "string", "Who gets the alert emails.", Hcl.Literal(model.AlertEmail)));
        }

        if (model.Budget > 0)
        {
            items.Add(Variable("monthly_budget", "number", "Monthly budget in US dollars.", Number(model.Budget)));
            items.Add(Variable("budget_email", "string", "Who gets the budget emails.", Hcl.Literal(model.Setting(SettingKeys.BudgetEmail))));
        }

        return items;
    }

    // network.tf ------------------------------------------------------------------------------------------------------

    // Subnets are cut from the /16: endpoints get the second /24, Container Apps the fifth /23, each App Service plan a /26 from .16.0.
    private const string EndpointsPrefix = "cidrsubnet(var.network_range, 8, 1)";
    private const string ContainerPrefix = "cidrsubnet(var.network_range, 7, 4)";

    private static List<(PlanGroup Plan, string Prefix)> PlanSubnets(SketchModel model) =>
        model.IntegratedPlans.Select((plan, index) => (plan, $"cidrsubnet(var.network_range, 10, {64 + index})")).ToList();

    private static string NetworkGroup(SketchModel model) => model.ExistingNetwork ? "var.network_resource_group" : ResourceGroup;

    private static string NetworkName(SketchModel model) => model.ExistingNetwork ? "var.network_name" : "azurerm_virtual_network.main.name";

    private static List<HclItem> Network(SketchModel model)
    {
        if (!model.NeedsNetwork)
        {
            return [];
        }

        var items = new List<HclItem>();
        if (model.ExistingNetwork)
        {
            items.Add(new HclComment("The existing network: the subnets below are added to it."));
            items.Add(new HclBlock("data", "azurerm_virtual_network", "main")
                .Attr("name", "var.network_name")
                .Attr("resource_group_name", "var.network_resource_group"));
        }
        else
        {
            items.Add(new HclComment("Private network: private endpoints in one subnet, one subnet per App Service plan that joins it, one for Container Apps."));
            items.Add(Resource("azurerm_virtual_network", "main")
                .Attr("name", Prefixed("vnet"))
                .Attr("location", Location)
                .Attr("resource_group_name", ResourceGroup)
                .Attr("address_space", "[var.network_range]")
                .Attr("tags", Tags));
        }

        var subnets = new List<string> { "endpoints" };
        items.Add(Resource("azurerm_subnet", "endpoints")
            .Attr("name", Prefixed("private-endpoints"))
            .Attr("resource_group_name", NetworkGroup(model))
            .Attr("virtual_network_name", NetworkName(model))
            .Attr("address_prefixes", $"[{EndpointsPrefix}]"));

        foreach (var (plan, prefix) in PlanSubnets(model))
        {
            subnets.Add(plan.Label);
            items.Add(DelegatedSubnet(model, plan.Label, plan.Label.Replace('_', '-'), prefix, "Microsoft.Web/serverFarms", ["Microsoft.Network/virtualNetworks/subnets/action"]));
        }

        if (model.AppGateways.Any())
        {
            items.Add(new HclComment("The Application Gateway's own subnet."));
            items.Add(Resource("azurerm_subnet", "gateway")
                .Attr("name", Prefixed("app-gateway"))
                .Attr("resource_group_name", NetworkGroup(model))
                .Attr("virtual_network_name", NetworkName(model))
                .Attr("address_prefixes", $"[{GatewayPrefix}]"));
        }

        if (model.ContainerEnvironmentJoinsNetwork)
        {
            subnets.Add("containers");
            items.Add(DelegatedSubnet(model, "containers", "container-apps", ContainerPrefix, "Microsoft.App/environments", ["Microsoft.Network/virtualNetworks/subnets/join/action"]));
        }

        if (model.Nsg)
        {
            items.Add(new HclComment("A network security group on every subnet: the Azure default rules, ready for your own."));
            foreach (var subnet in subnets)
            {
                items.Add(Resource("azurerm_network_security_group", subnet)
                    .Attr("name", Prefixed($"{subnet.Replace('_', '-')}-nsg"))
                    .Attr("location", Location)
                    .Attr("resource_group_name", ResourceGroup)
                    .Attr("tags", Tags));
                items.Add(Resource("azurerm_subnet_network_security_group_association", subnet)
                    .Attr("subnet_id", $"azurerm_subnet.{subnet}.id")
                    .Attr("network_security_group_id", $"azurerm_network_security_group.{subnet}.id"));
            }
        }

        var network = model.ExistingNetwork ? "data.azurerm_virtual_network.main.id" : "azurerm_virtual_network.main.id";
        var dnsGroup = model.ExistingDns ? "var.private_dns_resource_group" : ResourceGroup;
        foreach (var (label, zone) in model.PrivateZones)
        {
            if (model.ExistingDns)
            {
                items.Add(new HclBlock("data", "azurerm_private_dns_zone", label)
                    .Str("name", zone)
                    .Attr("resource_group_name", "var.private_dns_resource_group"));
            }
            else
            {
                items.Add(Resource("azurerm_private_dns_zone", label)
                    .Str("name", zone)
                    .Attr("resource_group_name", ResourceGroup)
                    .Attr("tags", Tags));
            }

            var source = model.ExistingDns ? "data.azurerm_private_dns_zone" : "azurerm_private_dns_zone";
            items.Add(Resource("azurerm_private_dns_zone_virtual_network_link", label)
                .Attr("name", Prefixed($"{label}-link"))
                .Attr("resource_group_name", dnsGroup)
                .Attr("private_dns_zone_name", $"{source}.{label}.name")
                .Attr("virtual_network_id", network)
                .Attr("registration_enabled", "false")
                .Attr("tags", Tags));
        }

        var zones = model.ExistingDns ? "data.azurerm_private_dns_zone" : "azurerm_private_dns_zone";
        items.Add(new HclBlock("locals").Map("private_dns_zone_ids",
            model.PrivateZones.Select(zone => new KeyValuePair<string, string>(zone.Label, $"{zones}.{zone.Label}.id"))));
        return items;
    }

    private static HclBlock DelegatedSubnet(SketchModel model, string label, string name, string prefix, string service, string[] actions) =>
        Resource("azurerm_subnet", label)
            .Attr("name", Prefixed(name))
            .Attr("resource_group_name", NetworkGroup(model))
            .Attr("virtual_network_name", NetworkName(model))
            .Attr("address_prefixes", $"[{prefix}]")
            .Block("delegation", delegation => delegation
                .Str("name", "delegation")
                .Block("service_delegation", delegated => delegated
                    .Str("name", service)
                    .Attr("actions", Hcl.List(actions.Select(Hcl.Str)))));

    private static HclBlock PrivateEndpoint(string label, string name, string resourceId, string subresource, params string[] dnsZones) =>
        Resource("azurerm_private_endpoint", label)
            .Attr("name", Prefixed($"{name}-pe"))
            .Attr("location", Location)
            .Attr("resource_group_name", ResourceGroup)
            .Attr("subnet_id", "azurerm_subnet.endpoints.id")
            .Attr("tags", Tags)
            .Block("private_service_connection", connection => connection
                .Str("name", $"{name}-connection")
                .Attr("private_connection_resource_id", resourceId)
                .Attr("subresource_names", Hcl.List([Hcl.Str(subresource)]))
                .Attr("is_manual_connection", "false"))
            .Block("private_dns_zone_group", group => group
                .Str("name", "default")
                .Attr("private_dns_zone_ids", Hcl.List(dnsZones.Select(zone => $"local.private_dns_zone_ids[\"{zone}\"]"))));

    // apps.tf ---------------------------------------------------------------------------------------------------------

    private static List<HclItem> Apps(SketchModel model)
    {
        var items = new List<HclItem>();
        var subnets = PlanSubnets(model).Select(subnet => subnet.Plan.Label).ToHashSet(StringComparer.Ordinal);
        foreach (var plan in model.Plans)
        {
            items.Add(new HclComment($"App Service plan {plan.Sku}: {string.Join(", ", plan.Apps.Select(app => app.Name))}."));
            items.Add(Resource("azurerm_service_plan", plan.Label)
                .Attr("name", Prefixed(plan.Label.Replace('_', '-')))
                .Attr("location", Location)
                .Attr("resource_group_name", ResourceGroup)
                .Str("os_type", "Linux")
                .Str("sku_name", plan.Sku)
                .Attr("tags", Tags));

            foreach (var app in plan.Apps)
            {
                var joins = subnets.Contains(plan.Label) && model.UsesPrivateResource(app);
                if (plan.ForFunctions)
                {
                    items.AddRange(FunctionApp(model, app, plan, joins));
                }
                else
                {
                    items.Add(WebApp(model, app, plan, joins));
                }
            }
        }

        if (model.ContainerApps.Any())
        {
            items.AddRange(ContainerApps(model));
        }

        foreach (var site in model.StaticSites)
        {
            items.Add(new HclComment($"Static Web App {site.Name}."));
            var block = Resource("azurerm_static_web_app", site.Label)
                .Attr("name", Prefixed(site.Name))
                .Attr("resource_group_name", ResourceGroup)
                .Str("location", model.StaticSiteRegion)
                .Str("sku_tier", site.P["sku"])
                .Str("sku_size", site.P["sku"])
                .Attr("tags", Tags);
            var settings = Settings(model, site);
            if (settings.Count > 0)
            {
                block.Map("app_settings", settings);
            }

            items.Add(block);
        }

        return items;
    }

    private static void SiteConfig(HclBlock site, SketchModel model, SketchResource app, bool joinsNetwork)
    {
        var frontDoors = model.Sources(app, Kinds.FrontDoor).ToList();
        site.Str("ftps_state", "Disabled").Str("minimum_tls_version", "1.2");
        if (joinsNetwork)
        {
            site.Attr("vnet_route_all_enabled", "true");
        }

        if (frontDoors.Count > 0 || model.Sources(app, Kinds.AppGateway).Any())
        {
            site.Str("ip_restriction_default_action", "Deny");
        }

        GatewayRestrictions(site, model, app);

        for (var index = 0; index < frontDoors.Count; index++)
        {
            var frontDoor = frontDoors[index];
            site.Comment($"Only traffic from the Front Door {frontDoor.Name}.");
            site.Block("ip_restriction", rule => rule
                .Str("name", $"frontdoor-{frontDoor.Name}")
                .Attr("priority", Number(100 + index))
                .Str("action", "Allow")
                .Str("service_tag", "AzureFrontDoor.Backend")
                .Block("headers", headers => headers
                    .Attr("x_azure_fdid", Hcl.List([$"azurerm_cdn_frontdoor_profile.{frontDoor.Label}.resource_guid"]))));
        }
    }

    private static HclBlock WebApp(SketchModel model, SketchResource app, PlanGroup plan, bool joinsNetwork)
    {
        var registry = model.Targets(app, Kinds.ContainerRegistry).FirstOrDefault();
        var block = Resource("azurerm_linux_web_app", app.Label)
            .Attr("name", Prefixed($"{app.Name}-${{local.suffix}}"))
            .Attr("location", Location)
            .Attr("resource_group_name", ResourceGroup)
            .Attr("service_plan_id", $"azurerm_service_plan.{plan.Label}.id")
            .Attr("https_only", "true");
        if (joinsNetwork)
        {
            block.Attr("virtual_network_subnet_id", $"azurerm_subnet.{plan.Label}.id");
        }

        block.Attr("tags", Tags);
        block.Block("identity", identity => AppIdentity(identity, BotIdentities(model, app)));
        block.Block("site_config", site =>
        {
            site.Attr("always_on", Hcl.Bool(app.Flag("alwaysOn")));
            if (registry is not null)
            {
                site.Attr("container_registry_use_managed_identity", "true");
            }

            SiteConfig(site, model, app, joinsNetwork);
            site.Block("application_stack", stack =>
            {
                switch (app.P["runtime"])
                {
                    case "dotnet8": stack.Str("dotnet_version", "8.0"); break;
                    case "dotnet9": stack.Str("dotnet_version", "9.0"); break;
                    case "node20": stack.Str("node_version", "20-lts"); break;
                    case "node22": stack.Str("node_version", "22-lts"); break;
                    case "python312": stack.Str("python_version", "3.12"); break;
                    default:
                        stack.Attr("docker_image_name", Hcl.Literal(app.P["dockerImage"]));
                        stack.Attr("docker_registry_url", registry is null
                            ? Hcl.Str("https://index.docker.io")
                            : Hcl.Str("https://" + Interpolate($"azurerm_container_registry.{registry.Label}.login_server")));
                        break;
                }
            });
        });

        var settings = Settings(model, app);
        if (settings.Count > 0)
        {
            block.Map("app_settings", settings);
        }

        return block;
    }

    private static IEnumerable<HclItem> FunctionApp(SketchModel model, SketchResource app, PlanGroup plan, bool joinsNetwork)
    {
        yield return new HclComment($"Function App {app.Name} and the storage Azure Functions needs for itself.");
        yield return Resource("azurerm_storage_account", $"{app.Label}_functions")
            .Attr("name", CompactName(app.Compact + "fn"))
            .Attr("location", Location)
            .Attr("resource_group_name", ResourceGroup)
            .Str("account_tier", "Standard")
            .Str("account_replication_type", "LRS")
            .Str("min_tls_version", "TLS1_2")
            .Attr("allow_nested_items_to_be_public", "false")
            .Attr("tags", Tags);

        var block = Resource("azurerm_linux_function_app", app.Label)
            .Attr("name", Prefixed($"{app.Name}-${{local.suffix}}"))
            .Attr("location", Location)
            .Attr("resource_group_name", ResourceGroup)
            .Attr("service_plan_id", $"azurerm_service_plan.{plan.Label}.id")
            .Attr("storage_account_name", $"azurerm_storage_account.{app.Label}_functions.name")
            .Attr("storage_account_access_key", $"azurerm_storage_account.{app.Label}_functions.primary_access_key")
            .Attr("https_only", "true");
        if (joinsNetwork)
        {
            block.Attr("virtual_network_subnet_id", $"azurerm_subnet.{plan.Label}.id");
        }

        block.Attr("tags", Tags);
        block.Block("identity", identity => AppIdentity(identity, BotIdentities(model, app)));
        block.Block("site_config", site =>
        {
            if (model.Monitoring)
            {
                site.Attr("application_insights_connection_string", "azurerm_application_insights.main.connection_string");
            }

            SiteConfig(site, model, app, joinsNetwork);
            site.Block("application_stack", stack =>
            {
                switch (app.P["runtime"])
                {
                    case "dotnet8": stack.Str("dotnet_version", "8.0").Attr("use_dotnet_isolated_runtime", "true"); break;
                    case "node20": stack.Str("node_version", "20"); break;
                    default: stack.Str("python_version", "3.11"); break;
                }
            });
        });

        var settings = Settings(model, app).Where(setting => setting.Key != "APPLICATIONINSIGHTS_CONNECTION_STRING").ToList();
        if (settings.Count > 0)
        {
            block.Map("app_settings", settings);
        }

        yield return block;
    }

    private static IEnumerable<HclItem> ContainerApps(SketchModel model)
    {
        var joins = model.ContainerEnvironmentJoinsNetwork;
        yield return new HclComment("Container Apps environment: where every container app runs.");
        var environment = Resource("azurerm_container_app_environment", "main")
            .Attr("name", Prefixed("containers"))
            .Attr("location", Location)
            .Attr("resource_group_name", ResourceGroup);
        if (Logs(model))
        {
            environment.Attr("log_analytics_workspace_id", Workspace(model));
        }

        if (joins)
        {
            environment.Attr("infrastructure_subnet_id", "azurerm_subnet.containers.id");
        }

        environment.Attr("tags", Tags);
        if (joins)
        {
            environment.Block("workload_profile", profile => profile.Str("name", "Consumption").Str("workload_profile_type", "Consumption"));
            environment.Block("lifecycle", lifecycle => lifecycle
                .Comment("Azure names the resource group of the environment's own infrastructure.")
                .Attr("ignore_changes", "[infrastructure_resource_group_name]"));
        }

        yield return environment;

        foreach (var app in model.ContainerApps)
        {
            var registry = model.Targets(app, Kinds.ContainerRegistry).FirstOrDefault();
            yield return new HclComment($"Container App {app.Name} with its own identity (so its roles exist before it starts).");
            yield return Resource("azurerm_user_assigned_identity", app.Label)
                .Attr("name", Prefixed($"{app.Name}-id"))
                .Attr("location", Location)
                .Attr("resource_group_name", ResourceGroup)
                .Attr("tags", Tags);

            var size = app.P["size"];
            var memory = size switch { "0.25" => "0.5Gi", "0.5" => "1Gi", "1" => "2Gi", _ => "4Gi" };
            var image = registry is null
                ? Hcl.Literal(app.P["image"])
                : Hcl.Str(Interpolate($"azurerm_container_registry.{registry.Label}.login_server") + "/" + app.P["image"].Replace("${", "$${", StringComparison.Ordinal));
            var block = Resource("azurerm_container_app", app.Label)
                .Str("name", app.Name)
                .Attr("container_app_environment_id", "azurerm_container_app_environment.main.id")
                .Attr("resource_group_name", ResourceGroup)
                .Str("revision_mode", "Single");
            if (joins)
            {
                block.Str("workload_profile_name", "Consumption");
            }

            block.Attr("tags", Tags);
            block.Block("identity", identity => identity
                .Str("type", "UserAssigned")
                .Attr("identity_ids", Hcl.List([$"azurerm_user_assigned_identity.{app.Label}.id", .. BotIdentities(model, app)])));
            if (registry is not null)
            {
                block.Block("registry", reg => reg
                    .Attr("server", $"azurerm_container_registry.{registry.Label}.login_server")
                    .Attr("identity", $"azurerm_user_assigned_identity.{app.Label}.id"));
            }

            block.Block("ingress", ingress => ingress
                .Attr("external_enabled", Hcl.Bool(app.Flag("external")))
                .Attr("target_port", app.P["port"])
                .Str("transport", "auto")
                .Block("traffic_weight", weight => weight.Attr("latest_revision", "true").Attr("percentage", "100")));
            block.Block("template", template => template
                .Attr("min_replicas", app.P["minReplicas"])
                .Attr("max_replicas", app.P["maxReplicas"])
                .Block("container", container =>
                {
                    container.Str("name", "main").Attr("image", image).Attr("cpu", size).Str("memory", memory);
                    foreach (var (name, value) in Settings(model, app))
                    {
                        container.Block("env", env => env.Str("name", name).Attr("value", value));
                    }
                }));
            if (registry is not null)
            {
                block.Comment("The pull role must exist before the first revision starts.");
                block.Attr("depends_on", $"[azurerm_role_assignment.{app.Label}__{registry.Label}]");
            }

            yield return block;
        }
    }

    // The settings (or environment variables) an app gets for everything it points to.
    private static List<KeyValuePair<string, string>> Settings(SketchModel model, SketchResource app)
    {
        var settings = new List<KeyValuePair<string, string>>();
        if (model.Monitoring && app.Kind != Kinds.StaticWebApp)
        {
            settings.Add(new("APPLICATIONINSIGHTS_CONNECTION_STRING", "azurerm_application_insights.main.connection_string"));
        }

        if (app.Kind == Kinds.ContainerApp)
        {
            settings.Add(new("AZURE_CLIENT_ID", $"azurerm_user_assigned_identity.{app.Label}.client_id"));
        }

        var dotnet = app.P.TryGetValue("runtime", out var runtime) && runtime.StartsWith("dotnet", StringComparison.Ordinal);
        var userId = app.Kind == Kinds.ContainerApp ? $"User Id={Interpolate($"azurerm_user_assigned_identity.{app.Label}.client_id")};" : "";
        foreach (var database in model.Targets(app, Kinds.SqlDatabase))
        {
            var key = dotnet ? $"ConnectionStrings__{database.Name}" : $"SQL_{database.Upper}_CONNECTION_STRING";
            settings.Add(new(key, Hcl.Str(
                $"Server=tcp:{Interpolate("azurerm_mssql_server.main.fully_qualified_domain_name")},1433;" +
                $"Database={Interpolate($"azurerm_mssql_database.{database.Label}.name")};" +
                $"Authentication=Active Directory Managed Identity;{userId}Encrypt=True;")));
        }

        foreach (var server in model.Targets(app, Kinds.Postgres))
        {
            settings.Add(new($"POSTGRES_{server.Upper}_HOST", $"azurerm_postgresql_flexible_server.{server.Label}.fqdn"));
            settings.Add(new($"POSTGRES_{server.Upper}_DATABASE", $"azurerm_postgresql_flexible_server_database.{server.Label}.name"));
            settings.Add(new($"POSTGRES_{server.Upper}_USER", IdentityNameOf(app)));
        }

        foreach (var account in model.Targets(app, Kinds.CosmosDb))
        {
            settings.Add(new($"COSMOS_{account.Upper}_ENDPOINT", $"azurerm_cosmosdb_account.{account.Label}.endpoint"));
            settings.Add(new($"COSMOS_{account.Upper}_DATABASE", $"azurerm_cosmosdb_sql_database.{account.Label}.name"));
        }

        foreach (var storage in model.Targets(app, Kinds.Storage))
        {
            settings.Add(new($"STORAGE_{storage.Upper}_BLOB_ENDPOINT", $"azurerm_storage_account.{storage.Label}.primary_blob_endpoint"));
            foreach (var part in new[] { "queue", "table" }.Where(part => storage.List(part + "s").Count > 0))
            {
                settings.Add(new($"STORAGE_{storage.Upper}_{part.ToUpperInvariant()}_ENDPOINT", $"azurerm_storage_account.{storage.Label}.primary_{part}_endpoint"));
            }
        }

        foreach (var cache in model.Targets(app, Kinds.Redis))
        {
            settings.Add(new($"REDIS_{cache.Upper}_HOST", $"azurerm_redis_cache.{cache.Label}.hostname"));
            settings.Add(new($"REDIS_{cache.Upper}_PORT", $"tostring(azurerm_redis_cache.{cache.Label}.ssl_port)"));
        }

        foreach (var bus in model.Targets(app, Kinds.ServiceBus))
        {
            settings.Add(new($"SERVICEBUS_{bus.Upper}_NAMESPACE", Hcl.Str(Interpolate($"azurerm_servicebus_namespace.{bus.Label}.name") + ".servicebus.windows.net")));
        }

        foreach (var vault in model.Targets(app, Kinds.KeyVault))
        {
            settings.Add(new($"KEYVAULT_{vault.Upper}_URI", $"azurerm_key_vault.{vault.Label}.vault_uri"));
        }

        foreach (var foundry in model.Targets(app, Kinds.AiFoundry))
        {
            settings.Add(new($"FOUNDRY_{foundry.Upper}_ENDPOINT", $"azurerm_cognitive_account.{foundry.Label}.endpoint"));
            settings.Add(new($"FOUNDRY_{foundry.Upper}_PROJECT", $"azurerm_cognitive_account_project.{foundry.Label}.name"));
            if (foundry.P["model"] != "none")
            {
                settings.Add(new($"FOUNDRY_{foundry.Upper}_CHAT_MODEL", $"azurerm_cognitive_deployment.{foundry.Label}_chat.name"));
            }

            if (foundry.P["embeddings"] != "none")
            {
                settings.Add(new($"FOUNDRY_{foundry.Upper}_EMBEDDINGS_MODEL", $"azurerm_cognitive_deployment.{foundry.Label}_embeddings.name"));
            }
        }

        foreach (var speech in model.Targets(app, Kinds.Speech))
        {
            settings.Add(new($"SPEECH_{speech.Upper}_ENDPOINT", $"azurerm_cognitive_account.{speech.Label}.endpoint"));
            settings.Add(new($"SPEECH_{speech.Upper}_REGION", $"azurerm_cognitive_account.{speech.Label}.location"));
            settings.Add(new($"SPEECH_{speech.Upper}_RESOURCE_ID", $"azurerm_cognitive_account.{speech.Label}.id"));
        }

        settings.AddRange(ServiceSettings(model, app));
        if (AppPipeline.BuildsOnArrival(model, app))
        {
            settings.Add(new("SCM_DO_BUILD_DURING_DEPLOYMENT", Hcl.Str("true")));
        }

        foreach (var other in model.ComputeTargets(app))
        {
            settings.Add(new($"{other.Upper}_URL", Hcl.Str("https://" + Interpolate(HostOf(other)))));
        }

        return settings;
    }

    // The model versions Terraform Generator deploys for each choice.
    public static readonly Dictionary<string, string> ModelVersions = new(StringComparer.Ordinal)
    {
        ["gpt-4.1-mini"] = "2025-04-14",
        ["gpt-4.1"] = "2025-04-14",
        ["gpt-4o-mini"] = "2024-07-18",
        ["o4-mini"] = "2025-04-16",
        ["text-embedding-3-small"] = "1",
        ["text-embedding-3-large"] = "1",
    };

    private static IEnumerable<HclItem> Ai(SketchModel model)
    {
        foreach (var foundry in model.Foundries)
        {
            var label = foundry.Label;
            yield return new HclComment($"AI Foundry {foundry.Name}: the AI resource, its project and its model deployments. Microsoft Entra sign-in only.");
            yield return Resource("azurerm_cognitive_account", label)
                .Attr("name", Prefixed($"{foundry.Name}-${{local.suffix}}"))
                .Attr("location", Location)
                .Attr("resource_group_name", ResourceGroup)
                .Str("kind", "AIServices")
                .Str("sku_name", "S0")
                .Attr("custom_subdomain_name", Prefixed($"{foundry.Name}-${{local.suffix}}"))
                .Attr("local_auth_enabled", "false")
                .Attr("project_management_enabled", "true")
                .Attr("public_network_access_enabled", Hcl.Bool(!foundry.IsPrivate))
                .Attr("tags", Tags)
                .Block("identity", identity => identity.Str("type", "SystemAssigned"));
            yield return Resource("azurerm_cognitive_account_project", label)
                .Str("name", $"{foundry.Name}-project")
                .Attr("cognitive_account_id", $"azurerm_cognitive_account.{label}.id")
                .Attr("location", Location)
                .Attr("tags", Tags)
                .Block("identity", identity => identity.Str("type", "SystemAssigned"));

            string? previous = null;
            foreach (var (suffix, model_) in new[] { ("chat", foundry.P["model"]), ("embeddings", foundry.P["embeddings"]) }.Where(item => item.Item2 != "none"))
            {
                var deployment = Resource("azurerm_cognitive_deployment", $"{label}_{suffix}")
                    .Str("name", model_)
                    .Attr("cognitive_account_id", $"azurerm_cognitive_account.{label}.id")
                    .Block("model", definition => definition.Str("format", "OpenAI").Str("name", model_).Str("version", ModelVersions[model_]))
                    .Block("sku", sku => sku.Str("name", "GlobalStandard").Attr("capacity", foundry.P["capacity"]));
                if (previous is not null)
                {
                    deployment.Comment("Azure deploys one model at a time per resource.");
                    deployment.Attr("depends_on", $"[azurerm_cognitive_deployment.{previous}]");
                }

                previous = $"{label}_{suffix}";
                yield return deployment;
            }

            if (foundry.IsPrivate)
            {
                yield return PrivateEndpoint(label, foundry.Name, $"azurerm_cognitive_account.{label}.id", "account", "cognitive", "openai", "aiservices");
            }
        }

        foreach (var speech in model.SpeechServices)
        {
            yield return new HclComment($"Speech {speech.Name}: Microsoft Entra sign-in only (the custom subdomain makes it possible).");
            yield return Resource("azurerm_cognitive_account", speech.Label)
                .Attr("name", Prefixed($"{speech.Name}-${{local.suffix}}"))
                .Attr("location", Location)
                .Attr("resource_group_name", ResourceGroup)
                .Str("kind", "SpeechServices")
                .Str("sku_name", speech.P["sku"])
                .Attr("custom_subdomain_name", Prefixed($"{speech.Name}-${{local.suffix}}"))
                .Attr("local_auth_enabled", "false")
                .Attr("public_network_access_enabled", Hcl.Bool(!speech.IsPrivate))
                .Attr("tags", Tags);
            if (speech.IsPrivate)
            {
                yield return PrivateEndpoint(speech.Label, speech.Name, $"azurerm_cognitive_account.{speech.Label}.id", "account", "cognitive");
            }
        }
    }

    // data.tf ---------------------------------------------------------------------------------------------------------

    private static List<HclItem> Data(SketchModel model)
    {
        var items = new List<HclItem>();
        if (model.Databases.Any())
        {
            items.AddRange(Sql(model));
        }

        foreach (var server in model.PostgresServers)
        {
            items.AddRange(Postgres(model, server));
        }

        foreach (var account in model.CosmosAccounts)
        {
            items.AddRange(Cosmos(model, account));
        }

        foreach (var storage in model.StorageAccounts)
        {
            items.Add(new HclComment($"Storage {storage.Name}."));
            items.Add(Resource("azurerm_storage_account", storage.Label)
                .Attr("name", CompactName(storage.Compact))
                .Attr("location", Location)
                .Attr("resource_group_name", ResourceGroup)
                .Str("account_tier", "Standard")
                .Str("account_replication_type", Geo.AzureReplication(model, storage.P["replication"]))
                .Str("min_tls_version", "TLS1_2")
                .Attr("https_traffic_only_enabled", "true")
                .Attr("allow_nested_items_to_be_public", "false")
                .Attr("public_network_access_enabled", Hcl.Bool(!storage.IsPrivate))
                .Attr("tags", Tags)
                .Block("blob_properties", blobs =>
                {
                    blobs.Attr("versioning_enabled", Hcl.Bool(storage.Flag("versioning")));
                    if (storage.P["softDeleteDays"] != "0")
                    {
                        blobs.Comment("Deleted files and containers can be brought back for this many days.");
                        blobs.Block("delete_retention_policy", policy => policy.Attr("days", storage.P["softDeleteDays"]));
                        blobs.Block("container_delete_retention_policy", policy => policy.Attr("days", storage.P["softDeleteDays"]));
                    }
                })
                .With(account => StorageKey(model, account)));
            items.Add(Resource("azurerm_storage_container", storage.Label)
                .Attr("for_each", $"toset({Hcl.List(storage.List("containers").Select(Hcl.Literal))})")
                .Attr("name", "each.key")
                .Attr("storage_account_id", $"azurerm_storage_account.{storage.Label}.id")
                .Str("container_access_type", "private"));
            foreach (var (part, type) in new[] { ("queues", "azurerm_storage_queue"), ("tables", "azurerm_storage_table") }.Where(item => storage.List(item.Item1).Count > 0))
            {
                items.Add(Resource(type, storage.Label)
                    .Attr("for_each", $"toset({Hcl.List(storage.List(part).Select(Hcl.Literal))})")
                    .Attr("name", "each.key")
                    .Attr("storage_account_id", $"azurerm_storage_account.{storage.Label}.id"));
            }

            if (storage.IsPrivate)
            {
                items.Add(PrivateEndpoint($"{storage.Label}_blob", $"{storage.Name}-blob", $"azurerm_storage_account.{storage.Label}.id", "blob", "blob"));
                foreach (var part in new[] { "queue", "table" }.Where(part => storage.List(part + "s").Count > 0))
                {
                    items.Add(PrivateEndpoint($"{storage.Label}_{part}", $"{storage.Name}-{part}", $"azurerm_storage_account.{storage.Label}.id", part, part));
                }
            }
        }

        foreach (var cache in model.Caches)
        {
            var (family, capacity, sku) = cache.P["sku"] switch
            {
                "Basic_C0" => ("C", 0, "Basic"),
                "Standard_C1" => ("C", 1, "Standard"),
                _ => ("P", 1, "Premium"),
            };
            items.Add(new HclComment($"Redis {cache.Name}: Microsoft Entra sign-in only, no access keys."));
            items.Add(Resource("azurerm_redis_cache", cache.Label)
                .Attr("name", Prefixed($"{cache.Name}-${{local.suffix}}"))
                .Attr("location", Location)
                .Attr("resource_group_name", ResourceGroup)
                .Attr("capacity", Number(capacity))
                .Str("family", family)
                .Str("sku_name", sku)
                .Str("minimum_tls_version", "1.2")
                .Attr("non_ssl_port_enabled", "false")
                .Attr("access_keys_authentication_enabled", "false")
                .Attr("public_network_access_enabled", Hcl.Bool(!cache.IsPrivate))
                .Attr("tags", Tags)
                .Block("redis_configuration", configuration => configuration.Attr("active_directory_authentication_enabled", "true")));
            if (cache.IsPrivate)
            {
                items.Add(PrivateEndpoint(cache.Label, cache.Name, $"azurerm_redis_cache.{cache.Label}.id", "redisCache", "redis"));
            }
        }

        foreach (var bus in model.Buses)
        {
            var premium = bus.P["sku"] == "Premium";
            items.Add(new HclComment($"Service Bus {bus.Name}: Microsoft Entra sign-in only, no connection strings."));
            var block = Resource("azurerm_servicebus_namespace", bus.Label)
                .Attr("name", Prefixed($"{bus.Name}-${{local.suffix}}"))
                .Attr("location", Location)
                .Attr("resource_group_name", ResourceGroup)
                .Str("sku", bus.P["sku"]);
            if (premium)
            {
                block.Attr("capacity", "1").Attr("premium_messaging_partitions", "1");
            }

            items.Add(block
                .Attr("local_auth_enabled", "false")
                .Str("minimum_tls_version", "1.2")
                .Attr("public_network_access_enabled", Hcl.Bool(!bus.IsPrivate))
                .Attr("tags", Tags));
            items.Add(Resource("azurerm_servicebus_queue", bus.Label)
                .Attr("for_each", $"toset({Hcl.List(bus.List("queues").Select(Hcl.Literal))})")
                .Attr("name", "each.key")
                .Attr("namespace_id", $"azurerm_servicebus_namespace.{bus.Label}.id"));
            if (bus.IsPrivate)
            {
                items.Add(PrivateEndpoint(bus.Label, bus.Name, $"azurerm_servicebus_namespace.{bus.Label}.id", "namespace", "servicebus"));
            }
        }

        foreach (var registry in model.Registries)
        {
            items.Add(new HclComment($"Container registry {registry.Name}: apps pull with their identity, no admin user."));
            items.Add(Resource("azurerm_container_registry", registry.Label)
                .Attr("name", CompactName(registry.Compact))
                .Attr("location", Location)
                .Attr("resource_group_name", ResourceGroup)
                .Str("sku", registry.P["sku"])
                .Attr("admin_enabled", "false")
                .Attr("tags", Tags));
        }

        return items;
    }

    private static IEnumerable<HclItem> Sql(SketchModel model)
    {
        yield return new HclComment("Azure SQL: one server for every database, Microsoft Entra sign-in only (no SQL passwords).");
        yield return Resource("azurerm_mssql_server", "main")
            .Attr("name", Prefixed("sql-${local.suffix}"))
            .Attr("location", Location)
            .Attr("resource_group_name", ResourceGroup)
            .Str("version", "12.0")
            .Str("minimum_tls_version", "1.2")
            .Attr("public_network_access_enabled", Hcl.Bool(!model.SqlPrivate))
            .Attr("tags", Tags)
            .Block("azuread_administrator", admin => admin
                .Attr("login_username", "var.admin_name != \"\" ? var.admin_name : \"sql-admins\"")
                .Attr("object_id", AdminObjectId)
                .Attr("azuread_authentication_only", "true"))
            .With(server => SqlKey(model, server));

        foreach (var database in model.Databases)
        {
            var block = Resource("azurerm_mssql_database", database.Label)
                .Str("name", database.Name)
                .Attr("server_id", "azurerm_mssql_server.main.id")
                .Str("sku_name", database.P["sku"])
                .Str("storage_account_type", model.IsProduction || Geo.On(model) ? "Geo" : "Local");
            if (database.P["sku"].StartsWith("GP_S_", StringComparison.Ordinal))
            {
                block.Attr("min_capacity", "0.5").Attr("auto_pause_delay_in_minutes", "60");
            }

            yield return block.Attr("tags", Tags)
                .Block("short_term_retention_policy", policy => policy.Attr("retention_days", database.P["backupDays"]));
        }

        if (model.SqlPrivate)
        {
            yield return PrivateEndpoint("sql", "sql", "azurerm_mssql_server.main.id", "sqlServer", "sql");
        }
        else
        {
            yield return new HclComment("Azure services (the apps) reach the server; nothing else does.");
            yield return Resource("azurerm_mssql_firewall_rule", "azure_services")
                .Str("name", "AllowAzureServices")
                .Attr("server_id", "azurerm_mssql_server.main.id")
                .Str("start_ip_address", "0.0.0.0")
                .Str("end_ip_address", "0.0.0.0");
        }
    }

    private static IEnumerable<HclItem> Postgres(SketchModel model, SketchResource server)
    {
        var label = server.Label;
        yield return new HclComment($"PostgreSQL {server.Name}: Microsoft Entra sign-in only (no passwords).");
        yield return Resource("azurerm_postgresql_flexible_server", label)
            .Attr("name", Prefixed($"{server.Name}-${{local.suffix}}"))
            .Attr("location", Location)
            .Attr("resource_group_name", ResourceGroup)
            .Str("version", server.P["version"])
            .Str("sku_name", server.P["sku"])
            .Attr("storage_mb", Number(int.Parse(server.P["storageGb"], CultureInfo.InvariantCulture) * 1024))
            .Attr("backup_retention_days", server.P["backupDays"])
            .Attr("geo_redundant_backup_enabled", Hcl.Bool(Geo.On(model)))
            .Attr("public_network_access_enabled", Hcl.Bool(!server.IsPrivate))
            .Attr("tags", Tags)
            .Block("authentication", authentication => authentication
                .Attr("active_directory_auth_enabled", "true")
                .Attr("password_auth_enabled", "false")
                .Attr("tenant_id", Tenant))
            .Block("lifecycle", lifecycle => lifecycle
                .Comment("Azure picks the availability zone.")
                .Attr("ignore_changes", "[zone]"));
        yield return Resource("azurerm_postgresql_flexible_server_active_directory_administrator", label)
            .Attr("server_name", $"azurerm_postgresql_flexible_server.{label}.name")
            .Attr("resource_group_name", ResourceGroup)
            .Attr("tenant_id", Tenant)
            .Attr("object_id", AdminObjectId)
            .Attr("principal_name", "var.admin_name")
            .Attr("principal_type", "var.admin_type");
        yield return Resource("azurerm_postgresql_flexible_server_database", label)
            .Str("name", label)
            .Attr("server_id", $"azurerm_postgresql_flexible_server.{label}.id")
            .Str("charset", "UTF8")
            .Str("collation", "en_US.utf8");
        if (server.Flag("vector"))
        {
            yield return new HclComment("Allows the pgvector extension: run CREATE EXTENSION vector; in the database.");
            yield return Resource("azurerm_postgresql_flexible_server_configuration", $"{label}_extensions")
                .Str("name", "azure.extensions")
                .Attr("server_id", $"azurerm_postgresql_flexible_server.{label}.id")
                .Str("value", "VECTOR");
        }

        if (server.IsPrivate)
        {
            yield return PrivateEndpoint(label, server.Name, $"azurerm_postgresql_flexible_server.{label}.id", "postgresqlServer", "postgres");
        }
        else
        {
            yield return new HclComment("Azure services (the apps) reach the server; nothing else does.");
            yield return Resource("azurerm_postgresql_flexible_server_firewall_rule", label)
                .Str("name", "AllowAzureServices")
                .Attr("server_id", $"azurerm_postgresql_flexible_server.{label}.id")
                .Str("start_ip_address", "0.0.0.0")
                .Str("end_ip_address", "0.0.0.0");
        }
    }

    private static IEnumerable<HclItem> Cosmos(SketchModel model, SketchResource account)
    {
        var label = account.Label;
        var serverless = account.P["capacity"] == "serverless";
        yield return new HclComment($"Cosmos DB {account.Name}: Microsoft Entra sign-in only, keys turned off.");
        var block = Resource("azurerm_cosmosdb_account", label)
            .Attr("name", CompactName(account.Compact))
            .Attr("location", Location)
            .Attr("resource_group_name", ResourceGroup)
            .Str("offer_type", "Standard")
            .Str("kind", "GlobalDocumentDB")
            .Attr("local_authentication_disabled", "true")
            .Attr("public_network_access_enabled", Hcl.Bool(!account.IsPrivate))
            .Attr("tags", Tags);
        if (serverless)
        {
            block.Block("capabilities", capability => capability.Str("name", "EnableServerless"));
        }

        if (account.Flag("vectorSearch"))
        {
            block.Block("capabilities", capability => capability.Str("name", "EnableNoSQLVectorSearch"));
        }

        if (Geo.On(model))
        {
            block.Attr("automatic_failover_enabled", "true");
        }

        block.Block("consistency_policy", policy => policy.Str("consistency_level", "Session"))
            .Block("geo_location", geo => geo.Attr("location", Location).Attr("failover_priority", "0"));
        if (Geo.On(model))
        {
            block.Block("geo_location", geo => geo.Str("location", Geo.Second(model)).Attr("failover_priority", "1"));
        }

        block
            .Block("backup", backup =>
            {
                if (account.P["backup"] == "Periodic")
                {
                    backup.Str("type", "Periodic")
                        .Attr("interval_in_minutes", "240")
                        .Attr("retention_in_hours", "8")
                        .Str("storage_redundancy", model.IsProduction || Geo.On(model) ? "Geo" : "Local");
                }
                else
                {
                    backup.Str("type", "Continuous").Str("tier", account.P["backup"]);
                }
            });
        yield return block;
        yield return Resource("azurerm_cosmosdb_sql_database", label)
            .Str("name", label)
            .Attr("resource_group_name", ResourceGroup)
            .Attr("account_name", $"azurerm_cosmosdb_account.{label}.name");
        var container = Resource("azurerm_cosmosdb_sql_container", label)
            .Attr("for_each", $"toset({Hcl.List(account.List("containers").Select(Hcl.Literal))})")
            .Attr("name", "each.key")
            .Attr("resource_group_name", ResourceGroup)
            .Attr("account_name", $"azurerm_cosmosdb_account.{label}.name")
            .Attr("database_name", $"azurerm_cosmosdb_sql_database.{label}.name")
            .Attr("partition_key_paths", Hcl.List([Hcl.Str("/id")]));
        if (!serverless)
        {
            container.Attr("throughput", "400");
        }

        yield return container;
        if (account.IsPrivate)
        {
            yield return PrivateEndpoint(label, account.Name, $"azurerm_cosmosdb_account.{label}.id", "Sql", "cosmos");
        }
    }

    // security.tf -----------------------------------------------------------------------------------------------------

    private static HclBlock RoleAssignment(string label, string scope, string role, string principal, bool servicePrincipal)
    {
        var block = Resource("azurerm_role_assignment", label)
            .Attr("scope", scope)
            .Str("role_definition_name", role)
            .Attr("principal_id", principal);
        if (servicePrincipal)
        {
            block.Str("principal_type", "ServicePrincipal");
        }

        return block;
    }

    private static List<HclItem> Security(SketchModel model)
    {
        var items = new List<HclItem>();
        foreach (var vault in model.Vaults)
        {
            items.Add(new HclComment($"Key Vault {vault.Name}: access by role, never by access policy."));
            items.Add(Resource("azurerm_key_vault", vault.Label)
                .Attr("name", CompactName(vault.Compact))
                .Attr("location", Location)
                .Attr("resource_group_name", ResourceGroup)
                .Attr("tenant_id", Tenant)
                .Str("sku_name", "standard")
                .Attr("rbac_authorization_enabled", "true")
                .Attr("purge_protection_enabled", Hcl.Bool(vault.Flag("purgeProtection")))
                .Attr("soft_delete_retention_days", vault.P["retentionDays"])
                .Attr("public_network_access_enabled", Hcl.Bool(!vault.IsPrivate))
                .Attr("tags", Tags));
            items.Add(new HclComment("Whoever runs Terraform can manage the secrets."));
            items.Add(RoleAssignment($"{vault.Label}__deployer", $"azurerm_key_vault.{vault.Label}.id", "Key Vault Secrets Officer",
                "data.azurerm_client_config.current.object_id", servicePrincipal: false));
            if (vault.IsPrivate)
            {
                items.Add(PrivateEndpoint(vault.Label, vault.Name, $"azurerm_key_vault.{vault.Label}.id", "vault", "vault"));
            }
        }

        foreach (var app in model.Compute)
        {
            var principal = PrincipalOf(app);
            foreach (var vault in model.Targets(app, Kinds.KeyVault))
            {
                items.Add(RoleAssignment($"{app.Label}__{vault.Label}", $"azurerm_key_vault.{vault.Label}.id", "Key Vault Secrets User", principal, servicePrincipal: true));
            }

            foreach (var storage in model.Targets(app, Kinds.Storage))
            {
                items.Add(RoleAssignment($"{app.Label}__{storage.Label}", $"azurerm_storage_account.{storage.Label}.id", "Storage Blob Data Contributor", principal, servicePrincipal: true));
                foreach (var (part, role) in new[] { ("queues", "Storage Queue Data Contributor"), ("tables", "Storage Table Data Contributor") }.Where(item => storage.List(item.Item1).Count > 0))
                {
                    items.Add(RoleAssignment($"{app.Label}__{storage.Label}_{part}", $"azurerm_storage_account.{storage.Label}.id", role, principal, servicePrincipal: true));
                }
            }

            foreach (var bus in model.Targets(app, Kinds.ServiceBus))
            {
                items.Add(RoleAssignment($"{app.Label}__{bus.Label}_send", $"azurerm_servicebus_namespace.{bus.Label}.id", "Azure Service Bus Data Sender", principal, servicePrincipal: true));
                items.Add(RoleAssignment($"{app.Label}__{bus.Label}_receive", $"azurerm_servicebus_namespace.{bus.Label}.id", "Azure Service Bus Data Receiver", principal, servicePrincipal: true));
            }

            foreach (var registry in model.Targets(app, Kinds.ContainerRegistry))
            {
                items.Add(RoleAssignment($"{app.Label}__{registry.Label}", $"azurerm_container_registry.{registry.Label}.id", "AcrPull", principal, servicePrincipal: true));
            }

            foreach (var foundry in model.Targets(app, Kinds.AiFoundry))
            {
                items.Add(RoleAssignment($"{app.Label}__{foundry.Label}_ai", $"azurerm_cognitive_account.{foundry.Label}.id", "Azure AI User", principal, servicePrincipal: true));
                items.Add(RoleAssignment($"{app.Label}__{foundry.Label}_openai", $"azurerm_cognitive_account.{foundry.Label}.id", "Cognitive Services OpenAI User", principal, servicePrincipal: true));
            }

            foreach (var speech in model.Targets(app, Kinds.Speech))
            {
                items.Add(RoleAssignment($"{app.Label}__{speech.Label}", $"azurerm_cognitive_account.{speech.Label}.id", "Cognitive Services Speech User", principal, servicePrincipal: true));
            }

            items.AddRange(ServiceRoles(model, app, principal));

            foreach (var account in model.Targets(app, Kinds.CosmosDb))
            {
                items.Add(new HclComment($"{app.Name} reads and writes the data of {account.Name} (built-in Cosmos DB Data Contributor)."));
                items.Add(Resource("azurerm_cosmosdb_sql_role_assignment", $"{app.Label}__{account.Label}")
                    .Attr("resource_group_name", ResourceGroup)
                    .Attr("account_name", $"azurerm_cosmosdb_account.{account.Label}.name")
                    .Attr("role_definition_id", Hcl.Str(Interpolate($"azurerm_cosmosdb_account.{account.Label}.id") + "/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002"))
                    .Attr("principal_id", principal)
                    .Attr("scope", $"azurerm_cosmosdb_account.{account.Label}.id"));
            }

            foreach (var cache in model.Targets(app, Kinds.Redis))
            {
                items.Add(Resource("azurerm_redis_cache_access_policy_assignment", $"{app.Label}__{cache.Label}")
                    .Str("name", app.Name)
                    .Attr("redis_cache_id", $"azurerm_redis_cache.{cache.Label}.id")
                    .Str("access_policy_name", "Data Contributor")
                    .Attr("object_id", principal)
                    .Str("object_id_alias", app.Name));
            }
        }

        return items;
    }

    // edge.tf ---------------------------------------------------------------------------------------------------------

    private static List<HclItem> Edge(SketchModel model)
    {
        var items = new List<HclItem>();
        foreach (var zone in model.Zones)
        {
            items.Add(new HclComment($"DNS zone {zone.P["zone"]}: point your registrar to its name servers (see the outputs)."));
            items.Add(Resource("azurerm_dns_zone", zone.Label)
                .Attr("name", Hcl.Literal(zone.P["zone"]))
                .Attr("resource_group_name", ResourceGroup)
                .Attr("tags", Tags));
        }

        foreach (var frontDoor in model.FrontDoors)
        {
            var fd = frontDoor.Label;
            items.Add(new HclComment($"Front Door {frontDoor.Name}."));
            items.Add(Resource("azurerm_cdn_frontdoor_profile", fd)
                .Attr("name", Prefixed(frontDoor.Name))
                .Attr("resource_group_name", ResourceGroup)
                .Str("sku_name", frontDoor.P["sku"])
                .Attr("tags", Tags));
            items.Add(Resource("azurerm_cdn_frontdoor_endpoint", fd)
                .Attr("name", Prefixed($"{frontDoor.Name}-${{local.suffix}}"))
                .Attr("cdn_frontdoor_profile_id", $"azurerm_cdn_frontdoor_profile.{fd}.id")
                .Attr("tags", Tags));

            var domains = new List<string>();
            foreach (var zone in model.Sources(frontDoor, Kinds.DnsZone))
            {
                var label = $"{fd}__{zone.Label}";
                var subdomain = frontDoor.P["subdomain"];
                var host = $"{subdomain}.{zone.P["zone"]}";
                domains.Add($"azurerm_cdn_frontdoor_custom_domain.{label}.id");
                items.Add(new HclComment($"{host} with a certificate managed by Front Door."));
                items.Add(Resource("azurerm_cdn_frontdoor_custom_domain", label)
                    .Str("name", host.Replace('.', '-'))
                    .Attr("cdn_frontdoor_profile_id", $"azurerm_cdn_frontdoor_profile.{fd}.id")
                    .Attr("dns_zone_id", $"azurerm_dns_zone.{zone.Label}.id")
                    .Attr("host_name", Hcl.Literal(host))
                    .Block("tls", tls => tls.Str("certificate_type", "ManagedCertificate")));
                items.Add(Resource("azurerm_dns_txt_record", label)
                    .Attr("name", Hcl.Literal($"_dnsauth.{subdomain}"))
                    .Attr("zone_name", $"azurerm_dns_zone.{zone.Label}.name")
                    .Attr("resource_group_name", ResourceGroup)
                    .Attr("ttl", "3600")
                    .Attr("tags", Tags)
                    .Block("record", record => record.Attr("value", $"azurerm_cdn_frontdoor_custom_domain.{label}.validation_token")));
                items.Add(Resource("azurerm_dns_cname_record", label)
                    .Attr("name", Hcl.Literal(subdomain))
                    .Attr("zone_name", $"azurerm_dns_zone.{zone.Label}.name")
                    .Attr("resource_group_name", ResourceGroup)
                    .Attr("ttl", "3600")
                    .Attr("record", $"azurerm_cdn_frontdoor_endpoint.{fd}.host_name")
                    .Attr("tags", Tags));
            }

            foreach (var origin in Kinds.Origins.SelectMany(kind => model.Targets(frontDoor, kind)))
            {
                var label = $"{fd}__{origin.Label}";
                items.Add(new HclComment($"{origin.P["path"]} goes to {origin.Name}."));
                items.Add(Resource("azurerm_cdn_frontdoor_origin_group", label)
                    .Str("name", origin.Name)
                    .Attr("cdn_frontdoor_profile_id", $"azurerm_cdn_frontdoor_profile.{fd}.id")
                    .Attr("session_affinity_enabled", "false")
                    .Block("load_balancing", balancing => balancing
                        .Attr("sample_size", "4")
                        .Attr("successful_samples_required", "3"))
                    .Block("health_probe", probe => probe
                        .Attr("interval_in_seconds", "100")
                        .Str("path", "/")
                        .Str("protocol", "Https")
                        .Str("request_type", "HEAD")));
                items.Add(Resource("azurerm_cdn_frontdoor_origin", label)
                    .Str("name", origin.Name)
                    .Attr("cdn_frontdoor_origin_group_id", $"azurerm_cdn_frontdoor_origin_group.{label}.id")
                    .Attr("enabled", "true")
                    .Attr("host_name", HostOf(origin))
                    .Attr("origin_host_header", HostOf(origin))
                    .Attr("http_port", "80")
                    .Attr("https_port", "443")
                    .Attr("priority", "1")
                    .Attr("weight", "1000")
                    .Attr("certificate_name_check_enabled", "true"));
                var route = Resource("azurerm_cdn_frontdoor_route", label)
                    .Str("name", origin.Name)
                    .Attr("cdn_frontdoor_endpoint_id", $"azurerm_cdn_frontdoor_endpoint.{fd}.id")
                    .Attr("cdn_frontdoor_origin_group_id", $"azurerm_cdn_frontdoor_origin_group.{label}.id")
                    .Attr("cdn_frontdoor_origin_ids", Hcl.List([$"azurerm_cdn_frontdoor_origin.{label}.id"]))
                    .Attr("supported_protocols", Hcl.List([Hcl.Str("Http"), Hcl.Str("Https")]))
                    .Attr("patterns_to_match", Hcl.List([Hcl.Literal(origin.P["path"])]))
                    .Str("forwarding_protocol", "HttpsOnly")
                    .Attr("https_redirect_enabled", "true")
                    .Attr("link_to_default_domain", "true");
                if (domains.Count > 0)
                {
                    route.Attr("cdn_frontdoor_custom_domain_ids", Hcl.List(domains));
                }

                items.Add(route);
            }

            if (Firewall.On(frontDoor))
            {
                items.AddRange(FrontDoorFirewall(model, frontDoor, domains));
            }
        }

        items.AddRange(Gateways(model));
        return items;
    }

    private static IEnumerable<HclItem> FrontDoorFirewall(SketchModel model, SketchResource frontDoor, List<string> domains)
    {
        var fd = frontDoor.Label;
        var premium = frontDoor.P["sku"] == "Premium_AzureFrontDoor";
        yield return new HclComment($"Firewall (WAF) of {frontDoor.Name}: " + (Firewall.Blocks(model, frontDoor) ? "blocks" : "only logs") +
            (premium ? " the attacks Microsoft's rules recognize, and" : "") + $" more than {Firewall.Rate(frontDoor)} requests a minute from one address.");
        var policy = Resource("azurerm_cdn_frontdoor_firewall_policy", fd)
            .Attr("name", Hcl.Str($"${{local.compact_prefix}}{frontDoor.Compact}waf"))
            .Attr("resource_group_name", ResourceGroup)
            .Str("sku_name", frontDoor.P["sku"])
            .Attr("enabled", "true")
            .Str("mode", Firewall.Blocks(model, frontDoor) ? "Prevention" : "Detection")
            .Attr("tags", Tags)
            .Block("custom_rule", rule => rule
                .Str("name", "RequestLimit")
                .Attr("enabled", "true")
                .Attr("priority", "100")
                .Str("type", "RateLimitRule")
                .Str("action", "Block")
                .Attr("rate_limit_duration_in_minutes", "1")
                .Attr("rate_limit_threshold", Firewall.Rate(frontDoor))
                .Comment("Every address: the rule counts per address.")
                .Block("match_condition", condition => condition
                    .Str("match_variable", "RemoteAddr")
                    .Str("operator", "IPMatch")
                    .Attr("negation_condition", "true")
                    .Attr("match_values", "[\"127.0.0.1\"]")));
        if (premium)
        {
            policy.Block("managed_rule", rule => rule.Str("type", "Microsoft_DefaultRuleSet").Str("version", "2.1").Str("action", "Block"))
                .Block("managed_rule", rule => rule.Str("type", "Microsoft_BotManagerRuleSet").Str("version", "1.1").Str("action", "Block"));
        }

        yield return policy;
        yield return Resource("azurerm_cdn_frontdoor_security_policy", fd)
            .Str("name", "firewall")
            .Attr("cdn_frontdoor_profile_id", $"azurerm_cdn_frontdoor_profile.{fd}.id")
            .Block("security_policies", policies => policies.Block("firewall", firewall => firewall
                .Attr("cdn_frontdoor_firewall_policy_id", $"azurerm_cdn_frontdoor_firewall_policy.{fd}.id")
                .Block("association", association =>
                {
                    association.Block("domain", domain => domain.Attr("cdn_frontdoor_domain_id", $"azurerm_cdn_frontdoor_endpoint.{fd}.id"));
                    foreach (var custom in domains)
                    {
                        association.Block("domain", domain => domain.Attr("cdn_frontdoor_domain_id", custom));
                    }

                    association.Attr("patterns_to_match", "[\"/*\"]");
                })));
    }

    // monitoring.tf ---------------------------------------------------------------------------------------------------

    // Every resource that keeps logs sends them, with its metrics, to the Log Analytics workspace.
    public static IEnumerable<(string Label, string ResourceId)> DiagnosticTargets(SketchModel model)
    {
        foreach (var app in model.Apps.Concat(model.Functions))
        {
            yield return (app.Label, $"{Ref(app)}.id");
        }

        foreach (var database in model.Databases)
        {
            yield return (database.Label, $"azurerm_mssql_database.{database.Label}.id");
        }

        foreach (var server in model.PostgresServers)
        {
            yield return (server.Label, $"azurerm_postgresql_flexible_server.{server.Label}.id");
        }

        foreach (var account in model.CosmosAccounts)
        {
            yield return (account.Label, $"azurerm_cosmosdb_account.{account.Label}.id");
        }

        foreach (var cache in model.Caches)
        {
            yield return (cache.Label, $"azurerm_redis_cache.{cache.Label}.id");
        }

        foreach (var bus in model.Buses)
        {
            yield return (bus.Label, $"azurerm_servicebus_namespace.{bus.Label}.id");
        }

        foreach (var vault in model.Vaults)
        {
            yield return (vault.Label, $"azurerm_key_vault.{vault.Label}.id");
        }

        foreach (var registry in model.Registries)
        {
            yield return (registry.Label, $"azurerm_container_registry.{registry.Label}.id");
        }

        foreach (var frontDoor in model.FrontDoors)
        {
            yield return (frontDoor.Label, $"azurerm_cdn_frontdoor_profile.{frontDoor.Label}.id");
        }

        foreach (var gateway in model.AppGateways)
        {
            yield return (gateway.Label, $"azurerm_application_gateway.{gateway.Label}.id");
        }

        foreach (var account in model.Foundries.Concat(model.SpeechServices))
        {
            yield return (account.Label, $"azurerm_cognitive_account.{account.Label}.id");
        }

        foreach (var target in ServiceDiagnostics(model))
        {
            yield return target;
        }
    }

    private static List<HclItem> Diagnostics(SketchModel model)
    {
        var items = new List<HclItem>();
        if (Logs(model))
        {
            foreach (var (label, resourceId) in DiagnosticTargets(model))
            {
                items.Add(Resource("azurerm_monitor_diagnostic_setting", label)
                    .Str("name", "to-log-analytics")
                    .Attr("target_resource_id", resourceId)
                    .Attr("log_analytics_workspace_id", Workspace(model))
                    .Block("enabled_log", log => log.Str("category_group", "allLogs"))
                    .Block("enabled_metric", metric => metric.Str("category", "AllMetrics")));
            }

            if (items.Count > 0)
            {
                items.Insert(0, new HclComment("Logs and metrics of every resource go to the Log Analytics workspace."));
            }
        }

        var alerts = AlertTargets(model).ToList();
        if (model.Alerts && alerts.Count > 0)
        {
            items.Add(new HclComment("Alerts: an email when an app returns server errors or a database is overloaded."));
            items.Add(Resource("azurerm_monitor_action_group", "main")
                .Attr("name", Prefixed("alerts"))
                .Attr("resource_group_name", ResourceGroup)
                .Str("short_name", "alerts")
                .Attr("tags", Tags)
                .Block("email_receiver", email => email
                    .Str("name", "email")
                    .Attr("email_address", "var.alert_email")
                    .Attr("use_common_alert_schema", "true")));
            foreach (var alert in alerts)
            {
                items.Add(Resource("azurerm_monitor_metric_alert", alert.Label)
                    .Attr("name", Prefixed(alert.Label.Replace('_', '-')))
                    .Attr("resource_group_name", ResourceGroup)
                    .Attr("scopes", $"[{alert.ResourceId}]")
                    .Str("description", alert.Description)
                    .Attr("severity", "2")
                    .Str("frequency", "PT5M")
                    .Str("window_size", "PT15M")
                    .Attr("tags", Tags)
                    .Block("criteria", criteria =>
                    {
                        criteria.Str("metric_namespace", alert.Namespace)
                            .Str("metric_name", alert.Metric)
                            .Str("aggregation", alert.Aggregation)
                            .Str("operator", "GreaterThan")
                            .Attr("threshold", alert.Threshold);
                        if (alert.Dimension is { } dimension)
                        {
                            criteria.Block("dimension", filter => filter
                                .Str("name", dimension.Name)
                                .Str("operator", "Include")
                                .Attr("values", Hcl.List([Hcl.Str(dimension.Value)])));
                        }
                    })
                    .Block("action", action => action.Attr("action_group_id", "azurerm_monitor_action_group.main.id")));
            }
        }

        return items;
    }

    public sealed record Alert(string Label, string ResourceId, string Namespace, string Metric, string Aggregation, string Threshold, string Description, (string Name, string Value)? Dimension = null);

    // What the alerts watch: server errors on apps and CPU on databases.
    public static IEnumerable<Alert> AlertTargets(SketchModel model)
    {
        foreach (var app in model.Apps.Concat(model.Functions))
        {
            yield return new($"{app.Label}_errors", $"{Ref(app)}.id", "Microsoft.Web/sites", "Http5xx", "Total", "10", $"{app.Name} returned more than 10 server errors in 15 minutes.");
        }

        foreach (var app in model.ContainerApps)
        {
            yield return new($"{app.Label}_errors", $"{Ref(app)}.id", "Microsoft.App/containerApps", "Requests", "Total", "10",
                $"{app.Name} returned more than 10 server errors in 15 minutes.", ("statusCodeCategory", "5xx"));
        }

        foreach (var database in model.Databases)
        {
            yield return new($"{database.Label}_cpu", $"azurerm_mssql_database.{database.Label}.id", "Microsoft.Sql/servers/databases", "cpu_percent", "Average", "90", $"{database.Name} used more than 90% CPU for 15 minutes.");
        }

        foreach (var server in model.PostgresServers)
        {
            yield return new($"{server.Label}_cpu", $"azurerm_postgresql_flexible_server.{server.Label}.id", "Microsoft.DBforPostgreSQL/flexibleServers", "cpu_percent", "Average", "90", $"{server.Name} used more than 90% CPU for 15 minutes.");
        }
    }

    // outputs.tf ------------------------------------------------------------------------------------------------------

    private static HclBlock Output(string name, string value, string description) =>
        new HclBlock("output", name).Str("description", description).Attr("value", value);

    private static List<HclItem> Outputs(SketchModel model)
    {
        var items = new List<HclItem> { Output("resource_group_name", "local.resource_group_name", "The resource group of the environment.") };
        foreach (var app in model.Compute.Concat(model.StaticSites))
        {
            if (app.Kind != Kinds.StaticWebApp)
            {
                items.Add(Output($"{app.Label}_identity", IdentityNameOf(app), $"Name of the identity of {app.Name} (the user to create in its databases)."));
            }

            items.Add(Output($"{app.Label}_url", Hcl.Str("https://" + Interpolate(HostOf(app))), $"Direct address of {app.Name}."));
            items.Add(Output($"{app.Label}_name", $"{Ref(app)}.name", $"The name of {app.Name} in Azure."));
        }

        if (model.Databases.Any())
        {
            items.Add(Output("sql_server_fqdn", "azurerm_mssql_server.main.fully_qualified_domain_name", "Address of the SQL server."));
        }

        foreach (var server in model.PostgresServers)
        {
            items.Add(Output($"{server.Label}_fqdn", $"azurerm_postgresql_flexible_server.{server.Label}.fqdn", $"Address of the PostgreSQL server {server.Name}."));
        }

        foreach (var registry in model.Registries)
        {
            items.Add(Output($"{registry.Label}_login_server", $"azurerm_container_registry.{registry.Label}.login_server", $"Where to push images for {registry.Name} (docker push <this>/<image>)."));
            items.Add(Output($"{registry.Label}_registry_name", $"azurerm_container_registry.{registry.Label}.name", $"The name of the registry {registry.Name}."));
        }

        foreach (var storage in model.StorageAccounts)
        {
            items.Add(Output($"{storage.Label}_blob_endpoint", $"azurerm_storage_account.{storage.Label}.primary_blob_endpoint", $"Blob address of {storage.Name}."));
        }

        foreach (var vault in model.Vaults)
        {
            items.Add(Output($"{vault.Label}_uri", $"azurerm_key_vault.{vault.Label}.vault_uri", $"Address of the Key Vault {vault.Name}."));
        }

        foreach (var frontDoor in model.FrontDoors)
        {
            items.Add(Output($"{frontDoor.Label}_endpoint", Hcl.Str("https://" + Interpolate($"azurerm_cdn_frontdoor_endpoint.{frontDoor.Label}.host_name")), $"Front Door address of {frontDoor.Name}."));
        }

        foreach (var gateway in model.AppGateways)
        {
            items.Add(Output($"{gateway.Label}_address", $"azurerm_public_ip.{gateway.Label}.ip_address", $"The public address of the Application Gateway {gateway.Name}."));
        }

        foreach (var zone in model.Zones)
        {
            items.Add(Output($"{zone.Label}_name_servers", $"azurerm_dns_zone.{zone.Label}.name_servers", $"Name servers to set at the registrar of {zone.P["zone"]}."));
        }

        foreach (var account in model.Foundries.Concat(model.SpeechServices))
        {
            items.Add(Output($"{account.Label}_endpoint", $"azurerm_cognitive_account.{account.Label}.endpoint", $"Endpoint of {account.Name}."));
        }

        items.AddRange(ServiceOutputs(model));

        foreach (var resource in model.Generics)
        {
            items.Add(Output($"{resource.Label}_id", $"{Kinds.TypeOf(resource.Kind)}.{resource.Label}.id", $"The id of {resource.Name}."));
        }

        return items;
    }

    private static string TfvarsExample(SketchModel model)
    {
        var lines = new List<string>
        {
            "# deploy.ps1 creates terraform.tfvars from your Azure sign-in. To write it yourself, copy this file to terraform.tfvars.",
            "# terraform.tfvars is yours: do not commit it.",
            "subscription_id = \"00000000-0000-0000-0000-000000000000\"",
        };
        if (NeedsAdmin(model))
        {
            lines.Add("# Database administrator: deploy.ps1 uses you. For an Entra group instead:");
            lines.Add("# admin_object_id = \"00000000-0000-0000-0000-000000000000\"");
            lines.Add("# admin_name      = \"Database admins\"");
            lines.Add("# admin_type      = \"Group\"");
        }

        return string.Join('\n', lines) + "\n";
    }
}
