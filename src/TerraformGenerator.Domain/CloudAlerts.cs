using System.Globalization;

namespace TerraformGenerator.Domain;

// Alerts and the monthly budget on AWS and Google Cloud: an email when code fails, a database is overloaded or failed
// messages pile up, and when the month's spending passes 80% of the budget or is forecast to pass it.
public static class CloudAlerts
{
    public const string GcpBillingAccount = "gcpBillingAccount";

    private static string Email(SketchModel model, string key) => Hcl.Literal(model.Setting(key));

    private static string Amount(SketchModel model) => model.Budget.ToString(CultureInfo.InvariantCulture);

    // AWS ------------------------------------------------------------------------------------------------------------

    private static HclBlock Alarm(string label, string description, string space, string metric, string statistic, string comparison, string threshold,
        IEnumerable<KeyValuePair<string, string>> dimensions) =>
        new HclBlock("resource", "aws_cloudwatch_metric_alarm", label)
            .Attr("alarm_name", Hcl.Str($"${{var.name_prefix}}-{label.Replace('_', '-')}"))
            .Str("alarm_description", description)
            .Str("namespace", space)
            .Str("metric_name", metric)
            .Str("statistic", statistic)
            .Attr("period", "300")
            .Attr("evaluation_periods", "1")
            .Attr("threshold", threshold)
            .Str("comparison_operator", comparison)
            .Str("treat_missing_data", "notBreaching")
            .Attr("alarm_actions", "[aws_sns_topic.alerts.arn]")
            .Attr("ok_actions", "[aws_sns_topic.alerts.arn]")
            .Map("dimensions", dimensions);

    public static IEnumerable<HclItem> Aws(SketchModel model)
    {
        if (model.Alerts)
        {
            yield return new HclComment($"Alerts by email to {model.Setting(SettingKeys.AlertEmail)}: confirm the subscription in the first email AWS sends.");
            yield return new HclBlock("resource", "aws_sns_topic", "alerts").Attr("name", Hcl.Str("${var.name_prefix}-alerts"));
            yield return new HclBlock("resource", "aws_sns_topic_subscription", "alerts")
                .Attr("topic_arn", "aws_sns_topic.alerts.arn")
                .Str("protocol", "email")
                .Attr("endpoint", Email(model, SettingKeys.AlertEmail));
            foreach (var function in model.OfKind(CloudKinds.Lambda))
            {
                yield return Alarm($"{function.Label}_errors", $"{function.Name} fails", "AWS/Lambda", "Errors", "Sum", "GreaterThanOrEqualToThreshold", "1",
                    [new("FunctionName", $"aws_lambda_function.{function.Label}.function_name")]);
            }

            foreach (var container in model.OfKind(CloudKinds.Fargate))
            {
                yield return Alarm($"{container.Label}_server_errors", $"{container.Name} answers with server errors", "AWS/ApplicationELB", "HTTPCode_Target_5XX_Count",
                    "Sum", "GreaterThanOrEqualToThreshold", "10", [new("LoadBalancer", $"aws_lb.{container.Label}.arn_suffix")]);
                yield return Alarm($"{container.Label}_unhealthy", $"A copy of {container.Name} fails its health check", "AWS/ApplicationELB", "UnHealthyHostCount",
                    "Maximum", "GreaterThanOrEqualToThreshold", "1",
                    [new("LoadBalancer", $"aws_lb.{container.Label}.arn_suffix"), new("TargetGroup", $"aws_lb_target_group.{container.Label}.arn_suffix")]);
                yield return Alarm($"{container.Label}_cpu", $"{container.Name} is busy", "AWS/ECS", "CPUUtilization", "Average", "GreaterThanThreshold", "80",
                    [new("ClusterName", "aws_ecs_cluster.main.name"), new("ServiceName", $"aws_ecs_service.{container.Label}.name")]);
            }

            foreach (var database in model.OfKind(CloudKinds.Rds))
            {
                yield return Alarm($"{database.Label}_cpu", $"{database.Name} is overloaded", "AWS/RDS", "CPUUtilization", "Average", "GreaterThanThreshold", "80",
                    [new("DBInstanceIdentifier", $"aws_db_instance.{database.Label}.identifier")]);
                yield return Alarm($"{database.Label}_storage", $"{database.Name} has less than 2 GB free", "AWS/RDS", "FreeStorageSpace", "Minimum", "LessThanThreshold",
                    "2147483648", [new("DBInstanceIdentifier", $"aws_db_instance.{database.Label}.identifier")]);
            }

            foreach (var queue in model.OfKind(CloudKinds.Sqs).Where(queue => queue.Flag("deadLetter")))
            {
                yield return Alarm($"{queue.Label}_failed", $"Messages of {queue.Name} failed 5 times", "AWS/SQS", "ApproximateNumberOfMessagesVisible", "Maximum",
                    "GreaterThanOrEqualToThreshold", "1", [new("QueueName", $"aws_sqs_queue.{queue.Label}_failed.name")]);
            }
        }

        if (model.Budget > 0)
        {
            yield return new HclComment($"Monthly budget of US${Amount(model)} for the whole account: an email at 80% and when the month is forecast to go over.");
            var budget = new HclBlock("resource", "aws_budgets_budget", "monthly")
                .Attr("name", Hcl.Str("${var.name_prefix}-monthly"))
                .Str("budget_type", "COST")
                .Str("limit_amount", Amount(model))
                .Str("limit_unit", "USD")
                .Str("time_unit", "MONTHLY");
            foreach (var (threshold, kind) in new[] { ("80", "ACTUAL"), ("100", "FORECASTED") })
            {
                budget.Block("notification", notification => notification
                    .Str("comparison_operator", "GREATER_THAN")
                    .Attr("threshold", threshold)
                    .Str("threshold_type", "PERCENTAGE")
                    .Str("notification_type", kind)
                    .Attr("subscriber_email_addresses", $"[{Email(model, SettingKeys.BudgetEmail)}]"));
            }

            yield return budget;
        }
    }

