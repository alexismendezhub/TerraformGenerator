using System.Text.Json;

namespace TerraformGenerator.Domain;

// App monitoring on AWS and Google Cloud, the way Application Insights does it on Azure: a trace of every request
// (X-Ray, Cloud Trace) and a dashboard with errors, response times and database load.
public static class Observability
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    // AWS: a CloudWatch dashboard for the functions, the containers' load balancers and the databases.
    public static IEnumerable<HclItem> Aws(SketchModel model)
    {
        if (!model.Settings.Monitoring)
        {
            yield break;
        }

        var widgets = new List<object>();
        void Widget(string title, IEnumerable<string[]> metrics, string stat) => widgets.Add(new
        {
            type = "metric",
            width = 12,
            height = 6,
            properties = new { title, region = "${var.region}", stat, period = 300, metrics = metrics.ToArray() },
        });

        foreach (var function in model.OfKind(CloudKinds.Lambda))
        {
            var name = $"${{aws_lambda_function.{function.Label}.function_name}}";
            Widget($"{function.Name}: calls and errors", [["AWS/Lambda", "Invocations", "FunctionName", name], ["AWS/Lambda", "Errors", "FunctionName", name]], "Sum");
            Widget($"{function.Name}: duration (ms)", [["AWS/Lambda", "Duration", "FunctionName", name]], "p95");
        }

        foreach (var container in model.OfKind(CloudKinds.Fargate))
        {
            var balancer = $"${{aws_lb.{container.Label}.arn_suffix}}";
            Widget($"{container.Name}: requests and server errors",
                [["AWS/ApplicationELB", "RequestCount", "LoadBalancer", balancer], ["AWS/ApplicationELB", "HTTPCode_Target_5XX_Count", "LoadBalancer", balancer]], "Sum");
            Widget($"{container.Name}: response time (s)", [["AWS/ApplicationELB", "TargetResponseTime", "LoadBalancer", balancer]], "p95");
        }

        foreach (var database in model.OfKind(CloudKinds.Rds))
        {
            Widget($"{database.Name}: CPU (%)", [["AWS/RDS", "CPUUtilization", "DBInstanceIdentifier", $"${{aws_db_instance.{database.Label}.identifier}}"]], "Average");
        }

        if (widgets.Count == 0)
        {
            yield break;
        }

        yield return new HclComment("A CloudWatch dashboard: calls, errors and response times of the code, and the load of the databases.");
        yield return new HclBlock("resource", "aws_cloudwatch_dashboard", "main")
            .Attr("dashboard_name", "var.name_prefix")
            .Attr("dashboard_body", Hcl.Str(JsonSerializer.Serialize(new { widgets }, Json)));
    }

    // Google Cloud: a Cloud Monitoring dashboard for the services and the databases.
    public static IEnumerable<HclItem> Gcp(SketchModel model)
    {
        if (!model.Settings.Monitoring)
        {
            yield break;
        }

        var tiles = new List<object>();
        void Tile(string title, string filter, string aligner) => tiles.Add(new
        {
            width = 24,
            height = 16,
            yPos = tiles.Count / 2 * 16,
            xPos = tiles.Count % 2 * 24,
            widget = new
            {
                title,
                xyChart = new
                {
                    dataSets = new[]
                    {
                        new { timeSeriesQuery = new { timeSeriesFilter = new { filter, aggregation = new { alignmentPeriod = "60s", perSeriesAligner = aligner } } } },
                    },
                },
            },
        });

        foreach (var app in model.Resources.Where(resource => CloudKinds.GcpCompute.Contains(resource.Kind)))
        {
            var service = app.Kind == CloudKinds.CloudRun ? $"${{google_cloud_run_v2_service.{app.Label}.name}}" : $"${{google_cloudfunctions2_function.{app.Label}.name}}";
            var resource = $"resource.type=\"cloud_run_revision\" resource.label.\"service_name\"=\"{service}\"";
            Tile($"{app.Name}: requests", $"metric.type=\"run.googleapis.com/request_count\" {resource}", "ALIGN_RATE");
            Tile($"{app.Name}: response time (p95)", $"metric.type=\"run.googleapis.com/request_latencies\" {resource}", "ALIGN_PERCENTILE_95");
        }

        foreach (var database in model.OfKind(CloudKinds.CloudSql))
        {
            Tile($"{database.Name}: CPU",
                $"metric.type=\"cloudsql.googleapis.com/database/cpu/utilization\" resource.type=\"cloudsql_database\" resource.label.\"database_id\"=\"${{var.project_id}}:${{google_sql_database_instance.{database.Label}.name}}\"",
                "ALIGN_MEAN");
        }

        if (tiles.Count == 0)
        {
            yield break;
        }

        yield return new HclComment("A Cloud Monitoring dashboard: requests and response times of the services, and the load of the databases.");
        yield return new HclBlock("resource", "google_monitoring_dashboard", "main")
            .Attr("dashboard_json", Hcl.Str(JsonSerializer.Serialize(new { displayName = "${var.name_prefix}", mosaicLayout = new { columns = 48, tiles } }, Json)));
        foreach (var app in model.Resources.Where(resource => CloudKinds.GcpCompute.Contains(resource.Kind)))
        {
            yield return new HclBlock("resource", "google_project_iam_member", $"{app.Label}_traces")
                .Attr("project", "var.project_id")
                .Str("role", "roles/cloudtrace.agent")
                .Attr("member", $"\"serviceAccount:${{google_service_account.{app.Label}.email}}\"");
        }
    }

    public static IEnumerable<string> GcpServices(SketchModel model) =>
        model.Settings.Monitoring && model.Resources.Any(resource => CloudKinds.GcpCompute.Contains(resource.Kind))
            ? ["cloudtrace.googleapis.com", "monitoring.googleapis.com"]
            : [];
}
