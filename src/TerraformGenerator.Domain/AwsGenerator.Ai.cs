namespace TerraformGenerator.Domain;

// The AWS AI boxes: Bedrock (models, safety filters, a knowledge base over an S3 bucket, an agent), SageMaker,
// the AI services that are only called, Lex, Kendra and OpenSearch Serverless for vectors.
public static partial class AwsGenerator
{
    private const string EmbeddingModel = "amazon.titan-embed-text-v2:0";

    private static readonly string[] ContentFilters = ["HATE", "INSULTS", "SEXUAL", "VIOLENCE", "MISCONDUCT"];

    private static string Profile(SketchModel model, SketchResource bedrock) => CloudKinds.BedrockProfile(bedrock.P["model"], model.Settings.Region);

    public static bool HasKnowledgeBase(SketchModel model, SketchResource bedrock) =>
        bedrock.Flag("knowledgeBase") && model.Targets(bedrock, CloudKinds.S3).Any();

    private static HclBlock AddStatement(HclBlock document, string sid, IEnumerable<string> actions, IEnumerable<string> resources) =>
        document.Block("statement", statement => statement
            .Str("sid", sid)
            .Attr("actions", Hcl.List(actions.Select(Hcl.Str)))
            .Attr("resources", Hcl.List(resources)));

    // A role a service takes on, with its permissions written as a policy document.
    private static IEnumerable<HclItem> ServiceRole(SketchResource resource, string suffix, string service, Action<HclBlock> permissions)
    {
        var label = $"{resource.Label}_{suffix}";
        var document = new HclBlock("data", "aws_iam_policy_document", label);
        permissions(document);
        yield return new HclBlock("data", "aws_iam_policy_document", $"{label}_assume")
            .Block("statement", statement => statement
                .Attr("actions", "[\"sts:AssumeRole\"]")
                .Block("principals", principals => principals.Str("type", "Service").Attr("identifiers", Hcl.List([Hcl.Str(service)]))));
        yield return new HclBlock("resource", "aws_iam_role", label)
            .Attr("name", Hcl.Str($"{Name(resource)}-{suffix.Replace('_', '-')}"))
            .Attr("assume_role_policy", $"data.aws_iam_policy_document.{label}_assume.json");
        yield return document;
        yield return new HclBlock("resource", "aws_iam_role_policy", label)
            .Str("name", "access")
            .Attr("role", $"aws_iam_role.{label}.id")
            .Attr("policy", $"data.aws_iam_policy_document.{label}.json");
    }

    private static List<HclItem> AiResources(SketchModel model)
    {
        var items = new List<HclItem>();
        if (model.OfKind(CloudKinds.AwsVectors).Any())
        {
            items.Add(new HclComment("Who is deploying: the vector collections let this role manage their indexes."));
            items.Add(new HclBlock("data", "aws_caller_identity", "current"));
            items.Add(new HclBlock("data", "aws_iam_session_context", "current").Attr("arn", "data.aws_caller_identity.current.arn"));
        }

        foreach (var bedrock in model.OfKind(CloudKinds.Bedrock))
        {
            items.AddRange(Bedrock(model, bedrock));
        }

        foreach (var studio in model.OfKind(CloudKinds.SageMaker))
        {
            items.AddRange(SageMaker(model, studio));
        }

        foreach (var bot in model.OfKind(CloudKinds.Lex))
        {
            items.Add(new HclComment($"Lex bot {bot.Name}: add its intents in the Lex console and build it."));
            items.AddRange(ServiceRole(bot, "lex", "lexv2.amazonaws.com", document =>
                AddStatement(document, "Voice", ["polly:SynthesizeSpeech"], ["\"*\""])));
            items.Add(new HclBlock("resource", "aws_lexv2models_bot", bot.Label)
                .Attr("name", Hcl.Str(Name(bot)))
                .Attr("role_arn", $"aws_iam_role.{bot.Label}_lex.arn")
                .Attr("idle_session_ttl_in_seconds", "300")
                .Block("data_privacy", privacy => privacy.Attr("child_directed", "false")));
            items.Add(new HclBlock("resource", "aws_lexv2models_bot_locale", bot.Label)
                .Attr("bot_id", $"aws_lexv2models_bot.{bot.Label}.id")
                .Str("bot_version", "DRAFT")
                .Str("locale_id", bot.P["locale"])
                .Attr("n_lu_intent_confidence_threshold", "0.7"));
        }

        foreach (var index in model.OfKind(CloudKinds.Kendra))
        {
            items.AddRange(KendraIndex(model, index));
        }

        foreach (var vectors in model.OfKind(CloudKinds.AwsVectors))
        {
            items.AddRange(Vectors(model, vectors));
        }

        return items;
    }

