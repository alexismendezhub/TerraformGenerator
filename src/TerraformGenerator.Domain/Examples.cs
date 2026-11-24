namespace TerraformGenerator.Domain;

public static class Examples
{
    // A web app and an API behind Front Door with a custom domain, a private database, files and secrets.
    public static Sketch WebApiSql() => new()
    {
        Settings = new SketchSettings { Name = "shop", Environment = "dev", Region = "eastus2", Monitoring = true },
        Nodes =
        [
            Node("dns", Kinds.DnsZone, "domain", 40, 200, new() { ["zone"] = "example.com" }),
            Node("fd", Kinds.FrontDoor, "edge", 250, 200, new() { ["subdomain"] = "www" }),
            Node("web", Kinds.WebApp, "web", 460, 100, new() { ["runtime"] = "node22", ["path"] = "/*" }),
            Node("api", Kinds.WebApp, "api", 460, 300, new() { ["runtime"] = "dotnet8", ["path"] = "/api/*" }),
            Node("sql", Kinds.SqlDatabase, "orders", 680, 180, new() { ["privateOnly"] = "true" }),
            Node("st", Kinds.Storage, "files", 680, 300, new() { ["containers"] = "uploads, invoices" }),
            Node("kv", Kinds.KeyVault, "secrets", 680, 420, []),
        ],
        Edges =
        [
            Edge("e1", "dns", "fd"),
            Edge("e2", "fd", "web"),
            Edge("e3", "fd", "api"),
            Edge("e4", "web", "api"),
            Edge("e5", "api", "sql"),
            Edge("e6", "api", "st"),
            Edge("e7", "api", "kv"),
        ],
    };

