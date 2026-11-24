using System.Text.Json.Nodes;
using TerraformGenerator.Domain;

namespace TerraformGenerator.Domain.UnitTests;

public sealed class ProposalTests
{
    private static ProposalNode Node(string id, string kind, string name, string review = "", params (string Key, string Value)[] props) =>
        new() { Id = id, Kind = kind, Name = name, Review = review, Props = props.Select(prop => new ProposalProp { Key = prop.Key, Value = prop.Value }).ToList() };

    private static ProposalEdge Edge(string from, string to) => new() { From = from, To = to };

    [Fact]
    public void A_new_drawing_gets_ids_positions_review_and_changes()
    {
        var draft = new ProposalDraft
        {
            Summary = "  A web app with its database.  ",
            Settings = new() { Name = "shop", Environment = "dev", Region = "eastus2", Monitoring = true },
            Nodes =
            [
                Node("new-1", Kinds.WebApp, "web", "I picked Node.js 22.", ("runtime", "node22"), ("sku", "B1")),
                Node("new-2", Kinds.SqlDatabase, "orders"),
                Node("new-3", Kinds.WebApp, "api"),
            ],
            Edges = [Edge("new-1", "new-2"), Edge("new-1", "new-2"), Edge("new-1", "missing")],
            Questions = ["Do you need a custom domain?", " "],
        };

        var proposal = ProposalBuilder.Build(draft, new Sketch());

        Assert.Equal("A web app with its database.", proposal.Summary);
        Assert.Equal(["ai-new1", "ai-new2", "ai-new3"], proposal.Sketch.Nodes.Select(node => node.Id).ToList());
        Assert.Equal(new Dictionary<string, string> { ["runtime"] = "node22" }, proposal.Sketch.Nodes[0].Props);
        Assert.Equal((460, 40), (proposal.Sketch.Nodes[0].X, proposal.Sketch.Nodes[0].Y));
        Assert.Equal((680, 40), (proposal.Sketch.Nodes[1].X, proposal.Sketch.Nodes[1].Y));
        Assert.Equal((460, 150), (proposal.Sketch.Nodes[2].X, proposal.Sketch.Nodes[2].Y));
        var edge = Assert.Single(proposal.Sketch.Edges);
        Assert.Equal(("ai-new1", "ai-new2"), (edge.From, edge.To));
        Assert.Equal(
            [new ReviewItem("web: I picked Node.js 22.", "ai-new1"), new ReviewItem("Do you need a custom domain?")],
            proposal.Review);
        Assert.Equal(
        [
            "Added Web App web", "Added SQL Database orders", "Added Web App api", "Added Connection web -> orders",
            "Changed Project: shop, dev, eastus2, monitoring on",
        ], proposal.Changes.Select(change => $"{change.Type} {change.Text}").ToList());
        // The proposal is checked like any drawing.
        Assert.Contains(proposal.Messages, message => message.Text.StartsWith("After the first apply", StringComparison.Ordinal));
    }

    [Fact]
    public void A_saved_drawing_puts_imported_boxes_back_where_they_were()
    {
        var draft = new ProposalDraft
        {
            Summary = "Read 3 resources from the Terraform.",
            Nodes = [Node("a", Kinds.WebApp, "web"), Node("b", Kinds.SqlDatabase, "orders"), Node("c", Kinds.WebApp, "admin")],
        };
        var layout = new Sketch
        {
            Nodes =
            [
                new SketchNode { Id = "x1", Kind = Kinds.Storage, Name = "web", X = 1, Y = 1 },
                new SketchNode { Id = "x2", Kind = Kinds.WebApp, Name = "web", X = 300, Y = 500 },
                new SketchNode { Id = "x3", Kind = Kinds.Postgres, Name = "orders", X = 700, Y = 20 },
            ],
        };

        var proposal = ProposalBuilder.Build(draft, new Sketch(), layout: layout);

        // Same name and kind first, then the same name; a box the drawing does not have goes to its column.
        Assert.Equal(
            [(300.0, 500.0), (700.0, 20.0), (460.0, 40.0)],
            proposal.Sketch.Nodes.Select(node => (node.X, node.Y)).ToList());
        Assert.Equal("Read 3 resources from the Terraform. 2 of 3 boxes placed as in sketch.json.", proposal.Summary);
    }