    private static IEnumerable<HclItem> Bedrock(SketchModel model, SketchResource bedrock)
    {
        var label = bedrock.Label;
        var knowledge = HasKnowledgeBase(model, bedrock);
        yield return new HclComment($"Bedrock {bedrock.Name}: {bedrock.P["model"]}, called through the inference profile {Profile(model, bedrock)}.");
        if (bedrock.Flag("guardrail"))
        {
            var guardrail = new HclBlock("resource", "aws_bedrock_guardrail", label)
                .Attr("name", Hcl.Str(Name(bedrock)))
                .Str("blocked_input_messaging", "Sorry, I can't help with that request.")
                .Str("blocked_outputs_messaging", "Sorry, I can't share that answer.")
                .Block("content_policy_config", content =>
                {
                    foreach (var type in ContentFilters)
                    {
                        content.Block("filters_config", filter => filter.Str("type", type).Str("input_strength", "HIGH").Str("output_strength", "HIGH"));
                    }

                    content.Block("filters_config", filter => filter.Str("type", "PROMPT_ATTACK").Str("input_strength", "HIGH").Str("output_strength", "NONE"));
                })
                .Block("sensitive_information_policy_config", sensitive =>
                {
                    foreach (var type in new[] { "EMAIL", "PHONE" })
                    {
                        sensitive.Block("pii_entities_config", pii => pii.Str("type", type).Str("action", "ANONYMIZE"));
                    }
                });
            yield return new HclComment("Safety filters: harmful content and prompt attacks are blocked; emails and phone numbers are hidden.");
            yield return guardrail;
        }

        if (knowledge)
        {
            var bucket = model.Targets(bedrock, CloudKinds.S3).First();
            yield return new HclComment($"The knowledge base reads the files of {bucket.Name} and keeps their vectors in S3 Vectors.");
            yield return new HclBlock("resource", "aws_s3vectors_vector_bucket", label)
                .Attr("vector_bucket_name", Hcl.Str($"{Name(bedrock)}-${{local.suffix}}"))
                .Attr("force_destroy", Hcl.Bool(!model.IsProduction));
            yield return new HclBlock("resource", "aws_s3vectors_index", label)
                .Attr("vector_bucket_name", $"aws_s3vectors_vector_bucket.{label}.vector_bucket_name")
                .Str("index_name", "documents")
                .Str("data_type", "float32")
                .Attr("dimension", "1024")
                .Str("distance_metric", "cosine")
                .Block("metadata_configuration", metadata => metadata.Attr("non_filterable_metadata_keys", "[\"AMAZON_BEDROCK_TEXT\", \"AMAZON_BEDROCK_METADATA\"]"));
            foreach (var item in ServiceRole(bedrock, "knowledge", "bedrock.amazonaws.com", document =>
            {
                AddStatement(document, "Embeddings", ["bedrock:InvokeModel"], [$"\"arn:aws:bedrock:${{var.region}}::foundation-model/{EmbeddingModel}\""]);
                AddStatement(document, "Documents", ["s3:GetObject"], [$"\"${{aws_s3_bucket.{bucket.Label}.arn}}/*\""]);
                AddStatement(document, "DocumentList", ["s3:ListBucket"], [$"aws_s3_bucket.{bucket.Label}.arn"]);
                AddStatement(document, "Vectors", ["s3vectors:GetIndex", "s3vectors:QueryVectors", "s3vectors:PutVectors", "s3vectors:GetVectors", "s3vectors:DeleteVectors"],
                    [$"aws_s3vectors_index.{label}.index_arn"]);
            }))
            {
                yield return item;
            }

            yield return new HclBlock("resource", "aws_bedrockagent_knowledge_base", label)
                .Attr("name", Hcl.Str(Name(bedrock)))
                .Attr("role_arn", $"aws_iam_role.{label}_knowledge.arn")
                .Block("knowledge_base_configuration", configuration => configuration
                    .Str("type", "VECTOR")
                    .Block("vector_knowledge_base_configuration", vector => vector
                        .Attr("embedding_model_arn", Hcl.Str($"arn:aws:bedrock:${{var.region}}::foundation-model/{EmbeddingModel}"))))
                .Block("storage_configuration", storage => storage
                    .Str("type", "S3_VECTORS")
                    .Block("s3_vectors_configuration", vectors => vectors.Attr("index_arn", $"aws_s3vectors_index.{label}.index_arn")))
                .Attr("depends_on", $"[aws_iam_role_policy.{label}_knowledge]");
            yield return new HclBlock("resource", "aws_bedrockagent_data_source", label)
                .Attr("knowledge_base_id", $"aws_bedrockagent_knowledge_base.{label}.id")
                .Str("name", bucket.Name)
                .Block("data_source_configuration", source => source
                    .Str("type", "S3")
                    .Block("s3_configuration", s3 => s3.Attr("bucket_arn", $"aws_s3_bucket.{bucket.Label}.arn")));
        }

        if (bedrock.Flag("agent"))
        {
            yield return new HclComment($"The agent of {bedrock.Name}: your instructions, the model{(knowledge ? ", the knowledge base" : "")}{(bedrock.Flag("guardrail") ? " and the safety filters" : "")}.");
            foreach (var item in ServiceRole(bedrock, "agent", "bedrock.amazonaws.com", document =>
            {
                AddStatement(document, "Models", ["bedrock:InvokeModel", "bedrock:InvokeModelWithResponseStream", "bedrock:GetInferenceProfile"],
                    ["\"arn:aws:bedrock:*::foundation-model/*\"", "\"arn:aws:bedrock:*:*:inference-profile/*\""]);
                if (knowledge)
                {
                    AddStatement(document, "Knowledge", ["bedrock:Retrieve"], [$"aws_bedrockagent_knowledge_base.{label}.arn"]);
                }

                if (bedrock.Flag("guardrail"))
                {
                    AddStatement(document, "Filters", ["bedrock:ApplyGuardrail"], [$"aws_bedrock_guardrail.{label}.guardrail_arn"]);
                }
            }))
            {
                yield return item;
            }

            var agent = new HclBlock("resource", "aws_bedrockagent_agent", label)
                .Attr("agent_name", Hcl.Str(Name(bedrock)))
                .Attr("agent_resource_role_arn", $"aws_iam_role.{label}_agent.arn")
                .Str("foundation_model", Profile(model, bedrock))
                .Attr("instruction", Hcl.Literal(bedrock.P["instructions"]))
                .Attr("idle_session_ttl_in_seconds", "600")
                .Attr("prepare_agent", "true");
            if (bedrock.Flag("guardrail"))
            {
                agent.Attr("guardrail_configuration",
                    $"[{{ guardrail_identifier = aws_bedrock_guardrail.{label}.guardrail_id, guardrail_version = aws_bedrock_guardrail.{label}.version }}]");
            }

            yield return agent.Attr("depends_on", $"[aws_iam_role_policy.{label}_agent]");
            var alias = new HclBlock("resource", "aws_bedrockagent_agent_alias", label)
                .Str("agent_alias_name", "live")
                .Attr("agent_id", $"aws_bedrockagent_agent.{label}.agent_id");
            if (knowledge)
            {
                yield return new HclBlock("resource", "aws_bedrockagent_agent_knowledge_base_association", label)
                    .Attr("agent_id", $"aws_bedrockagent_agent.{label}.agent_id")
                    .Attr("knowledge_base_id", $"aws_bedrockagent_knowledge_base.{label}.id")
                    .Str("description", "The company's documents.")
                    .Str("knowledge_base_state", "ENABLED");
                alias.Attr("depends_on", $"[aws_bedrockagent_agent_knowledge_base_association.{label}]");
            }

            yield return alias;
        }
    }