    // Every kind of box in production: a site, an API in a container, a worker function, every data service (most
    // private), a registry, Front Door with a domain, both code languages, state in Azure, GitHub and a budget.
    public static Sketch Everything() => new()
    {
        Settings = new SketchSettings
        {
            Name = "store",
            Environment = "prod",
            Region = "eastus2",
            Monitoring = true,
            Options = new()
            {
                [SettingKeys.Output] = "both",
                [SettingKeys.State] = "azure",
                [SettingKeys.Pipeline] = "github",
                [AppPipeline.Key] = "github",
                [SettingKeys.Budget] = "500",
                [SettingKeys.BudgetEmail] = "ops@example.com",
                [SettingKeys.Owner] = "Store team",
                [SettingKeys.CostCenter] = "CC-1042",
                [SettingKeys.Alerts] = "on",
                [SettingKeys.AlertEmail] = "ops@example.com",
            },
        },
        Nodes =
        [
            Node("dns", Kinds.DnsZone, "domain", 40, 260, new() { ["zone"] = "example.com" }),
            Node("fd", Kinds.FrontDoor, "edge", 250, 260, new() { ["sku"] = "Premium_AzureFrontDoor", ["subdomain"] = "shop" }),
            Node("site", Kinds.StaticWebApp, "site", 460, 40, new() { ["sku"] = "Standard" }),
            Node("web", Kinds.WebApp, "web", 460, 150, new() { ["runtime"] = "docker", ["dockerImage"] = "store/web:1.0", ["sku"] = "P0v3", ["path"] = "/app/*" }),
            Node("api", Kinds.ContainerApp, "api", 460, 260, new() { ["image"] = "store/api:1.0", ["minReplicas"] = "1", ["port"] = "8080", ["path"] = "/api/*" }),
            Node("jobs", Kinds.FunctionApp, "jobs", 460, 370, new() { ["plan"] = "EP1", ["runtime"] = "python311", ["path"] = "/jobs/*" }),
            Node("sql", Kinds.SqlDatabase, "orders", 680, 40, new() { ["sku"] = "S1", ["backupDays"] = "35", ["privateOnly"] = "true" }),
            Node("pg", Kinds.Postgres, "catalog", 680, 150, new() { ["sku"] = "GP_Standard_D2ds_v5", ["backupDays"] = "14", ["vector"] = "true", ["privateOnly"] = "true" }),
            Node("cosmos", Kinds.CosmosDb, "carts", 680, 260, new()
            {
                ["containers"] = "carts, sessions", ["backup"] = "Continuous7Days", ["vectorSearch"] = "true", ["privateOnly"] = "true",
            }),
            Node("st", Kinds.Storage, "files", 680, 370, new()
            {
                ["replication"] = "ZRS", ["containers"] = "uploads, invoices", ["queues"] = "thumbnails", ["tables"] = "audit", ["privateOnly"] = "true",
            }),
            Node("redis", Kinds.Redis, "cache", 680, 480, new() { ["sku"] = "Standard_C1", ["privateOnly"] = "true" }),
            Node("sb", Kinds.ServiceBus, "events", 890, 40, new() { ["sku"] = "Premium", ["queues"] = "orders, emails", ["privateOnly"] = "true" }),
            Node("kv", Kinds.KeyVault, "secrets", 890, 150, new() { ["purgeProtection"] = "true", ["retentionDays"] = "90", ["privateOnly"] = "true" }),
            Node("acr", Kinds.ContainerRegistry, "images", 890, 260, new() { ["sku"] = "Standard" }),
            Node("ai", Kinds.AiFoundry, "assistant", 890, 370, new() { ["embeddings"] = "text-embedding-3-small", ["capacity"] = "50", ["privateOnly"] = "true" }),
            Node("speech", Kinds.Speech, "voice", 890, 480, new() { ["privateOnly"] = "true" }),
            Node("docs", AzureServices.AiService, "documents", 1100, 40, new() { ["privateOnly"] = "true" }),
            Node("search", AzureServices.Search, "search", 1100, 150, new() { ["sku"] = "standard", ["replicas"] = "2", ["privateOnly"] = "true" }),
            Node("hubs", AzureServices.EventHubs, "telemetry", 1100, 260, new() { ["hubs"] = "clicks, orders", ["privateOnly"] = "true" }),
            Node("grid", AzureServices.EventGrid, "notices", 1100, 370, new() { ["privateOnly"] = "true" }),
            Node("config", AzureServices.AppConfig, "settings", 1100, 480, new() { ["sku"] = "standard", ["privateOnly"] = "true" }),
            Node("signalr", AzureServices.SignalR, "live", 1310, 40, new() { ["privateOnly"] = "true" }),
            Node("pubsub", AzureServices.WebPubSub, "sockets", 1310, 150, new() { ["privateOnly"] = "true" }),
            Node("maps", AzureServices.Maps, "maps", 1310, 260, []),
            Node("acs", AzureServices.Communication, "mail", 1310, 370, []),
            Node("ml", AzureServices.MachineLearning, "training", 1310, 480, new() { ["priority"] = "LowPriority" }),
            Node("bot", AzureServices.Bot, "helpdesk", 250, 480, []),
            Node("labels", AzureServices.AiService, "labels", 1520, 40, new() { ["type"] = "customVision", ["privateOnly"] = "true" }),
            Node("faces", AzureServices.AiService, "faces", 1520, 150, new() { ["type"] = "face" }),
            Node("videos", AzureServices.VideoIndexer, "videos", 1520, 260, []),
            Node("agw", Kinds.AppGateway, "regional", 250, 370, new() { ["https"] = "keyVault", ["certificate"] = "store-site" }),
        ],
        Edges =
        [
            Edge("e1", "dns", "fd"),
            Edge("e2", "fd", "site"),
            Edge("e3", "fd", "web"),
            Edge("e4", "fd", "api"),
            Edge("e5", "fd", "jobs"),
            Edge("e6", "site", "api"),
            Edge("e7", "web", "api"),
            Edge("e8", "web", "acr"),
            Edge("e9", "api", "acr"),
            Edge("e10", "api", "sql"),
            Edge("e11", "api", "pg"),
            Edge("e12", "api", "cosmos"),
            Edge("e13", "api", "redis"),
            Edge("e14", "api", "sb"),
            Edge("e15", "api", "kv"),
            Edge("e16", "jobs", "sb"),
            Edge("e17", "jobs", "st"),
            Edge("e18", "jobs", "sql"),
            Edge("e19", "web", "st"),
            Edge("e20", "api", "ai"),
            Edge("e21", "jobs", "speech"),
            Edge("e22", "jobs", "docs"),
            Edge("e23", "api", "search"),
            Edge("e24", "jobs", "hubs"),
            Edge("e25", "api", "grid"),
            Edge("e26", "web", "config"),
            Edge("e27", "api", "signalr"),
            Edge("e28", "api", "pubsub"),
            Edge("e29", "web", "maps"),
            Edge("e30", "jobs", "acs"),
            Edge("e31", "jobs", "ml"),
            Edge("e32", "ml", "acr"),
            Edge("e33", "bot", "web"),
            Edge("e34", "api", "labels"),
            Edge("e35", "api", "faces"),
            Edge("e36", "jobs", "videos"),
            Edge("e37", "agw", "web"),
            Edge("e38", "agw", "kv"),
        ],
    };

