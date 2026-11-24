namespace TerraformGenerator.Domain;

// Terraform for the Google Cloud smart boxes. Code runs with its own service account; each arrow from code grants
// that account the role it needs on the target and passes the target's name as an environment variable.
public static partial class GcpGenerator
{
    public static bool Any(SketchModel model) => model.Resources.Any(resource => CloudKinds.Gcp.Any(kind => kind.Kind == resource.Kind));

    // A VPC when the databases are private: Cloud SQL gets a private address and the code that uses them a way in.
    public static bool NeedsNetwork(SketchModel model) =>
        model.Platform == Platforms.Gcp && model.GcpPrivateNetwork && model.OfKind(CloudKinds.CloudSql).Any();

    private static bool UsesNetwork(SketchModel model, SketchResource app) => NeedsNetwork(model) && model.Targets(app, CloudKinds.CloudSql).Any();

    private static IEnumerable<HclItem> Network()
    {
        yield return new HclComment("A VPC for the private Cloud SQL addresses; Google's managed services connect to it.");
        yield return new HclBlock("resource", "google_compute_network", "main")
            .Attr("name", Hcl.Str("${var.name_prefix}-network"))
            .Attr("auto_create_subnetworks", "false");
        yield return new HclBlock("resource", "google_compute_subnetwork", "main")
            .Attr("name", Hcl.Str("${var.name_prefix}-subnet"))
            .Attr("region", "var.region")
            .Attr("network", "google_compute_network.main.id")
            .Attr("ip_cidr_range", "cidrsubnet(var.network_range, 4, 0)")
            .Attr("private_ip_google_access", "true");
        yield return new HclBlock("resource", "google_compute_global_address", "private_services")
            .Attr("name", Hcl.Str("${var.name_prefix}-private-services"))
            .Str("purpose", "VPC_PEERING")
            .Str("address_type", "INTERNAL")
            .Attr("prefix_length", "20")
            .Attr("network", "google_compute_network.main.id");
        yield return new HclBlock("resource", "google_service_networking_connection", "main")
            .Attr("network", "google_compute_network.main.id")
            .Str("service", "servicenetworking.googleapis.com")
            .Attr("reserved_peering_ranges", "[google_compute_global_address.private_services.name]");
    }

    private static string Name(SketchResource resource) => $"${{var.name_prefix}}-{resource.Name}";

    private static string Account(SketchResource app) => $"google_service_account.{app.Label}";

    private static string Member(SketchResource app) => $"\"serviceAccount:${{{Account(app)}.email}}\"";

    private static string Address(SketchResource app) => app.Kind == CloudKinds.CloudRun
        ? $"google_cloud_run_v2_service.{app.Label}"
        : $"google_cloudfunctions2_function.{app.Label}";

    // The APIs the drawing uses; check-prereqs.ps1 turns them on.
    public static IEnumerable<string> Services(SketchModel model)
    {
        var services = new SortedSet<string>(StringComparer.Ordinal) { "iam.googleapis.com" };
        if (NeedsNetwork(model))
        {
            services.Add("compute.googleapis.com");
            services.Add("servicenetworking.googleapis.com");
        }

        services.UnionWith(CloudAlerts.GcpServices(model));
        services.UnionWith(Observability.GcpServices(model));
        if (OwnKeys.Gcp(model))
        {
            services.Add("cloudkms.googleapis.com");
        }

        foreach (var resource in model.Resources)
        {
            foreach (var service in resource.Kind switch
            {
                CloudKinds.CloudRun => ["run.googleapis.com"],
                CloudKinds.CloudFunction => ["cloudfunctions.googleapis.com", "run.googleapis.com", "cloudbuild.googleapis.com", "artifactregistry.googleapis.com", "storage.googleapis.com"],
                CloudKinds.CloudSql => ["sqladmin.googleapis.com"],
                CloudKinds.Firestore => ["firestore.googleapis.com"],
                CloudKinds.Bucket => ["storage.googleapis.com"],
                CloudKinds.ArtifactRegistry => ["artifactregistry.googleapis.com"],
                CloudKinds.PubSub => ["pubsub.googleapis.com"],
                CloudKinds.GcpSecret => ["secretmanager.googleapis.com"],
                CloudKinds.LoadBalancer => ["compute.googleapis.com"],
                CloudKinds.CloudDns => ["dns.googleapis.com"],
                _ => AiServices(resource),
            })
            {
                services.Add(service);
            }
        }

        return services;
    }

