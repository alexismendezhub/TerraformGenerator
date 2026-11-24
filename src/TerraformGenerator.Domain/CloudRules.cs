namespace TerraformGenerator.Domain;

// The checks for the AWS and Google Cloud smart boxes: what the cloud would refuse, and choices that are risky.
public static class CloudRules
{
    public static void Check(SketchModel model, List<SketchMessage> messages)
    {
        messages.AddRange(CloudAlerts.Check(model));
        if (model.GitHub && !model.RemoteState)
        {
            Add(messages, MessageLevel.Error, "Deploying from GitHub needs the Terraform state in the cloud: choose \"In the cloud\" for where Terraform keeps its state.");
        }

        if (model.Platform == Platforms.Aws)
        {
            CheckAws(model, messages);
        }
        else
        {
            CheckGcp(model, messages);
        }
    }

    private static void Add(List<SketchMessage> messages, MessageLevel level, string text, string? nodeId = null) =>
        messages.Add(new(level, text, nodeId));

    private static List<SketchResource> Served(SketchModel model, SketchResource entry) =>
        model.Links.Where(link => link.From == entry).Select(link => link.To).ToList();

    // Each path behind an entry goes to one place.
    private static void CheckPaths(SketchResource entry, List<SketchResource> origins, List<SketchMessage> messages)
    {
        foreach (var group in origins.GroupBy(origin => origin.P["path"]).Where(group => group.Count() > 1))
        {
            Add(messages, MessageLevel.Error, $"{entry.Name}: {string.Join(" and ", group.Select(origin => origin.Name))} have the same path {group.Key}. Give each its own path.", entry.Node.Id);
        }
    }

    private static void CheckAws(SketchModel model, List<SketchMessage> messages)
    {
        var production = model.IsProduction;
        foreach (var distribution in model.OfKind(CloudKinds.CloudFront))
        {
            var origins = Served(model, distribution);
            if (origins.Count == 0)
            {
                Add(messages, MessageLevel.Error, $"{distribution.Name}: draw an arrow from CloudFront to what it serves (an S3 bucket, a Lambda or a container).", distribution.Node.Id);
                continue;
            }

            CheckPaths(distribution, origins, messages);
            foreach (var function in origins.Where(origin => origin.Kind == CloudKinds.Lambda && !origin.Flag("publicUrl")))
            {
                Add(messages, MessageLevel.Error, $"{function.Name}: CloudFront reaches a Lambda through its address; turn on \"Public HTTPS address\".", function.Node.Id);
            }

            foreach (var bucket in origins.Where(origin => origin.Kind == CloudKinds.S3 && origin.P["encryption"] == "aws:kms"))
            {
                Add(messages, MessageLevel.Error, $"{bucket.Name}: CloudFront cannot read files encrypted with the AWS managed KMS key; choose S3 managed keys.", bucket.Node.Id);
            }

            if (model.Sources(distribution, CloudKinds.Route53).Count() > 1)
            {
                Add(messages, MessageLevel.Error, $"{distribution.Name}: only one Route 53 zone can point to a CloudFront.", distribution.Node.Id);
            }

            if (origins.Any(origin => origin.Kind == CloudKinds.Fargate))
            {
                Add(messages, MessageLevel.Info, $"{distribution.Name}: visitors get HTTPS; CloudFront reaches the containers' load balancer over HTTP.", distribution.Node.Id);
            }
        }

        foreach (var zone in model.OfKind(CloudKinds.Route53))
        {
            foreach (var group in model.Targets(zone, CloudKinds.CloudFront).GroupBy(distribution => distribution.P["subdomain"]).Where(group => group.Count() > 1))
            {
                Add(messages, MessageLevel.Error, $"{zone.Name}: {string.Join(" and ", group.Select(item => item.Name))} both use {group.Key}.{zone.P["zone"]}. Give each its own subdomain.", zone.Node.Id);
            }
        }

        foreach (var queue in model.OfKind(CloudKinds.Sqs))
        {
            foreach (var function in model.Targets(queue, CloudKinds.Lambda)
                .Where(function => int.Parse(function.P["timeout"], System.Globalization.CultureInfo.InvariantCulture) > int.Parse(queue.P["visibility"], System.Globalization.CultureInfo.InvariantCulture)))
            {
                Add(messages, MessageLevel.Error, $"{queue.Name} -> {function.Name}: the queue's time to handle a message must be at least the function's time limit.", queue.Node.Id);
            }
        }

        foreach (var function in model.OfKind(CloudKinds.Lambda).Where(function => AwsGenerator.InNetwork(model, function) && model.NatGateways == 0))
        {
            var others = model.Links.Where(link => link.From == function && link.To.Kind is not CloudKinds.Rds and not CloudKinds.Lambda and not CloudKinds.Fargate).Select(link => link.To.Name).ToList();
            Add(messages, others.Count > 0 ? MessageLevel.Warning : MessageLevel.Info,
                $"{function.Name} runs inside the VPC to reach its database, so it has no internet access" +
                (others.Count > 0 ? $": it cannot reach {string.Join(", ", others)} until you add a NAT gateway (Network settings) or VPC endpoints." : "."),
                function.Node.Id);
        }

        foreach (var container in model.OfKind(CloudKinds.Fargate).Where(container => container.Flag("public") && !model.Sources(container, CloudKinds.CloudFront).Any()))
        {
            Add(messages, MessageLevel.Warning, $"{container.Name}: its load balancer answers on HTTP only. Put CloudFront in front of it for HTTPS.", container.Node.Id);
        }

        if (AwsGenerator.NeedsNetwork(model) && !model.AwsOwnNetwork)
        {
            Add(messages, model.IsProduction ? MessageLevel.Warning : MessageLevel.Info,
                "Containers and databases go in the account's default VPC, whose subnets are public; production should use a VPC of its own (Network settings).");
        }

        if (AwsGenerator.NeedsNetwork(model) && model.AwsOwnNetwork && model.NatGateways == 0 && model.OfKind(CloudKinds.Fargate).Any())
        {
            Add(messages, MessageLevel.Warning, "Without a NAT gateway the containers cannot pull public images; use images from ECR with VPC endpoints or turn the NAT on (Network settings).");
        }

        foreach (var database in model.OfKind(CloudKinds.Rds))
        {
            if (!model.Links.Any(link => link.To == database))
            {
                Add(messages, MessageLevel.Warning, $"{database.Name}: nothing can reach this private database. Draw an arrow from a Lambda or a container.", database.Node.Id);
            }

            if (production && !database.Flag("multiAz"))
            {
                Add(messages, MessageLevel.Warning, $"{database.Name}: production without a standby copy stops if its zone fails.", database.Node.Id);
            }

            if (production && int.Parse(database.P["backupDays"], System.Globalization.CultureInfo.InvariantCulture) < 7)
            {
                Add(messages, MessageLevel.Warning, $"{database.Name}: production with less than 7 days of backups.", database.Node.Id);
            }
        }

        foreach (var table in model.OfKind(CloudKinds.DynamoDb).Where(table => production && !table.Flag("recovery")))
        {
            Add(messages, MessageLevel.Warning, $"{table.Name}: production without point-in-time recovery.", table.Node.Id);
        }

        foreach (var bucket in model.OfKind(CloudKinds.S3).Where(bucket => production && !bucket.Flag("versioning")))
        {
            Add(messages, MessageLevel.Warning, $"{bucket.Name}: production without versions; a deleted or overwritten file cannot be brought back.", bucket.Node.Id);
        }

        foreach (var function in model.OfKind(CloudKinds.Lambda).Where(function => function.P["runtime"] is "java21" or "dotnet8"))
        {
            Add(messages, MessageLevel.Info, $"{function.Name}: put your built {function.P["runtime"]} package in functions/{function.Name} before deploying.", function.Node.Id);
        }

        CheckAwsAi(model, messages);
    }