    // AWS with smart boxes: a site in S3 and an API in Lambda behind CloudFront with a domain, a container, a queue
    // that starts a function, a table, a database and a secret.
    public static Sketch Aws() => new()
    {
        Settings = new SketchSettings { Platform = Platforms.Aws, Name = "shop", Environment = "prod", Region = "us-east-1", Options = new()
            {
                [SettingKeys.Owner] = "Store team", [SettingKeys.Pipeline] = "github", [AppPipeline.Key] = "github",
                [SettingKeys.Alerts] = "on", [SettingKeys.AlertEmail] = "ops@example.com",
                [SettingKeys.Budget] = "500", [SettingKeys.BudgetEmail] = "ops@example.com",
            } },
        Nodes =
        [
            Node("dns", CloudKinds.Route53, "domain", 40, 200, new() { ["zone"] = "example.com" }),
            Node("cdn", CloudKinds.CloudFront, "edge", 250, 200, new() { ["subdomain"] = "shop" }),
            Node("site", CloudKinds.S3, "site", 460, 40, new()),
            Node("api", CloudKinds.Lambda, "api", 460, 150, new() { ["publicUrl"] = "true", ["path"] = "/api/*" }),
            Node("web", CloudKinds.Fargate, "web", 460, 260, new() { ["path"] = "/app/*", ["count"] = "2" }),
            Node("jobs", CloudKinds.Lambda, "jobs", 460, 370, new() { ["runtime"] = "nodejs22.x", ["timeout"] = "60" }),
            Node("orders", CloudKinds.DynamoDb, "orders", 680, 40, new() { ["sortKey"] = "createdAt" }),
            Node("files", CloudKinds.S3, "files", 680, 150, new() { ["encryption"] = "aws:kms", ["expireDays"] = "365" }),
            Node("queue", CloudKinds.Sqs, "work", 680, 260, new()),
            Node("db", CloudKinds.Rds, "catalog", 680, 370, new() { ["multiAz"] = "true", ["backupDays"] = "14" }),
            Node("key", CloudKinds.AwsSecret, "payments-key", 680, 480, new()),
            Node("docs", CloudKinds.S3, "manuals", 1100, 40, new()),
            Node("assistant", CloudKinds.Bedrock, "assistant", 890, 40, new() { ["knowledgeBase"] = "true", ["agent"] = "true" }),
            Node("studio", CloudKinds.SageMaker, "studio", 890, 150, new()),
            Node("ocr", CloudKinds.AwsAiApi, "ocr", 890, 260, new()),
            Node("chat", CloudKinds.Lex, "chat", 890, 370, new() { ["locale"] = "es_US" }),
            Node("search", CloudKinds.Kendra, "search", 890, 480, new()),
            Node("vectors", CloudKinds.AwsVectors, "vectors", 1100, 150, new()),
            Node("images", CloudKinds.Ecr, "web-images", 1100, 260, new()),
        ],
        Edges =
        [
            Edge("e1", "dns", "cdn"),
            Edge("e2", "cdn", "site"),
            Edge("e3", "cdn", "api"),
            Edge("e14", "api", "assistant"),
            Edge("e15", "assistant", "docs"),
            Edge("e16", "studio", "files"),
            Edge("e17", "jobs", "ocr"),
            Edge("e18", "api", "chat"),
            Edge("e19", "search", "docs"),
            Edge("e20", "web", "search"),
            Edge("e21", "web", "vectors"),
            Edge("e22", "jobs", "studio"),
            Edge("e23", "web", "images"),
            Edge("e4", "cdn", "web"),
            Edge("e5", "api", "orders"),
            Edge("e6", "api", "files"),
            Edge("e7", "api", "queue"),
            Edge("e8", "api", "key"),
            Edge("e9", "queue", "jobs"),
            Edge("e10", "jobs", "orders"),
            Edge("e11", "web", "db"),
            Edge("e12", "web", "api"),
            Edge("e13", "web", "files"),
        ],
    };

