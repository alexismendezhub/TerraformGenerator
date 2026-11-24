using System.Text.RegularExpressions;

namespace TerraformGenerator.Domain;

// Errors stop the generation; warnings and notes explain what is risky or what was added automatically.
public static class SketchRules
{
    public static List<SketchMessage> Check(Sketch sketch, IResourceSchemas? schemas = null)
    {
        var messages = new List<SketchMessage>();
        CheckSettings(sketch.Settings, messages);
        CheckNodes(sketch, messages);
        CheckEdges(sketch, messages);
        if (messages.Any(message => message.Level == MessageLevel.Error))
        {
            return messages;
        }

        var model = new SketchModel(sketch);
        if (model.IsAzure)
        {
            CheckDesign(model, messages);
            messages.AddRange(AzureServices.Check(model));
        }
        else
        {
            CloudRules.Check(model, messages);
        }

        messages.AddRange(Firewall.Check(model));
        messages.AddRange(Team.Check(model));
        messages.AddRange(Protection.Check(model));
        messages.AddRange(AppPipeline.Check(model));
        messages.AddRange(Guardrails.Check(model));
        messages.AddRange(Geo.Check(model));
        messages.AddRange(OwnKeys.Check(model));
        CheckGeneric(model, schemas, messages);
        return messages;
    }

    private static void CheckGeneric(SketchModel model, IResourceSchemas? schemas, List<SketchMessage> messages)
    {
        if (!model.Generics.Any())
        {
            return;
        }

        if (schemas is null)
        {
            Note(messages, "The fields of the provider boxes were not checked (the provider lists are not loaded).");
        }
        else
        {
            messages.AddRange(GenericGenerator.Check(model, schemas));
        }

        if (model.Bicep)
        {
            Warn(messages, "Boxes from the full Azure list are written in Terraform only; the Bicep leaves them out.");
        }
    }

    private static void Error(List<SketchMessage> messages, string text, string? nodeId = null, string? edgeId = null) =>
        messages.Add(new(MessageLevel.Error, text, nodeId, edgeId));

    private static void Warn(List<SketchMessage> messages, string text, string? nodeId = null) =>
        messages.Add(new(MessageLevel.Warning, text, nodeId));

    private static void Note(List<SketchMessage> messages, string text, string? nodeId = null) =>
        messages.Add(new(MessageLevel.Info, text, nodeId));

    public static bool IsValid(PropertyDefinition property, string value) => property.Type switch
    {
        PropertyType.Select => property.Options.Any(option => option.Value == value),
        PropertyType.Toggle => value is "true" or "false",
        _ => property.Pattern is null || Regex.IsMatch(value, property.Pattern),
    };

    private static void CheckSettings(SketchSettings settings, List<SketchMessage> messages)
    {
        if (!Regex.IsMatch(settings.Name, Catalog.ProjectNamePattern))
        {
            Error(messages, "Project name: 2 to 12 lowercase letters or digits, starting with a letter.");
        }

        if (Catalog.Instance.Environments.All(option => option.Value != settings.Environment))
        {
            Error(messages, "Choose an environment from the list.");
        }

        if (Platforms.All.All(platform => platform.Id != settings.Platform))
        {
            Error(messages, "Choose Azure, AWS or Google Cloud.");
        }
        else if (Platforms.Get(settings.Platform).Regions.All(option => option.Value != settings.Region))
        {
            Error(messages, "Choose a region from the list.");
        }

        if (settings.Platform == Platforms.Gcp && Catalog.Setting(settings, SettingKeys.GcpProject).Length == 0)
        {
            Error(messages, "Google Cloud needs the project id (in the project settings).");
        }

        foreach (var (key, value) in settings.Options)
        {
            var property = Catalog.Instance.Settings.FirstOrDefault(item => item.Key == key);
            if (property is null)
            {
                Error(messages, $"Unknown project setting \"{key}\".");
            }
            else if (!IsValid(property, value) && Catalog.SettingApplies(settings, key))
            {
                Error(messages, $"Project: \"{property.Label}\" is not valid. {property.Help}".TrimEnd());
            }
        }
    }