    // Google Cloud ---------------------------------------------------------------------------------------------------

    public static bool NeedsProject(SketchModel model) => model.Budget > 0;

    private static HclBlock Policy(string label, string name, string filter, string comparison, string threshold, string aligner) =>
        new HclBlock("resource", "google_monitoring_alert_policy", label)
            .Attr("display_name", Hcl.Str($"${{var.name_prefix}} {name}"))
            .Str("combiner", "OR")
            .Attr("notification_channels", "[google_monitoring_notification_channel.alerts.id]")
            .Block("conditions", condition => condition
                .Str("display_name", name)
                .Block("condition_threshold", threshold_ => threshold_
                    .Attr("filter", Hcl.Str(filter))
                    .Str("comparison", comparison)
                    .Attr("threshold_value", threshold)
                    .Str("duration", "300s")
                    .Block("aggregations", aggregation => aggregation.Str("alignment_period", "300s").Str("per_series_aligner", aligner))));

    public static IEnumerable<string> GcpServices(SketchModel model)
    {
        if (model.Alerts || model.Budget > 0)
        {
            yield return "monitoring.googleapis.com";
        }

        if (model.Budget > 0)
        {
            yield return "billingbudgets.googleapis.com";
        }
    }

    public static IEnumerable<HclItem> Gcp(SketchModel model)
    {
        if (model.Alerts)
        {
            yield return new HclComment($"Alerts by email to {model.Setting(SettingKeys.AlertEmail)}.");
            yield return new HclBlock("resource", "google_monitoring_notification_channel", "alerts")
                .Attr("display_name", Hcl.Str("${var.name_prefix} alerts"))
                .Str("type", "email")
                .Map("labels", [new("email_address", Email(model, SettingKeys.AlertEmail))]);
            foreach (var app in model.Resources.Where(resource => CloudKinds.GcpCompute.Contains(resource.Kind)))
            {
                var service = app.Kind == CloudKinds.CloudRun ? $"google_cloud_run_v2_service.{app.Label}.name" : $"google_cloudfunctions2_function.{app.Label}.name";
                yield return Policy($"{app.Label}_server_errors", $"{app.Name} answers with server errors",
                    $"resource.type = \"cloud_run_revision\" AND resource.labels.service_name = \"${{{service}}}\" AND metric.type = \"run.googleapis.com/request_count\" AND metric.labels.response_code_class = \"5xx\"",
                    "COMPARISON_GT", "0.05", "ALIGN_RATE");
            }

            foreach (var database in model.OfKind(CloudKinds.CloudSql))
            {
                yield return Policy($"{database.Label}_cpu", $"{database.Name} is overloaded",
                    $"resource.type = \"cloudsql_database\" AND resource.labels.database_id = \"${{var.project_id}}:${{google_sql_database_instance.{database.Label}.name}}\" AND metric.type = \"cloudsql.googleapis.com/database/cpu/utilization\"",
                    "COMPARISON_GT", "0.8", "ALIGN_MEAN");
            }

            foreach (var topic in model.OfKind(CloudKinds.PubSub).Where(topic => topic.Flag("deadLetter")))
            {
                yield return Policy($"{topic.Label}_failed", $"Messages of {topic.Name} failed 5 times",
                    $"resource.type = \"pubsub_subscription\" AND resource.labels.subscription_id = \"${{google_pubsub_subscription.{topic.Label}_failed.name}}\" AND metric.type = \"pubsub.googleapis.com/subscription/num_undelivered_messages\"",
                    "COMPARISON_GT", "0", "ALIGN_MAX");
            }
        }

        if (model.Budget > 0)
        {
            yield return new HclComment($"Monthly budget of US${Amount(model)} for this project: an email at 80% and when the month is forecast to go over.");
            yield return new HclBlock("resource", "google_monitoring_notification_channel", "budget")
                .Attr("display_name", Hcl.Str("${var.name_prefix} budget"))
                .Str("type", "email")
                .Map("labels", [new("email_address", Email(model, SettingKeys.BudgetEmail))]);
            yield return new HclBlock("resource", "google_billing_budget", "monthly")
                .Attr("billing_account", Hcl.Literal(model.Setting(GcpBillingAccount)))
                .Attr("display_name", Hcl.Str("${var.name_prefix} monthly"))
                .Block("budget_filter", filter => filter.Attr("projects", "[\"projects/${data.google_project.current.number}\"]"))
                .Block("amount", amount => amount.Block("specified_amount", specified => specified.Str("currency_code", "USD").Str("units", Amount(model))))
                .Block("threshold_rules", rule => rule.Attr("threshold_percent", "0.8"))
                .Block("threshold_rules", rule => rule.Attr("threshold_percent", "1.0").Str("spend_basis", "FORECASTED_SPEND"))
                .Block("all_updates_rule", rule => rule
                    .Attr("monitoring_notification_channels", "[google_monitoring_notification_channel.budget.id]")
                    .Attr("disable_default_iam_recipients", "false"));
        }
    }