    private static IEnumerable<HclItem> SageMaker(SketchModel model, SketchResource studio)
    {
        var label = studio.Label;
        var buckets = model.Targets(studio, CloudKinds.S3).ToList();
        yield return new HclComment($"SageMaker {studio.Name}: a Studio domain in the default VPC; it costs only while notebooks or training jobs run.");
        foreach (var item in ServiceRole(studio, "studio", "sagemaker.amazonaws.com", document =>
        {
            AddStatement(document, "Logs", ["logs:CreateLogGroup", "logs:CreateLogStream", "logs:PutLogEvents"], ["\"*\""]);
            foreach (var bucket in buckets)
            {
                AddStatement(document, $"{bucket.Compact}files", ["s3:GetObject", "s3:PutObject", "s3:DeleteObject"], [$"\"${{aws_s3_bucket.{bucket.Label}.arn}}/*\""]);
                AddStatement(document, $"{bucket.Compact}list", ["s3:ListBucket"], [$"aws_s3_bucket.{bucket.Label}.arn"]);
            }
        }))
        {
            yield return item;
        }

        yield return new HclBlock("resource", "aws_iam_role_policy_attachment", $"{label}_studio")
            .Attr("role", $"aws_iam_role.{label}_studio.name")
            .Str("policy_arn", "arn:aws:iam::aws:policy/AmazonSageMakerFullAccess");
        yield return new HclBlock("resource", "aws_sagemaker_domain", label)
            .Attr("domain_name", Hcl.Str(Name(studio)))
            .Str("auth_mode", "IAM")
            .Attr("vpc_id", VpcId(model))
            .Attr("subnet_ids", PrivateSubnets(model))
            .Str("app_network_access_type", "PublicInternetOnly")
            .Block("default_user_settings", settings => settings.Attr("execution_role", $"aws_iam_role.{label}_studio.arn"))
            .Block("retention_policy", retention => retention.Str("home_efs_file_system", model.IsProduction ? "Retain" : "Delete"));
        yield return new HclBlock("resource", "aws_sagemaker_user_profile", label)
            .Attr("domain_id", $"aws_sagemaker_domain.{label}.id")
            .Str("user_profile_name", "owner");
    }