    private static void CheckNodes(Sketch sketch, List<SketchMessage> messages)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in sketch.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.Id) || !ids.Add(node.Id))
            {
                Error(messages, "Every box needs its own id.", node.Id);
            }

            if (Kinds.IsGeneric(node.Kind))
            {
                // Provider boxes: the name here; their fields against the provider's list in CheckGeneric.
                if (!Regex.IsMatch(node.Name, Catalog.NamePattern))
                {
                    Error(messages, $"\"{node.Name}\": the name needs 2 to 20 lowercase letters, digits or hyphens, starting with a letter.", node.Id);
                }
                else if (!names.Add(node.Name.Replace('-', '_')))
                {
                    Error(messages, $"Two boxes are named \"{node.Name}\". Each name must be unique.", node.Id);
                }

                continue;
            }

            var kind = Catalog.Find(node.Kind);
            if (kind is null)
            {
                Error(messages, $"Unknown kind of box: {node.Kind}.", node.Id);
                continue;
            }

            if (kind.Platform != sketch.Settings.Platform)
            {
                Error(messages, $"{node.Name}: the {kind.Label} box is for {Platforms.Get(kind.Platform).Label}, not {Platforms.Get(sketch.Settings.Platform).Label}.", node.Id);
            }

            if (!Regex.IsMatch(node.Name, Catalog.NamePattern))
            {
                Error(messages, $"{kind.Label} \"{node.Name}\": the name needs 2 to 20 lowercase letters, digits or hyphens, starting with a letter.", node.Id);
            }
            else if (!names.Add(node.Name.Replace('-', '_')))
            {
                Error(messages, $"Two boxes are named \"{node.Name}\". Each name must be unique.", node.Id);
            }

            foreach (var (key, value) in node.Props)
            {
                var (name, environment) = Environments.Split(key);
                var property = kind.Properties.FirstOrDefault(item => item.Key == name);
                if (property is null || (environment is not null && !Environments.IsEnvironment(environment)))
                {
                    Error(messages, $"{node.Name}: unknown setting \"{key}\".", node.Id);
                }
                else if (!IsValid(property, value))
                {
                    Error(messages, $"{node.Name}: \"{property.Label}\"{(environment is null ? "" : $" in {environment}")} is not valid. {property.Help}".TrimEnd(), node.Id);
                }
            }
        }
    }

    private static void CheckEdges(Sketch sketch, List<SketchMessage> messages)
    {
        var nodes = sketch.Nodes.GroupBy(node => node.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var pairs = new HashSet<(string, string)>();
        foreach (var edge in sketch.Edges)
        {
            if (!nodes.TryGetValue(edge.From, out var from) || !nodes.TryGetValue(edge.To, out var to))
            {
                Error(messages, "An arrow points to a box that no longer exists.", edgeId: edge.Id);
                continue;
            }

            if (edge.From == edge.To)
            {
                Error(messages, $"{from.Name} cannot point to itself.", edgeId: edge.Id);
                continue;
            }

            // An arrow from a provider box shows that one of its fields points to the other box.
            if (Kinds.IsGeneric(from.Kind))
            {
                var linked = from.Props.Values.Any(value => GenericGenerator.ParseReference(value)?.NodeId == to.Id);
                if (!linked)
                {
                    Error(messages, $"{from.Name} -> {to.Name}: choose which field of {from.Name} points to {to.Name} (in its properties).", edgeId: edge.Id);
                }

                if (!pairs.Add((edge.From, edge.To)))
                {
                    Error(messages, $"{from.Name} -> {to.Name} is drawn twice.", edgeId: edge.Id);
                }

                continue;
            }

            if (Catalog.FindConnection(from.Kind, to.Kind) is null)
            {
                var fromLabel = Catalog.Find(from.Kind)?.Label ?? from.Kind;
                var toLabel = Catalog.Find(to.Kind)?.Label ?? to.Kind;
                Error(messages, $"A {fromLabel} cannot point to a {toLabel} ({from.Name} -> {to.Name}).", edgeId: edge.Id);
                continue;
            }

            if (!pairs.Add((edge.From, edge.To)))
            {
                Error(messages, $"{from.Name} -> {to.Name} is drawn twice.", edgeId: edge.Id);
            }
        }

        CheckAppCycles(sketch, nodes, messages);
    }

    // App A calling B and B calling A would make each wait for the other's URL.
    private static void CheckAppCycles(Sketch sketch, Dictionary<string, SketchNode> nodes, List<SketchMessage> messages)
    {
        var calls = sketch.Edges
            .Where(edge => nodes.TryGetValue(edge.From, out var from) && (Kinds.IsCompute(from.Kind) || CloudKinds.IsCompute(from.Kind))
                && nodes.TryGetValue(edge.To, out var to) && (Kinds.IsCompute(to.Kind) || CloudKinds.IsCompute(to.Kind)))
            .GroupBy(edge => edge.From, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.To).ToList(), StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal);

        bool Visit(string id)
        {
            state[id] = 1;
            foreach (var next in calls.GetValueOrDefault(id, []))
            {
                var seen = state.GetValueOrDefault(next);
                if (seen == 1 || (seen == 0 && Visit(next)))
                {
                    return true;
                }
            }

            state[id] = 2;
            return false;
        }

        foreach (var id in calls.Keys)
        {
            if (state.GetValueOrDefault(id) == 0 && Visit(id))
            {
                Error(messages, "Apps call each other in a circle. Remove one of the arrows between apps: each app gets the other's URL, so one must exist first.", id);
                return;
            }
        }
    }

    private static void CheckDesign(SketchModel model, List<SketchMessage> messages)
    {
        CheckSettingsDesign(model, messages);
        CheckData(model, messages);
        CheckApps(model, messages);
        CheckEdge(model, messages);

        if (model.NeedsNetwork)
        {
            Note(messages, "A virtual network, private endpoints and private DNS zones were added for the private resources.");
        }

        if (model.IsProduction && !model.Settings.Monitoring && (model.Compute.Any() || model.StaticSites.Any()))
        {
            Warn(messages, "Production without monitoring: turn on Application Insights in the settings.");
        }

        if (model.Databases.Any() || model.PostgresServers.Any(server => model.ComputeSources(server).Any()))
        {
            Note(messages, "After the first apply, give each app access inside its database (see the README); Terraform cannot create database users.");
        }
    }

    private static void CheckSettingsDesign(SketchModel model, List<SketchMessage> messages)
    {
        if (model.GitHub && !model.RemoteState && model.Terraform)
        {
            Error(messages, "Deploying from GitHub needs the Terraform state in Azure: choose \"In an Azure storage account\".");
        }

        void Needs(string key, string text)
        {
            if (Catalog.SettingApplies(model.Settings, key) && model.Setting(key).Length == 0)
            {
                Error(messages, text);
            }
        }

        if (model.NeedsNetwork)
        {
            Needs(SettingKeys.NetworkName, "Using an existing network needs its name.");
            Needs(SettingKeys.NetworkResourceGroup, "Using an existing network needs its resource group.");
            Needs(SettingKeys.PrivateDnsResourceGroup, "Using central private DNS zones needs their resource group.");
        }

        Needs(SettingKeys.LogWorkspaceId, "Using an existing Log Analytics workspace needs its resource id.");
        Needs(SettingKeys.AlertEmail, "Alerts need the email that receives them.");
        if (TerraformGenerator.NeedsAdmin(model))
        {
            Needs(SettingKeys.DatabaseAdminGroupName, "A group as database administrator needs the group name.");
            Needs(SettingKeys.DatabaseAdminGroupId, "A group as database administrator needs the group object id.");
        }

        if (model.IsProduction && !model.Alerts && (model.Compute.Any() || model.Databases.Any() || model.PostgresServers.Any()))
        {
            Warn(messages, "Production without alerts: turn them on so someone hears when an app fails.");
        }

        if (model.ExistingNetwork && model.NeedsNetwork)
        {
            Note(messages, $"Subnets are added to {model.NetworkName} inside {model.NetworkRange}; that range must be free in it.");
        }

        if (model.ExistingResourceGroup)
        {
            Note(messages, $"The resource group {model.ResourceGroupName} must already exist; the code uses it and never deletes it.");
        }

        var email = model.Setting(SettingKeys.BudgetEmail);
        if (model.Budget > 0 && email.Length == 0)
        {
            Error(messages, "A budget alert needs the email that receives it.");
        }

        if (model.Budget == 0 && model.IsProduction)
        {
            Warn(messages, "Production without a budget alert: choose a monthly budget so a surprise bill is caught early.");
        }

        if (!model.RemoteState && model.Terraform && model.IsProduction)
        {
            Warn(messages, "Production with the Terraform state on one computer: if it is lost, Terraform no longer knows what it created.");
        }
    }

    private static void Unused(SketchModel model, SketchResource resource, string what, List<SketchMessage> messages, bool warning = true)
    {
        if (!model.ComputeSources(resource).Any())
        {
            var text = $"{resource.Name}: no app uses {what}. Draw an arrow from an app to it.";
            if (warning)
            {
                Warn(messages, text, resource.Node.Id);
            }
            else
            {
                Note(messages, text, resource.Node.Id);
            }
        }
    }

    private static void CheckData(SketchModel model, List<SketchMessage> messages)
    {
        foreach (var database in model.Databases)
        {
            if (database.P["sku"] == "Basic" && database.P["backupDays"] != "7")
            {
                Error(messages, $"{database.Name}: the Basic size keeps backups for 7 days at most. Choose 7 days or a larger size.", database.Node.Id);
            }

            Unused(model, database, "this database", messages);
            if (model.IsProduction && database.P["backupDays"] == "7")
            {
                Warn(messages, $"{database.Name}: production usually keeps at least 14 days of backups.", database.Node.Id);
            }
        }

        if (model.SqlPrivate && model.Databases.Any(database => !database.IsPrivate))
        {
            Warn(messages, "All databases share one SQL server, so it is private for all of them.");
        }

        foreach (var server in model.PostgresServers)
        {
            Unused(model, server, "this database", messages);
            if (model.IsProduction && server.P["backupDays"] == "7")
            {
                Warn(messages, $"{server.Name}: production usually keeps at least 14 days of backups.", server.Node.Id);
            }

            if (model.IsProduction && server.P["sku"].StartsWith("B_", StringComparison.Ordinal))
            {
                Warn(messages, $"{server.Name}: Burstable sizes suit development; production usually uses General Purpose.", server.Node.Id);
            }
        }

        foreach (var account in model.CosmosAccounts)
        {
            Unused(model, account, "this Cosmos DB", messages);
            if (model.IsProduction && account.P["backup"] == "Periodic")
            {
                Note(messages, $"{account.Name}: continuous backup lets you restore any point in time; periodic keeps only the last copies.", account.Node.Id);
            }
        }

        foreach (var storage in model.StorageAccounts)
        {
            Unused(model, storage, "this storage", messages);
            if (model.IsProduction && storage.P["replication"] == "LRS")
            {
                Warn(messages, $"{storage.Name}: in production, ZRS or GRS keeps the files if a datacenter fails.", storage.Node.Id);
            }
        }

        foreach (var cache in model.Caches)
        {
            Unused(model, cache, "this cache", messages);
            if (model.IsProduction && cache.P["sku"] == "Basic_C0")
            {
                Warn(messages, $"{cache.Name}: Basic has no copy and no SLA; production usually uses Standard or Premium.", cache.Node.Id);
            }
        }

        foreach (var bus in model.Buses)
        {
            Unused(model, bus, "this Service Bus", messages);
            if (bus.IsPrivate && bus.P["sku"] != "Premium")
            {
                Error(messages, $"{bus.Name}: only the Premium tier of Service Bus can be private. Choose Premium or turn off Private only.", bus.Node.Id);
            }
        }

        foreach (var vault in model.Vaults)
        {
            Unused(model, vault, "this Key Vault", messages, warning: false);
            if (model.IsProduction && !vault.Flag("purgeProtection"))
            {
                Warn(messages, $"{vault.Name}: production usually turns on purge protection.", vault.Node.Id);
            }
        }

        foreach (var foundry in model.Foundries)
        {
            Unused(model, foundry, "these AI models", messages);
            if (foundry.P["model"] == "none" && foundry.P["embeddings"] == "none")
            {
                Error(messages, $"{foundry.Name}: choose a chat model or an embeddings model.", foundry.Node.Id);
            }

            Note(messages, $"{foundry.Name}: the models are Global Standard deployments; the subscription needs quota for them in {model.Settings.Region} (Azure portal > Foundry > Quota).", foundry.Node.Id);
        }

        foreach (var registry in model.Registries.Where(registry => !model.ComputeSources(registry).Any()))
        {
            Note(messages, $"{registry.Name}: no app pulls from this registry yet.", registry.Node.Id);
        }
    }

    private static void CheckApps(SketchModel model, List<SketchMessage> messages)
    {
        foreach (var app in model.Apps)
        {
            if (app.P["runtime"] != "docker" && app.Node.Props.ContainsKey("dockerImage"))
            {
                Note(messages, $"{app.Name}: the Docker image is used only with the Docker image runtime.", app.Node.Id);
            }

            if (model.Targets(app, Kinds.ContainerRegistry).Any() && app.P["runtime"] != "docker")
            {
                Error(messages, $"{app.Name}: only the Docker image runtime runs from a registry. Choose Docker image or remove the arrow.", app.Node.Id);
            }

            if (model.Targets(app, Kinds.ContainerRegistry).Count() > 1)
            {
                Error(messages, $"{app.Name}: an app runs from one registry. Remove the other arrow.", app.Node.Id);
            }

            if (model.IsProduction && app.P["sku"].StartsWith('B'))
            {
                Warn(messages, $"{app.Name}: Basic plans have no deployment slots or autoscale; production usually uses S1 or Premium.", app.Node.Id);
            }
        }

        foreach (var app in model.ContainerApps)
        {
            if (model.Targets(app, Kinds.ContainerRegistry).Count() > 1)
            {
                Error(messages, $"{app.Name}: a container comes from one registry. Remove the other arrow.", app.Node.Id);
            }

            if (int.Parse(app.P["minReplicas"], System.Globalization.CultureInfo.InvariantCulture) > int.Parse(app.P["maxReplicas"], System.Globalization.CultureInfo.InvariantCulture))
            {
                Error(messages, $"{app.Name}: \"Always running\" is more than \"Most copies\".", app.Node.Id);
            }

            if (model.Sources(app, Kinds.FrontDoor).Any())
            {
                if (!app.Flag("external"))
                {
                    Error(messages, $"{app.Name}: Front Door reaches only containers that are reachable from the internet. Turn that on or remove the arrow.", app.Node.Id);
                }
                else
                {
                    Note(messages, $"{app.Name}: it can still be reached directly at its own address; Container Apps cannot limit traffic to Front Door by itself.", app.Node.Id);
                }
            }
        }

        foreach (var function in model.Functions)
        {
            if (function.P["plan"] == "Y1" && model.UsesPrivateResource(function))
            {
                Error(messages, $"{function.Name}: the Consumption plan cannot join a private network. Choose Elastic Premium EP1 or turn off Private only where it points.", function.Node.Id);
            }
        }

        foreach (var site in model.StaticSites.Where(_ => model.StaticSiteRegion != model.Settings.Region))
        {
            Note(messages, $"{site.Name}: Static Web Apps are not offered in {model.Settings.Region}; it runs in {model.StaticSiteRegion}.", site.Node.Id);
        }

        foreach (var app in model.Compute.Where(model.UsesPrivateResource))
        {
            Note(messages, $"{app.Name}: joined to the private network to reach its private resources.", app.Node.Id);
        }
    }

    private static void CheckEdge(SketchModel model, List<SketchMessage> messages)
    {
        foreach (var frontDoor in model.FrontDoors)
        {
            var origins = Kinds.Origins.SelectMany(kind => model.Targets(frontDoor, kind)).ToList();
            if (origins.Count == 0)
            {
                Warn(messages, $"{frontDoor.Name}: Front Door serves nothing. Draw an arrow from it to an app or site.", frontDoor.Node.Id);
            }

            foreach (var clash in origins.GroupBy(origin => origin.P["path"]).Where(group => group.Count() > 1))
            {
                Error(messages, $"{frontDoor.Name}: {string.Join(" and ", clash.Select(origin => origin.Name))} both use the path {clash.Key}. Give each its own Front Door path.", frontDoor.Node.Id);
            }

            var hosts = model.Sources(frontDoor, Kinds.DnsZone).Select(zone => $"{frontDoor.P["subdomain"]}.{zone.P["zone"]}").ToList();
            foreach (var duplicate in hosts.GroupBy(host => host).Where(group => group.Count() > 1))
            {
                Error(messages, $"{frontDoor.Name}: the domain {duplicate.Key} is used twice.", frontDoor.Node.Id);
            }
        }

        foreach (var duplicate in model.Zones.GroupBy(zone => zone.P["zone"]).Where(group => group.Count() > 1))
        {
            Error(messages, $"The domain {duplicate.Key} is drawn twice.");
        }

        foreach (var zone in model.Zones.Where(zone => !model.Targets(zone, Kinds.FrontDoor).Any()))
        {
            Note(messages, $"{zone.Name}: the zone is created without records. Point it to a Front Door for a custom domain.", zone.Node.Id);
        }
    }
}
