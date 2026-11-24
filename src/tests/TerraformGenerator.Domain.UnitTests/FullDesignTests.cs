using TerraformGenerator.Domain;

namespace TerraformGenerator.Domain.UnitTests;

// The resources beyond the first six, the project settings, the scripts, Bicep, the importer and recommendations.
public sealed class FullDesignTests
{
    private static GenerationResult Generate(Sketch sketch)
    {
        var result = SketchGenerator.Generate(sketch);
        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        return result;
    }

    private static List<string> Errors(Sketch sketch) =>
        SketchRules.Check(sketch).Where(message => message.Level == MessageLevel.Error).Select(message => message.Text).ToList();

    [Fact]
    public void The_full_examples_have_every_kind_and_no_errors()
    {
        foreach (var (platform, sketch) in new[] { (Platforms.Azure, Examples.Everything()), (Platforms.Aws, Examples.Aws()), (Platforms.Gcp, Examples.Gcp()) })
        {
            Assert.Equal(Catalog.Instance.Kinds.Where(kind => kind.Platform == platform).Select(kind => kind.Kind).Order(), sketch.Nodes.Select(node => node.Kind).Distinct().Order());
            Assert.Empty(Errors(sketch));
        }
    }

    [Fact]
    public void The_full_example_generates_terraform_bicep_scripts_and_pipeline()
    {
        var files = Generate(Examples.Everything()).Files;

        foreach (var name in new[]
        {
            "main.tf", "apps.tf", "data.tf", "security.tf", "edge.tf", "network.tf", "monitoring.tf", "bicep/main.bicep", "bicep/main.bicepparam",
            "deploy.ps1", "deploy-bicep.ps1", "bootstrap-state.ps1", "check-prereqs.ps1", "setup-github.ps1", ".github/workflows/infrastructure.yml", ".gitignore", "README.md",
        })
        {
            Assert.True(files.ContainsKey(name), name);
        }

        Assert.Contains("backend \"azurerm\" {}", files["versions.tf"], StringComparison.Ordinal);
        Assert.Contains("azurerm_consumption_budget_resource_group", files["main.tf"], StringComparison.Ordinal);
        Assert.Contains("azurerm_container_app_environment", files["apps.tf"], StringComparison.Ordinal);
        Assert.Contains("azurerm_linux_function_app", files["apps.tf"], StringComparison.Ordinal);
        Assert.Contains("azurerm_static_web_app", files["apps.tf"], StringComparison.Ordinal);
        Assert.Contains("azurerm_postgresql_flexible_server", files["data.tf"], StringComparison.Ordinal);
        Assert.Contains("azurerm_cosmosdb_account", files["data.tf"], StringComparison.Ordinal);
        Assert.Contains("azurerm_redis_cache", files["data.tf"], StringComparison.Ordinal);
        Assert.Contains("azurerm_servicebus_namespace", files["data.tf"], StringComparison.Ordinal);
        Assert.Contains("azurerm_container_registry", files["data.tf"], StringComparison.Ordinal);
        Assert.Contains("azurerm_monitor_diagnostic_setting", files["monitoring.tf"], StringComparison.Ordinal);
        Assert.Contains("resource fdEdge 'Microsoft.Cdn/profiles@2024-02-01'", files["bicep/main.bicep"], StringComparison.Ordinal);
    }

    [Fact]
    public void Each_private_service_gets_its_private_dns_zone()
    {
        var network = Generate(Examples.Everything()).Files["network.tf"];

        foreach (var zone in new[]
        {
            "privatelink.database.windows.net", "privatelink.postgres.database.azure.com", "privatelink.documents.azure.com",
            "privatelink.blob.core.windows.net", "privatelink.redis.cache.windows.net", "privatelink.servicebus.windows.net", "privatelink.vaultcore.azure.net",
        })
        {
            Assert.Contains(zone, network, StringComparison.Ordinal);
        }

        Assert.Contains("Microsoft.App/environments", network, StringComparison.Ordinal);
    }