    public static List<HclItem> Resources(SketchModel model)
    {
        var items = new List<HclItem>();
        if (!Any(model))
        {
            return items;
        }

        if (NeedsNetwork(model))
        {
            items.AddRange(Network());
        }

        items.AddRange(OwnKeys.GcpKeys(model));

        if (model.OfKind(CloudKinds.PubSub).Any(topic => topic.Flag("deadLetter")) || CloudAlerts.NeedsProject(model) || OwnKeys.NeedsProject(model))
        {
            items.Add(new HclComment("The project's number names Pub/Sub's own service account."));
            items.Add(new HclBlock("data", "google_project", "current"));
        }

        foreach (var app in model.Resources.Where(resource => CloudKinds.GcpCompute.Contains(resource.Kind)))
        {
            items.Add(new HclComment($"{app.Name} runs as its own service account, with only the roles its arrows give it."));
            items.Add(new HclBlock("resource", "google_service_account", app.Label)
                .Attr("account_id", Hcl.Str($"{app.Name}-${{local.suffix}}"))
                .Attr("display_name", Hcl.Str(Name(app))));
        }

        if (model.OfKind(CloudKinds.CloudFunction).Any())
        {
            items.AddRange(FunctionSupport());
        }

        foreach (var service in model.OfKind(CloudKinds.CloudRun))
        {
            items.AddRange(CloudRun(model, service));
        }

        foreach (var function in model.OfKind(CloudKinds.CloudFunction))
        {
            items.AddRange(Function(model, function));
        }

        foreach (var registry in model.OfKind(CloudKinds.ArtifactRegistry))
        {
            items.Add(new HclComment($"Artifact Registry {registry.Name}: Docker images; the last 30 versions are kept."));
            items.Add(new HclBlock("resource", "google_artifact_registry_repository", registry.Label)
                .Attr("repository_id", Hcl.Str(Name(registry)))
                .Attr("location", "var.region")
                .Str("format", "DOCKER")
                .Block("cleanup_policies", policy => policy
                    .Str("id", "keep-recent")
                    .Str("action", "KEEP")
                    .Block("most_recent_versions", recent => recent.Attr("keep_count", "30"))));
        }

        foreach (var bucket in model.OfKind(CloudKinds.Bucket))
        {
            items.AddRange(Bucket(model, bucket));
        }

        foreach (var database in model.OfKind(CloudKinds.CloudSql))
        {
            items.AddRange(Sql(model, database));
        }

        foreach (var database in model.OfKind(CloudKinds.Firestore))
        {
            items.AddRange(Firestore(model, database));
        }

        foreach (var topic in model.OfKind(CloudKinds.PubSub))
        {
            items.AddRange(Topic(model, topic));
        }

        foreach (var secret in model.OfKind(CloudKinds.GcpSecret))
        {
            items.Add(new HclComment($"Secret {secret.Name}: add its value after deploying (gcloud secrets versions add)."));
            items.Add(new HclBlock("resource", "google_secret_manager_secret", secret.Label)
                .Attr("secret_id", Hcl.Str(Name(secret)))
                .Block("replication", replication => replication.Block("auto", _ => { })));
            foreach (var app in model.Links.Where(link => link.To == secret).Select(link => link.From))
            {
                items.Add(new HclBlock("resource", "google_secret_manager_secret_iam_member", $"{secret.Label}_{app.Label}")
                    .Attr("secret_id", $"google_secret_manager_secret.{secret.Label}.id")
                    .Str("role", "roles/secretmanager.secretAccessor")
                    .Attr("member", Member(app)));
            }
        }

        foreach (var balancer in model.OfKind(CloudKinds.LoadBalancer))
        {
            items.AddRange(LoadBalancer(model, balancer));
        }

        foreach (var zone in model.OfKind(CloudKinds.CloudDns))
        {
            items.Add(new HclComment($"DNS zone {zone.P["zone"]}: point your domain registrar to its name servers (see the outputs)."));
            items.Add(new HclBlock("resource", "google_dns_managed_zone", zone.Label)
                .Attr("name", Hcl.Str(Name(zone)))
                .Str("dns_name", zone.P["zone"] + "."));
            foreach (var balancer in model.Targets(zone, CloudKinds.LoadBalancer))
            {
                items.Add(new HclBlock("resource", "google_dns_record_set", $"{zone.Label}_{balancer.Label}")
                    .Attr("managed_zone", $"google_dns_managed_zone.{zone.Label}.name")
                    .Str("name", balancer.P["domain"] + ".")
                    .Str("type", "A")
                    .Attr("ttl", "300")
                    .Attr("rrdatas", $"[google_compute_global_address.{balancer.Label}.address]"));
            }
        }

        items.AddRange(AiResources(model));
        items.AddRange(Team.GcpTerraform(model));
        items.AddRange(CloudAlerts.Gcp(model));
        items.AddRange(Protection.Gcp(model));
        items.AddRange(Observability.Gcp(model));

        // Code that calls other code may invoke it.
        foreach (var (from, to) in model.Links.Where(link => CloudKinds.GcpCompute.Contains(link.From.Kind) && CloudKinds.GcpCompute.Contains(link.To.Kind)))
        {
            items.Add(new HclBlock("resource", "google_cloud_run_v2_service_iam_member", $"{to.Label}_{from.Label}")
                .Attr("name", RunName(to))
                .Attr("location", "var.region")
                .Str("role", "roles/run.invoker")
                .Attr("member", Member(from)));
        }

        return items;
    }

    // Code that only the load balancer calls takes no traffic from anywhere else; code other code calls stays reachable
    // (and still asks for a Google identity unless it is public).
    private static bool OnlyBehindBalancer(SketchModel model, SketchResource app) =>
        model.Sources(app, CloudKinds.LoadBalancer).Any() && !model.Links.Any(link => link.To == app && CloudKinds.GcpCompute.Contains(link.From.Kind));

    // The Cloud Run service behind code: the service itself, or the one a function runs on.
    private static string RunName(SketchResource app) => app.Kind == CloudKinds.CloudRun
        ? $"google_cloud_run_v2_service.{app.Label}.name"
        : $"google_cloudfunctions2_function.{app.Label}.service_config[0].service";

