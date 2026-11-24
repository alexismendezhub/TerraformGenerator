using TerraformGenerator.Domain;

namespace TerraformGenerator.Domain.UnitTests;

// Boxes from the full provider list: code, checks, import and proposals, with a small hand-made provider list.
public sealed class GenericTests
{
    private static SchemaAttribute Field(string name, string type = "string", bool required = false) =>
        new(name, type, required, !required, false, false, null);

    private sealed class FakeSchemas : IResourceSchemas
    {
        private static readonly Dictionary<string, ResourceSchema> Aws = new(StringComparer.Ordinal)
        {
            ["aws_s3_bucket"] = new("aws_s3_bucket", Platforms.Aws, null,
                [Field("bucket_prefix"), Field("force_destroy", "bool"), Field("tags", "map(string)"), Field("tags_all", "map(string)")], []),
            ["aws_s3_bucket_versioning"] = new("aws_s3_bucket_versioning", Platforms.Aws, null,
                [Field("bucket", required: true)],
                [new SchemaBlock("versioning_configuration", "list", 1, 1, [Field("status", required: true)], [], null)]),
            ["aws_sqs_queue"] = new("aws_sqs_queue", Platforms.Aws, null,
                [Field("name"), Field("visibility_timeout_seconds", "number"), Field("redrive_policy"), Field("tags", "map(string)")], []),
        };

        public ResourceSchema? Find(string platform, string type) => platform == Platforms.Aws ? Aws.GetValueOrDefault(type) : null;
    }

    private static Sketch AwsDrawing() => new()
    {
        Settings = new SketchSettings { Platform = Platforms.Aws, Name = "shop", Environment = "dev", Region = "us-east-1" },
        Nodes =
        [
            new() { Id = "bucket", Kind = "tf:aws_s3_bucket", Name = "files", Props = new() { ["force_destroy"] = "false" } },
            new() { Id = "versioning", Kind = "tf:aws_s3_bucket_versioning", Name = "files-versioning", Props = new() { ["bucket"] = "ref:bucket.id", ["versioning_configuration.status"] = "Enabled" } },
        ],
        Edges = [new() { Id = "e1", From = "versioning", To = "bucket" }],
    };

    [Fact]
    public void An_aws_drawing_becomes_terraform_for_the_aws_provider()
    {
        var result = SketchGenerator.Generate(AwsDrawing(), new FakeSchemas());

        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        Assert.DoesNotContain(result.Files.Keys, name => name.StartsWith("bicep/", StringComparison.Ordinal));
        Assert.Contains("hashicorp/aws", result.Files["versions.tf"], StringComparison.Ordinal);
        Assert.Contains("default_tags", result.Files["main.tf"], StringComparison.Ordinal);
        var resources = result.Files["resources.tf"];
        Assert.Contains("resource \"aws_s3_bucket\" \"files\"", resources, StringComparison.Ordinal);
        Assert.Matches(@"force_destroy\s+=\s+false", resources);
        Assert.Matches(@"bucket\s+=\s+aws_s3_bucket\.files\.id", resources);
        Assert.Matches(@"versioning_configuration\s*\{\s*status\s+=\s+""Enabled""", resources);
    }