    private static IEnumerable<HclItem> KendraIndex(SketchModel model, SketchResource index)
    {
        var label = index.Label;
        var buckets = model.Targets(index, CloudKinds.S3).ToList();
        yield return new HclComment($"Kendra {index.Name} ({index.P["edition"]}); start a sync of each data source after adding documents.");
        foreach (var item in ServiceRole(index, "kendra", "kendra.amazonaws.com", document =>
        {
            AddStatement(document, "Metrics", ["cloudwatch:PutMetricData"], ["\"*\""]);
            AddStatement(document, "Logs", ["logs:DescribeLogGroups", "logs:CreateLogGroup", "logs:DescribeLogStreams", "logs:CreateLogStream", "logs:PutLogEvents"], ["\"*\""]);
            foreach (var bucket in buckets)
            {
                AddStatement(document, $"{bucket.Compact}files", ["s3:GetObject"], [$"\"${{aws_s3_bucket.{bucket.Label}.arn}}/*\""]);
                AddStatement(document, $"{bucket.Compact}list", ["s3:ListBucket"], [$"aws_s3_bucket.{bucket.Label}.arn"]);
            }

            if (buckets.Count > 0)
            {
                AddStatement(document, "Documents", ["kendra:BatchPutDocument", "kendra:BatchDeleteDocument"], ["\"*\""]);
            }
        }))
        {
            yield return item;
        }

        yield return new HclBlock("resource", "aws_kendra_index", label)
            .Attr("name", Hcl.Str(Name(index)))
            .Str("edition", index.P["edition"])
            .Attr("role_arn", $"aws_iam_role.{label}_kendra.arn")
            .Attr("depends_on", $"[aws_iam_role_policy.{label}_kendra]");
        foreach (var bucket in buckets)
        {
            yield return new HclBlock("resource", "aws_kendra_data_source", $"{label}_{bucket.Label}")
                .Attr("index_id", $"aws_kendra_index.{label}.id")
                .Str("name", bucket.Name)
                .Str("type", "S3")
                .Attr("role_arn", $"aws_iam_role.{label}_kendra.arn")
                .Block("configuration", configuration => configuration
                    .Block("s3_configuration", s3 => s3.Attr("bucket_name", $"aws_s3_bucket.{bucket.Label}.bucket")));
        }
    }

