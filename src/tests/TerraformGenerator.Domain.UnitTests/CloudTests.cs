using TerraformGenerator.Domain;

namespace TerraformGenerator.Domain.UnitTests;

// The AWS and Google Cloud smart boxes: what each arrow adds and what the checks refuse.
public sealed class CloudTests
{
    private static SketchBuilder Cloud(string platform)
    {
        var builder = new SketchBuilder();
        builder.Sketch.Settings.Platform = platform;
        builder.Sketch.Settings.Region = platform == Platforms.Aws ? "us-east-1" : "us-central1";
        builder.Sketch.Settings.Options[SettingKeys.GcpProject] = "shop-dev-123456";
        return builder;
    }

    private static List<string> Texts(Sketch sketch, MessageLevel level) =>
        SketchRules.Check(sketch).Where(message => message.Level == level).Select(message => message.Text).ToList();

    [Fact]
    public void An_arrow_from_a_lambda_gives_its_role_access_and_the_address_as_a_setting()
    {
        var aws = Cloud(Platforms.Aws);
        var api = aws.Add(CloudKinds.Lambda, "api");
        aws.Link(api, aws.Add(CloudKinds.DynamoDb, "orders"));
        aws.Link(aws.Add(CloudKinds.Sqs, "work", new() { ["visibility"] = "60" }), api);

        var result = SketchGenerator.Generate(aws.Sketch);

        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        var code = result.Files["aws.tf"];
        Assert.Matches(@"""ORDERS_TABLE""\s+=\s+aws_dynamodb_table\.orders\.name", code);
        Assert.Contains("\"dynamodb:PutItem\"", code, StringComparison.Ordinal);
        Assert.Contains("resource \"aws_lambda_event_source_mapping\" \"work_api\"", code, StringComparison.Ordinal);
        Assert.Contains("\"sqs:ReceiveMessage\"", code, StringComparison.Ordinal);
        Assert.Contains("functions/api/index.py", result.Files.Keys);
        Assert.Contains("hashicorp/archive", result.Files["versions.tf"], StringComparison.Ordinal);
    }

    [Fact]
    public void Aws_checks_refuse_what_aws_would_refuse()
    {
        var aws = Cloud(Platforms.Aws);
        var cdn = aws.Add(CloudKinds.CloudFront, "edge");
        var api = aws.Add(CloudKinds.Lambda, "api", new() { ["timeout"] = "300" });
        var files = aws.Add(CloudKinds.S3, "files", new() { ["encryption"] = "aws:kms", ["path"] = "/files/*" });
        var site = aws.Add(CloudKinds.S3, "site", new() { ["path"] = "/files/*" });
        aws.Link(cdn, api).Link(cdn, files).Link(cdn, site);
        aws.Link(aws.Add(CloudKinds.Sqs, "work", new() { ["visibility"] = "60" }), api);
        aws.Add(CloudKinds.CloudFront, "empty");

        var errors = Texts(aws.Sketch, MessageLevel.Error);

        Assert.Contains("api: CloudFront reaches a Lambda through its address; turn on \"Public HTTPS address\".", errors);
        Assert.Contains("files: CloudFront cannot read files encrypted with the AWS managed KMS key; choose S3 managed keys.", errors);
        Assert.Contains("edge: files and site have the same path /files/*. Give each its own path.", errors);
        Assert.Contains("work -> api: the queue's time to handle a message must be at least the function's time limit.", errors);
        Assert.Contains("empty: draw an arrow from CloudFront to what it serves (an S3 bucket, a Lambda or a container).", errors);
    }

    [Fact]
    public void Github_deploys_each_cloud_without_passwords()
    {
        var aws = SketchGenerator.Generate(Examples.Aws()).Files;
        var gcp = SketchGenerator.Generate(Examples.Gcp()).Files;

        Assert.Contains("role-to-assume: ${{ vars.AWS_ROLE_ARN }}", aws[".github/workflows/infrastructure.yml"], StringComparison.Ordinal);
        Assert.Contains("-backend-config=\"key=shop-prod.tfstate\"", aws[".github/workflows/infrastructure.yml"], StringComparison.Ordinal);
        Assert.Contains("infra/.build", aws[".github/workflows/infrastructure.yml"], StringComparison.Ordinal);
        Assert.Contains("create-open-id-connect-provider", aws["setup-github.ps1"], StringComparison.Ordinal);
        Assert.Contains("workload_identity_provider: ${{ vars.GCP_WORKLOAD_IDENTITY_PROVIDER }}", gcp[".github/workflows/infrastructure.yml"], StringComparison.Ordinal);
        Assert.Contains("--attribute-condition \"assertion.repository == '$Repository'\"", gcp["setup-github.ps1"], StringComparison.Ordinal);

        var local = Examples.Aws();
        local.Settings.Options[SettingKeys.State] = "local";
        Assert.Contains(Texts(local, MessageLevel.Error), text => text.StartsWith("Deploying from GitHub needs the Terraform state in the cloud", StringComparison.Ordinal));
    }

    [Fact]
    public void The_firewall_guards_each_cloud_entry_and_blocks_only_in_production()
    {
        var azure = SketchGenerator.Generate(Examples.Everything()).Files;
        Assert.Contains("mode                = \"Prevention\"", azure["edge.tf"], StringComparison.Ordinal);
        Assert.Contains("Microsoft_DefaultRuleSet", azure["edge.tf"], StringComparison.Ordinal);
        Assert.Contains("Microsoft.Network/FrontDoorWebApplicationFirewallPolicies", azure["bicep/main.bicep"], StringComparison.Ordinal);

        var aws = SketchGenerator.Generate(Examples.Aws()).Files;
        Assert.Matches(@"web_acl_id\s+=\s+aws_wafv2_web_acl\.edge\.arn", aws["aws.tf"]);
        Assert.Contains("AWSManagedRulesCommonRuleSet", aws["aws.tf"], StringComparison.Ordinal);
        Assert.Contains("com.amazonaws.global.cloudfront.origin-facing", aws["aws.tf"], StringComparison.Ordinal);
        Assert.Contains("alias  = \"us_east_1\"", aws["main.tf"], StringComparison.Ordinal);

        var gcp = Examples.Gcp();
        gcp.Settings.Environment = "dev";
        var code = SketchGenerator.Generate(gcp).Files["gcp.tf"];
        Assert.Matches(@"security_policy\s+=\s+google_compute_security_policy\.edge\.id", code);
        Assert.Matches(@"preview\s+=\s+true", code);

        var off = Examples.Aws();
        off.Nodes.Single(node => node.Kind == CloudKinds.CloudFront).Props[Firewall.Key] = "off";
        Assert.Contains(Texts(off, MessageLevel.Warning), text => text.StartsWith("edge: production without its firewall", StringComparison.Ordinal));
    }

    [Fact]
    public void Code_and_databases_get_a_private_network_of_their_own()
    {
        var aws = SketchGenerator.Generate(Examples.Aws()).Files["aws.tf"];
        Assert.Contains("resource \"aws_vpc\" \"main\"", aws, StringComparison.Ordinal);
        Assert.Matches(@"resource ""aws_nat_gateway"" ""main"" \{\s+count\s+=\s+2", aws);
        Assert.Matches(@"subnet_ids\s+=\s+aws_subnet\.private\[\*\]\.id", aws);
        Assert.Matches(@"assign_public_ip\s+=\s+false", aws);
        Assert.DoesNotContain("aws_vpc.default", aws, StringComparison.Ordinal);

        var shared = Examples.Aws();
        shared.Settings.Options[SettingKeys.AwsNetwork] = "default";
        Assert.Contains("data \"aws_vpc\" \"default\"", SketchGenerator.Generate(shared).Files["aws.tf"], StringComparison.Ordinal);
        Assert.Contains(Texts(shared, MessageLevel.Warning), text => text.StartsWith("Containers and databases go in the account's default VPC", StringComparison.Ordinal));

        var gcp = SketchGenerator.Generate(Examples.Gcp()).Files;
        Assert.Matches(@"ipv4_enabled\s+=\s+false", gcp["gcp.tf"]);
        Assert.Contains("google_service_networking_connection", gcp["gcp.tf"], StringComparison.Ordinal);
        Assert.Contains("servicenetworking.googleapis.com", gcp["check-prereqs.ps1"], StringComparison.Ordinal);
    }

    [Fact]
    public void The_team_gets_its_roles_and_developers_only_look_in_production()
    {
        var gcp = SketchGenerator.Generate(Examples.Gcp()).Files["gcp.tf"];
        Assert.Contains("member  = \"group:cloud-admins@example.com\"", gcp, StringComparison.Ordinal);
        Assert.Matches(@"resource ""google_project_iam_member"" ""team_developers"" \{\s+project\s+=\s+var\.project_id\s+role\s+=\s+""roles/viewer""", gcp);

        var aws = Examples.Aws();
        aws.Settings.Environment = "dev";
        Assert.Contains("arn:aws:iam::aws:policy/PowerUserAccess", SketchGenerator.Generate(aws).Files["aws.tf"], StringComparison.Ordinal);

        var azure = new SketchBuilder();
        azure.Sketch.Settings.Options[Team.AzureDevelopers] = "bbbbbbbb-1111-2222-3333-444444444444";
        azure.Add(Kinds.Storage, "files");
        var team = SketchGenerator.Generate(azure.Sketch).Files["team.tf"];
        Assert.Matches(@"role_definition_name\s+=\s+""Contributor""", team);
        var plain = new SketchBuilder();
        plain.Add(Kinds.Storage, "files");
        Assert.Contains("Nobody besides you gets access: name your team's groups in Team access (administrators, developers, readers).", Texts(plain.Sketch, MessageLevel.Info));
    }