    [Fact]
    public void Container_apps_use_their_own_identity_and_pull_from_the_registry()
    {
        var builder = new SketchBuilder();
        var app = builder.Add(Kinds.ContainerApp, "api", new() { ["image"] = "shop/api:1" });
        builder.Link(app, builder.Add(Kinds.ContainerRegistry, "images"));
        builder.Link(app, builder.Add(Kinds.SqlDatabase, "orders"));

        var files = Generate(builder.Sketch).Files;
        Assert.Contains("resource \"azurerm_user_assigned_identity\" \"api\"", files["apps.tf"], StringComparison.Ordinal);
        Assert.Contains("image  = \"${azurerm_container_registry.images.login_server}/shop/api:1\"", files["apps.tf"], StringComparison.Ordinal);
        Assert.Contains("depends_on = [azurerm_role_assignment.api__images]", files["apps.tf"], StringComparison.Ordinal);
        Assert.Contains("User Id=${azurerm_user_assigned_identity.api.client_id};", files["apps.tf"], StringComparison.Ordinal);
        Assert.Contains("role_definition_name = \"AcrPull\"", files["security.tf"], StringComparison.Ordinal);
    }

    [Fact]
    public void Apps_get_settings_and_access_for_each_service()
    {
        var builder = new SketchBuilder();
        builder.Sketch.Settings.Monitoring = false;
        var app = builder.Add(Kinds.FunctionApp, "jobs", new() { ["runtime"] = "python311" });
        builder.Link(app, builder.Add(Kinds.Postgres, "catalog"));
        builder.Link(app, builder.Add(Kinds.CosmosDb, "carts"));
        builder.Link(app, builder.Add(Kinds.Redis, "cache"));
        builder.Link(app, builder.Add(Kinds.ServiceBus, "events"));

        var files = Generate(builder.Sketch).Files;
        foreach (var setting in new[] { "POSTGRES_CATALOG_HOST", "POSTGRES_CATALOG_USER", "COSMOS_CARTS_ENDPOINT", "REDIS_CACHE_HOST", "SERVICEBUS_EVENTS_NAMESPACE" })
        {
            Assert.Contains($"\"{setting}\"", files["apps.tf"], StringComparison.Ordinal);
        }

        Assert.Contains("azurerm_cosmosdb_sql_role_assignment", files["security.tf"], StringComparison.Ordinal);
        Assert.Contains("azurerm_redis_cache_access_policy_assignment", files["security.tf"], StringComparison.Ordinal);
        Assert.Contains("\"Azure Service Bus Data Sender\"", files["security.tf"], StringComparison.Ordinal);
        Assert.Contains("\"Azure Service Bus Data Receiver\"", files["security.tf"], StringComparison.Ordinal);
        Assert.Contains("pgaadauth_create_principal", files["README.md"], StringComparison.Ordinal);
    }

    [Fact]
    public void Design_rules_for_the_new_kinds()
    {
        var builder = new SketchBuilder();
        var function = builder.Add(Kinds.FunctionApp, "jobs");
        builder.Link(function, builder.Add(Kinds.ServiceBus, "events", new() { ["privateOnly"] = "true" }));
        var web = builder.Add(Kinds.WebApp, "web");
        builder.Link(web, builder.Add(Kinds.ContainerRegistry, "images"));
        var container = builder.Add(Kinds.ContainerApp, "api", new() { ["external"] = "false", ["minReplicas"] = "3", ["maxReplicas"] = "1" });
        builder.Link(builder.Add(Kinds.FrontDoor, "edge"), container);

        var errors = Errors(builder.Sketch);
        Assert.Contains("events: only the Premium tier of Service Bus can be private. Choose Premium or turn off Private only.", errors);
        Assert.Contains("jobs: the Consumption plan cannot join a private network. Choose Elastic Premium EP1 or turn off Private only where it points.", errors);
        Assert.Contains("web: only the Docker image runtime runs from a registry. Choose Docker image or remove the arrow.", errors);
        Assert.Contains("api: \"Always running\" is more than \"Most copies\".", errors);
        Assert.Contains("api: Front Door reaches only containers that are reachable from the internet. Turn that on or remove the arrow.", errors);
    }