    private static IEnumerable<HclItem> Vectors(SketchModel model, SketchResource vectors)
    {
        var label = vectors.Label;
        var collection = $"{vectors.Name}-${{local.suffix}}";
        var readers = model.Links.Where(link => link.To == vectors).Select(link => $"aws_iam_role.{link.From.Label}.arn");
        yield return new HclComment($"OpenSearch Serverless {vectors.Name} for vector search: encrypted, reached over HTTPS with AWS sign-in only.");
        yield return new HclBlock("resource", "aws_opensearchserverless_security_policy", $"{label}_encryption")
            .Attr("name", Hcl.Str($"{vectors.Name}-enc-${{local.suffix}}"))
            .Str("type", "encryption")
            .Attr("policy", $"jsonencode({{ Rules = [{{ ResourceType = \"collection\", Resource = [\"collection/{collection}\"] }}], AWSOwnedKey = true }})");
        yield return new HclBlock("resource", "aws_opensearchserverless_security_policy", $"{label}_network")
            .Attr("name", Hcl.Str($"{vectors.Name}-net-${{local.suffix}}"))
            .Str("type", "network")
            .Attr("policy", $"jsonencode([{{ Rules = [{{ ResourceType = \"collection\", Resource = [\"collection/{collection}\"] }}], AllowFromPublic = true }}])");
        yield return new HclBlock("resource", "aws_opensearchserverless_collection", label)
            .Attr("name", Hcl.Str(collection))
            .Str("type", "VECTORSEARCH")
            .Str("standby_replicas", model.IsProduction ? "ENABLED" : "DISABLED")
            .Attr("depends_on", $"[aws_opensearchserverless_security_policy.{label}_encryption, aws_opensearchserverless_security_policy.{label}_network]");
        yield return new HclComment("Who may read and write the indexes: you (to create them) and the connected code.");
        yield return new HclBlock("resource", "aws_opensearchserverless_access_policy", label)
            .Attr("name", Hcl.Str($"{vectors.Name}-data-${{local.suffix}}"))
            .Str("type", "data")
            .Attr("policy", "jsonencode([{ Rules = [" +
                $"{{ ResourceType = \"index\", Resource = [\"index/{collection}/*\"], Permission = [\"aoss:*\"] }}, " +
                $"{{ ResourceType = \"collection\", Resource = [\"collection/{collection}\"], Permission = [\"aoss:*\"] }}], " +
                $"Principal = {Hcl.List(["data.aws_iam_session_context.current.issuer_arn", .. readers])} }}])");
    }

    // What code may do with the AI boxes its arrows point to.
    private static IEnumerable<Statement> AiStatements(SketchModel model, SketchResource app)
    {
        foreach (var target in model.Links.Where(link => link.From == app).Select(link => link.To))
        {
            var label = target.Label;
            switch (target.Kind)
            {
                case CloudKinds.Bedrock:
                    yield return new($"{target.Compact}models", ["bedrock:InvokeModel", "bedrock:InvokeModelWithResponseStream"],
                        ["\"arn:aws:bedrock:*::foundation-model/*\"", "\"arn:aws:bedrock:*:*:inference-profile/*\""]);
                    if (target.Flag("guardrail"))
                    {
                        yield return new($"{target.Compact}filters", ["bedrock:ApplyGuardrail"], [$"aws_bedrock_guardrail.{label}.guardrail_arn"]);
                    }

                    if (HasKnowledgeBase(model, target))
                    {
                        yield return new($"{target.Compact}knowledge", ["bedrock:Retrieve", "bedrock:RetrieveAndGenerate"], [$"aws_bedrockagent_knowledge_base.{label}.arn"]);
                    }

                    if (target.Flag("agent"))
                    {
                        yield return new($"{target.Compact}agent", ["bedrock:InvokeAgent"], [$"aws_bedrockagent_agent_alias.{label}.agent_alias_arn"]);
                    }

                    break;
                case CloudKinds.SageMaker:
                    yield return new($"{target.Compact}endpoints", ["sagemaker:InvokeEndpoint"], ["\"arn:aws:sagemaker:*:*:endpoint/*\""]);
                    break;
                case CloudKinds.AwsAiApi:
                    yield return new($"{target.Compact}calls", CloudKinds.AwsApiActions[target.P["service"]], ["\"*\""]);
                    break;
                case CloudKinds.Lex:
                    yield return new($"{target.Compact}bot", ["lex:RecognizeText", "lex:RecognizeUtterance", "lex:GetSession", "lex:PutSession"],
                        [$"\"arn:aws:lex:*:*:bot-alias/${{aws_lexv2models_bot.{label}.id}}/*\""]);
                    break;
                case CloudKinds.Kendra:
                    yield return new($"{target.Compact}search", ["kendra:Query", "kendra:Retrieve", "kendra:DescribeIndex"], [$"aws_kendra_index.{label}.arn"]);
                    break;
                case CloudKinds.AwsVectors:
                    yield return new($"{target.Compact}vectors", ["aoss:APIAccessAll"], [$"aws_opensearchserverless_collection.{label}.arn"]);
                    break;
            }
        }
    }