    // What AWS and Google Cloud need for these, and what production should have.
    public static IEnumerable<SketchMessage> Check(SketchModel model)
    {
        if (model.Alerts && model.Setting(SettingKeys.AlertEmail).Length == 0)
        {
            yield return new(MessageLevel.Error, "Alerts need the email that receives them.");
        }

        if (model.Budget > 0 && model.Setting(SettingKeys.BudgetEmail).Length == 0)
        {
            yield return new(MessageLevel.Error, "A budget alert needs the email that receives it.");
        }

        if (model.Budget > 0 && model.Platform == Platforms.Gcp && model.Setting(GcpBillingAccount).Length == 0)
        {
            yield return new(MessageLevel.Error, "A Google Cloud budget needs the billing account id (Billing in the console, or gcloud billing accounts list).");
        }

        if (model.IsProduction && !model.Alerts && model.Resources.Count > 0)
        {
            yield return new(MessageLevel.Warning, "Production without alerts: turn them on so someone hears when an app fails.");
        }

        if (model.IsProduction && model.Budget == 0)
        {
            yield return new(MessageLevel.Warning, "Production without a budget alert: choose a monthly budget so a surprise bill is caught early.");
        }

        if (model.Budget > 0 && model.Platform == Platforms.Aws)
        {
            yield return new(MessageLevel.Info, "The AWS budget counts the whole account; use one account per environment to keep them apart.");
        }
    }
}
