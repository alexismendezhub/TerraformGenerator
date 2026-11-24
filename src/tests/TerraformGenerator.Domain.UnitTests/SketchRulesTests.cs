using TerraformGenerator.Domain;

namespace TerraformGenerator.Domain.UnitTests;

public sealed class SketchRulesTests
{
    private static List<SketchMessage> Errors(Sketch sketch) =>
        SketchRules.Check(sketch).Where(message => message.Level == MessageLevel.Error).ToList();

    private static List<string> Texts(Sketch sketch, MessageLevel level) =>
        SketchRules.Check(sketch).Where(message => message.Level == level).Select(message => message.Text).ToList();

    [Fact]
    public void The_example_has_no_errors() => Assert.Empty(Errors(Examples.WebApiSql()));

    [Theory]
    [InlineData("A")]
    [InlineData("my-app")]
    [InlineData("x")]
    [InlineData("toolongprojectname")]
    public void Project_name_must_be_short_lowercase(string name)
    {
        var sketch = new Sketch { Settings = { Name = name } };

        Assert.Contains("Project name: 2 to 12 lowercase letters or digits, starting with a letter.", Errors(sketch).Select(error => error.Text));
    }

    [Fact]
    public void Unknown_region_and_environment_are_errors()
    {
        var sketch = new Sketch { Settings = { Region = "mars", Environment = "qa" } };

        var errors = Errors(sketch).Select(error => error.Text).ToList();
        Assert.Contains("Choose a region from the list.", errors);
        Assert.Contains("Choose an environment from the list.", errors);
    }