    [Fact]
    public void A_box_of_another_cloud_is_an_error()
    {
        var azure = new SketchBuilder();
        azure.Add(CloudKinds.Lambda, "api");

        Assert.Contains("api: the Lambda function box is for Amazon Web Services (AWS), not Microsoft Azure.", Texts(azure.Sketch, MessageLevel.Error));
    }

    [Fact]
    public void An_arrow_from_cloud_run_grants_its_service_account_and_passes_the_name()
    {
        var gcp = Cloud(Platforms.Gcp);
        var api = gcp.Add(CloudKinds.CloudRun, "api");
        gcp.Link(api, gcp.Add(CloudKinds.Bucket, "files"));
        gcp.Link(api, gcp.Add(CloudKinds.CloudSql, "ledger"));

        var result = SketchGenerator.Generate(gcp.Sketch);

        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        var code = result.Files["gcp.tf"];
        Assert.Contains("resource \"google_storage_bucket_iam_member\" \"files_api\"", code, StringComparison.Ordinal);
        Assert.Contains("roles/storage.objectUser", code, StringComparison.Ordinal);
        Assert.Contains("type     = \"CLOUD_IAM_SERVICE_ACCOUNT\"", code, StringComparison.Ordinal);
        Assert.Matches(@"name\s+=\s+""FILES_BUCKET""", code);
        Assert.Contains("sqladmin.googleapis.com", result.Files["check-prereqs.ps1"], StringComparison.Ordinal);
        Assert.Contains("run.googleapis.com", result.Files["check-prereqs.ps1"], StringComparison.Ordinal);
    }

    [Fact]
    public void Google_cloud_checks_refuse_what_google_would_refuse()
    {
        var gcp = Cloud(Platforms.Gcp);
        var balancer = gcp.Add(CloudKinds.LoadBalancer, "edge", new() { ["domain"] = "shop.contoso.com" });
        gcp.Link(balancer, gcp.Add(CloudKinds.Bucket, "files"));
        gcp.Link(gcp.Add(CloudKinds.CloudDns, "domain", new() { ["zone"] = "example.com" }), balancer);

        var errors = Texts(gcp.Sketch, MessageLevel.Error);

        Assert.Contains("files: a load balancer serves a bucket only as a public website; turn on \"Public website\".", errors);
        Assert.Contains("domain -> edge: shop.contoso.com is not inside example.com.", errors);
    }
}
