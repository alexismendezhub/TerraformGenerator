namespace TerraformGenerator.Domain;

// Terraform for the AWS smart boxes. Each box adds what it needs to work safely (roles, logs, encryption, no public
// access) and each arrow from code adds a statement to that code's role and the address as an environment variable.
public static partial class AwsGenerator
{
    public static bool Any(SketchModel model) => model.Resources.Any(resource => CloudKinds.Aws.Any(kind => kind.Kind == resource.Kind));

    // The default VPC holds the containers, the databases and the functions that reach a database.
    public static bool NeedsNetwork(SketchModel model) =>
        model.OfKind(CloudKinds.Fargate).Any() || model.OfKind(CloudKinds.Rds).Any() || model.OfKind(CloudKinds.SageMaker).Any();

    public static bool NeedsCertificate(SketchModel model) =>
        model.OfKind(CloudKinds.Route53).Any(zone => model.Targets(zone, CloudKinds.CloudFront).Any());

    // CloudFront takes its certificates and its firewall from us-east-1.
    public static bool NeedsUsEast1(SketchModel model) =>
        NeedsCertificate(model) || model.OfKind(CloudKinds.CloudFront).Any(Firewall.On);

    // A public container behind CloudFront takes traffic only from CloudFront; its own firewall is for when it stands alone.
    private static bool BehindCloudFront(SketchModel model, SketchResource container) => model.Sources(container, CloudKinds.CloudFront).Any();

    private static bool LoadBalancerFirewall(SketchModel model, SketchResource container) =>
        container.Flag("public") && !BehindCloudFront(model, container) && Firewall.On(container);

    private static readonly (string Name, string Group)[] ManagedRules =
    [
        ("common", "AWSManagedRulesCommonRuleSet"), ("bad-inputs", "AWSManagedRulesKnownBadInputsRuleSet"), ("bad-addresses", "AWSManagedRulesAmazonIpReputationList"),
    ];