    [Theory]
    [InlineData("pipeline", "github", "state", "local", "Deploying from GitHub needs the Terraform state in Azure: choose \"In an Azure storage account\".")]
    [InlineData("budget", "100", "budgetEmail", "", "A budget alert needs the email that receives it.")]
    [InlineData("budgetEmail", "not-an-email", "budget", "100", "Project: \"Budget email\" is not valid. Who gets the budget emails.")]
    [InlineData("output", "pdf", "state", "azure", "Project: \"Code to generate\" is not valid. Bicep is Azure's own language; Terraform also works with other clouds.")]
    public void Settings_rules(string key, string value, string otherKey, string otherValue, string error)
    {
        var sketch = new Sketch();
        sketch.Settings.Options[key] = value;
        sketch.Settings.Options[otherKey] = otherValue;

        Assert.Contains(error, Errors(sketch));
    }

    [Fact]
    public void Output_chooses_the_languages_and_their_scripts()
    {
        var bicepOnly = new Sketch();
        bicepOnly.Settings.Options[SettingKeys.Output] = "bicep";
        var files = Generate(bicepOnly).Files;
        Assert.False(files.ContainsKey("main.tf"));
        Assert.False(files.ContainsKey("deploy.ps1"));
        Assert.True(files.ContainsKey("bicep/main.bicep"));
        Assert.True(files.ContainsKey("deploy-bicep.ps1"));

        var local = new Sketch();
        local.Settings.Options[SettingKeys.State] = "local";
        files = Generate(local).Files;
        Assert.False(files.ContainsKey("bootstrap-state.ps1"));
        Assert.DoesNotContain("backend", files["versions.tf"], StringComparison.Ordinal);
        Assert.DoesNotContain("backend.hcl", files["deploy.ps1"], StringComparison.Ordinal);
    }

    [Fact]
    public void A_new_resource_group_is_created_with_its_name()
    {
        var sketch = new Sketch();
        sketch.Settings.Options[SettingKeys.Output] = "both";
        sketch.Settings.Options[SettingKeys.ResourceGroupName] = "rg-shop-prod";

        var files = Generate(sketch).Files;
        Assert.Contains("resource \"azurerm_resource_group\" \"main\" {", files["main.tf"], StringComparison.Ordinal);
        Assert.Contains("resource_group_name = azurerm_resource_group.main.name", files["main.tf"], StringComparison.Ordinal);
        Assert.Contains("default     = \"rg-shop-prod\"", files["variables.tf"], StringComparison.Ordinal);
        Assert.Contains("$group = 'rg-shop-prod'", files["deploy-bicep.ps1"], StringComparison.Ordinal);
        Assert.Contains("az group create --name $group", files["deploy-bicep.ps1"], StringComparison.Ordinal);
    }

    [Fact]
    public void An_existing_resource_group_is_only_used()
    {
        var sketch = new Sketch();
        sketch.Settings.Options[SettingKeys.Output] = "both";
        sketch.Settings.Options[SettingKeys.Pipeline] = "github";
        sketch.Settings.Options[SettingKeys.ResourceGroup] = "existing";

        var result = Generate(sketch);
        Assert.Contains("data \"azurerm_resource_group\" \"main\" {", result.Files["main.tf"], StringComparison.Ordinal);
        Assert.DoesNotContain("resource \"azurerm_resource_group\"", result.Files["main.tf"], StringComparison.Ordinal);
        Assert.Contains("az group exists --name $group", result.Files["deploy-bicep.ps1"], StringComparison.Ordinal);
        Assert.DoesNotContain("az group create", result.Files["deploy-bicep.ps1"], StringComparison.Ordinal);
        Assert.Contains("The resource group myapp-dev-rg must already exist; the code uses it and never deletes it.", result.Messages.Select(message => message.Text));
    }