    private static List<KeyValuePair<string, string>> Settings(SketchModel model, SketchResource app)
    {
        var settings = new List<KeyValuePair<string, string>>();
        foreach (var target in model.Links.Where(link => link.From == app).Select(link => link.To))
        {
            switch (target.Kind)
            {
                case CloudKinds.Bucket:
                    settings.Add(new($"{target.Upper}_BUCKET", $"google_storage_bucket.{target.Label}.name"));
                    break;
                case CloudKinds.Firestore:
                    settings.Add(new($"{target.Upper}_DATABASE", $"google_firestore_database.{target.Label}.name"));
                    break;
                case CloudKinds.PubSub:
                    settings.Add(new($"{target.Upper}_TOPIC", $"google_pubsub_topic.{target.Label}.id"));
                    foreach (var subscription in target.List("subscriptions"))
                    {
                        settings.Add(new($"{target.Upper}_{subscription.Replace('-', '_').ToUpperInvariant()}_SUBSCRIPTION",
                            $"google_pubsub_subscription.{target.Label}_{subscription.Replace('-', '_')}.id"));
                    }

                    break;
                case CloudKinds.GcpSecret:
                    settings.Add(new($"{target.Upper}_SECRET", $"google_secret_manager_secret.{target.Label}.id"));
                    break;
                case CloudKinds.CloudSql:
                    settings.Add(new($"{target.Upper}_INSTANCE", $"google_sql_database_instance.{target.Label}.connection_name"));
                    settings.Add(new($"{target.Upper}_DB_NAME", $"google_sql_database.{target.Label}.name"));
                    settings.Add(new($"{target.Upper}_DB_USER", $"google_sql_user.{target.Label}_{app.Label}.name"));
                    settings.Add(new($"{target.Upper}_IP_TYPE", Hcl.Str(NeedsNetwork(model) ? "PRIVATE" : "PUBLIC")));
                    break;
                case CloudKinds.CloudRun:
                    settings.Add(new($"{target.Upper}_URL", $"google_cloud_run_v2_service.{target.Label}.uri"));
                    break;
                case CloudKinds.CloudFunction:
                    settings.Add(new($"{target.Upper}_URL", $"google_cloudfunctions2_function.{target.Label}.url"));
                    break;
            }
        }

        foreach (var target in model.Links.Where(link => link.From == app).Select(link => link.To))
        {
            settings.AddRange(AiSettings(model, target));
        }

        settings.Add(new("PROJECT_ID", "var.project_id"));
        return settings;
    }

    private static IEnumerable<HclItem> CloudRun(SketchModel model, SketchResource service)
    {
        var label = service.Label;
        var (cpu, memory) = service.P["size"] switch
        {
            "1-1" => ("1", "1Gi"),
            "2" => ("2", "2Gi"),
            "4" => ("4", "4Gi"),
            _ => ("1", "512Mi"),
        };
        var databases = model.Targets(service, CloudKinds.CloudSql).ToList();
        var behindBalancer = OnlyBehindBalancer(model, service);
        yield return new HclComment($"Cloud Run service {service.Name}.");
        var block = new HclBlock("resource", "google_cloud_run_v2_service", label)
            .Attr("name", Hcl.Str(Name(service)))
            .Attr("location", "var.region")
            .Attr("deletion_protection", Hcl.Bool(model.IsProduction))
            .Str("ingress", behindBalancer ? "INGRESS_TRAFFIC_INTERNAL_LOAD_BALANCER" : "INGRESS_TRAFFIC_ALL")
            .Block("template", template =>
            {
                template.Attr("service_account", $"{Account(service)}.email")
                    .Block("scaling", scaling => scaling.Attr("min_instance_count", service.P["minInstances"]).Attr("max_instance_count", service.P["maxInstances"]))
                    .Block("containers", container =>
                    {
                        container.Str("image", service.P["image"])
                            .Block("ports", ports => ports.Attr("container_port", service.P["port"]))
                            .Block("resources", resources => resources.Map("limits", [new("cpu", Hcl.Str(cpu)), new("memory", Hcl.Str(memory))]));
                        foreach (var (name, value) in Settings(model, service))
                        {
                            container.Block("env", env => env.Str("name", name).Attr("value", value));
                        }

                        if (databases.Count > 0)
                        {
                            container.Block("volume_mounts", mount => mount.Str("name", "cloudsql").Str("mount_path", "/cloudsql"));
                        }
                    });
                if (UsesNetwork(model, service))
                {
                    template.Comment("Private addresses (the databases) through the VPC; the internet as usual.")
                        .Block("vpc_access", access => access
                            .Str("egress", "PRIVATE_RANGES_ONLY")
                            .Block("network_interfaces", network => network
                                .Attr("network", "google_compute_network.main.id")
                                .Attr("subnetwork", "google_compute_subnetwork.main.id")));
                }

                if (databases.Count > 0)
                {
                    template.Comment("The Cloud SQL sockets under /cloudsql.")
                        .Block("volumes", volume => volume
                            .Str("name", "cloudsql")
                            .Block("cloud_sql_instance", sql => sql.Attr("instances",
                                Hcl.List(databases.Select(database => $"google_sql_database_instance.{database.Label}.connection_name")))));
                }
            });
        if (model.Targets(service, CloudKinds.ArtifactRegistry).Any())
        {
            block.Comment("The app pipeline deploys the service's own image from its registry; Terraform leaves it alone.")
                .Block("lifecycle", lifecycle => lifecycle.Attr("ignore_changes", "[template[0].containers[0].image]"));
        }

        yield return block;
        if (service.Flag("public"))
        {
            yield return new HclComment($"Anyone can call {service.Name}.");
            yield return new HclBlock("resource", "google_cloud_run_v2_service_iam_member", $"{label}_public")
                .Attr("name", $"google_cloud_run_v2_service.{label}.name")
                .Attr("location", "var.region")
                .Str("role", "roles/run.invoker")
                .Str("member", "allUsers");
        }
    }

