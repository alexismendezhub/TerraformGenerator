using static TerraformGenerator.Domain.Catalog;

namespace TerraformGenerator.Domain;

// Audit (who changed what) and threat detection (suspicious sign-ins, crypto-mining, data taken out): Defender for
// Cloud and the activity log in Log Analytics on Azure, CloudTrail and GuardDuty on AWS, data-access audit logs and an
// alert on permission changes on Google Cloud.
public static class Protection
{
    public const string Key = "threatProtection";

    public static PropertyDefinition Setting() =>
        new(Key, "Audit and threat detection", PropertyType.Select, "auto",
        [
            O("auto", "On in production"), O("on", "On"), O("off", "Off"),
        ], null,
        "Records who changed what and watches for attacks. Defender for Cloud and GuardDuty are billed by use; they cover the whole subscription or account.",
        "Security");

    public static bool On(SketchModel model) => model.Setting(Key) switch
    {
        "on" => true,
        "off" => false,
        _ => model.IsProduction,
    } && model.Resources.Count > 0;

    private static string? ContactEmail(SketchModel model) =>
        new[] { model.Setting(SettingKeys.AlertEmail), model.Setting(SettingKeys.BudgetEmail) }.FirstOrDefault(email => email.Length > 0);

    // Azure ----------------------------------------------------------------------------------------------------------

    // The Defender plans for what the drawing has.
    public static IEnumerable<string> DefenderPlans(SketchModel model)
    {
        var plans = new SortedSet<string>(StringComparer.Ordinal) { "Arm" };
        foreach (var resource in model.Resources)
        {
            switch (resource.Kind)
            {
                case Kinds.WebApp or Kinds.FunctionApp:
                    plans.Add("AppServices");
                    break;
                case Kinds.SqlDatabase:
                    plans.Add("SqlServers");
                    break;
                case Kinds.Postgres:
                    plans.Add("OpenSourceRelationalDatabases");
                    break;
                case Kinds.CosmosDb:
                    plans.Add("CosmosDbs");
                    break;
                case Kinds.Storage:
                    plans.Add("StorageAccounts");
                    break;
                case Kinds.KeyVault:
                    plans.Add("KeyVaults");
                    break;
                case Kinds.ContainerApp or Kinds.ContainerRegistry:
                    plans.Add("Containers");
                    break;
                case Kinds.AiFoundry or AzureServices.AiService:
                    plans.Add("AI");
                    break;
            }
        }

        return plans;
    }

    private static readonly string[] ActivityCategories = ["Administrative", "Security", "Policy", "Alert"];

    public static List<HclItem> AzureTerraform(SketchModel model, string workspace)
    {
        var items = new List<HclItem>();
        if (!On(model))
        {
            return items;
        }

        items.Add(new HclComment("Defender for Cloud watches these kinds of resources in the whole subscription (billed by use)."));
        foreach (var plan in DefenderPlans(model))
        {
            items.Add(new HclBlock("resource", "azurerm_security_center_subscription_pricing", plan.ToLowerInvariant())
                .Str("tier", "Standard")
                .Str("resource_type", plan));
        }

        if (ContactEmail(model) is { } email)
        {
            items.Add(new HclBlock("resource", "azurerm_security_center_contact", "main")
                .Str("name", "default")
                .Attr("email", Hcl.Literal(email))
                .Attr("alert_notifications", "true")
                .Attr("alerts_to_admins", "true"));
        }

        if (TerraformGenerator.Logs(model))
        {
            items.Add(new HclComment("Who changed what in the subscription, kept in Log Analytics (Azure keeps it 90 days by itself)."));
            items.Add(new HclBlock("data", "azurerm_subscription", "current"));
            var setting = new HclBlock("resource", "azurerm_monitor_diagnostic_setting", "activity_log")
                .Attr("name", Hcl.Str("${var.name_prefix}-activity"))
                .Attr("target_resource_id", "data.azurerm_subscription.current.id")
                .Attr("log_analytics_workspace_id", workspace);
            foreach (var category in ActivityCategories)
            {
                setting.Block("enabled_log", log => log.Str("category", category));
            }

            items.Add(setting);
        }

        return items;
    }

    public const string BicepModule = "subscription-security.bicep";