    [Fact]
    public void Importing_reads_the_resource_group()
    {
        var existing = TerraformImporter.Read("data \"azurerm_resource_group\" \"main\" {\n  name = \"corp-apps\"\n}\n");
        Assert.Equal(("existing", "corp-apps"), (existing.Settings.Option(SettingKeys.ResourceGroup), existing.Settings.Option(SettingKeys.ResourceGroupName)));

        var created = TerraformImporter.Read("resource \"azurerm_resource_group\" \"main\" {\n  name     = \"shop-dev-rg\"\n  location = \"westus3\"\n}\n");
        Assert.Equal(("", "shop-dev-rg", "westus3"), (created.Settings.Option(SettingKeys.ResourceGroup), created.Settings.Option(SettingKeys.ResourceGroupName), created.Settings.Region));
    }

    [Fact]
    public void Foundry_and_speech_give_models_roles_and_settings()
    {
        var builder = new SketchBuilder();
        builder.Sketch.Settings.Options[SettingKeys.Output] = "both";
        var app = builder.Add(Kinds.WebApp, "web");
        builder.Link(app, builder.Add(Kinds.AiFoundry, "assistant", new() { ["embeddings"] = "text-embedding-3-small" }));
        builder.Link(app, builder.Add(Kinds.Speech, "voice"));

        var files = Generate(builder.Sketch).Files;
        Assert.Matches("kind +=  ?\"AIServices\"", files["ai.tf"]);
        Assert.Contains("resource \"azurerm_cognitive_account_project\" \"assistant\"", files["ai.tf"], StringComparison.Ordinal);
        Assert.Contains("depends_on = [azurerm_cognitive_deployment.assistant_chat]", files["ai.tf"], StringComparison.Ordinal);
        Assert.Contains("\"FOUNDRY_ASSISTANT_CHAT_MODEL\"", files["apps.tf"], StringComparison.Ordinal);
        Assert.Contains("\"SPEECH_VOICE_REGION\"", files["apps.tf"], StringComparison.Ordinal);
        Assert.Contains("\"Cognitive Services Speech User\"", files["security.tf"], StringComparison.Ordinal);
        Assert.Contains("'Microsoft.CognitiveServices/accounts/deployments@2025-06-01'", files["bicep/main.bicep"], StringComparison.Ordinal);
        Assert.Contains("'Microsoft.CognitiveServices'", files["check-prereqs.ps1"], StringComparison.Ordinal);
    }

    [Fact]
    public void Foundry_needs_a_model()
    {
        var builder = new SketchBuilder();
        builder.Add(Kinds.AiFoundry, "assistant", new() { ["model"] = "none" });

        Assert.Contains("assistant: choose a chat model or an embeddings model.", Errors(builder.Sketch));
    }

