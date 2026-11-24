namespace TerraformGenerator.Domain;

// The Google Cloud AI boxes: Gemini on Vertex AI, Vector Search, Vertex AI Search, Document AI, the AI APIs that are
// only called, Dialogflow CX and Vertex AI Workbench.
public static partial class GcpGenerator
{
    // Document AI keeps processors in the US or the EU.
    public static string DocumentLocation(SketchModel model) => model.Settings.Region.StartsWith("europe-", StringComparison.Ordinal) ? "eu" : "us";

    public static IEnumerable<string> AiServices(SketchResource resource) => resource.Kind switch
    {
        CloudKinds.VertexAi or CloudKinds.GcpVectors => ["aiplatform.googleapis.com"],
        CloudKinds.AiSearch => ["discoveryengine.googleapis.com"],
        CloudKinds.DocumentAi => ["documentai.googleapis.com"],
        CloudKinds.GcpAiApi => [CloudKinds.GcpApis[resource.P["service"]].Api],
        CloudKinds.Dialogflow => ["dialogflow.googleapis.com"],
        CloudKinds.Workbench => ["notebooks.googleapis.com", "aiplatform.googleapis.com", "compute.googleapis.com"],
        _ => [],
    };

    private static IEnumerable<HclItem> AiResources(SketchModel model)
    {
        foreach (var index in model.OfKind(CloudKinds.GcpVectors))
        {
            var label = index.Label;
            yield return new HclComment($"Vector Search {index.Name}: an index you add vectors to (streaming updates), served by an endpoint that is always on.");
            yield return new HclBlock("resource", "google_vertex_ai_index", label)
                .Attr("display_name", Hcl.Str(Name(index)))
                .Attr("region", "var.region")
                .Str("index_update_method", "STREAM_UPDATE")
                .Block("metadata", metadata => metadata.Block("config", config => config
                    .Attr("dimensions", index.P["dimensions"])
                    .Attr("approximate_neighbors_count", "150")
                    .Str("distance_measure_type", "DOT_PRODUCT_DISTANCE")
                    .Block("algorithm_config", algorithm => algorithm.Block("tree_ah_config", _ => { }))));
            yield return new HclBlock("resource", "google_vertex_ai_index_endpoint", label)
                .Attr("display_name", Hcl.Str(Name(index)))
                .Attr("region", "var.region")
                .Attr("public_endpoint_enabled", "true");
            yield return new HclBlock("resource", "google_vertex_ai_index_endpoint_deployed_index", label)
                .Attr("index_endpoint", $"google_vertex_ai_index_endpoint.{label}.id")
                .Attr("index", $"google_vertex_ai_index.{label}.id")
                .Str("deployed_index_id", label)
                .Block("dedicated_resources", resources => resources
                    .Attr("min_replica_count", "1")
                    .Attr("max_replica_count", model.IsProduction ? "2" : "1")
                    .Block("machine_spec", machine => machine.Str("machine_type", "e2-standard-2")));
        }

        foreach (var search in model.OfKind(CloudKinds.AiSearch))
        {
            var label = search.Label;
            yield return new HclComment($"Vertex AI Search {search.Name}: import your documents into its data store in the console.");
            yield return new HclBlock("resource", "google_discovery_engine_data_store", label)
                .Str("location", "global")
                .Attr("data_store_id", Hcl.Str($"{search.Name}-${{local.suffix}}"))
                .Attr("display_name", Hcl.Str(Name(search)))
                .Str("industry_vertical", "GENERIC")
                .Str("content_config", "CONTENT_REQUIRED")
                .Attr("solution_types", "[\"SOLUTION_TYPE_SEARCH\"]");
            yield return new HclBlock("resource", "google_discovery_engine_search_engine", label)
                .Str("location", "global")
                .Str("collection_id", "default_collection")
                .Attr("engine_id", Hcl.Str($"{search.Name}-${{local.suffix}}"))
                .Attr("display_name", Hcl.Str(Name(search)))
                .Attr("data_store_ids", $"[google_discovery_engine_data_store.{label}.data_store_id]")
                .Block("search_engine_config", config => config
                    .Str("search_tier", "SEARCH_TIER_ENTERPRISE")
                    .Attr("search_add_ons", "[\"SEARCH_ADD_ON_LLM\"]"));
        }

        foreach (var processor in model.OfKind(CloudKinds.DocumentAi))
        {
            yield return new HclComment($"Document AI {processor.Name}, in {DocumentLocation(model).ToUpperInvariant()}.");
            yield return new HclBlock("resource", "google_document_ai_processor", processor.Label)
                .Str("location", DocumentLocation(model))
                .Attr("display_name", Hcl.Str(Name(processor)))
                .Str("type", processor.P["type"]);
        }

        foreach (var agent in model.OfKind(CloudKinds.Dialogflow))
        {
            yield return new HclComment($"Dialogflow CX agent {agent.Name}: design its flows in the Dialogflow CX console.");
            yield return new HclBlock("resource", "google_dialogflow_cx_agent", agent.Label)
                .Attr("display_name", Hcl.Str(Name(agent)))
                .Str("location", "global")
                .Str("default_language_code", agent.P["language"])
                .Str("time_zone", agent.P["timeZone"]);
        }

        foreach (var notebook in model.OfKind(CloudKinds.Workbench))
        {
            var label = notebook.Label;
            yield return new HclComment($"Workbench {notebook.Name}: a JupyterLab machine that stops after an idle hour; it works as its own service account.");
            yield return new HclBlock("resource", "google_service_account", $"{label}_notebook")
                .Attr("account_id", Hcl.Str($"{notebook.Name}-${{local.suffix}}"))
                .Attr("display_name", Hcl.Str($"{Name(notebook)} notebook"));
            yield return new HclBlock("resource", "google_project_iam_member", $"{label}_notebook")
                .Attr("project", "var.project_id")
                .Str("role", "roles/aiplatform.user")
                .Attr("member", $"\"serviceAccount:${{google_service_account.{label}_notebook.email}}\"");
            yield return new HclBlock("resource", "google_workbench_instance", label)
                .Attr("name", Hcl.Str(Name(notebook)))
                .Attr("location", "\"${var.region}-a\"")
                .Block("gce_setup", setup => setup
                    .Str("machine_type", notebook.P["machine"])
                    .Map("metadata", [new("idle-timeout-seconds", Hcl.Str("3600"))])
                    .Block("service_accounts", account => account.Attr("email", $"google_service_account.{label}_notebook.email")));
            foreach (var bucket in model.Targets(notebook, CloudKinds.Bucket))
            {
                yield return new HclBlock("resource", "google_storage_bucket_iam_member", $"{bucket.Label}_{label}")
                    .Attr("bucket", $"google_storage_bucket.{bucket.Label}.name")
                    .Str("role", "roles/storage.objectUser")
                    .Attr("member", $"\"serviceAccount:${{google_service_account.{label}_notebook.email}}\"");
            }
        }

        // What code may do with the AI boxes its arrows point to.
        foreach (var (app, target) in model.Links.Where(link => CloudKinds.GcpCompute.Contains(link.From.Kind) && CloudKinds.GcpAi.Contains(link.To.Kind)))
        {
            var roles = target.Kind switch
            {
                CloudKinds.VertexAi or CloudKinds.GcpVectors => ["roles/aiplatform.user"],
                CloudKinds.AiSearch => ["roles/discoveryengine.user"],
                CloudKinds.DocumentAi => ["roles/documentai.apiUser"],
                CloudKinds.Dialogflow => ["roles/dialogflow.client"],
                _ => new[] { "roles/serviceusage.serviceUsageConsumer", CloudKinds.GcpApis[target.P["service"]].Role }.OfType<string>().ToArray(),
            };
            foreach (var role in roles)
            {
                yield return new HclBlock("resource", "google_project_iam_member", $"{app.Label}_{target.Label}_{role[(role.LastIndexOf('.') + 1)..].ToLowerInvariant()}")
                    .Attr("project", "var.project_id")
                    .Str("role", role)
                    .Attr("member", Member(app));
            }
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> AiSettings(SketchModel model, SketchResource target)
    {
        var label = target.Label;
        var upper = target.Upper;
        switch (target.Kind)
        {
            case CloudKinds.VertexAi:
                yield return new($"VERTEX_{upper}_MODEL", Hcl.Str(target.P["model"]));
                yield return new($"VERTEX_{upper}_LOCATION", "var.region");
                break;
            case CloudKinds.GcpVectors:
                yield return new($"VECTOR_{upper}_INDEX_ENDPOINT", $"google_vertex_ai_index_endpoint.{label}.id");
                yield return new($"VECTOR_{upper}_DEPLOYED_INDEX", $"google_vertex_ai_index_endpoint_deployed_index.{label}.deployed_index_id");
                yield return new($"VECTOR_{upper}_INDEX", $"google_vertex_ai_index.{label}.id");
                break;
            case CloudKinds.AiSearch:
                yield return new($"SEARCH_{upper}_ENGINE", $"google_discovery_engine_search_engine.{label}.name");
                break;
            case CloudKinds.DocumentAi:
                yield return new($"DOCUMENT_AI_{upper}_PROCESSOR", $"google_document_ai_processor.{label}.id");
                break;
            case CloudKinds.Dialogflow:
                yield return new($"DIALOGFLOW_{upper}_AGENT", $"google_dialogflow_cx_agent.{label}.id");
                break;
        }
    }

    private static IEnumerable<HclItem> AiOutputs(SketchModel model)
    {
        HclItem Output(string name, string description, string value) => new HclBlock("output", name).Str("description", description).Attr("value", value);

        foreach (var processor in model.OfKind(CloudKinds.DocumentAi))
        {
            yield return Output($"{processor.Label}_processor", $"The Document AI processor {processor.Name}.", $"google_document_ai_processor.{processor.Label}.id");
        }

        foreach (var agent in model.OfKind(CloudKinds.Dialogflow))
        {
            yield return Output($"{agent.Label}_agent", $"The Dialogflow CX agent {agent.Name}.", $"google_dialogflow_cx_agent.{agent.Label}.id");
        }

        foreach (var notebook in model.OfKind(CloudKinds.Workbench))
        {
            yield return Output($"{notebook.Label}_jupyterlab", $"JupyterLab of {notebook.Name}.", $"google_workbench_instance.{notebook.Label}.proxy_uri");
        }

        foreach (var index in model.OfKind(CloudKinds.GcpVectors))
        {
            yield return Output($"{index.Label}_index_endpoint", $"The Vector Search endpoint of {index.Name}.", $"google_vertex_ai_index_endpoint.{index.Label}.public_endpoint_domain_name");
        }
    }
}