    [Fact]
    public void Changing_a_drawing_keeps_ids_positions_and_lists_differences()
    {
        var builder = new SketchBuilder();
        var app = builder.Add(Kinds.WebApp, "api");
        app.X = 300;
        app.Y = 200;
        var database = builder.Add(Kinds.SqlDatabase, "orders");
        var vault = builder.Add(Kinds.KeyVault, "secrets");
        builder.Link(app, database).Link(app, vault);
        var current = builder.Sketch;

        var draft = new ProposalDraft
        {
            Settings = new() { Name = current.Settings.Name, Environment = "dev", Region = "eastus2", Monitoring = true },
            Nodes =
            [
                Node(app.Id, Kinds.WebApp, "api", "", ("runtime", "python312")),
                Node(database.Id, Kinds.SqlDatabase, "orders", "", ("backupDays", "14")),
                Node("new-1", Kinds.Storage, "files"),
            ],
            Edges = [Edge(app.Id, database.Id), Edge(app.Id, "new-1")],
        };

        var proposal = ProposalBuilder.Build(draft, current);

        var kept = proposal.Sketch.Nodes[0];
        Assert.Equal((app.Id, 300d, 200d), (kept.Id, kept.X, kept.Y));
        Assert.Equal("api->orders", proposal.Sketch.Edges[0].Id);
        Assert.Equal(
        [
            "Changed Web App api: Runtime: Python 3.12",
            "Changed SQL Database orders: Point-in-time backup: 14 days",
            "Added Storage files",
            "Removed Key Vault secrets",
            "Added Connection api -> files",
            "Removed Connection api -> secrets",
        ], proposal.Changes.Select(change => $"{change.Type} {change.Text}").ToList());
    }

    [Fact]
    public void Unknown_kinds_are_left_out_and_reported()
    {
        var draft = new ProposalDraft { Nodes = [Node("new-1", "cosmos", "docs"), Node("new-2", Kinds.WebApp, "api")] };

        var proposal = ProposalBuilder.Build(draft, new Sketch());

        Assert.Single(proposal.Sketch.Nodes);
        Assert.Contains(new ReviewItem("Left out \"docs\": Terraform Generator has no cosmos box yet."), proposal.Review);
    }

    [Fact]
    public void An_existing_id_with_another_kind_becomes_a_new_box()
    {
        var builder = new SketchBuilder();
        var app = builder.Add(Kinds.WebApp, "api");

        var proposal = ProposalBuilder.Build(new ProposalDraft { Nodes = [Node(app.Id, Kinds.KeyVault, "api")] }, builder.Sketch);

        Assert.NotEqual(app.Id, Assert.Single(proposal.Sketch.Nodes).Id);
        Assert.Contains(proposal.Changes, change => change is { Type: ChangeType.Removed, Text: "Web App api" });
    }

    [Fact]
    public void Changes_compare_with_the_baseline_when_correcting_a_proposal()
    {
        var baseline = new Sketch();
        var first = ProposalBuilder.Build(new ProposalDraft { Nodes = [Node("new-1", Kinds.WebApp, "api")] }, baseline);

        var corrected = ProposalBuilder.Build(
            new ProposalDraft { Nodes = [Node(first.Sketch.Nodes[0].Id, Kinds.WebApp, "api", "", ("runtime", "node22"))] },
            first.Sketch,
            baseline);

        Assert.Equal(first.Sketch.Nodes[0].Id, corrected.Sketch.Nodes[0].Id);
        Assert.Contains(corrected.Changes, change => change is { Type: ChangeType.Added, Text: "Web App api" });
    }