    private static void CheckAwsAi(SketchModel model, List<SketchMessage> messages)
    {
        foreach (var bedrock in model.OfKind(CloudKinds.Bedrock))
        {
            var buckets = model.Targets(bedrock, CloudKinds.S3).Count();
            if (bedrock.Flag("knowledgeBase") && buckets != 1)
            {
                Add(messages, MessageLevel.Error, $"{bedrock.Name}: the knowledge base reads one S3 bucket; draw one arrow from Bedrock to it.", bedrock.Node.Id);
            }
            else if (!bedrock.Flag("knowledgeBase") && buckets > 0)
            {
                Add(messages, MessageLevel.Error, $"{bedrock.Name}: an arrow to a bucket feeds the knowledge base; turn on \"Knowledge base\".", bedrock.Node.Id);
            }

            if (bedrock.P["model"].StartsWith("anthropic.", StringComparison.Ordinal))
            {
                Add(messages, MessageLevel.Info, $"{bedrock.Name}: the first time, send Anthropic's short use-case form in the Bedrock console (Model catalog).", bedrock.Node.Id);
            }

            if (AwsGenerator.HasKnowledgeBase(model, bedrock))
            {
                Add(messages, MessageLevel.Info, $"{bedrock.Name}: after adding files to the bucket, sync the knowledge base (Bedrock console, or aws bedrock-agent start-ingestion-job).", bedrock.Node.Id);
            }
        }

        foreach (var index in model.OfKind(CloudKinds.Kendra))
        {
            Add(messages, MessageLevel.Warning, $"{index.Name}: a Kendra index is billed every hour it exists, from about 800 dollars a month (Developer edition).", index.Node.Id);
        }

        foreach (var vectors in model.OfKind(CloudKinds.AwsVectors))
        {
            Add(messages, MessageLevel.Warning, $"{vectors.Name}: OpenSearch Serverless is billed every hour it exists, from about 175 dollars a month (twice that with the production standby copy).", vectors.Node.Id);
        }

        foreach (var bot in model.OfKind(CloudKinds.Lex))
        {
            Add(messages, MessageLevel.Info, $"{bot.Name}: add the bot's intents in the Lex console and build it; code talks to its test alias until you publish one.", bot.Node.Id);
        }
    }