    private static HclBlock WebAcl(SketchModel model, SketchResource entry, string scope)
    {
        var blocks = Firewall.Blocks(model, entry);
        var acl = new HclBlock("resource", "aws_wafv2_web_acl", entry.Label);
        if (scope == "CLOUDFRONT")
        {
            acl.Attr("provider", "aws.us_east_1");
        }

        acl.Attr("name", Hcl.Str(Name(entry)))
            .Str("scope", scope)
            .Block("default_action", action => action.Block("allow", _ => { }));
        var priority = 1;
        foreach (var (name, group) in ManagedRules)
        {
            var order = priority++;
            acl.Block("rule", rule => rule
                .Str("name", name)
                .Attr("priority", order.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Block("override_action", action => action.Block(blocks ? "none" : "count", _ => { }))
                .Block("statement", statement => statement.Block("managed_rule_group_statement", managed => managed.Str("name", group).Str("vendor_name", "AWS")))
                .Block("visibility_config", Visibility(name)));
        }

        acl.Block("rule", rule => rule
            .Str("name", "request-limit")
            .Attr("priority", priority.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Block("action", action => action.Block(blocks ? "block" : "count", _ => { }))
            .Block("statement", statement => statement.Block("rate_based_statement", rate => rate
                .Attr("limit", Firewall.Rate(entry))
                .Str("aggregate_key_type", "IP")
                .Attr("evaluation_window_sec", "60")))
            .Block("visibility_config", Visibility("request-limit")));
        return acl.Block("visibility_config", Visibility(entry.Name));
    }

    private static Action<HclBlock> Visibility(string metric) => visibility => visibility
        .Attr("cloudwatch_metrics_enabled", "true")
        .Str("metric_name", metric)
        .Attr("sampled_requests_enabled", "true");

    public static bool InNetwork(SketchModel model, SketchResource function) => model.Targets(function, CloudKinds.Rds).Any();

    private static string Name(SketchResource resource) => $"${{var.name_prefix}}-{resource.Name}";

    private static int Retention(SketchModel model) => model.IsProduction ? 90 : 30;

    // The network: a VPC of its own with public subnets (load balancers, NAT) and private subnets (code, databases),
    // or the account's default VPC.
    private static string VpcId(SketchModel model) => model.AwsOwnNetwork ? "aws_vpc.main.id" : "data.aws_vpc.default.id";

    private static string VpcCidr(SketchModel model) => model.AwsOwnNetwork ? "aws_vpc.main.cidr_block" : "data.aws_vpc.default.cidr_block";

    private static string PublicSubnets(SketchModel model) => model.AwsOwnNetwork ? "aws_subnet.public[*].id" : "data.aws_subnets.default.ids";

    private static string PrivateSubnets(SketchModel model) => model.AwsOwnNetwork ? "aws_subnet.private[*].id" : "data.aws_subnets.default.ids";

    private static IEnumerable<HclItem> Network(SketchModel model)
    {
        if (!model.AwsOwnNetwork)
        {
            yield return new HclComment("The account's default VPC and its subnets (one per zone).");
            yield return new HclBlock("data", "aws_vpc", "default").Attr("default", "true");
            yield return new HclBlock("data", "aws_subnets", "default")
                .Block("filter", filter => filter.Str("name", "vpc-id").Attr("values", "[data.aws_vpc.default.id]"));
            yield break;
        }

        var nat = model.NatGateways;
        yield return new HclComment("A VPC of its own in two zones: public subnets for the load balancers" + (nat > 0 ? " and the NAT gateways" : "") + ", private subnets for code and databases.");
        yield return new HclBlock("data", "aws_availability_zones", "available").Str("state", "available");
        yield return new HclBlock("locals").Attr("zones", "slice(data.aws_availability_zones.available.names, 0, 2)");
        yield return new HclBlock("resource", "aws_vpc", "main")
            .Attr("cidr_block", "var.network_range")
            .Attr("enable_dns_support", "true")
            .Attr("enable_dns_hostnames", "true")
            .Map("tags", [new("Name", "var.name_prefix")]);
        foreach (var (kind, offset) in new[] { ("public", 0), ("private", 10) })
        {
            yield return new HclBlock("resource", "aws_subnet", kind)
                .Attr("count", "2")
                .Attr("vpc_id", "aws_vpc.main.id")
                .Attr("cidr_block", $"cidrsubnet(var.network_range, 8, {offset} + count.index)")
                .Attr("availability_zone", "local.zones[count.index]")
                .Attr("map_public_ip_on_launch", "false")
                .Map("tags", [new("Name", $"\"${{var.name_prefix}}-{kind}-${{count.index + 1}}\"")]);
        }

        yield return new HclBlock("resource", "aws_internet_gateway", "main").Attr("vpc_id", "aws_vpc.main.id");
        yield return new HclBlock("resource", "aws_route_table", "public")
            .Attr("vpc_id", "aws_vpc.main.id")
            .Block("route", route => route.Str("cidr_block", "0.0.0.0/0").Attr("gateway_id", "aws_internet_gateway.main.id"));
        yield return new HclBlock("resource", "aws_route_table_association", "public")
            .Attr("count", "2")
            .Attr("subnet_id", "aws_subnet.public[count.index].id")
            .Attr("route_table_id", "aws_route_table.public.id");
        if (nat > 0)
        {
            yield return new HclComment(nat == 1 ? "One NAT gateway lets the private subnets reach the internet." : "A NAT gateway per zone: the private subnets keep internet access if a zone fails.");
            yield return new HclBlock("resource", "aws_eip", "nat")
                .Attr("count", nat.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Str("domain", "vpc");
            yield return new HclBlock("resource", "aws_nat_gateway", "main")
                .Attr("count", nat.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Attr("allocation_id", "aws_eip.nat[count.index].id")
                .Attr("subnet_id", "aws_subnet.public[count.index].id")
                .Attr("depends_on", "[aws_internet_gateway.main]");
        }

        var privateTable = new HclBlock("resource", "aws_route_table", "private")
            .Attr("count", "2")
            .Attr("vpc_id", "aws_vpc.main.id");
        if (nat > 0)
        {
            privateTable.Block("route", route => route.Str("cidr_block", "0.0.0.0/0").Attr("nat_gateway_id", $"aws_nat_gateway.main[{(nat == 1 ? "0" : "count.index")}].id"));
        }

        yield return privateTable;
        yield return new HclBlock("resource", "aws_route_table_association", "private")
            .Attr("count", "2")
            .Attr("subnet_id", "aws_subnet.private[count.index].id")
            .Attr("route_table_id", "aws_route_table.private[count.index].id");
        yield return new HclComment("S3 and DynamoDB from the private subnets without going through the internet (free).");
        foreach (var service in new[] { "s3", "dynamodb" })
        {
            yield return new HclBlock("resource", "aws_vpc_endpoint", service)
                .Attr("vpc_id", "aws_vpc.main.id")
                .Attr("service_name", $"\"com.amazonaws.${{var.region}}.{service}\"")
                .Str("vpc_endpoint_type", "Gateway")
                .Attr("route_table_ids", "aws_route_table.private[*].id");
        }
    }

    public static List<HclItem> Resources(SketchModel model)
    {
        var items = new List<HclItem>();
        if (!Any(model))
        {
            items.AddRange(CloudAlerts.Aws(model));
            items.AddRange(Protection.Aws(model));
            return items;
        }

        if (NeedsNetwork(model))
        {
            items.AddRange(Network(model));
        }

        items.AddRange(OwnKeys.AwsKeys(model));
        if (Geo.On(model) && model.OfKind(CloudKinds.S3).Any(bucket => bucket.P["encryption"] == "aws:kms" && !OwnKeys.AwsBucket(model, bucket)))
        {
            items.Add(new HclComment("The S3 keys AWS manages, here and in the second region, for the copies of the encrypted buckets."));
            items.Add(new HclBlock("data", "aws_kms_alias", "s3").Str("name", "alias/aws/s3"));
            items.Add(new HclBlock("data", "aws_kms_alias", "s3_second").Attr("provider", "aws.second_region").Str("name", "alias/aws/s3"));
        }

        if (Geo.On(model) && model.OfKind(CloudKinds.Rds).Any() && !OwnKeys.Aws(model))
        {
            items.Add(new HclBlock("data", "aws_kms_alias", "rds_second").Attr("provider", "aws.second_region").Str("name", "alias/aws/rds"));
        }

        if (model.OfKind(CloudKinds.Lambda).Any())
        {
            items.Add(Assume("lambda", "lambda.amazonaws.com"));
        }

        if (model.OfKind(CloudKinds.Fargate).Any(container => container.Flag("public") && BehindCloudFront(model, container)))
        {
            items.Add(new HclComment("CloudFront's addresses, so the containers' load balancers take traffic only from it."));
            items.Add(new HclBlock("data", "aws_ec2_managed_prefix_list", "cloudfront").Str("name", "com.amazonaws.global.cloudfront.origin-facing"));
        }

        if (model.OfKind(CloudKinds.Fargate).Any())
        {
            items.Add(Assume("ecs_tasks", "ecs-tasks.amazonaws.com"));
            items.Add(new HclComment("One ECS cluster for every container of the project."));
            items.Add(new HclBlock("resource", "aws_ecs_cluster", "main")
                .Attr("name", "var.name_prefix")
                .Block("setting", setting => setting.Str("name", "containerInsights").Str("value", model.IsProduction || model.Settings.Monitoring ? "enabled" : "disabled")));
        }

        foreach (var function in model.OfKind(CloudKinds.Lambda))
        {
            items.AddRange(Lambda(model, function));
        }

        foreach (var container in model.OfKind(CloudKinds.Fargate))
        {
            items.AddRange(Fargate(model, container));
        }

        foreach (var repository in model.OfKind(CloudKinds.Ecr))
        {
            items.Add(new HclComment($"ECR repository {repository.Name}: images scanned on every push; the last 30 are kept."));
            items.Add(new HclBlock("resource", "aws_ecr_repository", repository.Label)
                .Attr("name", Hcl.Str(Name(repository)))
                .Str("image_tag_mutability", "MUTABLE")
                .Attr("force_delete", Hcl.Bool(!model.IsProduction))
                .Block("image_scanning_configuration", scanning => scanning.Attr("scan_on_push", "true"))
                .Block("encryption_configuration", encryption => encryption.Str("encryption_type", "AES256")));
            items.Add(new HclBlock("resource", "aws_ecr_lifecycle_policy", repository.Label)
                .Attr("repository", $"aws_ecr_repository.{repository.Label}.name")
                .Attr("policy", "jsonencode({ rules = [{ rulePriority = 1, description = \"Keep the last 30 images\", selection = { tagStatus = \"any\", countType = \"imageCountMoreThan\", countNumber = 30 }, action = { type = \"expire\" } }] })"));
        }

        foreach (var bucket in model.OfKind(CloudKinds.S3))
        {
            items.AddRange(Bucket(model, bucket));
        }

        foreach (var table in model.OfKind(CloudKinds.DynamoDb))
        {
            items.AddRange(Table(model, table));
        }

        foreach (var queue in model.OfKind(CloudKinds.Sqs))
        {
            items.AddRange(Queue(model, queue));
        }

        foreach (var database in model.OfKind(CloudKinds.Rds))
        {
            items.AddRange(Database(model, database));
        }

        foreach (var secret in model.OfKind(CloudKinds.AwsSecret))
        {
            items.Add(new HclComment($"Secret {secret.Name}: put its value in after deploying (aws secretsmanager put-secret-value)."));
            items.Add(new HclBlock("resource", "aws_secretsmanager_secret", secret.Label)
                .Attr("name", Hcl.Str($"${{var.name_prefix}}/{secret.Name}"))
                .Attr("recovery_window_in_days", secret.P["recoveryDays"])
                .With(block =>
                {
                    if (OwnKeys.Aws(model))
                    {
                        block.Attr("kms_key_id", $"{OwnKeys.AwsKey}.id");
                    }
                }));
        }

        foreach (var zone in model.OfKind(CloudKinds.Route53))
        {
            items.Add(new HclComment($"DNS zone {zone.P["zone"]}: point your domain registrar to its name servers (see the outputs)."));
            items.Add(new HclBlock("resource", "aws_route53_zone", zone.Label).Str("name", zone.P["zone"]));
        }

        foreach (var distribution in model.OfKind(CloudKinds.CloudFront))
        {
            items.AddRange(Distribution(model, distribution));
        }

        items.AddRange(AiResources(model));
        items.AddRange(Team.AwsTerraform(model));
        items.AddRange(CloudAlerts.Aws(model));
        items.AddRange(Protection.Aws(model));
        items.AddRange(Observability.Aws(model));
        items.AddRange(Guardrails.Aws(model));
        return items;
    }

    private static HclBlock Assume(string label, string service) =>
        new HclBlock("data", "aws_iam_policy_document", $"{label}_assume")
            .Block("statement", statement => statement
                .Attr("actions", "[\"sts:AssumeRole\"]")
                .Block("principals", principals => principals.Str("type", "Service").Attr("identifiers", Hcl.List([Hcl.Str(service)]))));

    // What code (a function or a container) may do with the boxes it points to, and the settings it gets for them.
    private sealed record Statement(string Sid, string[] Actions, string[] Resources);

    private static List<Statement> Statements(SketchModel model, SketchResource app)
    {
        var statements = new List<Statement>();
        foreach (var bucket in model.Targets(app, CloudKinds.S3))
        {
            statements.Add(new($"{bucket.Compact}files", ["s3:GetObject", "s3:PutObject", "s3:DeleteObject"], [$"\"${{aws_s3_bucket.{bucket.Label}.arn}}/*\""]));
            statements.Add(new($"{bucket.Compact}list", ["s3:ListBucket"], [$"aws_s3_bucket.{bucket.Label}.arn"]));
        }

        foreach (var table in model.Targets(app, CloudKinds.DynamoDb))
        {
            statements.Add(new($"{table.Compact}items",
                ["dynamodb:GetItem", "dynamodb:PutItem", "dynamodb:UpdateItem", "dynamodb:DeleteItem", "dynamodb:Query", "dynamodb:Scan", "dynamodb:BatchGetItem", "dynamodb:BatchWriteItem", "dynamodb:ConditionCheckItem"],
                [$"aws_dynamodb_table.{table.Label}.arn", $"\"${{aws_dynamodb_table.{table.Label}.arn}}/index/*\""]));
        }

        var queues = model.Targets(app, CloudKinds.Sqs).Concat(model.Sources(app, CloudKinds.Sqs)).Distinct();
        foreach (var queue in queues)
        {
            statements.Add(new($"{queue.Compact}messages",
                ["sqs:SendMessage", "sqs:ReceiveMessage", "sqs:DeleteMessage", "sqs:ChangeMessageVisibility", "sqs:GetQueueAttributes"], [$"aws_sqs_queue.{queue.Label}.arn"]));
        }

        foreach (var secret in model.Targets(app, CloudKinds.AwsSecret))
        {
            statements.Add(new($"{secret.Compact}secret", ["secretsmanager:GetSecretValue", "secretsmanager:DescribeSecret"], [$"aws_secretsmanager_secret.{secret.Label}.arn"]));
        }

        foreach (var database in model.Targets(app, CloudKinds.Rds))
        {
            statements.Add(new($"{database.Compact}password", ["secretsmanager:GetSecretValue"], [$"aws_db_instance.{database.Label}.master_user_secret[0].secret_arn"]));
        }

        foreach (var function in model.Targets(app, CloudKinds.Lambda))
        {
            statements.Add(new($"{function.Compact}invoke", ["lambda:InvokeFunction"], [$"aws_lambda_function.{function.Label}.arn"]));
        }

        statements.AddRange(AiStatements(model, app));
        var encrypted = model.Links.Where(link => link.From == app).Select(link => link.To)
            .Concat(model.Sources(app, CloudKinds.Sqs))
            .Any(target => target.Kind is CloudKinds.DynamoDb or CloudKinds.Sqs or CloudKinds.Rds or CloudKinds.AwsSecret
                || (target.Kind == CloudKinds.S3 && OwnKeys.AwsBucket(model, target)));
        if (OwnKeys.Aws(model) && encrypted)
        {
            statements.Add(new("projectkey", ["kms:Decrypt", "kms:Encrypt", "kms:GenerateDataKey"], [$"{OwnKeys.AwsKey}.arn"]));
        }

        return statements;
    }

    private static List<KeyValuePair<string, string>> Settings(SketchModel model, SketchResource app)
    {
        var settings = new List<KeyValuePair<string, string>>();
        foreach (var target in model.Links.Where(link => link.From == app).Select(link => link.To))
        {
            switch (target.Kind)
            {
                case CloudKinds.S3:
                    settings.Add(new($"{target.Upper}_BUCKET", $"aws_s3_bucket.{target.Label}.bucket"));
                    break;
                case CloudKinds.DynamoDb:
                    settings.Add(new($"{target.Upper}_TABLE", $"aws_dynamodb_table.{target.Label}.name"));
                    break;
                case CloudKinds.Sqs:
                    settings.Add(new($"{target.Upper}_QUEUE_URL", $"aws_sqs_queue.{target.Label}.url"));
                    break;
                case CloudKinds.AwsSecret:
                    settings.Add(new($"{target.Upper}_SECRET_ARN", $"aws_secretsmanager_secret.{target.Label}.arn"));
                    break;
                case CloudKinds.Rds:
                    settings.Add(new($"{target.Upper}_DB_HOST", $"aws_db_instance.{target.Label}.address"));
                    settings.Add(new($"{target.Upper}_DB_NAME", $"aws_db_instance.{target.Label}.db_name"));
                    settings.Add(new($"{target.Upper}_DB_USER", $"aws_db_instance.{target.Label}.username"));
                    settings.Add(new($"{target.Upper}_DB_SECRET_ARN", $"aws_db_instance.{target.Label}.master_user_secret[0].secret_arn"));
                    break;
                case CloudKinds.Lambda:
                    settings.Add(new($"{target.Upper}_FUNCTION", $"aws_lambda_function.{target.Label}.function_name"));
                    break;
                case CloudKinds.Fargate:
                    settings.Add(new($"{target.Upper}_URL", $"\"http://${{aws_lb.{target.Label}.dns_name}}\""));
                    break;
            }
        }

        settings.AddRange(AiSettings(model, app));
        return settings;
    }

    // The role's permissions, written as a policy document.
    private static IEnumerable<HclItem> AccessPolicy(SketchModel model, SketchResource app, string role)
    {
        var statements = Statements(model, app);
        if (statements.Count == 0)
        {
            yield break;
        }

        var document = new HclBlock("data", "aws_iam_policy_document", $"{app.Label}_access");
        foreach (var statement in statements)
        {
            document.Block("statement", block => block
                .Str("sid", statement.Sid)
                .Attr("actions", Hcl.List(statement.Actions.Select(Hcl.Str)))
                .Attr("resources", Hcl.List(statement.Resources)));
        }

        yield return new HclComment($"What {app.Name} may do with the boxes its arrows point to.");
        yield return document;
        yield return new HclBlock("resource", "aws_iam_role_policy", $"{app.Label}_access")
            .Str("name", "access")
            .Attr("role", role)
            .Attr("policy", $"data.aws_iam_policy_document.{app.Label}_access.json");
    }

    private static readonly Dictionary<string, string> Handlers = new(StringComparer.Ordinal)
    {
        ["python3.13"] = "index.handler",
        ["nodejs22.x"] = "index.handler",
        ["java21"] = "example.Handler::handleRequest",
    };

    public static string Handler(SketchResource function) =>
        Handlers.TryGetValue(function.P["runtime"], out var handler) ? handler : $"{function.Compact}::{function.Compact}.Function::FunctionHandler";

    private static IEnumerable<HclItem> Lambda(SketchModel model, SketchResource function)
    {
        var label = function.Label;
        var inNetwork = InNetwork(model, function);
        yield return new HclComment($"Lambda {function.Name}: its code is the folder functions/{function.Name}, zipped on every deploy.");
        yield return new HclBlock("data", "archive_file", label)
            .Str("type", "zip")
            .Str("source_dir", $"${{path.module}}/functions/{function.Name}")
            .Str("output_path", $"${{path.module}}/.build/{function.Name}.zip");
        yield return new HclBlock("resource", "aws_iam_role", label)
            .Attr("name", Hcl.Str(Name(function)))
            .Attr("assume_role_policy", "data.aws_iam_policy_document.lambda_assume.json");
        yield return new HclBlock("resource", "aws_iam_role_policy_attachment", $"{label}_basics")
            .Attr("role", $"aws_iam_role.{label}.name")
            .Str("policy_arn", inNetwork
                ? "arn:aws:iam::aws:policy/service-role/AWSLambdaVPCAccessExecutionRole"
                : "arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole");
        if (model.Settings.Monitoring)
        {
            yield return new HclBlock("resource", "aws_iam_role_policy_attachment", $"{label}_traces")
                .Attr("role", $"aws_iam_role.{label}.name")
                .Str("policy_arn", "arn:aws:iam::aws:policy/AWSXRayDaemonWriteAccess");
        }

        foreach (var item in AccessPolicy(model, function, $"aws_iam_role.{label}.id"))
        {
            yield return item;
        }

        yield return new HclBlock("resource", "aws_cloudwatch_log_group", label)
            .Attr("name", Hcl.Str($"/aws/lambda/{Name(function)}"))
            .Attr("retention_in_days", Retention(model).ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (inNetwork)
        {
            yield return new HclComment($"{function.Name} runs inside the VPC to reach its database.");
            yield return SecurityGroup(model, function, "lambda");
            yield return Egress(function);
        }

        var settings = Settings(model, function);
        var lambda = new HclBlock("resource", "aws_lambda_function", label)
            .Attr("function_name", Hcl.Str(Name(function)))
            .Attr("role", $"aws_iam_role.{label}.arn")
            .Str("runtime", function.P["runtime"])
            .Str("handler", Handler(function))
            .Attr("memory_size", function.P["memory"])
            .Attr("timeout", function.P["timeout"])
            .Attr("filename", $"data.archive_file.{label}.output_path")
            .Attr("source_code_hash", $"data.archive_file.{label}.output_base64sha256")
            .Block("logging_config", logging => logging.Str("log_format", "Text").Attr("log_group", $"aws_cloudwatch_log_group.{label}.name"));
        if (model.Settings.Monitoring)
        {
            lambda.Block("tracing_config", tracing => tracing.Str("mode", "Active"));
        }
        if (settings.Count > 0)
        {
            lambda.Block("environment", environment => environment.Map("variables", settings));
        }

        if (inNetwork)
        {
            lambda.Block("vpc_config", vpc => vpc
                .Attr("subnet_ids", PrivateSubnets(model))
                .Attr("security_group_ids", $"[aws_security_group.{label}.id]"));
        }

        lambda.Attr("depends_on", $"[aws_iam_role_policy_attachment.{label}_basics]");
        yield return lambda;

        if (function.Flag("publicUrl"))
        {
            yield return new HclComment($"A public HTTPS address for {function.Name} (see the outputs).");
            yield return new HclBlock("resource", "aws_lambda_function_url", label)
                .Attr("function_name", $"aws_lambda_function.{label}.function_name")
                .Str("authorization_type", "NONE");
            yield return new HclBlock("resource", "aws_lambda_permission", $"{label}_url")
                .Str("statement_id", "PublicFunctionUrl")
                .Str("action", "lambda:InvokeFunctionUrl")
                .Attr("function_name", $"aws_lambda_function.{label}.function_name")
                .Str("principal", "*")
                .Str("function_url_auth_type", "NONE");
            yield return new HclBlock("resource", "aws_lambda_permission", $"{label}_url_invoke")
                .Str("statement_id", "PublicFunctionUrlInvoke")
                .Str("action", "lambda:InvokeFunction")
                .Attr("function_name", $"aws_lambda_function.{label}.function_name")
                .Str("principal", "*")
                .Attr("invoked_via_function_url", "true");
        }

        foreach (var queue in model.Sources(function, CloudKinds.Sqs))
        {
            yield return new HclComment($"Messages in {queue.Name} start {function.Name}.");
            yield return new HclBlock("resource", "aws_lambda_event_source_mapping", $"{queue.Label}_{label}")
                .Attr("event_source_arn", $"aws_sqs_queue.{queue.Label}.arn")
                .Attr("function_name", $"aws_lambda_function.{label}.arn")
                .Attr("batch_size", "10")
                .Attr("depends_on", $"[aws_iam_role_policy.{label}_access]");
        }
    }

    private static HclBlock SecurityGroup(SketchModel model, SketchResource resource, string what, string suffix = "") =>
        new HclBlock("resource", "aws_security_group", resource.Label + suffix)
            .Attr("name", Hcl.Str($"{Name(resource)}{suffix.Replace('_', '-')}"))
            .Str("description", $"{resource.Name} ({what})")
            .Attr("vpc_id", VpcId(model));

    private static HclBlock Egress(SketchResource resource, string suffix = "") =>
        new HclBlock("resource", "aws_vpc_security_group_egress_rule", resource.Label + suffix)
            .Attr("security_group_id", $"aws_security_group.{resource.Label}{suffix}.id")
            .Str("description", "Everything out")
            .Str("ip_protocol", "-1")
            .Str("cidr_ipv4", "0.0.0.0/0");

    // A container with a repository arrow runs that repository's latest image; the app pipeline builds and pushes it.
    private static string Image(SketchModel model, SketchResource container) =>
        model.Targets(container, CloudKinds.Ecr).FirstOrDefault() is { } repository
            ? $"\"${{aws_ecr_repository.{repository.Label}.repository_url}}:latest\""
            : Hcl.Literal(container.P["image"]);

    private static readonly Dictionary<string, string> FargateMemory = new(StringComparer.Ordinal)
    {
        ["256"] = "512", ["512"] = "1024", ["1024"] = "2048", ["2048"] = "4096",
    };

    private static IEnumerable<HclItem> Fargate(SketchModel model, SketchResource container)
    {
        var label = container.Label;
        var port = container.P["port"];
        var isPublic = container.Flag("public");
        yield return new HclComment($"Container {container.Name} on ECS Fargate, behind a load balancer.");
        yield return new HclBlock("resource", "aws_cloudwatch_log_group", label)
            .Attr("name", Hcl.Str($"/ecs/{Name(container)}"))
            .Attr("retention_in_days", Retention(model).ToString(System.Globalization.CultureInfo.InvariantCulture));
        yield return new HclComment("The role ECS uses to pull the image and write the logs.");
        yield return new HclBlock("resource", "aws_iam_role", $"{label}_execution")
            .Attr("name", Hcl.Str($"{Name(container)}-execution"))
            .Attr("assume_role_policy", "data.aws_iam_policy_document.ecs_tasks_assume.json");
        yield return new HclBlock("resource", "aws_iam_role_policy_attachment", $"{label}_execution")
            .Attr("role", $"aws_iam_role.{label}_execution.name")
            .Str("policy_arn", "arn:aws:iam::aws:policy/service-role/AmazonECSTaskExecutionRolePolicy");
        yield return new HclComment("The role the code in the container uses.");
        yield return new HclBlock("resource", "aws_iam_role", label)
            .Attr("name", Hcl.Str(Name(container)))
            .Attr("assume_role_policy", "data.aws_iam_policy_document.ecs_tasks_assume.json");
        foreach (var item in AccessPolicy(model, container, $"aws_iam_role.{label}.id"))
        {
            yield return item;
        }

        // The load balancer's group takes HTTP from the internet (or the VPC); the container's only from the load balancer.
        yield return SecurityGroup(model, container, "load balancer", "_lb");
        var fromCloudFront = isPublic && BehindCloudFront(model, container);
        var ingress = new HclBlock("resource", "aws_vpc_security_group_ingress_rule", $"{label}_lb")
            .Attr("security_group_id", $"aws_security_group.{label}_lb.id")
            .Str("description", fromCloudFront ? "HTTP from CloudFront only" : isPublic ? "HTTP from the internet" : "HTTP from the VPC")
            .Str("ip_protocol", "tcp")
            .Attr("from_port", "80")
            .Attr("to_port", "80");
        yield return fromCloudFront
            ? ingress.Attr("prefix_list_id", "data.aws_ec2_managed_prefix_list.cloudfront.id")
            : ingress.Attr("cidr_ipv4", isPublic ? Hcl.Str("0.0.0.0/0") : VpcCidr(model));
        yield return Egress(container, "_lb");
        yield return SecurityGroup(model, container, "container");
        yield return new HclBlock("resource", "aws_vpc_security_group_ingress_rule", label)
            .Attr("security_group_id", $"aws_security_group.{label}.id")
            .Str("description", "From the load balancer")
            .Str("ip_protocol", "tcp")
            .Attr("from_port", port)
            .Attr("to_port", port)
            .Attr("referenced_security_group_id", $"aws_security_group.{label}_lb.id");
        yield return Egress(container);

        yield return new HclBlock("resource", "aws_lb", label)
            .Attr("internal", Hcl.Bool(!isPublic))
            .Str("load_balancer_type", "application")
            .Attr("security_groups", $"[aws_security_group.{label}_lb.id]")
            .Attr("subnets", isPublic ? PublicSubnets(model) : PrivateSubnets(model))
            .Attr("drop_invalid_header_fields", "true")
            .Map("tags", [new("Name", Hcl.Str(Name(container)))]);
        yield return new HclBlock("resource", "aws_lb_target_group", label)
            .Attr("port", port)
            .Str("protocol", "HTTP")
            .Str("target_type", "ip")
            .Attr("vpc_id", VpcId(model))
            .Attr("deregistration_delay", "30")
            .Block("health_check", health => health.Str("path", "/").Str("matcher", "200-399"));
        yield return new HclBlock("resource", "aws_lb_listener", label)
            .Attr("load_balancer_arn", $"aws_lb.{label}.arn")
            .Attr("port", "80")
            .Str("protocol", "HTTP")
            .Block("default_action", action => action.Str("type", "forward").Attr("target_group_arn", $"aws_lb_target_group.{label}.arn"));
        if (LoadBalancerFirewall(model, container))
        {
            yield return new HclComment($"Firewall (WAF) of {container.Name}'s load balancer.");
            yield return WebAcl(model, container, "REGIONAL");
            yield return new HclBlock("resource", "aws_wafv2_web_acl_association", label)
                .Attr("resource_arn", $"aws_lb.{label}.arn")
                .Attr("web_acl_arn", $"aws_wafv2_web_acl.{label}.arn");
        }

        var settings = Settings(model, container);
        var environment = settings.Count == 0
            ? "    environment = []"
            : "    environment = [\n" + string.Join("", settings.Select(setting => $"      {{ name = {Hcl.Str(setting.Key)}, value = {setting.Value} }},\n")) + "    ]";
        var definitions = string.Join('\n',
            "container_definitions = jsonencode([",
            "  {",
            "    name      = \"app\"",
            $"    image     = {Image(model, container)}",
            "    essential = true",
            "    portMappings = [",
            $"      {{ containerPort = {port}, protocol = \"tcp\" }}",
            "    ]",
            environment,
            "    logConfiguration = {",
            "      logDriver = \"awslogs\"",
            "      options = {",
            $"        awslogs-group         = aws_cloudwatch_log_group.{label}.name",
            "        awslogs-region        = var.region",
            "        awslogs-stream-prefix = \"app\"",
            "      }",
            "    }",
            "  }",
            "])");
        var task = new HclBlock("resource", "aws_ecs_task_definition", label)
            .Attr("family", Hcl.Str(Name(container)))
            .Attr("requires_compatibilities", "[\"FARGATE\"]")
            .Str("network_mode", "awsvpc")
            .Str("cpu", container.P["size"])
            .Str("memory", FargateMemory[container.P["size"]])
            .Attr("execution_role_arn", $"aws_iam_role.{label}_execution.arn")
            .Attr("task_role_arn", $"aws_iam_role.{label}.arn");
        task.Body.Add(new HclRawText(definitions));
        task.Block("runtime_platform", platform => platform.Str("operating_system_family", "LINUX").Str("cpu_architecture", "X86_64"));
        yield return task;

        yield return new HclBlock("resource", "aws_ecs_service", label)
            .Attr("name", Hcl.Str(Name(container)))
            .Attr("cluster", "aws_ecs_cluster.main.id")
            .Attr("task_definition", $"aws_ecs_task_definition.{label}.arn")
            .Attr("desired_count", container.P["count"])
            .Str("launch_type", "FARGATE")
            .Comment(model.AwsOwnNetwork
                ? "In the private subnets: only the load balancer reaches the tasks."
                : "The default VPC's subnets are public: the tasks get a public address to pull the image; only the load balancer can reach them.")
            .Block("network_configuration", network => network
                .Attr("subnets", PrivateSubnets(model))
                .Attr("security_groups", $"[aws_security_group.{label}.id]")
                .Attr("assign_public_ip", Hcl.Bool(!model.AwsOwnNetwork)))
            .Block("load_balancer", balancer => balancer
                .Attr("target_group_arn", $"aws_lb_target_group.{label}.arn")
                .Str("container_name", "app")
                .Attr("container_port", port))
            .Block("deployment_circuit_breaker", breaker => breaker.Attr("enable", "true").Attr("rollback", "true"))
            .Attr("depends_on", $"[aws_lb_listener.{label}]");
    }

    private static IEnumerable<HclItem> Bucket(SketchModel model, SketchResource bucket)
    {
        var label = bucket.Label;
        var distributions = model.Sources(bucket, CloudKinds.CloudFront).ToList();
        yield return new HclComment($"S3 bucket {bucket.Name}: encrypted, no public access, HTTPS only.");
        yield return new HclBlock("resource", "aws_s3_bucket", label)
            .Attr("bucket", Hcl.Str($"{Name(bucket)}-${{local.suffix}}"))
            .Attr("force_destroy", Hcl.Bool(!model.IsProduction));
        yield return new HclBlock("resource", "aws_s3_bucket_public_access_block", label)
            .Attr("bucket", $"aws_s3_bucket.{label}.id")
            .Attr("block_public_acls", "true")
            .Attr("block_public_policy", "true")
            .Attr("ignore_public_acls", "true")
            .Attr("restrict_public_buckets", "true");
        yield return new HclBlock("resource", "aws_s3_bucket_ownership_controls", label)
            .Attr("bucket", $"aws_s3_bucket.{label}.id")
            .Block("rule", rule => rule.Str("object_ownership", "BucketOwnerEnforced"));
        yield return new HclBlock("resource", "aws_s3_bucket_server_side_encryption_configuration", label)
            .Attr("bucket", $"aws_s3_bucket.{label}.id")
            .Block("rule", rule => rule
                .Block("apply_server_side_encryption_by_default", encryption => Encryption(model, bucket, encryption, $"{OwnKeys.AwsKey}.arn"))
                .Attr("bucket_key_enabled", Hcl.Bool(UsesKms(model, bucket))));
        if (Versioned(model, bucket))
        {
            yield return new HclBlock("resource", "aws_s3_bucket_versioning", label)
                .Attr("bucket", $"aws_s3_bucket.{label}.id")
                .Block("versioning_configuration", versioning => versioning.Str("status", "Enabled"));
        }

        var expire = bucket.P["expireDays"];
        if (expire != "0" || bucket.Flag("versioning"))
        {
            var lifecycle = new HclBlock("resource", "aws_s3_bucket_lifecycle_configuration", label).Attr("bucket", $"aws_s3_bucket.{label}.id");
            if (expire != "0")
            {
                lifecycle.Block("rule", rule => rule
                    .Str("id", "delete-old-files")
                    .Str("status", "Enabled")
                    .Block("filter", _ => { })
                    .Block("expiration", expiration => expiration.Attr("days", expire)));
            }

            if (Versioned(model, bucket))
            {
                lifecycle.Block("rule", rule => rule
                    .Str("id", "delete-old-versions")
                    .Str("status", "Enabled")
                    .Block("filter", _ => { })
                    .Block("noncurrent_version_expiration", versions => versions.Attr("noncurrent_days", "90")));
            }

            yield return lifecycle;
        }

        var policy = new HclBlock("data", "aws_iam_policy_document", label)
            .Block("statement", statement => statement
                .Str("sid", "HttpsOnly")
                .Str("effect", "Deny")
                .Attr("actions", "[\"s3:*\"]")
                .Attr("resources", $"[aws_s3_bucket.{label}.arn, \"${{aws_s3_bucket.{label}.arn}}/*\"]")
                .Block("principals", principals => principals.Str("type", "*").Attr("identifiers", "[\"*\"]"))
                .Block("condition", condition => condition.Str("test", "Bool").Str("variable", "aws:SecureTransport").Attr("values", "[\"false\"]")));
        foreach (var distribution in distributions)
        {
            policy.Block("statement", statement => statement
                .Str("sid", $"CloudFront{distribution.Compact}")
                .Attr("actions", "[\"s3:GetObject\"]")
                .Attr("resources", $"[\"${{aws_s3_bucket.{label}.arn}}/*\"]")
                .Block("principals", principals => principals.Str("type", "Service").Attr("identifiers", "[\"cloudfront.amazonaws.com\"]"))
                .Block("condition", condition => condition
                    .Str("test", "StringEquals")
                    .Str("variable", "AWS:SourceArn")
                    .Attr("values", $"[aws_cloudfront_distribution.{distribution.Label}.arn]")));
        }

        yield return policy;
        yield return new HclBlock("resource", "aws_s3_bucket_policy", label)
            .Attr("bucket", $"aws_s3_bucket.{label}.id")
            .Attr("policy", $"data.aws_iam_policy_document.{label}.json")
            .Attr("depends_on", $"[aws_s3_bucket_public_access_block.{label}]");
        if (Geo.On(model))
        {
            foreach (var item in BucketCopy(model, bucket))
            {
                yield return item;
            }
        }
    }

    // A bucket copied to the second region keeps every version: S3 replication needs it.
    private static bool Versioned(SketchModel model, SketchResource bucket) => bucket.Flag("versioning") || Geo.On(model);

    public static bool NeedsSecondRegion(SketchModel model) =>
        Geo.On(model) && (model.OfKind(CloudKinds.S3).Any() || model.OfKind(CloudKinds.Rds).Any() || model.OfKind(CloudKinds.DynamoDb).Any());

    // The copy of a bucket in the second region, kept up to date by S3 replication.
    private static IEnumerable<HclItem> BucketCopy(SketchModel model, SketchResource bucket)
    {
        var label = bucket.Label;
        var kms = UsesKms(model, bucket);
        var own = OwnKeys.AwsBucket(model, bucket);
        yield return new HclComment($"{bucket.Name} is copied to {Geo.Second(model)}: every new or changed file, and deletions.");
        yield return new HclBlock("resource", "aws_s3_bucket", $"{label}_copy")
            .Attr("provider", "aws.second_region")
            .Attr("bucket", Hcl.Str($"{Name(bucket)}-copy-${{local.suffix}}"))
            .Attr("force_destroy", Hcl.Bool(!model.IsProduction));
        yield return new HclBlock("resource", "aws_s3_bucket_public_access_block", $"{label}_copy")
            .Attr("provider", "aws.second_region")
            .Attr("bucket", $"aws_s3_bucket.{label}_copy.id")
            .Attr("block_public_acls", "true")
            .Attr("block_public_policy", "true")
            .Attr("ignore_public_acls", "true")
            .Attr("restrict_public_buckets", "true");
        yield return new HclBlock("resource", "aws_s3_bucket_versioning", $"{label}_copy")
            .Attr("provider", "aws.second_region")
            .Attr("bucket", $"aws_s3_bucket.{label}_copy.id")
            .Block("versioning_configuration", versioning => versioning.Str("status", "Enabled"));
        yield return new HclBlock("resource", "aws_s3_bucket_server_side_encryption_configuration", $"{label}_copy")
            .Attr("provider", "aws.second_region")
            .Attr("bucket", $"aws_s3_bucket.{label}_copy.id")
            .Block("rule", rule => rule.Block("apply_server_side_encryption_by_default", encryption => Encryption(model, bucket, encryption, $"{OwnKeys.AwsSecondKey}.arn")));
        foreach (var item in ServiceRole(bucket, "replication", "s3.amazonaws.com", document =>
        {
            AddStatement(document, "Source", ["s3:GetReplicationConfiguration", "s3:ListBucket"], [$"aws_s3_bucket.{label}.arn"]);
            AddStatement(document, "SourceObjects", ["s3:GetObjectVersionForReplication", "s3:GetObjectVersionAcl", "s3:GetObjectVersionTagging"], [$"\"${{aws_s3_bucket.{label}.arn}}/*\""]);
            AddStatement(document, "Copy", ["s3:ReplicateObject", "s3:ReplicateDelete", "s3:ReplicateTags"], [$"\"${{aws_s3_bucket.{label}_copy.arn}}/*\""]);
            if (kms)
            {
                AddStatement(document, "SourceKey", ["kms:Decrypt"], [own ? $"{OwnKeys.AwsKey}.arn" : "data.aws_kms_alias.s3.target_key_arn"]);
                AddStatement(document, "CopyKey", ["kms:Encrypt", "kms:GenerateDataKey"], [own ? $"{OwnKeys.AwsSecondKey}.arn" : "data.aws_kms_alias.s3_second.target_key_arn"]);
            }
        }))
        {
            yield return item;
        }

        yield return new HclBlock("resource", "aws_s3_bucket_replication_configuration", label)
            .Attr("role", $"aws_iam_role.{label}_replication.arn")
            .Attr("bucket", $"aws_s3_bucket.{label}.id")
            .Block("rule", rule =>
            {
                rule.Str("id", "second-region").Str("status", "Enabled")
                    .Block("filter", _ => { })
                    .Block("delete_marker_replication", marker => marker.Str("status", "Enabled"));
                if (kms)
                {
                    rule.Block("source_selection_criteria", criteria => criteria.Block("sse_kms_encrypted_objects", objects => objects.Str("status", "Enabled")));
                }

                rule.Block("destination", destination =>
                {
                    destination.Attr("bucket", $"aws_s3_bucket.{label}_copy.arn").Str("storage_class", "STANDARD");
                    if (kms)
                    {
                        destination.Block("encryption_configuration", encryption => encryption.Attr("replica_kms_key_id", own ? $"{OwnKeys.AwsSecondKey}.arn" : "data.aws_kms_alias.s3_second.target_key_arn"));
                    }
                });
            })
            .Attr("depends_on", $"[aws_s3_bucket_versioning.{label}, aws_s3_bucket_versioning.{label}_copy]");
    }

    private static bool UsesKms(SketchModel model, SketchResource bucket) => bucket.P["encryption"] == "aws:kms" || OwnKeys.AwsBucket(model, bucket);

    // S3 managed keys, the AWS managed KMS key, or the project's key.
    private static void Encryption(SketchModel model, SketchResource bucket, HclBlock encryption, string key)
    {
        if (OwnKeys.AwsBucket(model, bucket))
        {
            encryption.Str("sse_algorithm", "aws:kms").Attr("kms_master_key_id", key);
        }
        else
        {
            encryption.Str("sse_algorithm", bucket.P["encryption"]);
        }
    }

    private static IEnumerable<HclItem> Table(SketchModel model, SketchResource table)
    {
        var sortKey = table.P["sortKey"];
        yield return new HclComment($"DynamoDB table {table.Name}: paid per request, encrypted.");
        var block = new HclBlock("resource", "aws_dynamodb_table", table.Label)
            .Attr("name", Hcl.Str(Name(table)))
            .Str("billing_mode", "PAY_PER_REQUEST")
            .Str("hash_key", table.P["partitionKey"]);
        if (sortKey.Length > 0)
        {
            block.Str("range_key", sortKey);
        }

        block.Attr("deletion_protection_enabled", Hcl.Bool(model.IsProduction))
            .Block("attribute", attribute => attribute.Str("name", table.P["partitionKey"]).Str("type", "S"));
        if (sortKey.Length > 0)
        {
            block.Block("attribute", attribute => attribute.Str("name", sortKey).Str("type", "S"));
        }

        if (Geo.On(model))
        {
            block.Comment($"A replica in {Geo.Second(model)} (a global table), fed by the table's stream.")
                .Attr("stream_enabled", "true")
                .Str("stream_view_type", "NEW_AND_OLD_IMAGES")
                .Block("replica", replica => replica.Str("region_name", Geo.Second(model)).Attr("point_in_time_recovery", Hcl.Bool(table.Flag("recovery")))
                    .With(copy =>
                    {
                        if (OwnKeys.Aws(model))
                        {
                            copy.Attr("kms_key_arn", $"{OwnKeys.AwsSecondKey}.arn");
                        }
                    }));
        }

        block.Block("point_in_time_recovery", recovery => recovery.Attr("enabled", Hcl.Bool(table.Flag("recovery"))))
            .Block("server_side_encryption", encryption => encryption.Attr("enabled", "true").With(key =>
            {
                if (OwnKeys.Aws(model))
                {
                    key.Attr("kms_key_arn", $"{OwnKeys.AwsKey}.arn");
                }
            }));
        yield return block;
    }

    private static void QueueEncryption(SketchModel model, HclBlock queue)
    {
        if (OwnKeys.Aws(model))
        {
            queue.Attr("kms_master_key_id", $"{OwnKeys.AwsKey}.id");
        }
        else
        {
            queue.Attr("sqs_managed_sse_enabled", "true");
        }
    }

    private static IEnumerable<HclItem> Queue(SketchModel model, SketchResource queue)
    {
        var fifo = queue.Flag("fifo");
        var ending = fifo ? ".fifo" : "";
        yield return new HclComment($"SQS queue {queue.Name}, encrypted.");
        if (queue.Flag("deadLetter"))
        {
            yield return new HclBlock("resource", "aws_sqs_queue", $"{queue.Label}_failed")
                .Attr("name", Hcl.Str($"{Name(queue)}-failed{ending}"))
                .Attr("fifo_queue", Hcl.Bool(fifo))
                .Attr("message_retention_seconds", "1209600")
                .With(failed => QueueEncryption(model, failed));
        }

        var block = new HclBlock("resource", "aws_sqs_queue", queue.Label)
            .Attr("name", Hcl.Str($"{Name(queue)}{ending}"))
            .Attr("fifo_queue", Hcl.Bool(fifo))
            .Attr("visibility_timeout_seconds", queue.P["visibility"])
            .With(main => QueueEncryption(model, main));
        if (queue.Flag("deadLetter"))
        {
            block.Comment("After 5 failed tries a message moves to the failed queue.")
                .Attr("redrive_policy", $"jsonencode({{ deadLetterTargetArn = aws_sqs_queue.{queue.Label}_failed.arn, maxReceiveCount = 5 }})");
        }

        yield return block;
    }

    private static IEnumerable<HclItem> Database(SketchModel model, SketchResource database)
    {
        var label = database.Label;
        yield return new HclComment($"RDS PostgreSQL {database.Name}: private, encrypted; its password lives in Secrets Manager.");
        yield return new HclBlock("resource", "aws_db_subnet_group", label)
            .Attr("name", Hcl.Str(Name(database)))
            .Attr("subnet_ids", PrivateSubnets(model));
        yield return SecurityGroup(model, database, "database");
        foreach (var app in model.Links.Where(link => link.To == database).Select(link => link.From))
        {
            yield return new HclBlock("resource", "aws_vpc_security_group_ingress_rule", $"{label}_{app.Label}")
                .Attr("security_group_id", $"aws_security_group.{label}.id")
                .Str("description", $"PostgreSQL from {app.Name}")
                .Str("ip_protocol", "tcp")
                .Attr("from_port", "5432")
                .Attr("to_port", "5432")
                .Attr("referenced_security_group_id", $"aws_security_group.{app.Label}.id");
        }

        var production = model.IsProduction;
        var block = new HclBlock("resource", "aws_db_instance", label)
            .Attr("identifier", Hcl.Str(Name(database)))
            .Str("engine", "postgres")
            .Str("engine_version", database.P["version"])
            .Str("instance_class", database.P["size"])
            .Attr("allocated_storage", database.P["storageGb"])
            .Attr("max_allocated_storage", (int.Parse(database.P["storageGb"], System.Globalization.CultureInfo.InvariantCulture) * 4).ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Str("storage_type", "gp3")
            .Attr("storage_encrypted", "true")
            .Str("db_name", database.Label)
            .Str("username", "dbadmin")
            .Attr("manage_master_user_password", "true")
            .With(instance =>
            {
                if (OwnKeys.Aws(model))
                {
                    instance.Attr("kms_key_id", $"{OwnKeys.AwsKey}.arn").Attr("master_user_secret_kms_key_id", $"{OwnKeys.AwsKey}.key_id");
                }
            })
            .Attr("iam_database_authentication_enabled", "true")
            .Attr("db_subnet_group_name", $"aws_db_subnet_group.{label}.name")
            .Attr("vpc_security_group_ids", $"[aws_security_group.{label}.id]")
            .Attr("publicly_accessible", "false")
            .Attr("multi_az", Hcl.Bool(database.Flag("multiAz")))
            .Attr("backup_retention_period", database.P["backupDays"])
            .Attr("copy_tags_to_snapshot", "true")
            .Attr("auto_minor_version_upgrade", "true")
            .Attr("deletion_protection", Hcl.Bool(production))
            .Attr("skip_final_snapshot", Hcl.Bool(!production));
        if (production)
        {
            block.Attr("final_snapshot_identifier", Hcl.Str($"{Name(database)}-final"));
        }
        else
        {
            block.Attr("apply_immediately", "true");
        }

        yield return block;
        if (Geo.On(model))
        {
            yield return new HclComment($"The automated backups of {database.Name} are copied to {Geo.Second(model)}.");
            yield return new HclBlock("resource", "aws_db_instance_automated_backups_replication", label)
                .Attr("provider", "aws.second_region")
                .Attr("source_db_instance_arn", $"aws_db_instance.{label}.arn")
                .Attr("retention_period", database.P["backupDays"])
                .Attr("kms_key_id", OwnKeys.Aws(model) ? $"{OwnKeys.AwsSecondKey}.arn" : "data.aws_kms_alias.rds_second.target_key_arn");
        }
    }

    private static IEnumerable<HclItem> Distribution(SketchModel model, SketchResource distribution)
    {
        var label = distribution.Label;
        var origins = model.Links.Where(link => link.From == distribution).Select(link => link.To).ToList();
        var zone = model.Sources(distribution, CloudKinds.Route53).FirstOrDefault();
        var host = zone is null ? null : $"{distribution.P["subdomain"]}.{zone.P["zone"]}";
        yield return new HclComment($"CloudFront {distribution.Name}: one HTTPS entry in front of {string.Join(", ", origins.Select(origin => origin.Name))}.");
        if (model.OfKind(CloudKinds.CloudFront).First() == distribution)
        {
            yield return new HclComment("AWS's own cache and request settings, by name.");
            yield return new HclBlock("data", "aws_cloudfront_cache_policy", "optimized").Str("name", "Managed-CachingOptimized");
            yield return new HclBlock("data", "aws_cloudfront_cache_policy", "disabled").Str("name", "Managed-CachingDisabled");
            yield return new HclBlock("data", "aws_cloudfront_origin_request_policy", "all_viewer").Str("name", "Managed-AllViewerExceptHostHeader");
        }

        foreach (var bucket in origins.Where(origin => origin.Kind == CloudKinds.S3))
        {
            yield return new HclBlock("resource", "aws_cloudfront_origin_access_control", $"{label}_{bucket.Label}")
                .Attr("name", Hcl.Str($"{Name(distribution)}-{bucket.Name}"))
                .Str("origin_access_control_origin_type", "s3")
                .Str("signing_behavior", "always")
                .Str("signing_protocol", "sigv4");
        }

        if (zone is not null && host is not null)
        {
            yield return new HclComment($"A free certificate for {host} (CloudFront takes it from us-east-1), checked through DNS.");
            yield return new HclBlock("resource", "aws_acm_certificate", label)
                .Attr("provider", "aws.us_east_1")
                .Str("domain_name", host)
                .Str("validation_method", "DNS")
                .Block("lifecycle", lifecycle => lifecycle.Attr("create_before_destroy", "true"));
            yield return new HclBlock("resource", "aws_route53_record", $"{label}_validation")
                .Attr("for_each", $"{{ for option in aws_acm_certificate.{label}.domain_validation_options : option.domain_name => option }}")
                .Attr("zone_id", $"aws_route53_zone.{zone.Label}.zone_id")
                .Attr("name", "each.value.resource_record_name")
                .Attr("type", "each.value.resource_record_type")
                .Attr("records", "[each.value.resource_record_value]")
                .Attr("ttl", "300")
                .Attr("allow_overwrite", "true");
            yield return new HclBlock("resource", "aws_acm_certificate_validation", label)
                .Attr("provider", "aws.us_east_1")
                .Attr("certificate_arn", $"aws_acm_certificate.{label}.arn")
                .Attr("validation_record_fqdns", $"[for record in aws_route53_record.{label}_validation : record.fqdn]");
        }

        if (Firewall.On(distribution))
        {
            yield return new HclComment($"Firewall (WAF) of {distribution.Name}: " + (Firewall.Blocks(model, distribution) ? "blocks" : "only counts") +
                $" common attacks, known bad inputs, addresses with a bad reputation, and more than {Firewall.Rate(distribution)} requests a minute from one address.");
            yield return WebAcl(model, distribution, "CLOUDFRONT");
        }

        var main = origins.FirstOrDefault(origin => origin.P["path"] == "/*") ?? origins[0];
        var block = new HclBlock("resource", "aws_cloudfront_distribution", label)
            .Attr("enabled", "true")
            .Attr("is_ipv6_enabled", "true")
            .Attr("comment", Hcl.Str(Name(distribution)))
            .Str("price_class", distribution.P["priceClass"]);
        if (main.Kind == CloudKinds.S3)
        {
            block.Str("default_root_object", "index.html");
        }

        if (Firewall.On(distribution))
        {
            block.Attr("web_acl_id", $"aws_wafv2_web_acl.{label}.arn");
        }

        if (host is not null)
        {
            block.Attr("aliases", Hcl.List([Hcl.Str(host)]));
        }

        foreach (var origin in origins)
        {
            block.Block("origin", item =>
            {
                item.Str("origin_id", origin.Name);
                switch (origin.Kind)
                {
                    case CloudKinds.S3:
                        item.Attr("domain_name", $"aws_s3_bucket.{origin.Label}.bucket_regional_domain_name")
                            .Attr("origin_access_control_id", $"aws_cloudfront_origin_access_control.{label}_{origin.Label}.id");
                        break;
                    case CloudKinds.Lambda:
                        item.Attr("domain_name", $"split(\"/\", aws_lambda_function_url.{origin.Label}.function_url)[2]")
                            .Block("custom_origin_config", custom => custom
                                .Attr("http_port", "80")
                                .Attr("https_port", "443")
                                .Str("origin_protocol_policy", "https-only")
                                .Attr("origin_ssl_protocols", "[\"TLSv1.2\"]"));
                        break;
                    default:
                        item.Attr("domain_name", $"aws_lb.{origin.Label}.dns_name")
                            .Block("custom_origin_config", custom => custom
                                .Attr("http_port", "80")
                                .Attr("https_port", "443")
                                .Comment("The load balancer listens on HTTP; CloudFront gives visitors HTTPS.")
                                .Str("origin_protocol_policy", "http-only")
                                .Attr("origin_ssl_protocols", "[\"TLSv1.2\"]"));
                        break;
                }
            });
        }

        block.Block("default_cache_behavior", behavior => Behavior(behavior, main));
        foreach (var origin in origins.Where(origin => origin != main))
        {
            block.Block("ordered_cache_behavior", behavior => Behavior(behavior.Str("path_pattern", origin.P["path"]), origin));
        }

        block.Block("restrictions", restrictions => restrictions.Block("geo_restriction", geo => geo.Str("restriction_type", "none")));
        block.Block("viewer_certificate", certificate =>
        {
            if (host is null)
            {
                certificate.Attr("cloudfront_default_certificate", "true");
            }
            else
            {
                certificate.Attr("acm_certificate_arn", $"aws_acm_certificate_validation.{label}.certificate_arn")
                    .Str("ssl_support_method", "sni-only")
                    .Str("minimum_protocol_version", "TLSv1.2_2021");
            }
        });
        yield return block;

        if (zone is not null && host is not null)
        {
            foreach (var type in new[] { "A", "AAAA" })
            {
                yield return new HclBlock("resource", "aws_route53_record", $"{label}_{type.ToLowerInvariant()}")
                    .Attr("zone_id", $"aws_route53_zone.{zone.Label}.zone_id")
                    .Str("name", host)
                    .Str("type", type)
                    .Block("alias", alias => alias
                        .Attr("name", $"aws_cloudfront_distribution.{label}.domain_name")
                        .Attr("zone_id", $"aws_cloudfront_distribution.{label}.hosted_zone_id")
                        .Attr("evaluate_target_health", "false"));
            }
        }
    }

    // Files are cached; code answers every request with every method.
    private static void Behavior(HclBlock behavior, SketchResource origin)
    {
        var files = origin.Kind == CloudKinds.S3;
        behavior.Str("target_origin_id", origin.Name)
            .Str("viewer_protocol_policy", "redirect-to-https")
            .Attr("allowed_methods", files ? "[\"GET\", \"HEAD\", \"OPTIONS\"]" : "[\"DELETE\", \"GET\", \"HEAD\", \"OPTIONS\", \"PATCH\", \"POST\", \"PUT\"]")
            .Attr("cached_methods", "[\"GET\", \"HEAD\"]")
            .Attr("compress", "true")
            .Attr("cache_policy_id", files ? "data.aws_cloudfront_cache_policy.optimized.id" : "data.aws_cloudfront_cache_policy.disabled.id");
        if (!files)
        {
            behavior.Attr("origin_request_policy_id", "data.aws_cloudfront_origin_request_policy.all_viewer.id");
        }
    }

    public static List<HclItem> Outputs(SketchModel model)
    {
        var items = new List<HclItem>();
        void Output(string name, string description, string value) =>
            items.Add(new HclBlock("output", name).Str("description", description).Attr("value", value));

        foreach (var function in model.OfKind(CloudKinds.Lambda))
        {
            Output($"{function.Label}_name", $"The name of the function {function.Name}.", $"aws_lambda_function.{function.Label}.function_name");
            if (function.Flag("publicUrl"))
            {
                Output($"{function.Label}_url", $"The public address of {function.Name}.", $"aws_lambda_function_url.{function.Label}.function_url");
            }
        }

        foreach (var container in model.OfKind(CloudKinds.Fargate))
        {
            Output($"{container.Label}_url", $"The address of {container.Name}'s load balancer.", $"\"http://${{aws_lb.{container.Label}.dns_name}}\"");
            Output($"{container.Label}_service", $"The ECS service of {container.Name}.", $"aws_ecs_service.{container.Label}.name");
            if (model.Targets(container, CloudKinds.Ecr).Any())
            {
                Output($"{container.Label}_image", $"The image {container.Name} runs; push a new one to update it.", Image(model, container));
            }
        }

        if (model.OfKind(CloudKinds.Fargate).Any())
        {
            Output("ecs_cluster", "The ECS cluster of the containers.", "aws_ecs_cluster.main.name");
        }

        foreach (var repository in model.OfKind(CloudKinds.Ecr))
        {
            Output($"{repository.Label}_repository_url", $"Where to push the images of {repository.Name}.", $"aws_ecr_repository.{repository.Label}.repository_url");
        }

        foreach (var bucket in model.OfKind(CloudKinds.S3))
        {
            Output($"{bucket.Label}_bucket", $"The name of the bucket {bucket.Name}.", $"aws_s3_bucket.{bucket.Label}.bucket");
        }

        foreach (var table in model.OfKind(CloudKinds.DynamoDb))
        {
            Output($"{table.Label}_table", $"The name of the table {table.Name}.", $"aws_dynamodb_table.{table.Label}.name");
        }

        foreach (var queue in model.OfKind(CloudKinds.Sqs))
        {
            Output($"{queue.Label}_queue_url", $"The address of the queue {queue.Name}.", $"aws_sqs_queue.{queue.Label}.url");
        }

        foreach (var database in model.OfKind(CloudKinds.Rds))
        {
            Output($"{database.Label}_host", $"The host of the database {database.Name}.", $"aws_db_instance.{database.Label}.address");
            Output($"{database.Label}_secret_arn", $"The secret with the database password of {database.Name}.", $"aws_db_instance.{database.Label}.master_user_secret[0].secret_arn");
        }

        foreach (var secret in model.OfKind(CloudKinds.AwsSecret))
        {
            Output($"{secret.Label}_secret_arn", $"The secret {secret.Name}: put its value in with aws secretsmanager put-secret-value.", $"aws_secretsmanager_secret.{secret.Label}.arn");
        }

        foreach (var zone in model.OfKind(CloudKinds.Route53))
        {
            Output($"{zone.Label}_name_servers", $"Give these name servers to the registrar of {zone.P["zone"]}.", $"aws_route53_zone.{zone.Label}.name_servers");
        }

        foreach (var distribution in model.OfKind(CloudKinds.CloudFront))
        {
            Output($"{distribution.Label}_url", $"The HTTPS address of {distribution.Name}.", $"\"https://${{aws_cloudfront_distribution.{distribution.Label}.domain_name}}\"");
        }

        items.AddRange(AiOutputs(model));
        return items;
    }

    // A starter for each function, so the first deploy works; replace it with your code.
    public static Dictionary<string, string> CodeFiles(SketchModel model)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var function in model.OfKind(CloudKinds.Lambda))
        {
            var folder = $"functions/{function.Name}";
            switch (function.P["runtime"])
            {
                case "python3.13":
                    files[$"{folder}/index.py"] = $$"""
                        import json
                        import os


                        def handler(event, context):
                            # The settings Terraform Generator added are environment variables, for example os.environ.get("ORDERS_TABLE").
                            return {
                                "statusCode": 200,
                                "headers": {"Content-Type": "application/json"},
                                "body": json.dumps({"message": "Hello from {{function.Name}}"}),
                            }

                        """;
                    break;
                case "nodejs22.x":
                    files[$"{folder}/index.mjs"] = $$"""
                        // The settings Terraform Generator added are environment variables, for example process.env.ORDERS_TABLE.
                        export const handler = async (event) => ({
                          statusCode: 200,
                          headers: { 'Content-Type': 'application/json' },
                          body: JSON.stringify({ message: 'Hello from {{function.Name}}' }),
                        });

                        """;
                    break;
                default:
                    files[$"{folder}/README.md"] = $"""
                        # {function.Name}

                        Put the built package of your {function.P["runtime"]} function here (the files that go inside the zip).
                        Its handler is `{Handler(function)}`. Terraform zips this folder on every deploy.

                        """;
                    break;
            }
        }

        return files;
    }
}