    public static string AzureBicepModule => """
        // Defender for Cloud plans, the security contact and the activity log export: they belong to the subscription.
        targetScope = 'subscription'

        @description('The Defender for Cloud plans to turn on.')
        param plans array

        @description('Who gets the security alerts; empty for nobody besides the owners.')
        param email string

        @description('The Log Analytics workspace for the activity log; empty to keep only the 90 days Azure keeps.')
        param workspaceId string

        @description('Starts the name of the activity log export.')
        param namePrefix string

        resource pricing 'Microsoft.Security/pricings@2024-01-01' = [for plan in plans: {
          name: plan
          properties: {
            pricingTier: 'Standard'
          }
        }]

        resource contact 'Microsoft.Security/securityContacts@2020-01-01-preview' = if (!empty(email)) {
          name: 'default'
          properties: {
            emails: email
            alertNotifications: {
              state: 'On'
              minimalSeverity: 'Medium'
            }
            notificationsByRole: {
              state: 'On'
              roles: [
                'Owner'
              ]
            }
          }
        }

        resource activity 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (!empty(workspaceId)) {
          name: '${namePrefix}-activity'
          properties: {
            workspaceId: workspaceId
            logs: [for category in ['Administrative', 'Security', 'Policy', 'Alert']: {
              category: category
              enabled: true
            }]
          }
        }

        """;

    public static void AzureBicep(SketchModel model, BicepFile file)
    {
        if (!On(model))
        {
            return;
        }

        file.Module("subscriptionSecurity", BicepModule, Bicep.Object(body => body
            .Add("scope", "subscription()")
            .Obj("params", parameters => parameters
                .Add("plans", Bicep.Array(DefenderPlans(model).Select(plan => (BicepValue)Bicep.Raw(Bicep.Str(plan))).ToArray()))
                .Add("email", Bicep.Literal(ContactEmail(model) ?? ""))
                .Add("workspaceId", TerraformGenerator.Logs(model) ? "logs.id" : "''")
                .Add("namePrefix", "namePrefix"))),
            "Defender for Cloud and the activity log, set on the subscription.");
    }

    // AWS ------------------------------------------------------------------------------------------------------------

    public static IEnumerable<HclItem> Aws(SketchModel model)
    {
        if (!On(model))
        {
            yield break;
        }

        const string trail = "${var.name_prefix}-trail";
        const string source = "arn:aws:cloudtrail:${var.region}:${data.aws_caller_identity.trail.account_id}:trail/" + trail;
        yield return new HclComment("CloudTrail: who changed what in the account, in every region, kept a year in its own bucket.");
        yield return new HclBlock("data", "aws_caller_identity", "trail");
        yield return new HclBlock("resource", "aws_s3_bucket", "trail")
            .Attr("bucket", Hcl.Str("${var.name_prefix}-trail-${local.suffix}"))
            .Attr("force_destroy", Hcl.Bool(!model.IsProduction));
        yield return new HclBlock("resource", "aws_s3_bucket_public_access_block", "trail")
            .Attr("bucket", "aws_s3_bucket.trail.id")
            .Attr("block_public_acls", "true")
            .Attr("block_public_policy", "true")
            .Attr("ignore_public_acls", "true")
            .Attr("restrict_public_buckets", "true");
        yield return new HclBlock("resource", "aws_s3_bucket_server_side_encryption_configuration", "trail")
            .Attr("bucket", "aws_s3_bucket.trail.id")
            .Block("rule", rule => rule.Block("apply_server_side_encryption_by_default", encryption => encryption.Str("sse_algorithm", "AES256")));
        yield return new HclBlock("resource", "aws_s3_bucket_lifecycle_configuration", "trail")
            .Attr("bucket", "aws_s3_bucket.trail.id")
            .Block("rule", rule => rule
                .Str("id", "keep-a-year")
                .Str("status", "Enabled")
                .Block("filter", _ => { })
                .Block("expiration", expiration => expiration.Attr("days", "365")));
        yield return new HclBlock("data", "aws_iam_policy_document", "trail")
            .Block("statement", statement => statement
                .Str("sid", "CloudTrailCheck")
                .Attr("actions", "[\"s3:GetBucketAcl\"]")
                .Attr("resources", "[aws_s3_bucket.trail.arn]")
                .Block("principals", principals => principals.Str("type", "Service").Attr("identifiers", "[\"cloudtrail.amazonaws.com\"]"))
                .Block("condition", condition => condition.Str("test", "StringEquals").Str("variable", "aws:SourceArn").Attr("values", $"[{Hcl.Str(source)}]")))
            .Block("statement", statement => statement
                .Str("sid", "CloudTrailWrite")
                .Attr("actions", "[\"s3:PutObject\"]")
                .Attr("resources", "[\"${aws_s3_bucket.trail.arn}/AWSLogs/${data.aws_caller_identity.trail.account_id}/*\"]")
                .Block("principals", principals => principals.Str("type", "Service").Attr("identifiers", "[\"cloudtrail.amazonaws.com\"]"))
                .Block("condition", condition => condition.Str("test", "StringEquals").Str("variable", "s3:x-amz-acl").Attr("values", "[\"bucket-owner-full-control\"]"))
                .Block("condition", condition => condition.Str("test", "StringEquals").Str("variable", "aws:SourceArn").Attr("values", $"[{Hcl.Str(source)}]")));
        yield return new HclBlock("resource", "aws_s3_bucket_policy", "trail")
            .Attr("bucket", "aws_s3_bucket.trail.id")
            .Attr("policy", "data.aws_iam_policy_document.trail.json");
        yield return new HclBlock("resource", "aws_cloudtrail", "main")
            .Attr("name", Hcl.Str(trail))
            .Attr("s3_bucket_name", "aws_s3_bucket.trail.id")
            .Attr("is_multi_region_trail", "true")
            .Attr("include_global_service_events", "true")
            .Attr("enable_log_file_validation", "true")
            .Attr("depends_on", "[aws_s3_bucket_policy.trail]");
        yield return new HclComment("GuardDuty watches the account for attacks: suspicious sign-ins, crypto-mining, data taken out.");
        yield return new HclBlock("resource", "aws_guardduty_detector", "main")
            .Attr("enable", "true")
            .Str("finding_publishing_frequency", "FIFTEEN_MINUTES");
        if (model.Alerts)
        {
            yield return new HclComment("GuardDuty's findings arrive by email through the alerts topic.");
            yield return new HclBlock("resource", "aws_cloudwatch_event_rule", "guardduty")
                .Attr("name", Hcl.Str("${var.name_prefix}-guardduty"))
                .Attr("event_pattern", "jsonencode({ source = [\"aws.guardduty\"], \"detail-type\" = [\"GuardDuty Finding\"] })");
            yield return new HclBlock("resource", "aws_cloudwatch_event_target", "guardduty")
                .Attr("rule", "aws_cloudwatch_event_rule.guardduty.name")
                .Attr("arn", "aws_sns_topic.alerts.arn");
            yield return new HclBlock("data", "aws_iam_policy_document", "alerts")
                .Block("statement", statement => statement
                    .Str("sid", "AlarmsAndFindings")
                    .Attr("actions", "[\"sns:Publish\"]")
                    .Attr("resources", "[aws_sns_topic.alerts.arn]")
                    .Block("principals", principals => principals.Str("type", "Service").Attr("identifiers", "[\"cloudwatch.amazonaws.com\", \"events.amazonaws.com\"]")))
                .Block("statement", statement => statement
                    .Str("sid", "Account")
                    .Attr("actions", "[\"sns:Publish\", \"sns:Subscribe\", \"sns:GetTopicAttributes\", \"sns:SetTopicAttributes\"]")
                    .Attr("resources", "[aws_sns_topic.alerts.arn]")
                    .Block("principals", principals => principals.Str("type", "AWS").Attr("identifiers", "[\"arn:aws:iam::${data.aws_caller_identity.trail.account_id}:root\"]")));
            yield return new HclBlock("resource", "aws_sns_topic_policy", "alerts")
                .Attr("arn", "aws_sns_topic.alerts.arn")
                .Attr("policy", "data.aws_iam_policy_document.alerts.json");
        }
    }