    private static void CheckGcp(SketchModel model, List<SketchMessage> messages)
    {
        var production = model.IsProduction;
        foreach (var balancer in model.OfKind(CloudKinds.LoadBalancer))
        {
            var origins = Served(model, balancer);
            if (origins.Count == 0)
            {
                Add(messages, MessageLevel.Error, $"{balancer.Name}: draw an arrow from the load balancer to what it serves (Cloud Run, a function or a bucket).", balancer.Node.Id);
                continue;
            }

            CheckPaths(balancer, origins, messages);
            foreach (var bucket in origins.Where(origin => origin.Kind == CloudKinds.Bucket && !origin.Flag("website")))
            {
                Add(messages, MessageLevel.Error, $"{bucket.Name}: a load balancer serves a bucket only as a public website; turn on \"Public website\".", bucket.Node.Id);
            }
        }

        foreach (var zone in model.OfKind(CloudKinds.CloudDns))
        {
            foreach (var balancer in model.Targets(zone, CloudKinds.LoadBalancer)
                .Where(balancer => balancer.P["domain"] != zone.P["zone"] && !balancer.P["domain"].EndsWith("." + zone.P["zone"], StringComparison.Ordinal)))
            {
                Add(messages, MessageLevel.Error, $"{zone.Name} -> {balancer.Name}: {balancer.P["domain"]} is not inside {zone.P["zone"]}.", zone.Node.Id);
            }
        }

        if (model.Resources.Any(resource => resource.Flag("public") || resource.Flag("website")))
        {
            Add(messages, MessageLevel.Info, "Public services and websites give access to allUsers; an organization policy may block that (Domain restricted sharing).");
        }

        foreach (var database in model.OfKind(CloudKinds.CloudSql))
        {
            if (production && !database.Flag("highAvailability"))
            {
                Add(messages, MessageLevel.Warning, $"{database.Name}: production without a standby copy stops if its zone fails.", database.Node.Id);
            }

            if (production && database.P["tier"] is "db-f1-micro" or "db-g1-small")
            {
                Add(messages, MessageLevel.Warning, $"{database.Name}: shared-core sizes are meant for testing, not production.", database.Node.Id);
            }
        }

        foreach (var database in model.OfKind(CloudKinds.Firestore).Where(database => production && !database.Flag("recovery")))
        {
            Add(messages, MessageLevel.Warning, $"{database.Name}: production without point-in-time recovery.", database.Node.Id);
        }

        foreach (var bucket in model.OfKind(CloudKinds.Bucket).Where(bucket => production && !bucket.Flag("versioning")))
        {
            Add(messages, MessageLevel.Warning, $"{bucket.Name}: production without versions; a deleted or overwritten file cannot be brought back.", bucket.Node.Id);
        }

        foreach (var index in model.OfKind(CloudKinds.GcpVectors))
        {
            Add(messages, MessageLevel.Warning, $"{index.Name}: the Vector Search endpoint runs all the time, from about 70 dollars a month per machine.", index.Node.Id);
        }

        foreach (var search in model.OfKind(CloudKinds.AiSearch))
        {
            Add(messages, MessageLevel.Info, $"{search.Name}: import your documents into its data store in the console (AI Applications), for example from a bucket.", search.Node.Id);
        }

        foreach (var notebook in model.OfKind(CloudKinds.Workbench))
        {
            Add(messages, MessageLevel.Info, $"{notebook.Name}: the notebook machine is billed while it runs; it stops by itself after an idle hour.", notebook.Node.Id);
        }

        foreach (var processor in model.OfKind(CloudKinds.DocumentAi))
        {
            Add(messages, MessageLevel.Info, $"{processor.Name}: Document AI keeps it in the {GcpGenerator.DocumentLocation(model).ToUpperInvariant()} location; call it at {GcpGenerator.DocumentLocation(model)}-documentai.googleapis.com.", processor.Node.Id);
        }

        foreach (var function in model.OfKind(CloudKinds.CloudFunction).Where(function => function.P["runtime"] is "go123" or "java21"))
        {
            Add(messages, MessageLevel.Info, $"{function.Name}: put your {function.P["runtime"]} source in functions/{function.Name} before deploying.", function.Node.Id);
        }
    }
}