    // Google Cloud with smart boxes: a load balancer with a domain in front of Cloud Run, a function and a website
    // bucket; a database, Firestore, events and a secret.
    public static Sketch Gcp() => new()
    {
        Settings = new SketchSettings
        {
            Platform = Platforms.Gcp, Name = "shop", Environment = "prod", Region = "us-central1",
            Options = new()
            {
                [SettingKeys.GcpProject] = "shop-prod-123456", [SettingKeys.Owner] = "Store team", [SettingKeys.Pipeline] = "github", [AppPipeline.Key] = "github",
                [Team.GcpAdmins] = "cloud-admins@example.com", [Team.GcpDevelopers] = "developers@example.com",
                [SettingKeys.Alerts] = "on", [SettingKeys.AlertEmail] = "ops@example.com", [SettingKeys.Budget] = "500", [SettingKeys.BudgetEmail] = "ops@example.com",
                [CloudAlerts.GcpBillingAccount] = "01ABCD-23EF45-6789AB",
            },
        },
        Nodes =
        [
            Node("dns", CloudKinds.CloudDns, "domain", 40, 200, new() { ["zone"] = "example.com" }),
            Node("lb", CloudKinds.LoadBalancer, "edge", 250, 200, new() { ["domain"] = "shop.example.com" }),
            Node("site", CloudKinds.Bucket, "site", 460, 40, new() { ["website"] = "true", ["versioning"] = "false" }),
            Node("api", CloudKinds.CloudRun, "api", 460, 150, new() { ["path"] = "/api/*", ["public"] = "false", ["minInstances"] = "1" }),
            Node("hooks", CloudKinds.CloudFunction, "hooks", 460, 260, new() { ["path"] = "/hooks/*", ["runtime"] = "nodejs22" }),
            Node("worker", CloudKinds.CloudFunction, "worker", 460, 370, new()),
            Node("sql", CloudKinds.CloudSql, "catalog", 680, 40, new() { ["highAvailability"] = "true", ["tier"] = "db-custom-2-7680" }),
            Node("carts", CloudKinds.Firestore, "carts", 680, 150, new()),
            Node("files", CloudKinds.Bucket, "files", 680, 260, new() { ["expireDays"] = "365" }),
            Node("events", CloudKinds.PubSub, "events", 680, 370, new() { ["subscriptions"] = "worker, audit" }),
            Node("key", CloudKinds.GcpSecret, "payments-key", 680, 480, new()),
            Node("gemini", CloudKinds.VertexAi, "assistant", 890, 40, new()),
            Node("vectors", CloudKinds.GcpVectors, "vectors", 890, 150, new()),
            Node("search", CloudKinds.AiSearch, "search", 890, 260, new()),
            Node("invoices", CloudKinds.DocumentAi, "invoices", 890, 370, new() { ["type"] = "INVOICE_PROCESSOR" }),
            Node("translate", CloudKinds.GcpAiApi, "translate", 890, 480, new() { ["service"] = "translate" }),
            Node("chat", CloudKinds.Dialogflow, "chat", 1100, 40, new() { ["language"] = "es", ["timeZone"] = "America/Mexico_City" }),
            Node("lab", CloudKinds.Workbench, "lab", 1100, 150, new()),
            Node("images", CloudKinds.ArtifactRegistry, "images", 1100, 260, new()),
        ],
        Edges =
        [
            Edge("e1", "dns", "lb"),
            Edge("e2", "lb", "site"),
            Edge("e3", "lb", "api"),
            Edge("e4", "lb", "hooks"),
            Edge("e5", "api", "sql"),
            Edge("e6", "api", "carts"),
            Edge("e7", "api", "files"),
            Edge("e8", "api", "events"),
            Edge("e9", "api", "key"),
            Edge("e10", "hooks", "events"),
            Edge("e11", "worker", "events"),
            Edge("e12", "worker", "files"),
            Edge("e13", "hooks", "api"),
            Edge("e14", "api", "gemini"),
            Edge("e15", "api", "vectors"),
            Edge("e16", "api", "search"),
            Edge("e17", "worker", "invoices"),
            Edge("e18", "worker", "translate"),
            Edge("e19", "hooks", "chat"),
            Edge("e20", "lab", "files"),
            Edge("e21", "api", "images"),
        ],
    };