    // Google Cloud ---------------------------------------------------------------------------------------------------

    public static IEnumerable<HclItem> Gcp(SketchModel model)
    {
        if (!On(model))
        {
            yield break;
        }

        yield return new HclComment("Audit logs: who read settings and who changed data, besides the changes Google always records.");
        yield return new HclBlock("resource", "google_project_iam_audit_config", "all")
            .Attr("project", "var.project_id")
            .Str("service", "allServices")
            .Block("audit_log_config", config => config.Str("log_type", "ADMIN_READ"))
            .Block("audit_log_config", config => config.Str("log_type", "DATA_WRITE"));
        if (model.Alerts)
        {
            yield return new HclComment("An email when someone changes who has access to the project.");
            yield return new HclBlock("resource", "google_monitoring_alert_policy", "access_changes")
                .Attr("display_name", Hcl.Str("${var.name_prefix} access changed"))
                .Str("combiner", "OR")
                .Attr("notification_channels", "[google_monitoring_notification_channel.alerts.id]")
                .Block("conditions", condition => condition
                    .Str("display_name", "Someone changed the project's access")
                    .Block("condition_matched_log", log => log.Str("filter", "protoPayload.methodName=\"SetIamPolicy\"")))
                .Block("alert_strategy", strategy => strategy.Block("notification_rate_limit", limit => limit.Str("period", "300s")));
        }
    }

    public static IEnumerable<SketchMessage> Check(SketchModel model)
    {
        if (!On(model))
        {
            if (model.IsProduction && model.Resources.Count > 0)
            {
                yield return new(MessageLevel.Warning, "Production without audit and threat detection: nobody would notice an attack or know who changed what.");
            }

            yield break;
        }

        yield return new(MessageLevel.Info, model.Platform switch
        {
            Platforms.Azure => "Defender for Cloud plans cover the whole subscription; if another environment shares it, turn this on in only one drawing.",
            Platforms.Aws => "CloudTrail and GuardDuty cover the whole account; if the account already has a trail or GuardDuty, turn this off here.",
            _ => "Threat detection on Google Cloud is Security Command Center, set at the organization; here the audit logs and an alert on access changes are added.",
        });
    }
}