    // Functions are built by Cloud Build from a zip in a bucket, with a build account that has only what it needs.
    private static IEnumerable<HclItem> FunctionSupport()
    {
        yield return new HclComment("The zips of the functions' code.");
        yield return new HclBlock("resource", "google_storage_bucket", "functions_source")
            .Attr("name", Hcl.Str("${var.name_prefix}-functions-${local.suffix}"))
            .Attr("location", "var.region")
            .Attr("uniform_bucket_level_access", "true")
            .Str("public_access_prevention", "enforced")
            .Attr("force_destroy", "true");
        yield return new HclComment("The account Cloud Build uses to build the functions.");
        yield return new HclBlock("resource", "google_service_account", "functions_build")
            .Attr("account_id", Hcl.Str("functions-build-${local.suffix}"))
            .Attr("display_name", Hcl.Str("${var.name_prefix} functions build"));
        yield return new HclBlock("resource", "google_project_iam_member", "functions_build")
            .Attr("for_each", "toset([\"roles/logging.logWriter\", \"roles/artifactregistry.writer\", \"roles/storage.objectViewer\"])")
            .Attr("project", "var.project_id")
            .Attr("role", "each.value")
            .Attr("member", "\"serviceAccount:${google_service_account.functions_build.email}\"");
    }

    private static readonly Dictionary<string, string> EntryPoints = new(StringComparer.Ordinal)
    {
        ["go123"] = "Handler",
        ["java21"] = "functions.Handler",
    };

    public static string EntryPoint(SketchResource function) => EntryPoints.GetValueOrDefault(function.P["runtime"], "handler");

    private static IEnumerable<HclItem> Function(SketchModel model, SketchResource function)
    {
        var label = function.Label;
        yield return new HclComment($"Function {function.Name}: its code is the folder functions/{function.Name}, zipped and built on every change.");
        yield return new HclBlock("data", "archive_file", label)
            .Str("type", "zip")
            .Str("source_dir", $"${{path.module}}/functions/{function.Name}")
            .Str("output_path", $"${{path.module}}/.build/{function.Name}.zip");
        yield return new HclBlock("resource", "google_storage_bucket_object", label)
            .Attr("name", Hcl.Str($"{function.Name}-${{data.archive_file.{label}.output_md5}}.zip"))
            .Attr("bucket", "google_storage_bucket.functions_source.name")
            .Attr("source", $"data.archive_file.{label}.output_path");
        var settings = Settings(model, function);
        yield return new HclBlock("resource", "google_cloudfunctions2_function", label)
            .Attr("name", Hcl.Str(Name(function)))
            .Attr("location", "var.region")
            .Block("build_config", build => build
                .Str("runtime", function.P["runtime"])
                .Str("entry_point", EntryPoint(function))
                .Attr("service_account", "google_service_account.functions_build.id")
                .Block("source", source => source.Block("storage_source", storage => storage
                    .Attr("bucket", "google_storage_bucket.functions_source.name")
                    .Attr("object", $"google_storage_bucket_object.{label}.name"))))
            .Block("service_config", service =>
            {
                service.Attr("max_instance_count", "10")
                    .Str("available_memory", function.P["memory"])
                    .Attr("timeout_seconds", "60")
                    .Attr("service_account_email", $"{Account(function)}.email")
                    .Str("ingress_settings", OnlyBehindBalancer(model, function) ? "ALLOW_INTERNAL_AND_GCLB" : "ALLOW_ALL");
                if (UsesNetwork(model, function))
                {
                    service.Comment("Private addresses (the databases) through the VPC; the internet as usual.")
                        .Str("direct_vpc_egress", "VPC_EGRESS_PRIVATE_RANGES_ONLY")
                        .Block("direct_vpc_network_interface", network => network
                            .Attr("network", "google_compute_network.main.id")
                            .Attr("subnetwork", "google_compute_subnetwork.main.id"));
                }

                service.Map("environment_variables", settings);
            })

            .Attr("depends_on", "[google_project_iam_member.functions_build]");
        if (function.Flag("public"))
        {
            yield return new HclComment($"Anyone can call {function.Name}.");
            yield return new HclBlock("resource", "google_cloud_run_v2_service_iam_member", $"{label}_public")
                .Attr("name", $"google_cloudfunctions2_function.{label}.service_config[0].service")
                .Attr("location", "var.region")
                .Str("role", "roles/run.invoker")
                .Str("member", "allUsers");
        }
    }

    private static IEnumerable<HclItem> Bucket(SketchModel model, SketchResource bucket)
    {
        var label = bucket.Label;
        var website = bucket.Flag("website");
        yield return new HclComment(website ? $"Bucket {bucket.Name}: a public website." : $"Bucket {bucket.Name}: no public access.");
        var block = new HclBlock("resource", "google_storage_bucket", label)
            .Attr("name", Hcl.Str($"{Name(bucket)}-${{local.suffix}}"))
            .Attr("location", Geo.On(model) ? Hcl.Str(Geo.GcpMultiRegion(model)) : "var.region")
            .Str("storage_class", bucket.P["storageClass"])
            .Attr("uniform_bucket_level_access", "true")
            .Str("public_access_prevention", website ? "inherited" : "enforced")
            .Attr("force_destroy", Hcl.Bool(!model.IsProduction))
            .Block("versioning", versioning => versioning.Attr("enabled", Hcl.Bool(bucket.Flag("versioning"))))
            .With(encrypted =>
            {
                if (OwnKeys.Gcp(model))
                {
                    encrypted.Block("encryption", encryption => encryption.Attr("default_kms_key_name", OwnKeys.GcpKey(OwnKeys.BucketRing(model))))
                        .Attr("depends_on", "[google_kms_crypto_key_iam_member.storage]");
                }
            });
        if (bucket.P["expireDays"] != "0")
        {
            block.Block("lifecycle_rule", rule => rule
                .Block("condition", condition => condition.Attr("age", bucket.P["expireDays"]))
                .Block("action", action => action.Str("type", "Delete")));
        }

        if (bucket.Flag("versioning"))
        {
            block.Block("lifecycle_rule", rule => rule
                .Block("condition", condition => condition.Attr("days_since_noncurrent_time", "90"))
                .Block("action", action => action.Str("type", "Delete")));
        }

        if (website)
        {
            block.Block("website", site => site.Str("main_page_suffix", "index.html").Str("not_found_page", "404.html"));
        }

        yield return block;
        if (website)
        {
            yield return new HclBlock("resource", "google_storage_bucket_iam_member", $"{label}_public")
                .Attr("bucket", $"google_storage_bucket.{label}.name")
                .Str("role", "roles/storage.objectViewer")
                .Str("member", "allUsers");
        }

        foreach (var app in model.Links.Where(link => link.To == bucket && CloudKinds.GcpCompute.Contains(link.From.Kind)).Select(link => link.From))
        {
            yield return new HclBlock("resource", "google_storage_bucket_iam_member", $"{label}_{app.Label}")
                .Attr("bucket", $"google_storage_bucket.{label}.name")
                .Str("role", "roles/storage.objectUser")
                .Attr("member", Member(app));
        }
    }