    [Fact]
    public void Missing_unknown_and_broken_fields_are_errors()
    {
        var sketch = AwsDrawing();
        sketch.Nodes[1].Props.Remove("versioning_configuration.status");
        sketch.Nodes[1].Props["bucket"] = "ref:gone.id";
        sketch.Edges.Clear();
        sketch.Nodes[0].Props["colour"] = "blue";
        sketch.Nodes.Add(new() { Id = "other", Kind = "tf:google_pubsub_topic", Name = "events" });
        sketch.Nodes.Add(new() { Id = "made-up", Kind = "tf:aws_made_up_thing", Name = "thing" });

        var texts = SketchRules.Check(sketch, new FakeSchemas()).Where(message => message.Level == MessageLevel.Error).Select(message => message.Text).ToList();

        Assert.Contains("files: aws_s3_bucket has no field \"colour\".", texts);
        Assert.Contains("files-versioning: \"bucket\" points to a box that no longer exists.", texts);
        Assert.Contains(texts, text => text.StartsWith("files-versioning: the \"versioning_configuration\" block is required", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.StartsWith("events: google_pubsub_topic belongs to another cloud", StringComparison.Ordinal));
        Assert.Contains(texts, text => text.StartsWith("thing: aws_made_up_thing is not a resource", StringComparison.Ordinal));
    }

    [Fact]
    public void The_cloud_of_some_terraform_is_the_one_most_resources_belong_to()
    {
        Assert.Equal(Platforms.Aws, TerraformImporter.DetectPlatform("resource \"aws_s3_bucket\" \"a\" {}\nresource \"aws_sqs_queue\" \"b\" {}\nresource \"google_pubsub_topic\" \"c\" {}"));
        Assert.Equal(Platforms.Gcp, TerraformImporter.DetectPlatform("resource \"google_pubsub_topic\" \"c\" {}"));
        Assert.Null(TerraformImporter.DetectPlatform("variable \"x\" {}"));
    }

    [Fact]
    public void Imported_aws_resources_become_boxes_with_their_fields_references_and_arrows()
    {
        const string terraform = """
            variable "retention" {
              default = 345600
            }

            resource "aws_s3_bucket" "files" {
              bucket_prefix = "shop-files-"
              force_destroy = false
              tags = {
                team = "store"
              }
            }

            resource "aws_s3_bucket_versioning" "files" {
              bucket = aws_s3_bucket.files.id
              versioning_configuration {
                status = "Enabled"
              }
            }

            resource "aws_sqs_queue" "jobs" {
              message_retention_seconds = var.retention
              name                      = "${var.prefix}-jobs"
            }
            """;

        var draft = TerraformImporter.Read(terraform);

        Assert.Equal(Platforms.Aws, draft.Settings.Platform);
        Assert.Equal(["tf:aws_s3_bucket", "tf:aws_s3_bucket_versioning", "tf:aws_sqs_queue"], draft.Nodes.Select(node => node.Kind).ToList());
        Assert.Equal(["files", "files-versioning", "jobs"], draft.Nodes.Select(node => node.Name).ToList());
        var bucket = draft.Nodes[0].Props.ToDictionary(prop => prop.Key, prop => prop.Value);
        Assert.Equal("shop-files-", bucket["bucket_prefix"]);
        Assert.Equal("false", bucket["force_destroy"]);
        Assert.Equal("tags = {\n  team = \"store\"\n}", bucket[GenericGenerator.ExtraHcl]);
        var versioning = draft.Nodes[1].Props.ToDictionary(prop => prop.Key, prop => prop.Value);
        Assert.Equal("ref:aws_s3_bucket.files.id", versioning["bucket"]);
        Assert.Equal("versioning_configuration {\n  status = \"Enabled\"\n}", versioning[GenericGenerator.ExtraHcl]);
        var queue = draft.Nodes[2].Props.ToDictionary(prop => prop.Key, prop => prop.Value);
        Assert.Equal("345600", queue["message_retention_seconds"]);
        Assert.Equal("=\"${var.prefix}-jobs\"", queue["name"]);
        var edge = Assert.Single(draft.Edges);
        Assert.Equal(("aws_s3_bucket_versioning.files", "aws_s3_bucket.files"), (edge.From, edge.To));
        Assert.DoesNotContain(draft.Questions, question => question.StartsWith("Not drawn", StringComparison.Ordinal));
    }

    [Fact]
    public void A_proposal_keeps_boxes_of_its_cloud_and_points_references_at_the_final_ids()
    {
        var draft = new ProposalDraft
        {
            Settings = new() { Platform = Platforms.Aws, Name = "shop", Environment = "dev", Region = "us-east-1" },
            Nodes =
            [
                new() { Id = "new-1", Kind = "tf:aws_s3_bucket", Name = "files" },
                new() { Id = "new-2", Kind = "tf:aws_s3_bucket_versioning", Name = "files-versioning", Props = [new() { Key = "bucket", Value = "ref:new-1.id" }] },
                new() { Id = "new-3", Kind = "tf:google_pubsub_topic", Name = "events" },
                new() { Id = "new-4", Kind = "tf:aws_made_up_thing", Name = "thing" },
            ],
        };

        var proposal = ProposalBuilder.Build(draft, new Sketch(), schemas: new FakeSchemas());

        Assert.Equal(Platforms.Aws, proposal.Sketch.Settings.Platform);
        Assert.Equal(["files", "files-versioning"], proposal.Sketch.Nodes.Select(node => node.Name).ToList());
        Assert.Equal($"ref:{proposal.Sketch.Nodes[0].Id}.id", proposal.Sketch.Nodes[1].Props["bucket"]);
        Assert.Contains(proposal.Review, item => item.Text == "Left out \"events\": google_pubsub_topic is not a resource of the Amazon Web Services (AWS) Terraform provider.");
        Assert.Contains(proposal.Review, item => item.Text.StartsWith("Left out \"thing\": aws_made_up_thing", StringComparison.Ordinal));
    }

    [Fact]
    public void The_provider_list_says_which_version_it_came_from_and_when_it_was_written()
    {
        var folder = Path.Combine(Path.GetTempPath(), "terraformgenerator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.Equal((null, null), ProviderSchemaFiles.Describe(Platforms.Azure, folder));
            File.WriteAllText(Path.Combine(folder, "resources.json"), "{}");
            Assert.Null(ProviderSchemaFiles.Describe(Platforms.Azure, folder).Version);
            File.WriteAllText(Path.Combine(folder, ".terraform.lock.hcl"), """
                provider "registry.terraform.io/hashicorp/random" {
                  version     = "3.7.2"
                }

                provider "registry.terraform.io/hashicorp/azurerm" {
                  version     = "4.51.0"
                  constraints = "~> 4.0"
                }
                """);
            var (version, updated) = ProviderSchemaFiles.Describe(Platforms.Azure, folder);
            Assert.Equal("4.51.0", version);
            Assert.Equal(File.GetLastWriteTimeUtc(Path.Combine(folder, "resources.json")), updated);
            Assert.Null(ProviderSchemaFiles.Describe(Platforms.Aws, folder).Version);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