    [Fact]
    public void Company_settings_use_what_already_exists()
    {
        var builder = new SketchBuilder();
        foreach (var (key, value) in new Dictionary<string, string>
        {
            [SettingKeys.Output] = "both", [SettingKeys.Subscription] = "11111111-2222-3333-4444-555555555555",
            [SettingKeys.Network] = "existing", [SettingKeys.NetworkName] = "hub", [SettingKeys.NetworkResourceGroup] = "rg-net",
            [SettingKeys.PrivateDns] = "existing", [SettingKeys.PrivateDnsResourceGroup] = "rg-dns",
            [SettingKeys.Logs] = "existing", [SettingKeys.LogWorkspaceId] = "/subscriptions/x/resourceGroups/rg/providers/Microsoft.OperationalInsights/workspaces/w",
            [SettingKeys.DatabaseAdmin] = "group", [SettingKeys.DatabaseAdminGroupName] = "DB admins", [SettingKeys.DatabaseAdminGroupId] = "66666666-7777-8888-9999-000000000000",
            [SettingKeys.Owner] = "Team A", [SettingKeys.Lock] = "on", [SettingKeys.Alerts] = "on", [SettingKeys.AlertEmail] = "ops@example.com",
        })
        {
            builder.Sketch.Settings.Options[key] = value;
        }

        builder.Link(builder.Add(Kinds.WebApp, "web", new() { ["sku"] = "S1" }), builder.Add(Kinds.SqlDatabase, "orders", new() { ["privateOnly"] = "true" }));

        var files = Generate(builder.Sketch).Files;
        Assert.Contains("data \"azurerm_virtual_network\" \"main\"", files["network.tf"], StringComparison.Ordinal);
        Assert.Contains("data \"azurerm_private_dns_zone\" \"sql\"", files["network.tf"], StringComparison.Ordinal);
        Assert.Contains("cidrsubnet(var.network_range, 8, 1)", files["network.tf"], StringComparison.Ordinal);
        Assert.Contains("azurerm_subnet_network_security_group_association", files["network.tf"], StringComparison.Ordinal);
        Assert.DoesNotContain("azurerm_log_analytics_workspace\" \"main\"", files["main.tf"], StringComparison.Ordinal);
        Assert.Contains("azurerm_management_lock", files["main.tf"], StringComparison.Ordinal);
        Assert.Contains("-target=azurerm_management_lock.main", files["deploy.ps1"], StringComparison.Ordinal);
        Assert.DoesNotContain("TF_VAR_admin_name", files["deploy.ps1"], StringComparison.Ordinal);
        Assert.Matches("default += \"DB admins\"", files["variables.tf"]);
        Assert.Contains("\"owner\"", files["variables.tf"], StringComparison.Ordinal);
        Assert.Contains("az account set --subscription '11111111-2222-3333-4444-555555555555'", files["check-prereqs.ps1"], StringComparison.Ordinal);
        Assert.Contains("azurerm_monitor_metric_alert", files["monitoring.tf"], StringComparison.Ordinal);
        Assert.True(files.ContainsKey("bicep/subnets.bicep"));
        Assert.True(files.ContainsKey("bicep/dns-links.bicep"));
    }

    [Fact]
    public void Settings_that_need_a_value_say_so_only_when_they_apply()
    {
        var sketch = new Sketch();
        sketch.Settings.Options[SettingKeys.NetworkName] = "";
        sketch.Settings.Options[SettingKeys.Alerts] = "on";

        var errors = Errors(sketch);
        Assert.Contains("Alerts need the email that receives them.", errors);
        Assert.DoesNotContain("Using an existing network needs its name.", errors);
        var email = Catalog.Instance.Settings.First(setting => setting.Key == SettingKeys.BudgetEmail);
        Assert.True(email.Visible(key => key switch { "budget" => "100", "platform" => "azure", _ => "" }));
        Assert.False(email.Visible(key => key switch { "budget" => "0", "platform" => "azure", _ => "" }));
        Assert.True(email.Visible(key => key switch { "budget" => "100", "platform" => "aws", _ => "" }));
        var billing = Catalog.Instance.Settings.First(setting => setting.Key == CloudAlerts.GcpBillingAccount);
        Assert.True(billing.Visible(key => key switch { "budget" => "100", "platform" => "gcp", _ => "" }));
        Assert.False(billing.Visible(key => key switch { "budget" => "100", "platform" => "aws", _ => "" }));
    }

    [Fact]
    public void Several_environments_come_from_one_drawing_with_their_own_values()
    {
        var sketch = Examples.WebApiSql();
        sketch.Settings.Options[Environments.Key] = "dev,prod";
        sketch.Nodes.Single(node => node.Name == "api").Props["sku@prod"] = "P1v3";

        var result = Generate(sketch);

        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        Assert.Contains("environments/dev/main.tf", result.Files.Keys);
        Assert.Contains("environments/prod/main.tf", result.Files.Keys);
        Assert.Matches(@"sku_name\s+=\s+""B1""", result.Files["environments/dev/apps.tf"]);
        Assert.Matches(@"sku_name\s+=\s+""P1v3""", result.Files["environments/prod/apps.tf"]);
        Assert.Contains(result.Messages, message => message.Text.StartsWith("prod: ", StringComparison.Ordinal));

        sketch.Nodes.Single(node => node.Name == "api").Props["sku@nowhere"] = "B1";
        Assert.Contains("api: unknown setting \"sku@nowhere\".", Errors(sketch));
    }