    [Theory]
    [InlineData("Api")]
    [InlineData("1api")]
    [InlineData("api-")]
    [InlineData("a")]
    [InlineData("a_b")]
    public void Box_names_follow_the_pattern(string name)
    {
        var builder = new SketchBuilder();
        var node = builder.Add(Kinds.WebApp, name);

        var error = Assert.Single(Errors(builder.Sketch));
        Assert.Equal(node.Id, error.NodeId);
        Assert.StartsWith($"Web App \"{name}\": the name needs", error.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Box_names_are_unique()
    {
        var builder = new SketchBuilder();
        builder.Add(Kinds.WebApp, "api");
        builder.Sketch.Nodes.Add(new SketchNode { Id = "other", Kind = Kinds.KeyVault, Name = "api" });

        Assert.Equal("Two boxes are named \"api\". Each name must be unique.", Assert.Single(Errors(builder.Sketch)).Text);
    }

    [Fact]
    public void Unknown_kinds_settings_and_choices_are_errors()
    {
        var builder = new SketchBuilder();
        builder.Add("cosmos", "docs");
        builder.Add(Kinds.WebApp, "api", new() { ["sku"] = "F1", ["color"] = "red", ["alwaysOn"] = "yes", ["path"] = "api" });

        var errors = Errors(builder.Sketch).Select(error => error.Text).ToList();
        Assert.Contains("Unknown kind of box: cosmos.", errors);
        Assert.Contains("api: \"Size (plan)\" is not valid. Apps with the same size share one App Service plan.", errors);
        Assert.Contains("api: \"Always on\" is not valid. Keeps the app loaded so the first request is not slow.", errors);
        Assert.Contains("api: \"Path at the entry\" is not valid. The path Front Door or Application Gateway sends here, for example /* or /api/*.", errors);
        Assert.Contains("api: unknown setting \"color\".", errors);
    }

    [Fact]
    public void Arrows_must_be_allowed_unique_and_between_existing_boxes()
    {
        var builder = new SketchBuilder();
        var app = builder.Add(Kinds.WebApp, "api");
        var database = builder.Add(Kinds.SqlDatabase, "orders");
        builder.Link(app, database).Link(app, database).Link(database, app).Link(app, app);
        builder.Sketch.Edges.Add(new SketchEdge { Id = "ghost", From = app.Id, To = "missing" });

        var errors = Errors(builder.Sketch).Select(error => error.Text).ToList();
        Assert.Contains("api -> orders is drawn twice.", errors);
        Assert.Contains("A SQL Database cannot point to a Web App (orders -> api).", errors);
        Assert.Contains("api cannot point to itself.", errors);
        Assert.Contains("An arrow points to a box that no longer exists.", errors);
    }

    [Fact]
    public void Apps_calling_each_other_in_a_circle_are_an_error()
    {
        var builder = new SketchBuilder();
        var a = builder.Add(Kinds.WebApp, "aaa");
        var b = builder.Add(Kinds.WebApp, "bbb");
        var c = builder.Add(Kinds.WebApp, "ccc");
        builder.Link(a, b).Link(b, c).Link(c, a);

        Assert.StartsWith("Apps call each other in a circle.", Assert.Single(Errors(builder.Sketch)).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Basic_database_keeps_seven_days_at_most()
    {
        var builder = new SketchBuilder();
        var app = builder.Add(Kinds.WebApp, "api");
        builder.Link(app, builder.Add(Kinds.SqlDatabase, "orders", new() { ["sku"] = "Basic", ["backupDays"] = "14" }));

        Assert.Equal("orders: the Basic size keeps backups for 7 days at most. Choose 7 days or a larger size.", Assert.Single(Errors(builder.Sketch)).Text);
    }

    [Fact]
    public void Front_door_paths_and_domains_cannot_repeat()
    {
        var builder = new SketchBuilder();
        var frontDoor = builder.Add(Kinds.FrontDoor, "edge");
        builder.Link(frontDoor, builder.Add(Kinds.WebApp, "web"));
        builder.Link(frontDoor, builder.Add(Kinds.WebApp, "site"));
        builder.Link(builder.Add(Kinds.DnsZone, "one", new() { ["zone"] = "example.com" }), frontDoor);
        builder.Link(builder.Add(Kinds.DnsZone, "two", new() { ["zone"] = "example.com" }), frontDoor);

        var errors = Errors(builder.Sketch).Select(error => error.Text).ToList();
        Assert.Contains("edge: web and site both use the path /*. Give each its own Front Door path.", errors);
        Assert.Contains("edge: the domain www.example.com is used twice.", errors);
        Assert.Contains("The domain example.com is drawn twice.", errors);
    }

    [Fact]
    public void Production_gets_safety_warnings()
    {
        var builder = new SketchBuilder().Environment("prod");
        var app = builder.Add(Kinds.WebApp, "api");
        builder.Link(app, builder.Add(Kinds.SqlDatabase, "orders"));
        builder.Link(app, builder.Add(Kinds.Storage, "files"));
        builder.Link(app, builder.Add(Kinds.KeyVault, "secrets"));
        builder.Sketch.Settings.Monitoring = false;

        var warnings = Texts(builder.Sketch, MessageLevel.Warning);
        Assert.Equal(
        [
            "Production without alerts: turn them on so someone hears when an app fails.",
            "Production without a budget alert: choose a monthly budget so a surprise bill is caught early.",
            "orders: production usually keeps at least 14 days of backups.",
            "files: in production, ZRS or GRS keeps the files if a datacenter fails.",
            "secrets: production usually turns on purge protection.",
            "api: Basic plans have no deployment slots or autoscale; production usually uses S1 or Premium.",
            "Production without monitoring: turn on Application Insights in the settings.",
            "api: it is public with no firewall in front. Put Front Door or Application Gateway in front of it and turn on its firewall.",
            "Nobody besides you gets access: name your team's groups in Team access (administrators, developers, readers).",
        ], warnings);
    }

    [Fact]
    public void Unused_resources_are_flagged()
    {
        var builder = new SketchBuilder();
        builder.Add(Kinds.SqlDatabase, "orders");
        builder.Add(Kinds.Storage, "files");
        builder.Add(Kinds.KeyVault, "secrets");
        builder.Add(Kinds.FrontDoor, "edge");
        builder.Add(Kinds.DnsZone, "domain");

        var messages = SketchRules.Check(builder.Sketch).Select(message => message.Text).ToList();
        Assert.Contains("orders: no app uses this database. Draw an arrow from an app to it.", messages);
        Assert.Contains("files: no app uses this storage. Draw an arrow from an app to it.", messages);
        Assert.Contains("secrets: no app uses this Key Vault. Draw an arrow from an app to it.", messages);
        Assert.Contains("edge: Front Door serves nothing. Draw an arrow from it to an app or site.", messages);
        Assert.Contains("domain: the zone is created without records. Point it to a Front Door for a custom domain.", messages);
    }

    [Fact]
    public void Mixed_private_databases_share_one_private_server()
    {
        var builder = new SketchBuilder();
        var app = builder.Add(Kinds.WebApp, "api");
        builder.Link(app, builder.Add(Kinds.SqlDatabase, "orders", new() { ["privateOnly"] = "true" }));
        builder.Link(app, builder.Add(Kinds.SqlDatabase, "reports"));

        Assert.Contains("All databases share one SQL server, so it is private for all of them.", Texts(builder.Sketch, MessageLevel.Warning));
    }
}
