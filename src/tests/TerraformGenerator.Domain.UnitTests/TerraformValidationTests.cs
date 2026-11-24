using System.Diagnostics;
using TerraformGenerator.Domain;

namespace TerraformGenerator.Domain.UnitTests;

// Runs the real tools on generated drawings: terraform (init without a backend, validate, fmt -check), the Bicep
// compiler (no errors and no warnings) and the PowerShell parser on every script.
// run-tests.ps1 turns it on with TERRAFORMGENERATOR_TERRAFORM=1 when terraform is installed (and Bicep, from az bicep install).
public sealed class TerraformValidationTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("TERRAFORMGENERATOR_TERRAFORM") == "1";

    public static TheoryData<string> Drawings => new() { "example", "everything", "production", "public", "enterprise", "aws", "gcp", "aws-boxes", "gcp-boxes", "aws-dev", "gcp-dev", "services-dev", "environments", "gateway", "empty" };

    private static Sketch Drawing(string name)
    {
        switch (name)
        {
            case "example":
                var example = Examples.WebApiSql();
                example.Settings.Options[SettingKeys.Output] = "both";
                return example;
            case "everything":
                return Examples.Everything();
            case "aws":
                return Examples.Aws();
            case "gcp":
                return Examples.Gcp();
            case "services-dev":
                // The other tiers of the Azure services, public, in both languages.
                var services = new SketchBuilder();
                services.Sketch.Settings.Options[SettingKeys.Output] = "both";
                services.Sketch.Settings.Options[SettingKeys.State] = "local";
                var app = services.Add(Kinds.FunctionApp, "worker", new() { ["runtime"] = "python311" });
                foreach (var (kind, box, props) in new (string, string, Dictionary<string, string>)[]
                {
                    (AzureServices.AiService, "language", new() { ["type"] = "language", ["tier"] = "free" }),
                    (AzureServices.AiService, "translate", new() { ["type"] = "translator" }),
                    (AzureServices.AiService, "vision", new() { ["type"] = "vision" }),
                    (AzureServices.AiService, "safety", new() { ["type"] = "contentSafety" }),
                    (AzureServices.Search, "search", new() { ["sku"] = "free" }),
                    (AzureServices.EventHubs, "stream", new() { ["sku"] = "Basic", ["partitions"] = "1" }),
                    (AzureServices.AppConfig, "settings", new()),
                    (AzureServices.SignalR, "live", new() { ["sku"] = "Free_F1", ["mode"] = "Serverless" }),
                    (AzureServices.WebPubSub, "sockets", new() { ["sku"] = "Free_F1" }),
                    (AzureServices.Communication, "sms", new() { ["email"] = "false", ["dataLocation"] = "Europe" }),
                    (Kinds.Storage, "files", new() { ["queues"] = "jobs, emails" }),
                    (AzureServices.MachineLearning, "models", new()),
                    (AzureServices.AiService, "labels", new() { ["type"] = "customVision", ["tier"] = "free" }),
                    (AzureServices.VideoIndexer, "videos", new()),
                    (Kinds.Postgres, "catalog", new() { ["vector"] = "true" }),
                })
                {
                    services.Link(app, services.Add(kind, box, props));
                }

                // A bot that answers through the container app (its identity is added to the app's own).
                services.Link(services.Add(AzureServices.Bot, "support", new() { ["teams"] = "false", ["sku"] = "S1" }), services.Add(Kinds.ContainerApp, "answers"));
                return services.Sketch;
            case "gateway":
                // Application Gateway instead of Front Door: two apps by path, only logging, network security groups on.
                var regional = new SketchBuilder();
                regional.Sketch.Settings.Options[SettingKeys.Output] = "both";
                regional.Sketch.Settings.Options[SettingKeys.State] = "local";
                regional.Sketch.Settings.Options[SettingKeys.Nsg] = "true";
                var gateway = regional.Add(Kinds.AppGateway, "entry", new() { ["firewall"] = "detection", ["minCapacity"] = "0" });
                regional.Link(gateway, regional.Add(Kinds.WebApp, "site"));
                regional.Link(gateway, regional.Add(Kinds.FunctionApp, "api", new() { ["path"] = "/api/*" }));
                regional.Link(regional.Add(Kinds.DnsZone, "domain", new() { ["zone"] = "contoso.org" }), gateway);
                return regional.Sketch;
            case "environments":
                // Development, test and production from one drawing, production with bigger choices.
                var several = Examples.WebApiSql();
                several.Settings.Options[Environments.Key] = "dev,test,prod";
                several.Settings.Options[SettingKeys.Output] = "both";
                several.Nodes.Single(node => node.Name == "api").Props["sku@prod"] = "P1v3";
                several.Nodes.Single(node => node.Name == "orders").Props["backupDays@prod"] = "35";
                return several;
            case "aws-boxes":
                return Examples.AwsProviderBoxes();
            case "gcp-boxes":
                return Examples.GcpProviderBoxes();
            case "aws-dev":
                // Development: the other choices of each AWS box, with the state on this computer.
                var aws = new SketchBuilder();
                aws.Sketch.Settings.Platform = Platforms.Aws;
                aws.Sketch.Settings.Region = "eu-west-1";
                aws.Sketch.Settings.Options[SettingKeys.State] = "local";
                aws.Sketch.Settings.Options[Geo.Key] = "on";
                aws.Sketch.Settings.Options[Guardrails.Key] = "on";
                aws.Sketch.Settings.Options[OwnKeys.Key] = "on";
                var javaWorker = aws.Add(CloudKinds.Lambda, "worker", new() { ["runtime"] = "java21", ["memory"] = "1024" });
                var reports = aws.Add(CloudKinds.Lambda, "reports", new() { ["runtime"] = "dotnet8" });
                var inside = aws.Add(CloudKinds.Fargate, "internal", new() { ["public"] = "false", ["port"] = "8080", ["size"] = "256" });
                aws.Link(javaWorker, aws.Add(CloudKinds.Rds, "ledger", new() { ["version"] = "16" }));
                aws.Link(reports, aws.Add(CloudKinds.Sqs, "orders", new() { ["fifo"] = "true", ["deadLetter"] = "false" }));
                aws.Link(inside, aws.Add(CloudKinds.DynamoDb, "sessions", new() { ["recovery"] = "false" }));
                aws.Link(inside, javaWorker);
                aws.Add(CloudKinds.S3, "archive", new() { ["versioning"] = "false" });
                aws.Link(reports, aws.Add(CloudKinds.Bedrock, "writer", new() { ["model"] = "amazon.nova-lite-v1:0", ["guardrail"] = "false" }));
                aws.Link(inside, aws.Add(CloudKinds.AwsAiApi, "voice", new() { ["service"] = "polly" }));
                return aws.Sketch;
            case "gcp-dev":
                var gcp = new SketchBuilder();
                gcp.Sketch.Settings.Platform = Platforms.Gcp;
                gcp.Sketch.Settings.Region = "europe-west1";
                gcp.Sketch.Settings.Options[SettingKeys.GcpProject] = "shop-dev-123456";
                gcp.Sketch.Settings.Options[SettingKeys.State] = "local";
                gcp.Sketch.Settings.Options[Geo.Key] = "on";
                gcp.Sketch.Settings.Options[Guardrails.Key] = "on";
                gcp.Sketch.Settings.Options[OwnKeys.Key] = "on";
                var run = gcp.Add(CloudKinds.CloudRun, "api");
                var python = gcp.Add(CloudKinds.CloudFunction, "worker", new() { ["public"] = "true" });
                var java = gcp.Add(CloudKinds.CloudFunction, "reports", new() { ["runtime"] = "java21" });
                gcp.Link(run, python).Link(java, gcp.Add(CloudKinds.PubSub, "events", new() { ["deadLetter"] = "false" }));
                gcp.Link(python, gcp.Add(CloudKinds.CloudSql, "ledger", new() { ["tier"] = "db-f1-micro", ["version"] = "POSTGRES_15" }));
                gcp.Add(CloudKinds.Bucket, "archive", new() { ["storageClass"] = "COLDLINE", ["versioning"] = "false" });
                gcp.Link(run, gcp.Add(CloudKinds.GcpAiApi, "speech", new() { ["service"] = "speech" }));
                gcp.Link(java, gcp.Add(CloudKinds.VertexAi, "writer", new() { ["model"] = "gemini-2.5-pro" }));
                gcp.Link(run, gcp.Add(CloudKinds.DocumentAi, "scans", new() { ["type"] = "OCR_PROCESSOR" }));
                return gcp.Sketch;
            case "production":
                var builder = new SketchBuilder().Environment("prod");
                builder.Sketch.Settings.Options[SettingKeys.State] = "local";
                var web = builder.Add(Kinds.WebApp, "web", new() { ["runtime"] = "docker", ["dockerImage"] = "nginx:1.27", ["sku"] = "P0v3" });
                var api = builder.Add(Kinds.WebApp, "api", new() { ["runtime"] = "python312", ["sku"] = "S1", ["path"] = "/api/*" });
                var jobs = builder.Add(Kinds.WebApp, "jobs", new() { ["runtime"] = "node20", ["alwaysOn"] = "false" });
                var frontDoor = builder.Add(Kinds.FrontDoor, "edge", new() { ["sku"] = "Premium_AzureFrontDoor" });
                builder.Link(frontDoor, web).Link(frontDoor, api).Link(web, api).Link(jobs, api);
                builder.Link(api, builder.Add(Kinds.SqlDatabase, "orders", new() { ["sku"] = "GP_S_Gen5_1", ["backupDays"] = "35" }));
                builder.Link(jobs, builder.Add(Kinds.SqlDatabase, "reports", new() { ["sku"] = "Basic" }));
                builder.Link(api, builder.Add(Kinds.Storage, "files", new() { ["replication"] = "GRS", ["privateOnly"] = "true" }));
                builder.Link(jobs, builder.Add(Kinds.KeyVault, "secrets", new() { ["purgeProtection"] = "true", ["retentionDays"] = "90" }));
                builder.Add(Kinds.DnsZone, "domain", new() { ["zone"] = "contoso.org" });
                return builder.Sketch;
            case "public":
                // Every service public, Consumption functions, a container app without a registry, both languages.
                var open = new SketchBuilder();
                open.Sketch.Settings.Options[SettingKeys.Output] = "both";
                open.Sketch.Settings.Options[SettingKeys.Pipeline] = "github";
                open.Sketch.Settings.Options[SettingKeys.ResourceGroup] = "existing";
                open.Sketch.Settings.Options[SettingKeys.ResourceGroupName] = "corp-apps";
                var function = open.Add(Kinds.FunctionApp, "worker", new() { ["runtime"] = "node20" });
                var container = open.Add(Kinds.ContainerApp, "api", new() { ["external"] = "false" });
                var site = open.Add(Kinds.StaticWebApp, "site");
                open.Link(site, container).Link(function, container);
                open.Link(function, open.Add(Kinds.Postgres, "catalog"));
                open.Link(container, open.Add(Kinds.CosmosDb, "carts", new() { ["capacity"] = "provisioned" }));
                open.Link(container, open.Add(Kinds.Redis, "cache"));
                open.Link(function, open.Add(Kinds.ServiceBus, "events", new() { ["sku"] = "Basic" }));
                return open.Sketch;
            case "enterprise":
                // Everything that already exists in a company: subscription, group, network, DNS zones, logs, an admin group.
                var corp = new SketchBuilder().Environment("prod");
                foreach (var (key, value) in new Dictionary<string, string>
                {
                    [SettingKeys.Output] = "both", [SettingKeys.Subscription] = "11111111-2222-3333-4444-555555555555",
                    [SettingKeys.ResourceGroup] = "existing", [SettingKeys.ResourceGroupName] = "rg-store-prod",
                    [SettingKeys.Network] = "existing", [SettingKeys.NetworkName] = "hub-spoke-1", [SettingKeys.NetworkResourceGroup] = "rg-network",
                    [SettingKeys.NetworkRange] = "10.60.0.0/16", [SettingKeys.PrivateDns] = "existing", [SettingKeys.PrivateDnsResourceGroup] = "rg-dns",
                    [SettingKeys.Logs] = "existing",
                    [SettingKeys.LogWorkspaceId] = "/subscriptions/11111111-2222-3333-4444-555555555555/resourceGroups/rg-logs/providers/Microsoft.OperationalInsights/workspaces/central",
                    [SettingKeys.DatabaseAdmin] = "group", [SettingKeys.DatabaseAdminGroupName] = "DB admins",
                    [SettingKeys.DatabaseAdminGroupId] = "66666666-7777-8888-9999-000000000000",
                    [SettingKeys.Alerts] = "on", [SettingKeys.AlertEmail] = "ops@example.com", [SettingKeys.Lock] = "on",
                    [SettingKeys.Budget] = "1000", [SettingKeys.BudgetEmail] = "ops@example.com",
                    [Team.AzureAdmins] = "aaaaaaaa-1111-2222-3333-444444444444", [Team.AzureDevelopers] = "bbbbbbbb-1111-2222-3333-444444444444",
                    [Team.AzureReaders] = "cccccccc-1111-2222-3333-444444444444",
                    [Geo.Key] = "on", [Guardrails.Key] = "on", [OwnKeys.Key] = "on",
                })
                {
                    corp.Sketch.Settings.Options[key] = value;
                }

                var worker = corp.Add(Kinds.FunctionApp, "worker", new() { ["plan"] = "EP1" });
                var portal = corp.Add(Kinds.WebApp, "portal", new() { ["sku"] = "P1v3" });
                var service = corp.Add(Kinds.ContainerApp, "service");
                corp.Link(worker, corp.Add(Kinds.SqlDatabase, "ledger", new() { ["backupDays"] = "35", ["privateOnly"] = "true" }));
                corp.Link(service, corp.Add(Kinds.Postgres, "catalog", new() { ["backupDays"] = "14", ["privateOnly"] = "true", ["sku"] = "GP_Standard_D2ds_v5" }));
                corp.Link(portal, corp.Add(Kinds.AiFoundry, "assistant", new() { ["privateOnly"] = "true" }));
                corp.Link(service, corp.Add(Kinds.Speech, "voice", new() { ["privateOnly"] = "true" }));
                corp.Link(portal, corp.Add(Kinds.Storage, "documents",
                    new() { ["replication"] = "GRS", ["versioning"] = "true", ["softDeleteDays"] = "30", ["privateOnly"] = "true" }));
                return corp.Sketch;
            default:
                return new Sketch();
        }
    }

    [Theory]
    [MemberData(nameof(Drawings))]
    public async Task The_tools_accept_the_generated_files(string drawing)
    {
        if (!Enabled)
        {
            return;
        }

        var sketch = Drawing(drawing);
        var schemas = sketch.Nodes.Any(node => Kinds.IsGeneric(node.Kind))
            ? new LoadedSchemas(sketch.Settings.Platform, await ProviderSchemaFiles.LoadAsync(sketch.Settings.Platform, ProviderSchemaFiles.DefaultFolder(sketch.Settings.Platform), CancellationToken.None))
            : null;
        var result = SketchGenerator.Generate(sketch, schemas);
        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        var folder = Path.Combine(Path.GetTempPath(), "terraformgenerator-tests", $"{drawing}-{Guid.NewGuid():N}");
        try
        {
            foreach (var (name, content) in result.Files)
            {
                var path = Path.Combine(folder, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, content);
            }

            // Each environment of a drawing with several is its own Terraform and Bicep folder.
            var roots = result.Files.Keys.Where(name => name.StartsWith("environments/", StringComparison.Ordinal))
                .Select(name => string.Join('/', name.Split('/').Take(2))).Distinct().ToList();
            foreach (var root in roots.Count > 0 ? roots : [""])
            {
                var here = root.Length == 0 ? folder : Path.Combine(folder, root);
                var prefix = root.Length == 0 ? "" : root + "/";
                if (result.Files.ContainsKey(prefix + "main.tf"))
                {
                    // Downloading the providers may meet a network hiccup; only that step is tried again.
                    await Run("terraform", "init -backend=false -input=false -no-color", here, attempts: 3);
                    await Run("terraform", "validate -no-color", here);
                    await Run("terraform", "fmt -check -no-color", here);
                }

                if (result.Files.ContainsKey(prefix + "bicep/main.bicep"))
                {
                    var output = await Run(BicepTool, "build bicep/main.bicep --stdout", here);
                    Assert.True(!output.Error.Contains("Warning", StringComparison.OrdinalIgnoreCase), $"Bicep warnings:\n{output.Error}");
                    await Run(BicepTool, "build-params bicep/main.bicepparam --stdout", here);
                }
            }

            foreach (var script in result.Files.Keys.Where(name => name.EndsWith(".ps1", StringComparison.Ordinal)))
            {
                var path = Path.Combine(folder, script);
                var check = await Run("pwsh", $"-NoProfile -Command \"$e = $null; [System.Management.Automation.Language.Parser]::ParseFile('{path}', [ref]$null, [ref]$e) | Out-Null; $e | ForEach-Object {{ $_.Message }}\"", folder);
                Assert.True(check.Output.Trim().Length == 0, $"{script}: {check.Output}");
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // The Bicep CLI that az bicep install puts in the profile, or one on the PATH.
    private static string BicepTool
    {
        get
        {
            var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".azure", "bin", OperatingSystem.IsWindows() ? "bicep.exe" : "bicep");
            return File.Exists(installed) ? installed : "bicep";
        }
    }

    private static async Task<(string Output, string Error)> Run(string tool, string arguments, string folder, int attempts = 1)
    {
        for (var attempt = 1; attempt < attempts; attempt++)
        {
            var (code, output, error) = await Start(tool, arguments, folder);
            if (code == 0)
            {
                return (output, error);
            }

            await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
        }

        var last = await Start(tool, arguments, folder);
        Assert.True(last.Code == 0, $"{tool} {arguments} failed:\n{last.Output}\n{last.Error}");
        return (last.Output, last.Error);
    }

    private static async Task<(int Code, string Output, string Error)> Start(string tool, string arguments, string folder)
    {
        var start = new ProcessStartInfo(tool, arguments)
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TerraformGenerator", "plugin-cache");
        Directory.CreateDirectory(cache);
        start.Environment["TF_PLUGIN_CACHE_DIR"] = cache;
        start.Environment["TF_IN_AUTOMATION"] = "1";
        start.Environment["CHECKPOINT_DISABLE"] = "1";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, await error);
    }

    private sealed class LoadedSchemas(string platform, Dictionary<string, ResourceSchema> schemas) : IResourceSchemas
    {
        public ResourceSchema? Find(string platformId, string type) => platformId == platform ? schemas.GetValueOrDefault(type) : null;
    }
}