    [Fact]
    public void Importing_reads_foundry_and_speech()
    {
        const string terraform = """
            resource "azurerm_cognitive_account" "ai" {
              kind = "AIServices"
            }
            resource "azurerm_cognitive_deployment" "chat" {
              cognitive_account_id = azurerm_cognitive_account.ai.id
              model {
                format = "OpenAI"
                name   = "gpt-4.1"
              }
            }
            resource "azurerm_cognitive_account" "voice" {
              kind     = "SpeechServices"
              sku_name = "F0"
            }
            resource "azurerm_cognitive_account" "vision" {
              kind = "ComputerVision"
            }
            """;

        var draft = TerraformImporter.Read(terraform);

        Assert.Contains(draft.Nodes, node => node.Kind == Kinds.AiFoundry && node.Props.Any(prop => prop is { Key: "model", Value: "gpt-4.1" }));
        Assert.Contains(draft.Nodes, node => node.Kind == Kinds.Speech && node.Props.Any(prop => prop is { Key: "sku", Value: "F0" }));
        var vision = Assert.Single(draft.Nodes, node => node.Kind == AzureServices.AiService);
        Assert.Equal("vision", vision.Name);
        Assert.Contains(vision.Props, prop => prop is { Key: "type", Value: "vision" });
    }

    [Fact]
    public void The_prerequisites_script_registers_only_the_services_used()
    {
        var builder = new SketchBuilder();
        builder.Add(Kinds.Redis, "cache");

        var script = Generate(builder.Sketch).Files["check-prereqs.ps1"];
        Assert.Contains("'Microsoft.Cache'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("'Microsoft.Sql'", script, StringComparison.Ordinal);
        Assert.Contains("winget install Hashicorp.Terraform", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Deploy_asks_before_changing_anything()
    {
        var deploy = Generate(new Sketch()).Files["deploy.ps1"];

        Assert.Contains("terraform plan -input=false -out tfplan", deploy, StringComparison.Ordinal);
        Assert.Contains("Type yes to do it", deploy, StringComparison.Ordinal);
        Assert.Contains("Type myapp-dev to confirm", deploy, StringComparison.Ordinal);
    }

    [Fact]
    public void Bicep_never_interpolates_typed_text()
    {
        Assert.Equal("'a\\'b'", Bicep.Str("a'b"));
        Assert.Equal("'\\${x}'", Bicep.Literal("${x}"));
        var rendered = Bicep.Render(Bicep.Object(body => body.Str("odd-key", "v").Add("plain", "1").Add("list", Bicep.Array("1", "2"))), 0);
        Assert.Equal("{\n  'odd-key': 'v'\n  plain: 1\n  list: [\n    1\n    2\n  ]\n}", rendered);
    }

    [Fact]
    public void Importing_generated_terraform_gives_back_the_drawing()
    {
        var original = Examples.Everything();
        var files = Generate(original).Files;
        var terraform = string.Join("\n", files.Where(file => file.Key.EndsWith(".tf", StringComparison.Ordinal)).Select(file => file.Value));

        var proposal = ProposalBuilder.Build(TerraformImporter.Read(terraform), new Sketch());

        var names = proposal.Sketch.Nodes.ToDictionary(node => node.Id, node => node.Name);
        Assert.Equal(original.Nodes.Select(node => (node.Kind, node.Name)).Order(), proposal.Sketch.Nodes.Select(node => (node.Kind, node.Name)).Order());
        var originalNames = original.Nodes.ToDictionary(node => node.Id, node => node.Name);
        Assert.Equal(
            original.Edges.Select(edge => $"{originalNames[edge.From]}->{originalNames[edge.To]}").Order(),
            proposal.Sketch.Edges.Select(edge => $"{names[edge.From]}->{names[edge.To]}").Order());
        foreach (var node in original.Nodes)
        {
            var imported = proposal.Sketch.Nodes.Single(item => item.Name == node.Name);
            Assert.Equal(node.Props.ContainsKey("privateOnly"), imported.Props.ContainsKey("privateOnly"));
        }

        Assert.Equal(("store", "prod", "eastus2"), (proposal.Sketch.Settings.Name, proposal.Sketch.Settings.Environment, proposal.Sketch.Settings.Region));
    }

    [Fact]
    public void Importing_lists_what_it_cannot_draw_and_reads_values()
    {
        const string terraform = """
            # a comment with resource "azurerm_fake" "x" {
            variable "plan_size" {
              default = "S1"
            }
            resource "azurerm_service_plan" "plan" {
              sku_name = var.plan_size
            }
            resource "azurerm_linux_web_app" "my_site" {
              service_plan_id = azurerm_service_plan.plan.id
              site_config {
                application_stack {
                  python_version = "3.12"
                }
              }
            }
            resource "azurerm_kubernetes_cluster" "aks" {
              name = "{not a brace"
            }
            """;

        var draft = TerraformImporter.Read(terraform);

        var site = Assert.Single(draft.Nodes, node => !Kinds.IsGeneric(node.Kind));
        Assert.Equal(("my-site", Kinds.WebApp), (site.Name, site.Kind));
        Assert.Contains(site.Props, prop => prop is { Key: "sku", Value: "S1" });
        Assert.Contains(site.Props, prop => prop is { Key: "runtime", Value: "python312" });
        var cluster = Assert.Single(draft.Nodes, node => Kinds.IsGeneric(node.Kind));
        Assert.Equal(("tf:azurerm_kubernetes_cluster", "aks"), (cluster.Kind, cluster.Name));
        Assert.Equal([("name", "{not a brace")], cluster.Props.Select(prop => (prop.Key, prop.Value)));
        Assert.Empty(draft.Questions);
    }

    [Fact]
    public void Proposals_can_change_project_settings()
    {
        var draft = new ProposalDraft();
        draft.Settings.Set(SettingKeys.Budget, "100").Set(SettingKeys.BudgetEmail, "me@example.com").Set(SettingKeys.Pipeline, "rocket");

        var proposal = ProposalBuilder.Build(draft, new Sketch());

        Assert.Equal("100", proposal.Sketch.Settings.Options[SettingKeys.Budget]);
        Assert.False(proposal.Sketch.Settings.Options.ContainsKey(SettingKeys.Pipeline));
        Assert.Contains(proposal.Changes, change => change.Text == "Project: Monthly budget alert: US$100");
        Assert.Contains(proposal.Review, item => item.Text == "Kept the current \"Deploy from GitHub\": \"rocket\" is not one of its choices.");
    }

    [Fact]
    public void Recommendations_are_read_and_sorted_by_priority()
    {
        var draft = RecommendationPrompt.Parse("""
            {"summary":"s","recommendations":[
              {"title":"low one","category":"cost","priority":"low","why":"w","change":""},
              {"title":"","category":"cost","priority":"high","why":"w","change":""},
              {"title":"high one","category":"security","priority":"high","why":"w","change":"Make it private"}]}
            """);

        Assert.Equal(["high one", "low one"], draft.Recommendations.Select(item => item.Title));
        Assert.Contains("Language: Español", RecommendationPrompt.User(Examples.WebApiSql(), [], "Español"), StringComparison.Ordinal);
        Assert.Contains("never recommend it", RecommendationPrompt.System(), StringComparison.Ordinal);
    }
}
