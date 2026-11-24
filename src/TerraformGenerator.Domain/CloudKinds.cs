using static TerraformGenerator.Domain.Catalog;

namespace TerraformGenerator.Domain;

// The smart boxes of AWS and Google Cloud: like the Azure ones, each adds its supporting pieces (roles, logs,
// encryption, network) and every arrow from code gives that code access and the address as a setting.
public static class CloudKinds
{
    // AWS.
    public const string Lambda = "awsLambda";
    public const string Fargate = "awsFargate";
    public const string S3 = "awsS3";
    public const string DynamoDb = "awsDynamoDb";
    public const string Sqs = "awsSqs";
    public const string Rds = "awsRds";
    public const string AwsSecret = "awsSecret";
    public const string CloudFront = "awsCloudFront";
    public const string Route53 = "awsRoute53";
    public const string Ecr = "awsEcr";
    public const string Bedrock = "awsBedrock";
    public const string SageMaker = "awsSageMaker";
    public const string AwsAiApi = "awsAiApi";
    public const string Lex = "awsLex";
    public const string Kendra = "awsKendra";
    public const string AwsVectors = "awsVectors";

    // Google Cloud.
    public const string CloudRun = "gcpCloudRun";
    public const string CloudFunction = "gcpFunction";
    public const string CloudSql = "gcpCloudSql";
    public const string Firestore = "gcpFirestore";
    public const string Bucket = "gcpBucket";
    public const string PubSub = "gcpPubSub";
    public const string GcpSecret = "gcpSecret";
    public const string LoadBalancer = "gcpLoadBalancer";
    public const string CloudDns = "gcpDns";
    public const string ArtifactRegistry = "gcpArtifactRegistry";
    public const string VertexAi = "gcpVertexAi";
    public const string GcpVectors = "gcpVectorSearch";
    public const string AiSearch = "gcpAiSearch";
    public const string DocumentAi = "gcpDocumentAi";
    public const string GcpAiApi = "gcpAiApi";
    public const string Dialogflow = "gcpDialogflow";
    public const string Workbench = "gcpWorkbench";

    public static readonly IReadOnlyList<string> AwsCompute = [Lambda, Fargate];
    public static readonly IReadOnlyList<string> GcpCompute = [CloudRun, CloudFunction];
    public static readonly IReadOnlyList<string> CloudFrontOrigins = [S3, Lambda, Fargate];
    public static readonly IReadOnlyList<string> LoadBalancerOrigins = [CloudRun, CloudFunction, Bucket];

    public static bool IsCompute(string kind) => AwsCompute.Contains(kind) || GcpCompute.Contains(kind);

    // The AI services code can use, by cloud.
    public static readonly IReadOnlyList<string> AwsAi = [Bedrock, SageMaker, AwsAiApi, Lex, Kendra, AwsVectors];
    public static readonly IReadOnlyList<string> GcpAi = [VertexAi, GcpVectors, AiSearch, DocumentAi, GcpAiApi, Dialogflow];

    // Bedrock models, invoked through the cross-region inference profile of the region's geography.
    public static readonly IReadOnlyList<PropertyOption> BedrockModels =
    [
        O("anthropic.claude-sonnet-4-5-20250929-v1:0", "Claude Sonnet 4.5 (Anthropic)"), O("anthropic.claude-haiku-4-5-20251001-v1:0", "Claude Haiku 4.5 (Anthropic)"),
        O("amazon.nova-pro-v1:0", "Amazon Nova Pro"), O("amazon.nova-lite-v1:0", "Amazon Nova Lite"), O("meta.llama3-3-70b-instruct-v1:0", "Llama 3.3 70B (Meta)"),
    ];

    public static string BedrockProfile(string model, string region) =>
        (region.StartsWith("eu-", StringComparison.Ordinal) ? "eu" : region.StartsWith("ap-", StringComparison.Ordinal) ? "apac" : "us") + "." + model;