    private static IEnumerable<KeyValuePair<string, string>> AiSettings(SketchModel model, SketchResource app)
    {
        foreach (var target in model.Links.Where(link => link.From == app).Select(link => link.To))
        {
            var label = target.Label;
            var upper = target.Upper;
            switch (target.Kind)
            {
                case CloudKinds.Bedrock:
                    yield return new($"BEDROCK_{upper}_MODEL", Hcl.Str(Profile(model, target)));
                    if (target.Flag("guardrail"))
                    {
                        yield return new($"BEDROCK_{upper}_GUARDRAIL_ID", $"aws_bedrock_guardrail.{label}.guardrail_id");
                        yield return new($"BEDROCK_{upper}_GUARDRAIL_VERSION", $"aws_bedrock_guardrail.{label}.version");
                    }

                    if (HasKnowledgeBase(model, target))
                    {
                        yield return new($"BEDROCK_{upper}_KNOWLEDGE_BASE_ID", $"aws_bedrockagent_knowledge_base.{label}.id");
                    }

                    if (target.Flag("agent"))
                    {
                        yield return new($"BEDROCK_{upper}_AGENT_ID", $"aws_bedrockagent_agent.{label}.agent_id");
                        yield return new($"BEDROCK_{upper}_AGENT_ALIAS_ID", $"aws_bedrockagent_agent_alias.{label}.agent_alias_id");
                    }

                    break;
                case CloudKinds.SageMaker:
                    yield return new($"SAGEMAKER_{upper}_DOMAIN_ID", $"aws_sagemaker_domain.{label}.id");
                    break;
                case CloudKinds.Lex:
                    yield return new($"LEX_{upper}_BOT_ID", $"aws_lexv2models_bot.{label}.id");
                    yield return new($"LEX_{upper}_ALIAS_ID", Hcl.Str("TSTALIASID"));
                    yield return new($"LEX_{upper}_LOCALE", Hcl.Str(target.P["locale"]));
                    break;
                case CloudKinds.Kendra:
                    yield return new($"KENDRA_{upper}_INDEX_ID", $"aws_kendra_index.{label}.id");
                    break;
                case CloudKinds.AwsVectors:
                    yield return new($"OPENSEARCH_{upper}_ENDPOINT", $"aws_opensearchserverless_collection.{label}.collection_endpoint");
                    break;
            }
        }
    }

    private static IEnumerable<HclItem> AiOutputs(SketchModel model)
    {
        HclItem Output(string name, string description, string value) => new HclBlock("output", name).Str("description", description).Attr("value", value);

        foreach (var bedrock in model.OfKind(CloudKinds.Bedrock))
        {
            if (HasKnowledgeBase(model, bedrock))
            {
                yield return Output($"{bedrock.Label}_knowledge_base_id", $"The knowledge base of {bedrock.Name}: sync it after adding files.", $"aws_bedrockagent_knowledge_base.{bedrock.Label}.id");
            }

            if (bedrock.Flag("agent"))
            {
                yield return Output($"{bedrock.Label}_agent_alias_id", $"The live alias of the agent of {bedrock.Name}.", $"aws_bedrockagent_agent_alias.{bedrock.Label}.agent_alias_id");
            }
        }

        foreach (var studio in model.OfKind(CloudKinds.SageMaker))
        {
            yield return Output($"{studio.Label}_studio_url", $"SageMaker Studio of {studio.Name}.", $"aws_sagemaker_domain.{studio.Label}.url");
        }

        foreach (var bot in model.OfKind(CloudKinds.Lex))
        {
            yield return Output($"{bot.Label}_bot_id", $"The Lex bot {bot.Name}.", $"aws_lexv2models_bot.{bot.Label}.id");
        }

        foreach (var index in model.OfKind(CloudKinds.Kendra))
        {
            yield return Output($"{index.Label}_index_id", $"The Kendra index {index.Name}.", $"aws_kendra_index.{index.Label}.id");
        }

        foreach (var vectors in model.OfKind(CloudKinds.AwsVectors))
        {
            yield return Output($"{vectors.Label}_endpoint", $"The vector collection {vectors.Name}.", $"aws_opensearchserverless_collection.{vectors.Label}.collection_endpoint");
        }
    }
}