    private static IEnumerable<HclItem> Sql(SketchModel model, SketchResource database)
    {
        var label = database.Label;
        var days = database.P["backupDays"];
        yield return new HclComment($"Cloud SQL {database.Name}: apps sign in with their service account through the Cloud SQL connector; no passwords and no open network.");
        HclItem instance = new HclBlock("resource", "google_sql_database_instance", label)
            .Attr("name", Hcl.Str($"{Name(database)}-${{local.suffix}}"))
            .Str("database_version", database.P["version"])
            .Attr("region", "var.region")
            .Attr("deletion_protection", Hcl.Bool(model.IsProduction))
            .With(instance =>
            {
                if (OwnKeys.Gcp(model))
                {
                    instance.Attr("encryption_key_name", OwnKeys.GcpKey("main"));
                }
            })
            .Block("settings", settings => settings
                .Str("tier", database.P["tier"])
                .Str("edition", "ENTERPRISE")
                .Str("availability_type", database.Flag("highAvailability") ? "REGIONAL" : "ZONAL")
                .Attr("disk_autoresize", "true")
                .Attr("deletion_protection_enabled", Hcl.Bool(model.IsProduction))
                .Block("backup_configuration", backup => backup
                    .Attr("enabled", "true")
                    .Str("location", Geo.On(model) ? Geo.GcpMultiRegion(model).ToLowerInvariant() : model.Settings.Region)
                    .Attr("point_in_time_recovery_enabled", "true")
                    .Attr("transaction_log_retention_days", Math.Min(7, int.Parse(days, System.Globalization.CultureInfo.InvariantCulture)).ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Block("backup_retention_settings", retention => retention.Attr("retained_backups", days)))
                .Block("ip_configuration", ip =>
                {
                    if (NeedsNetwork(model))
                    {
                        ip.Comment("Only a private address in the VPC.")
                            .Attr("ipv4_enabled", "false")
                            .Attr("private_network", "google_compute_network.main.id")
                            .Attr("enable_private_path_for_google_cloud_services", "true");
                    }
                    else
                    {
                        ip.Comment("A public address that only the Cloud SQL connector, with a signed-in account, can use.")
                            .Attr("ipv4_enabled", "true");
                    }

                    ip.Str("ssl_mode", "ENCRYPTED_ONLY");
                })
                .Block("database_flags", flag => flag.Str("name", "cloudsql.iam_authentication").Str("value", "on")));
        var waits = new[] { NeedsNetwork(model) ? "google_service_networking_connection.main" : null, OwnKeys.Gcp(model) ? "google_kms_crypto_key_iam_member.sql" : null }.OfType<string>().ToList();
        if (waits.Count > 0)
        {
            ((HclBlock)instance).Attr("depends_on", Hcl.List(waits));
        }

        yield return instance;
        if (Geo.On(model))
        {
            yield return new HclComment($"A read replica of {database.Name} in {Geo.Second(model)}: it can be promoted if the region goes down.");
            yield return new HclBlock("resource", "google_sql_database_instance", $"{label}_copy")
                .Attr("name", Hcl.Str($"{Name(database)}-copy-${{local.suffix}}"))
                .Str("database_version", database.P["version"])
                .Str("region", Geo.Second(model))
                .Attr("master_instance_name", $"google_sql_database_instance.{label}.name")
                .With(copy =>
                {
                    if (OwnKeys.Gcp(model))
                    {
                        copy.Attr("encryption_key_name", OwnKeys.GcpKey("second_region")).Attr("depends_on", "[google_kms_crypto_key_iam_member.sql_second]");
                    }
                })
                .Attr("deletion_protection", Hcl.Bool(model.IsProduction))
                .Block("replica_configuration", replica => replica.Attr("failover_target", "false"))
                .Block("settings", settings => settings
                    .Str("tier", database.P["tier"])
                    .Str("edition", "ENTERPRISE")
                    .Str("availability_type", "ZONAL")
                    .Attr("disk_autoresize", "true")
                    .Block("ip_configuration", ip =>
                    {
                        if (NeedsNetwork(model))
                        {
                            ip.Attr("ipv4_enabled", "false").Attr("private_network", "google_compute_network.main.id");
                        }
                        else
                        {
                            ip.Attr("ipv4_enabled", "true");
                        }

                        ip.Str("ssl_mode", "ENCRYPTED_ONLY");
                    })
                    .Block("database_flags", flag => flag.Str("name", "cloudsql.iam_authentication").Str("value", "on")));
        }

        yield return new HclBlock("resource", "google_sql_database", label)
            .Str("name", database.Label)
            .Attr("instance", $"google_sql_database_instance.{label}.name");
        foreach (var app in model.Links.Where(link => link.To == database).Select(link => link.From))
        {
            yield return new HclComment($"{app.Name} signs in to {database.Name} as its service account.");
            yield return new HclBlock("resource", "google_sql_user", $"{label}_{app.Label}")
                .Attr("name", $"trimsuffix({Account(app)}.email, \".gserviceaccount.com\")")
                .Attr("instance", $"google_sql_database_instance.{label}.name")
                .Str("type", "CLOUD_IAM_SERVICE_ACCOUNT");
            yield return new HclBlock("resource", "google_project_iam_member", $"{label}_{app.Label}")
                .Attr("for_each", "toset([\"roles/cloudsql.client\", \"roles/cloudsql.instanceUser\"])")
                .Attr("project", "var.project_id")
                .Attr("role", "each.value")
                .Attr("member", Member(app));
        }
    }

    private static IEnumerable<HclItem> Firestore(SketchModel model, SketchResource database)
    {
        var production = model.IsProduction;
        yield return new HclComment($"Firestore database {database.Name}.");
        yield return new HclBlock("resource", "google_firestore_database", database.Label)
            .Attr("name", Hcl.Str(Name(database)))
            .Attr("location_id", Geo.On(model) && Geo.FirestoreMultiRegion(model) is { } multi ? Hcl.Str(multi) : "var.region")
            .Str("type", "FIRESTORE_NATIVE")
            .Str("point_in_time_recovery_enablement", database.Flag("recovery") ? "POINT_IN_TIME_RECOVERY_ENABLED" : "POINT_IN_TIME_RECOVERY_DISABLED")
            .Str("delete_protection_state", production ? "DELETE_PROTECTION_ENABLED" : "DELETE_PROTECTION_DISABLED")
            .Str("deletion_policy", production ? "ABANDON" : "DELETE");
        foreach (var app in model.Links.Where(link => link.To == database).Select(link => link.From))
        {
            yield return new HclBlock("resource", "google_project_iam_member", $"{database.Label}_{app.Label}")
                .Attr("project", "var.project_id")
                .Str("role", "roles/datastore.user")
                .Attr("member", Member(app))
                .Block("condition", condition => condition
                    .Str("title", $"Only {database.Name}")
                    .Attr("expression", $"\"resource.name.startsWith(\\\"${{google_firestore_database.{database.Label}.id}}\\\")\""));
        }
    }

    private static void TopicKey(SketchModel model, HclBlock topic)
    {
        if (OwnKeys.Gcp(model))
        {
            topic.Attr("kms_key_name", OwnKeys.GcpKey("main")).Attr("depends_on", "[google_kms_crypto_key_iam_member.pubsub]");
        }
    }

    private static IEnumerable<HclItem> Topic(SketchModel model, SketchResource topic)
    {
        var label = topic.Label;
        var deadLetter = topic.Flag("deadLetter");
        var agent = "\"serviceAccount:service-${data.google_project.current.number}@gcp-sa-pubsub.iam.gserviceaccount.com\"";
        var apps = model.Links.Where(link => link.To == topic).Select(link => link.From).ToList();
        yield return new HclComment($"Pub/Sub topic {topic.Name} and its subscriptions.");
        yield return new HclBlock("resource", "google_pubsub_topic", label).Attr("name", Hcl.Str(Name(topic))).With(main => TopicKey(model, main));
        if (deadLetter)
        {
            yield return new HclComment("Messages that failed 5 times, kept 7 days in their own subscription.");
            yield return new HclBlock("resource", "google_pubsub_topic", $"{label}_failed").Attr("name", Hcl.Str($"{Name(topic)}-failed")).With(failed => TopicKey(model, failed));
            yield return new HclBlock("resource", "google_pubsub_subscription", $"{label}_failed")
                .Attr("name", Hcl.Str($"{Name(topic)}-failed"))
                .Attr("topic", $"google_pubsub_topic.{label}_failed.id")
                .Str("message_retention_duration", "604800s")
                .Block("expiration_policy", expiration => expiration.Str("ttl", ""));
            yield return new HclBlock("resource", "google_pubsub_topic_iam_member", $"{label}_failed")
                .Attr("topic", $"google_pubsub_topic.{label}_failed.id")
                .Str("role", "roles/pubsub.publisher")
                .Attr("member", agent);
        }

        foreach (var app in apps)
        {
            yield return new HclBlock("resource", "google_pubsub_topic_iam_member", $"{label}_{app.Label}")
                .Attr("topic", $"google_pubsub_topic.{label}.id")
                .Str("role", "roles/pubsub.publisher")
                .Attr("member", Member(app));
        }

        foreach (var subscription in topic.List("subscriptions"))
        {
            var subscriptionLabel = $"{label}_{subscription.Replace('-', '_')}";
            var block = new HclBlock("resource", "google_pubsub_subscription", subscriptionLabel)
                .Attr("name", Hcl.Str($"{Name(topic)}-{subscription}"))
                .Attr("topic", $"google_pubsub_topic.{label}.id")
                .Attr("ack_deadline_seconds", "60")
                .Str("message_retention_duration", "604800s")
                .Block("expiration_policy", expiration => expiration.Str("ttl", ""))
                .Block("retry_policy", retry => retry.Str("minimum_backoff", "10s").Str("maximum_backoff", "600s"));
            if (deadLetter)
            {
                block.Block("dead_letter_policy", dead => dead
                    .Attr("dead_letter_topic", $"google_pubsub_topic.{label}_failed.id")
                    .Attr("max_delivery_attempts", "5"));
            }

            yield return block;
            if (deadLetter)
            {
                yield return new HclBlock("resource", "google_pubsub_subscription_iam_member", $"{subscriptionLabel}_failed")
                    .Attr("subscription", $"google_pubsub_subscription.{subscriptionLabel}.id")
                    .Str("role", "roles/pubsub.subscriber")
                    .Attr("member", agent);
            }

            foreach (var app in apps)
            {
                yield return new HclBlock("resource", "google_pubsub_subscription_iam_member", $"{subscriptionLabel}_{app.Label}")
                    .Attr("subscription", $"google_pubsub_subscription.{subscriptionLabel}.id")
                    .Str("role", "roles/pubsub.subscriber")
                    .Attr("member", Member(app));
            }
        }
    }

    private static IEnumerable<HclItem> LoadBalancer(SketchModel model, SketchResource balancer)
    {
        var label = balancer.Label;
        var origins = model.Links.Where(link => link.From == balancer).Select(link => link.To).ToList();
        yield return new HclComment($"HTTPS load balancer {balancer.Name} for {balancer.P["domain"]}: the certificate is issued once the domain points to its address.");
        yield return new HclBlock("resource", "google_compute_global_address", label).Attr("name", Hcl.Str(Name(balancer)));
        yield return new HclBlock("resource", "google_compute_managed_ssl_certificate", label)
            .Attr("name", Hcl.Str(Name(balancer)))
            .Block("managed", managed => managed.Attr("domains", Hcl.List([Hcl.Str(balancer.P["domain"])])));

        var armor = Firewall.On(balancer);
        if (armor)
        {
            var preview = Hcl.Bool(!Firewall.Blocks(model, balancer));
            yield return new HclComment($"Cloud Armor of {balancer.Name}: " + (Firewall.Blocks(model, balancer) ? "blocks" : "only logs (preview)") +
                $" SQL injection, scripts, file and command injection, scanners, and more than {Firewall.Rate(balancer)} requests a minute from one address.");
            yield return new HclBlock("resource", "google_compute_security_policy", label)
                .Attr("name", Hcl.Str(Name(balancer)))
                .Str("type", "CLOUD_ARMOR")
                .Block("rule", rule => rule
                    .Str("description", "Common attacks (Google's preconfigured rules)")
                    .Str("action", "deny(403)")
                    .Attr("priority", "1000")
                    .Attr("preview", preview)
                    .Block("match", match => match.Block("expr", expr => expr.Str("expression", string.Join(" || ",
                        new[] { "sqli", "xss", "lfi", "rce", "scannerdetection", "protocolattack" }.Select(set => $"evaluatePreconfiguredWaf('{set}-v33-stable')"))))))
                .Block("rule", rule => rule
                    .Str("description", "Request limit per address")
                    .Str("action", "throttle")
                    .Attr("priority", "2000")
                    .Attr("preview", preview)
                    .Block("match", match => match.Str("versioned_expr", "SRC_IPS_V1").Block("config", config => config.Attr("src_ip_ranges", "[\"*\"]")))
                    .Block("rate_limit_options", limit => limit
                        .Str("conform_action", "allow")
                        .Str("exceed_action", "deny(429)")
                        .Str("enforce_on_key", "IP")
                        .Block("rate_limit_threshold", threshold => threshold.Attr("count", Firewall.Rate(balancer)).Attr("interval_sec", "60"))))
                .Block("rule", rule => rule
                    .Str("description", "Everything else")
                    .Str("action", "allow")
                    .Attr("priority", "2147483647")
                    .Block("match", match => match.Str("versioned_expr", "SRC_IPS_V1").Block("config", config => config.Attr("src_ip_ranges", "[\"*\"]"))));
        }

        var backends = new Dictionary<SketchResource, string>();
        foreach (var origin in origins)
        {
            var originLabel = $"{label}_{origin.Label}";
            if (origin.Kind == CloudKinds.Bucket)
            {
                yield return new HclBlock("resource", "google_compute_backend_bucket", originLabel)
                    .Attr("name", Hcl.Str($"{Name(balancer)}-{origin.Name}"))
                    .Attr("bucket_name", $"google_storage_bucket.{origin.Label}.name")
                    .Attr("enable_cdn", "true");
                backends[origin] = $"google_compute_backend_bucket.{originLabel}.id";
                continue;
            }

            yield return new HclBlock("resource", "google_compute_region_network_endpoint_group", originLabel)
                .Attr("name", Hcl.Str($"{Name(balancer)}-{origin.Name}"))
                .Str("network_endpoint_type", "SERVERLESS")
                .Attr("region", "var.region")
                .Block("cloud_run", run => run.Attr("service", origin.Kind == CloudKinds.CloudRun
                    ? $"google_cloud_run_v2_service.{origin.Label}.name"
                    : $"google_cloudfunctions2_function.{origin.Label}.name"));
            var service = new HclBlock("resource", "google_compute_backend_service", originLabel)
                .Attr("name", Hcl.Str($"{Name(balancer)}-{origin.Name}"))
                .Str("load_balancing_scheme", "EXTERNAL_MANAGED")
                .Str("protocol", "HTTPS");
            if (armor)
            {
                service.Attr("security_policy", $"google_compute_security_policy.{label}.id");
            }

            yield return service.Block("backend", backend => backend.Attr("group", $"google_compute_region_network_endpoint_group.{originLabel}.id"));
            backends[origin] = $"google_compute_backend_service.{originLabel}.id";
        }

        var main = origins.FirstOrDefault(origin => origin.P["path"] == "/*") ?? origins[0];
        var map = new HclBlock("resource", "google_compute_url_map", label)
            .Attr("name", Hcl.Str(Name(balancer)))
            .Attr("default_service", backends[main]);
        if (origins.Count > 1)
        {
            map.Block("host_rule", rule => rule.Attr("hosts", "[\"*\"]").Str("path_matcher", "paths"))
                .Block("path_matcher", matcher =>
                {
                    matcher.Str("name", "paths").Attr("default_service", backends[main]);
                    foreach (var origin in origins.Where(origin => origin != main))
                    {
                        matcher.Block("path_rule", rule => rule.Attr("paths", Hcl.List([Hcl.Str(origin.P["path"])])).Attr("service", backends[origin]));
                    }
                });
        }

        yield return map;
        yield return new HclBlock("resource", "google_compute_target_https_proxy", label)
            .Attr("name", Hcl.Str(Name(balancer)))
            .Attr("url_map", $"google_compute_url_map.{label}.id")
            .Attr("ssl_certificates", $"[google_compute_managed_ssl_certificate.{label}.id]");
        yield return new HclBlock("resource", "google_compute_global_forwarding_rule", label)
            .Attr("name", Hcl.Str(Name(balancer)))
            .Attr("target", $"google_compute_target_https_proxy.{label}.id")
            .Str("port_range", "443")
            .Attr("ip_address", $"google_compute_global_address.{label}.id")
            .Str("load_balancing_scheme", "EXTERNAL_MANAGED");
        yield return new HclComment("HTTP sends visitors to HTTPS.");
        yield return new HclBlock("resource", "google_compute_url_map", $"{label}_http")
            .Attr("name", Hcl.Str($"{Name(balancer)}-http"))
            .Block("default_url_redirect", redirect => redirect.Attr("https_redirect", "true").Attr("strip_query", "false"));
        yield return new HclBlock("resource", "google_compute_target_http_proxy", label)
            .Attr("name", Hcl.Str($"{Name(balancer)}-http"))
            .Attr("url_map", $"google_compute_url_map.{label}_http.id");
        yield return new HclBlock("resource", "google_compute_global_forwarding_rule", $"{label}_http")
            .Attr("name", Hcl.Str($"{Name(balancer)}-http"))
            .Attr("target", $"google_compute_target_http_proxy.{label}.id")
            .Str("port_range", "80")
            .Attr("ip_address", $"google_compute_global_address.{label}.id")
            .Str("load_balancing_scheme", "EXTERNAL_MANAGED");
    }

    public static List<HclItem> Outputs(SketchModel model)
    {
        var items = new List<HclItem>();
        void Output(string name, string description, string value) =>
            items.Add(new HclBlock("output", name).Str("description", description).Attr("value", value));

        foreach (var service in model.OfKind(CloudKinds.CloudRun))
        {
            Output($"{service.Label}_url", $"The address of {service.Name}.", $"google_cloud_run_v2_service.{service.Label}.uri");
            Output($"{service.Label}_service", $"The Cloud Run service {service.Name}.", $"google_cloud_run_v2_service.{service.Label}.name");
            if (model.Targets(service, CloudKinds.ArtifactRegistry).FirstOrDefault() is { } registry)
            {
                Output($"{service.Label}_image", $"The image the app pipeline builds for {service.Name}.",
                    $"\"${{var.region}}-docker.pkg.dev/${{var.project_id}}/${{google_artifact_registry_repository.{registry.Label}.repository_id}}/{service.Name}:latest\"");
            }
        }

        foreach (var function in model.OfKind(CloudKinds.CloudFunction))
        {
            Output($"{function.Label}_url", $"The address of {function.Name}.", $"google_cloudfunctions2_function.{function.Label}.url");
        }

        foreach (var bucket in model.OfKind(CloudKinds.Bucket))
        {
            Output($"{bucket.Label}_bucket", $"The name of the bucket {bucket.Name}.", $"google_storage_bucket.{bucket.Label}.name");
        }

        foreach (var database in model.OfKind(CloudKinds.CloudSql))
        {
            Output($"{database.Label}_connection_name", $"The connection name of {database.Name} for the Cloud SQL connector.", $"google_sql_database_instance.{database.Label}.connection_name");
        }

        foreach (var balancer in model.OfKind(CloudKinds.LoadBalancer))
        {
            Output($"{balancer.Label}_address", $"Point {balancer.P["domain"]} to this address.", $"google_compute_global_address.{balancer.Label}.address");
        }

        foreach (var zone in model.OfKind(CloudKinds.CloudDns))
        {
            Output($"{zone.Label}_name_servers", $"Give these name servers to the registrar of {zone.P["zone"]}.", $"google_dns_managed_zone.{zone.Label}.name_servers");
        }

        items.AddRange(AiOutputs(model));
        return items;
    }

    // A starter for each function, so the first deploy works; replace it with your code.
    public static Dictionary<string, string> CodeFiles(SketchModel model)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var function in model.OfKind(CloudKinds.CloudFunction))
        {
            var folder = $"functions/{function.Name}";
            switch (function.P["runtime"])
            {
                case "python312":
                    files[$"{folder}/main.py"] = $$"""
                        import os

                        import functions_framework


                        @functions_framework.http
                        def handler(request):
                            # The settings Terraform Generator added are environment variables, for example os.environ.get("FILES_BUCKET").
                            return {"message": "Hello from {{function.Name}}"}

                        """;
                    files[$"{folder}/requirements.txt"] = "functions-framework==3.*\n";
                    break;
                case "nodejs22":
                    files[$"{folder}/index.js"] = $$"""
                        const functions = require('@google-cloud/functions-framework');

                        // The settings Terraform Generator added are environment variables, for example process.env.FILES_BUCKET.
                        functions.http('handler', (request, response) => {
                          response.json({ message: 'Hello from {{function.Name}}' });
                        });

                        """;
                    files[$"{folder}/package.json"] = $$"""
                        {
                          "name": "{{function.Name}}",
                          "main": "index.js",
                          "dependencies": {
                            "@google-cloud/functions-framework": "^3.4.0"
                          }
                        }

                        """;
                    break;
                default:
                    files[$"{folder}/README.md"] = $"""
                        # {function.Name}

                        Put the source of your {function.P["runtime"]} function here. Its entry point is `{EntryPoint(function)}`.
                        Terraform zips this folder on every deploy and Cloud Build builds it.

                        """;
                    break;
            }
        }

        return files;
    }
}