    // The AWS AI services that are only called (no resource to create): the actions each one needs.
    public static readonly IReadOnlyDictionary<string, string[]> AwsApiActions = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["textract"] = ["textract:AnalyzeDocument", "textract:AnalyzeExpense", "textract:AnalyzeID", "textract:DetectDocumentText", "textract:StartDocumentAnalysis",
            "textract:GetDocumentAnalysis", "textract:StartDocumentTextDetection", "textract:GetDocumentTextDetection"],
        ["comprehend"] = ["comprehend:Detect*", "comprehend:BatchDetect*", "comprehend:ClassifyDocument", "comprehend:ContainsPiiEntities"],
        ["rekognition"] = ["rekognition:Detect*", "rekognition:Recognize*", "rekognition:CompareFaces"],
        ["transcribe"] = ["transcribe:StartTranscriptionJob", "transcribe:GetTranscriptionJob", "transcribe:StartStreamTranscription", "transcribe:StartStreamTranscriptionWebSocket"],
        ["polly"] = ["polly:SynthesizeSpeech", "polly:DescribeVoices"],
        ["translate"] = ["translate:TranslateText", "translate:TranslateDocument"],
    };

    // The Google AI APIs that are only called: the API to turn on and the role code needs besides using the project.
    public static readonly IReadOnlyDictionary<string, (string Api, string? Role)> GcpApis = new Dictionary<string, (string, string?)>(StringComparer.Ordinal)
    {
        ["speech"] = ("speech.googleapis.com", "roles/speech.client"),
        ["texttospeech"] = ("texttospeech.googleapis.com", null),
        ["translate"] = ("translate.googleapis.com", "roles/cloudtranslate.user"),
        ["vision"] = ("vision.googleapis.com", null),
        ["language"] = ("language.googleapis.com", null),
    };

    private const string DomainPattern = "^([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z]{2,63}$";
    private const string KeyPattern = "^[A-Za-z_][A-Za-z0-9_.-]{0,254}$";
    private const string OptionalKeyPattern = "^$|^[A-Za-z_][A-Za-z0-9_.-]{0,254}$";

    private static PropertyDefinition Path() =>
        new("path", "Path behind the entry", PropertyType.Text, "/*", [], PathPattern,
            "Used when CloudFront or a load balancer serves it: the path sent here, for example /* or /api/*.");

    public static readonly IReadOnlyList<ResourceKind> Aws =
    [
        new(Lambda, "Lambda function", "λ", "Apps", "An AWS Lambda function for APIs and background jobs. Its role, logs and a starter code folder are added for you.",
        [
            new("runtime", "Runtime", PropertyType.Select, "python3.13",
            [
                O("python3.13", "Python 3.13"), O("nodejs22.x", "Node.js 22"), O("java21", "Java 21"), O("dotnet8", ".NET 8"),
            ]),
            new("memory", "Memory", PropertyType.Select, "512", [O("128", "128 MB"), O("256", "256 MB"), O("512", "512 MB"), O("1024", "1 GB"), O("2048", "2 GB")]),
            new("timeout", "Time limit", PropertyType.Select, "30", [O("10", "10 seconds"), O("30", "30 seconds"), O("60", "1 minute"), O("300", "5 minutes"), O("900", "15 minutes")]),
            Toggle("publicUrl", "Public HTTPS address", false, "A function URL anyone can call (or CloudFront in front of it)."),
            Path(),
        ], Platforms.Aws),
        new(Fargate, "Container (ECS Fargate)", "ECS", "Apps", "A container on ECS Fargate behind a load balancer. The cluster, roles, logs and security groups are added for you.",
        [
            new("image", "Image", PropertyType.Text, "public.ecr.aws/nginx/nginx:stable", [], ImagePattern, "The container image, for example public.ecr.aws/nginx/nginx:stable or your ECR image."),
            new("size", "Size", PropertyType.Select, "512",
                [O("256", "0.25 vCPU, 0.5 GB"), O("512", "0.5 vCPU, 1 GB"), O("1024", "1 vCPU, 2 GB"), O("2048", "2 vCPU, 4 GB")]),
            new("count", "Copies", PropertyType.Select, "1", [O("1", "1"), O("2", "2"), O("3", "3")]),
            new("port", "Port the container listens on", PropertyType.Select, "80",
                [O("80", "80"), O("8080", "8080"), O("3000", "3000"), O("5000", "5000"), O("8000", "8000")]),
            Toggle("public", "Reachable from the internet", true, "A public load balancer. Off keeps it inside the network."),
            Firewall.Option("AWS WAF on its public load balancer. Behind CloudFront, the CloudFront firewall is used and the load balancer takes traffic only from CloudFront."),
            Firewall.Rate(),
            Path(),
        ], Platforms.Aws),
        new(S3, "S3 bucket", "S3", "Data", "An S3 bucket for files: encrypted, no public access, HTTPS only.",
        [
            Toggle("versioning", "Keep every version", true, "Each change keeps the previous version of the file."),
            new("expireDays", "Delete files after", PropertyType.Select, "0",
                [O("0", "Never"), O("30", "30 days"), O("90", "90 days"), O("365", "1 year")]),
            new("encryption", "Encryption", PropertyType.Select, "AES256",
                [O("AES256", "S3 managed keys"), O("aws:kms", "AWS KMS (audit every use of the key)")]),
            Path(),
        ], Platforms.Aws),
        new(DynamoDb, "DynamoDB table", "DDB", "Data", "A DynamoDB table, paid per request, encrypted, with point-in-time recovery.",
        [
            new("partitionKey", "Partition key", PropertyType.Text, "id", [], KeyPattern, "The attribute that identifies each item, for example id."),
            new("sortKey", "Sort key", PropertyType.Text, "", [], OptionalKeyPattern, "Optional: a second attribute to sort items with the same partition key."),
            Toggle("recovery", "Point-in-time recovery", true, "Brings the table back to any second of the last 35 days."),
        ], Platforms.Aws),
        new(Sqs, "SQS queue", "SQS", "Messaging", "An SQS queue for work between apps, encrypted, with a queue for failed messages.",
        [
            new("visibility", "Time to handle a message", PropertyType.Select, "60", [O("30", "30 seconds"), O("60", "1 minute"), O("300", "5 minutes"), O("900", "15 minutes")]),
            Toggle("deadLetter", "Queue for failed messages", true, "After 5 failed tries a message moves there instead of being lost."),
            Toggle("fifo", "First in, first out", false, "Exactly once and in order (FIFO queue)."),
        ], Platforms.Aws),
        new(Rds, "RDS PostgreSQL", "RDS", "Data", "An RDS PostgreSQL database, private, encrypted, with backups. Its password is kept in Secrets Manager.",
        [
            new("version", "Version", PropertyType.Select, "17", [O("15", "15"), O("16", "16"), O("17", "17")]),
            new("size", "Size", PropertyType.Select, "db.t4g.micro",
                [O("db.t4g.micro", "db.t4g.micro (2 vCPU, 1 GB)"), O("db.t4g.small", "db.t4g.small (2 vCPU, 2 GB)"), O("db.m7g.large", "db.m7g.large (2 vCPU, 8 GB)")]),
            new("storageGb", "Storage", PropertyType.Select, "20", [O("20", "20 GB"), O("50", "50 GB"), O("100", "100 GB")]),
            new("backupDays", "Backups", PropertyType.Select, "7", [O("1", "1 day"), O("7", "7 days"), O("14", "14 days"), O("35", "35 days")]),
            Toggle("multiAz", "Standby copy in another zone", false, "Keeps working if a zone fails; costs twice as much."),
        ], Platforms.Aws),
        new(AwsSecret, "Secrets Manager secret", "SEC", "Security", "A secret in AWS Secrets Manager. You put its value in after deploying; connected code can read it.",
        [
            new("recoveryDays", "Can be restored for", PropertyType.Select, "30", [O("7", "7 days"), O("30", "30 days")]),
        ], Platforms.Aws),
        new(CloudFront, "CloudFront", "CF", "Edge", "A CloudFront distribution: one HTTPS entry with a cache in front of S3, Lambda or containers.",
        [
            new("priceClass", "Where it serves from", PropertyType.Select, "PriceClass_100",
            [
                O("PriceClass_100", "North America and Europe (cheapest)"), O("PriceClass_200", "Most of the world"), O("PriceClass_All", "Everywhere"),
            ]),
            new("subdomain", "Subdomain", PropertyType.Text, "www", [], "^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$", "With a Route 53 arrow: the name in front of the domain."),
            Firewall.Option("AWS WAF with the rules AWS keeps up to date: common attacks, known bad inputs and addresses with a bad reputation."),
            Firewall.Rate(),
        ], Platforms.Aws),
        new(Route53, "Route 53 zone", "R53", "Edge", "A public DNS zone for your domain. Point it to CloudFront for a custom domain with a free certificate.",
        [
            new("zone", "Domain", PropertyType.Text, "example.com", [], DomainPattern, "The domain you own, for example example.com."),
        ], Platforms.Aws),
        new(Ecr, "ECR repository", "ECR", "Security", "A private repository for a container's images, scanned on every push; it keeps the last 30.",
        [], Platforms.Aws),
        new(Bedrock, "Bedrock", "BR", "AI",
            "AI models on Amazon Bedrock, with optional safety filters, a knowledge base that reads an S3 bucket, and an agent. Code calls them with its role.",
        [
            new("model", "Model", PropertyType.Select, "anthropic.claude-sonnet-4-5-20250929-v1:0", BedrockModels, null,
                "Anthropic models ask for a short use-case form once per account in the Bedrock console."),
            Toggle("guardrail", "Safety filters (guardrail)", true, "Blocks hateful, violent or sexual content and prompt attacks, and hides emails and phone numbers."),
            Toggle("knowledgeBase", "Knowledge base", false, "Reads the files of the S3 bucket its arrow points to, so answers can quote your documents."),
            Toggle("agent", "Agent", false, "An agent with your instructions that uses the model, the filters and the knowledge base."),
            new("instructions", "Agent instructions", PropertyType.Text, "Answer the questions of our customers clearly and briefly, using the company documents.", [],
                "^[^\\n]{40,4000}$", "What the agent does, in 40 to 4,000 characters.", null, "agent=true"),
        ], Platforms.Aws),
        new(SageMaker, "SageMaker", "SM", "AI",
            "A SageMaker Studio domain to train and publish your own models. Its role and network are added for you; connected code can call the published endpoints.",
        [], Platforms.Aws),
        new(AwsAiApi, "AI service (AWS)", "API", "AI", "Textract, Comprehend, Rekognition, Transcribe, Polly or Translate: nothing to create, connected code gets permission to call it.",
        [
            new("service", "Service", PropertyType.Select, "textract",
            [
                O("textract", "Textract (text and forms from documents)"), O("comprehend", "Comprehend (language, sentiment, entities)"),
                O("rekognition", "Rekognition (images and video)"), O("transcribe", "Transcribe (speech to text)"), O("polly", "Polly (text to speech)"),
                O("translate", "Translate"),
            ]),
        ], Platforms.Aws),
        new(Lex, "Lex bot", "LEX", "AI", "An Amazon Lex chatbot with its language. Add its intents in the Lex console; connected code talks to it.",
        [
            new("locale", "Language", PropertyType.Select, "en_US", [O("en_US", "English (US)"), O("es_US", "Spanish (US)"), O("es_ES", "Spanish (Spain)")]),
        ], Platforms.Aws),
        new(Kendra, "Kendra index", "KEN", "AI", "An Amazon Kendra search index; an arrow to an S3 bucket indexes its documents. It costs from about 800 dollars a month.",
        [
            new("edition", "Edition", PropertyType.Select, "DEVELOPER_EDITION",
                [O("DEVELOPER_EDITION", "Developer (testing)"), O("ENTERPRISE_EDITION", "Enterprise (production)")]),
        ], Platforms.Aws),
        new(AwsVectors, "OpenSearch vectors", "OSV", "AI", "An OpenSearch Serverless collection for vector search (search by meaning). Connected code reads and writes its indexes.",
        [], Platforms.Aws),
    ];

    public static readonly IReadOnlyList<ResourceKind> Gcp =
    [
        new(CloudRun, "Cloud Run service", "RUN", "Apps", "A container on Cloud Run that scales by itself. Its service account is added for you.",
        [
            new("image", "Image", PropertyType.Text, "us-docker.pkg.dev/cloudrun/container/hello", [], ImagePattern, "The container image, for example from Artifact Registry."),
            new("size", "Size", PropertyType.Select, "1",
                [O("1", "1 CPU, 512 MB"), O("1-1", "1 CPU, 1 GB"), O("2", "2 CPU, 2 GB"), O("4", "4 CPU, 4 GB")]),
            new("minInstances", "Always running", PropertyType.Select, "0", [O("0", "0: stops when idle (cheapest)"), O("1", "1 copy"), O("2", "2 copies")]),
            new("maxInstances", "Most copies", PropertyType.Select, "10", [O("3", "3"), O("10", "10"), O("50", "50")]),
            new("port", "Port the container listens on", PropertyType.Select, "8080",
                [O("80", "80"), O("8080", "8080"), O("3000", "3000"), O("5000", "5000"), O("8000", "8000")]),
            Toggle("public", "Anyone can call it", true, "Off asks for a Google identity on every call."),
            Path(),
        ], Platforms.Gcp),
        new(CloudFunction, "Cloud Function", "FN", "Apps", "A Cloud Run function (2nd gen) for APIs and background jobs. Its service account and a starter code folder are added for you.",
        [
            new("runtime", "Runtime", PropertyType.Select, "python312",
                [O("python312", "Python 3.12"), O("nodejs22", "Node.js 22"), O("go123", "Go 1.23"), O("java21", "Java 21")]),
            new("memory", "Memory", PropertyType.Select, "256M", [O("256M", "256 MB"), O("512M", "512 MB"), O("1Gi", "1 GB"), O("2Gi", "2 GB")]),
            Toggle("public", "Anyone can call it", false, "Off asks for a Google identity on every call."),
            Path(),
        ], Platforms.Gcp),
        new(CloudSql, "Cloud SQL PostgreSQL", "SQL", "Data", "A Cloud SQL PostgreSQL instance with one database. Apps sign in with their service account (no passwords).",
        [
            new("version", "Version", PropertyType.Select, "POSTGRES_17", [O("POSTGRES_15", "15"), O("POSTGRES_16", "16"), O("POSTGRES_17", "17")]),
            new("tier", "Size", PropertyType.Select, "db-g1-small",
                [O("db-f1-micro", "Shared core, 0.6 GB (testing)"), O("db-g1-small", "Shared core, 1.7 GB"), O("db-custom-2-7680", "2 vCPU, 7.5 GB")]),
            new("backupDays", "Backups", PropertyType.Select, "7", [O("7", "7 days"), O("14", "14 days"), O("30", "30 days")]),
            Toggle("highAvailability", "Standby copy in another zone", false, "Keeps working if a zone fails; costs twice as much."),
        ], Platforms.Gcp),
        new(Firestore, "Firestore", "FS", "Data", "A Firestore database (native mode), with point-in-time recovery.",
        [
            Toggle("recovery", "Point-in-time recovery", true, "Brings the data back to any minute of the last 7 days."),
        ], Platforms.Gcp),
        new(Bucket, "Cloud Storage bucket", "GCS", "Data", "A Cloud Storage bucket for files: no public access, one permission model.",
        [
            Toggle("versioning", "Keep every version", true, "Each change keeps the previous version of the file."),
            new("expireDays", "Delete files after", PropertyType.Select, "0",
                [O("0", "Never"), O("30", "30 days"), O("90", "90 days"), O("365", "1 year")]),
            new("storageClass", "Storage class", PropertyType.Select, "STANDARD",
                [O("STANDARD", "Standard"), O("NEARLINE", "Nearline (read once a month)"), O("COLDLINE", "Coldline (read once a quarter)")]),
            Toggle("website", "Public website", false, "Anyone can read the files; use it behind a load balancer."),
            Path(),
        ], Platforms.Gcp),
        new(PubSub, "Pub/Sub topic", "PS", "Messaging", "A Pub/Sub topic with its subscriptions, and a topic for failed messages.",
        [
            new("subscriptions", "Subscriptions", PropertyType.Text, "worker", [], ListPattern, "Names separated by commas; each gets every message."),
            Toggle("deadLetter", "Topic for failed messages", true, "After 5 failed tries a message moves there instead of being retried forever."),
        ], Platforms.Gcp),
        new(GcpSecret, "Secret Manager secret", "SEC", "Security", "A secret in Secret Manager. You put its value in after deploying; connected code can read it.",
        [], Platforms.Gcp),
        new(LoadBalancer, "HTTPS load balancer", "LB", "Edge", "A global HTTPS load balancer: one address with a Google managed certificate in front of Cloud Run, functions and buckets.",
        [
            new("domain", "Domain", PropertyType.Text, "www.example.com", [], DomainPattern, "The name people use; its certificate is issued once the DNS points here."),
            Firewall.Option("Cloud Armor with Google's rules for the common attacks (SQL injection, scripts, scanners) on the services behind it."),
            Firewall.Rate(),
        ], Platforms.Gcp),
        new(CloudDns, "Cloud DNS zone", "DNS", "Edge", "A public DNS zone for your domain. Point it to the load balancer to publish its address.",
        [
            new("zone", "Domain", PropertyType.Text, "example.com", [], DomainPattern, "The domain you own, for example example.com."),
        ], Platforms.Gcp),
        new(ArtifactRegistry, "Artifact Registry", "AR", "Security", "A private Docker repository for Cloud Run's images; it keeps the last 30 versions.",
        [], Platforms.Gcp),
        new(VertexAi, "Vertex AI (Gemini)", "GEM", "AI", "Gemini models on Vertex AI. Nothing to create: the API is turned on and connected code gets permission to call it.",
        [
            new("model", "Model", PropertyType.Select, "gemini-2.5-flash",
                [O("gemini-2.5-flash", "Gemini 2.5 Flash"), O("gemini-2.5-pro", "Gemini 2.5 Pro"), O("gemini-2.5-flash-lite", "Gemini 2.5 Flash-Lite")]),
        ], Platforms.Gcp),
        new(GcpVectors, "Vector Search", "VS", "AI", "Vertex AI Vector Search: an index you add vectors to, served by an endpoint that is always on (about 70 dollars a month or more).",
        [
            new("dimensions", "Vector size", PropertyType.Select, "768", [O("768", "768 (text-embedding-005)"), O("1536", "1536"), O("3072", "3072 (gemini-embedding-001)")]),
        ], Platforms.Gcp),
        new(AiSearch, "Vertex AI Search", "VAS", "AI", "Vertex AI Search over your documents, with AI answers. Import the documents in the console; connected code searches it.", [], Platforms.Gcp),
        new(DocumentAi, "Document AI", "DOC", "AI", "A Document AI processor that reads documents. Connected code sends documents to it.",
        [
            new("type", "Processor", PropertyType.Select, "FORM_PARSER_PROCESSOR",
            [
                O("FORM_PARSER_PROCESSOR", "Form parser"), O("OCR_PROCESSOR", "Text (OCR)"), O("INVOICE_PROCESSOR", "Invoices"),
                O("EXPENSE_PROCESSOR", "Receipts and expenses"),
            ]),
        ], Platforms.Gcp),
        new(GcpAiApi, "AI service (Google)", "API", "AI", "Speech-to-Text, Text-to-Speech, Translation, Vision or Natural Language: the API is turned on and connected code gets permission.",
        [
            new("service", "Service", PropertyType.Select, "speech",
            [
                O("speech", "Speech-to-Text"), O("texttospeech", "Text-to-Speech"), O("translate", "Translation"), O("vision", "Vision (images, OCR)"),
                O("language", "Natural Language"),
            ]),
        ], Platforms.Gcp),
        new(Dialogflow, "Dialogflow CX agent", "DF", "AI", "A Dialogflow CX chatbot agent. Design its flows in the console; connected code talks to it.",
        [
            new("language", "Language", PropertyType.Select, "en", [O("en", "English"), O("es", "Spanish")]),
            new("timeZone", "Time zone", PropertyType.Select, "America/New_York",
            [
                O("America/New_York", "New York"), O("America/Chicago", "Chicago"), O("America/Los_Angeles", "Los Angeles"), O("America/Mexico_City", "Mexico City"),
                O("America/Sao_Paulo", "São Paulo"), O("Europe/Madrid", "Madrid"), O("Europe/London", "London"),
            ]),
        ], Platforms.Gcp),
        new(Workbench, "Vertex AI Workbench", "WB", "AI", "A JupyterLab notebook machine to train models. It stops by itself after an idle hour; an arrow to a bucket lets it use the files.",
        [
            new("machine", "Machine", PropertyType.Select, "e2-standard-4",
                [O("e2-standard-4", "e2-standard-4 (4 vCPU, 16 GB)"), O("n2-standard-8", "n2-standard-8 (8 vCPU, 32 GB)"), O("n1-standard-8", "n1-standard-8 (8 vCPU, 30 GB)")]),
        ], Platforms.Gcp),
    ];

    private static IEnumerable<ConnectionDefinition> From(IReadOnlyList<string> apps, string to, string description) =>
        apps.Select(app => new ConnectionDefinition(app, to, description));

    public static readonly IReadOnlyList<ConnectionDefinition> Connections =
    [
        .. From(AwsCompute, S3, "The code reads and writes the files: its role gets access to the bucket and the bucket name as a setting."),
        .. From(AwsCompute, DynamoDb, "The code reads and writes the table: its role gets access and the table name as a setting."),
        .. From(AwsCompute, Sqs, "The code sends and receives messages: its role gets access and the queue URL as a setting."),
        .. From(AwsCompute, Rds, "The code uses the database: it gets network access, the host, and read access to the secret with the password."),
        .. From(AwsCompute, AwsSecret, "The code reads the secret: its role gets access and the secret's ARN as a setting."),
        .. AwsCompute.SelectMany(from => AwsCompute.Select(to => new ConnectionDefinition(from, to, "The code calls the other one: it gets its address as a setting."))),
        .. CloudFrontOrigins.Select(to => new ConnectionDefinition(CloudFront, to, "CloudFront serves it on its path.")),
        new(Route53, CloudFront, "The domain points to CloudFront: a free certificate and the DNS records."),
        .. From(AwsCompute, Bedrock, "The code calls the models, the knowledge base and the agent: its role gets permission and their ids as settings."),
        .. From(AwsCompute, SageMaker, "The code calls the models published from SageMaker: its role may invoke the endpoints."),
        .. From(AwsCompute, AwsAiApi, "The code calls the service: its role gets permission."),
        .. From(AwsCompute, Lex, "The code talks to the bot: its role gets permission and the bot id as a setting."),
        .. From(AwsCompute, Kendra, "The code searches the index: its role gets permission and the index id as a setting."),
        .. From(AwsCompute, AwsVectors, "The code reads and writes the vector indexes: access in the collection's policy and the endpoint as a setting."),
        new(Bedrock, S3, "The knowledge base reads the documents of this bucket."),
        new(Fargate, Ecr, "The container's image comes from this repository; the app pipeline builds it there."),
        new(SageMaker, S3, "SageMaker reads and writes training data in this bucket."),
        new(Kendra, S3, "The index reads the documents of this bucket."),
        new(Sqs, Lambda, "Messages in the queue start the function."),

        .. From(GcpCompute, Bucket, "The code reads and writes the files: its service account gets access and the bucket name as a setting."),
        .. From(GcpCompute, Firestore, "The code uses Firestore: its service account gets access and the database name as a setting."),
        .. From(GcpCompute, PubSub, "The code publishes and receives messages: its service account gets access and the topic as a setting."),
        .. From(GcpCompute, CloudSql, "The code uses the database: its service account signs in to it, and gets the connection name as a setting."),
        .. From(GcpCompute, GcpSecret, "The code reads the secret: its service account gets access and the secret's name as a setting."),
        .. GcpCompute.SelectMany(from => GcpCompute.Select(to => new ConnectionDefinition(from, to, "The code calls the other one: it gets its address and permission to call it."))),
        .. LoadBalancerOrigins.Select(to => new ConnectionDefinition(LoadBalancer, to, "The load balancer serves it on its path.")),
        new(CloudDns, LoadBalancer, "The domain points to the load balancer's address."),
        .. From(GcpCompute, VertexAi, "The code calls Gemini: its service account gets the Vertex AI User role and the model as a setting."),
        .. From(GcpCompute, GcpVectors, "The code queries and updates the vectors: its service account gets the Vertex AI User role and the ids as settings."),
        .. From(GcpCompute, AiSearch, "The code searches: its service account gets the Discovery Engine User role and the engine as a setting."),
        .. From(GcpCompute, DocumentAi, "The code sends documents: its service account gets the Document AI API User role and the processor as a setting."),
        .. From(GcpCompute, GcpAiApi, "The code calls the API: its service account gets permission to use it."),
        .. From(GcpCompute, Dialogflow, "The code talks to the agent: its service account gets the Dialogflow API Client role and the agent as a setting."),
        new(Workbench, Bucket, "The notebook reads and writes the files of this bucket."),
        new(CloudRun, ArtifactRegistry, "The service's image comes from this registry; the app pipeline builds it there."),
    ];
}