    [Fact]
    public void Empty_settings_keep_the_current_ones()
    {
        var current = new Sketch { Settings = { Name = "store", Environment = "prod", Region = "westus3" } };

        var proposal = ProposalBuilder.Build(new ProposalDraft(), current);

        Assert.Equal(("store", "prod", "westus3"), (proposal.Sketch.Settings.Name, proposal.Sketch.Settings.Environment, proposal.Sketch.Settings.Region));
    }

    [Fact]
    public void The_prompt_lists_every_box_and_connection_from_the_catalog()
    {
        foreach (var platform in Platforms.All)
        {
            var system = ProposalPrompt.System(platform.Id);
            foreach (var kind in Catalog.Instance.Kinds)
            {
                Assert.Equal(kind.Platform == platform.Id, system.Contains($"\"kind\":\"{kind.Kind}\"", StringComparison.Ordinal));
            }

            Assert.Contains(Catalog.NamePattern, system, StringComparison.Ordinal);
        }

        Assert.Contains("\"from\":\"dnsZone\",\"to\":\"frontDoor\"", ProposalPrompt.System(), StringComparison.Ordinal);
        Assert.Contains("\"from\":\"awsRoute53\",\"to\":\"awsCloudFront\"", ProposalPrompt.System(Platforms.Aws), StringComparison.Ordinal);
    }

    [Fact]
    public void The_user_message_carries_the_current_drawing_only_when_there_is_one()
    {
        Assert.EndsWith("There is no current drawing: start a new one.", ProposalPrompt.User(" a shop ", new Sketch()), StringComparison.Ordinal);

        var sketch = Examples.WebApiSql();
        var message = ProposalPrompt.User("add a cache", sketch);
        Assert.StartsWith("Description:\nadd a cache\n\nCurrent drawing", message, StringComparison.Ordinal);
        Assert.Contains("\"id\":\"api\",\"kind\":\"webApp\"", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_schema_closes_every_object_and_requires_every_field()
    {
        var schema = ProposalPrompt.Schema();

        static void Check(JsonNode? node)
        {
            if (node is not JsonObject obj)
            {
                return;
            }

            if (obj["type"]?.GetValue<string>() == "object")
            {
                Assert.False(obj["additionalProperties"]!.GetValue<bool>());
                var properties = obj["properties"]!.AsObject().Select(pair => pair.Key).Order(StringComparer.Ordinal);
                var required = obj["required"]!.AsArray().Select(item => item!.GetValue<string>()).Order(StringComparer.Ordinal);
                Assert.Equal(properties, required);
            }

            foreach (var (_, child) in obj)
            {
                Check(child);
                if (child is JsonObject properties)
                {
                    foreach (var (_, property) in properties)
                    {
                        Check(property);
                    }
                }
            }
        }

        Check(schema);
        // Any kind is accepted (boxes from the full provider list); ProposalBuilder leaves out the unknown ones.
        Assert.Equal("string", schema["properties"]!["nodes"]!["items"]!["properties"]!["kind"]!["type"]!.GetValue<string>());
        var platforms = schema["properties"]!["settings"]!["properties"]!["platform"]!["enum"]!.AsArray().Select(item => item!.GetValue<string>());
        Assert.Equal(Platforms.All.Select(platform => platform.Id), platforms);
    }

    [Fact]
    public void Parse_reads_the_structured_output()
    {
        var draft = ProposalPrompt.Parse("""
            {"summary":"s","settings":{"name":"shop","environment":"dev","region":"eastus2","monitoring":false},
             "nodes":[{"id":"new-1","kind":"webApp","name":"api","props":[{"key":"runtime","value":"node22"}],"review":""}],
             "edges":[],"questions":["q"]}
            """);

        Assert.Equal("api", draft.Nodes[0].Name);
        Assert.Equal("node22", draft.Nodes[0].Props[0].Value);
        Assert.False(draft.Settings.Monitoring);
        Assert.Equal(["q"], draft.Questions);
    }
}