    // AWS from the full provider list: files with versions, a table, a queue with its dead-letter queue, a topic.
    public static Sketch AwsProviderBoxes() => new()
    {
        Settings = new SketchSettings { Platform = Platforms.Aws, Name = "shop", Environment = "dev", Region = "us-east-1", Options = new() { [SettingKeys.Owner] = "Store team" } },
        Nodes =
        [
            Node("bucket", "tf:aws_s3_bucket", "files", 40, 40, new() { ["bucket_prefix"] = "shop-files-", ["force_destroy"] = "false" }),
            Node("versioning", "tf:aws_s3_bucket_versioning", "files-versioning", 270, 40, new() { ["bucket"] = "ref:bucket.id", ["versioning_configuration.status"] = "Enabled" }),
            Node("table", "tf:aws_dynamodb_table", "orders", 40, 160, new()
            {
                ["billing_mode"] = "PAY_PER_REQUEST", ["hash_key"] = "id", ["attribute.name"] = "id", ["attribute.type"] = "S", ["point_in_time_recovery.enabled"] = "true",
            }),
            Node("dead", "tf:aws_sqs_queue", "jobs-failed", 270, 280, new() { ["message_retention_seconds"] = "1209600" }),
            Node("queue", "tf:aws_sqs_queue", "jobs", 40, 280, new()
            {
                ["visibility_timeout_seconds"] = "60",
                [GenericGenerator.ExtraHcl] = "redrive_policy = jsonencode({\n  deadLetterTargetArn = aws_sqs_queue.jobs_failed.arn\n  maxReceiveCount     = 5\n})",
            }),
            Node("topic", "tf:aws_sns_topic", "events", 500, 160, new()),
        ],
        Edges = [Edge("e1", "versioning", "bucket")],
    };

    // Google Cloud from the full provider list: files, a container service, a registry for its images, events.
    public static Sketch GcpProviderBoxes() => new()
    {
        Settings = new SketchSettings
        {
            Platform = Platforms.Gcp, Name = "shop", Environment = "dev", Region = "us-central1",
            Options = new() { [SettingKeys.GcpProject] = "shop-dev-123456", [SettingKeys.Owner] = "Store team" },
        },
        Nodes =
        [
            Node("bucket", "tf:google_storage_bucket", "files", 40, 40, new() { ["uniform_bucket_level_access"] = "true", ["versioning.enabled"] = "true" }),
            Node("registry", "tf:google_artifact_registry_repository", "images", 40, 160, new() { ["format"] = "DOCKER", ["repository_id"] = "shop-images" }),
            Node("service", "tf:google_cloud_run_v2_service", "api", 270, 100, new()
            {
                ["deletion_protection"] = "false",
                [GenericGenerator.ExtraHcl] = "template {\n  containers {\n    image = \"us-docker.pkg.dev/cloudrun/container/hello\"\n  }\n}",
            }),
            Node("topic", "tf:google_pubsub_topic", "events", 500, 40, new()),
            Node("subscription", "tf:google_pubsub_subscription", "events-worker", 500, 160, new() { ["topic"] = "ref:topic.id", ["ack_deadline_seconds"] = "30" }),
        ],
        Edges = [Edge("e1", "subscription", "topic")],
    };

    private static SketchNode Node(string id, string kind, string name, double x, double y, Dictionary<string, string> props) =>
        new() { Id = id, Kind = kind, Name = name, X = x, Y = y, Props = props };

    private static SketchEdge Edge(string id, string from, string to) => new() { Id = id, From = from, To = to };
}
