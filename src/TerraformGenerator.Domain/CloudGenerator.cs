namespace TerraformGenerator.Domain;

// The Terraform around the resources for AWS and Google Cloud: provider, state, variables and outputs.
// (Azure has its own, richer files in TerraformGenerator.)
public static class CloudGenerator
{
    public static Dictionary<string, string> Files(SketchModel model, IResourceSchemas? schemas)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["versions.tf"] = Hcl.Render(Versions(model)),
            ["main.tf"] = Hcl.Render(Main(model)),
            ["variables.tf"] = Hcl.Render(Variables(model)),
            ["outputs.tf"] = Hcl.Render(Outputs(model)),
            ["terraform.tfvars.example"] = TfvarsExample(model),
        };
        var smart = model.Platform == Platforms.Aws ? AwsGenerator.Resources(model) : GcpGenerator.Resources(model);
        if (smart.Count > 0)
        {
            files[model.Platform == Platforms.Aws ? "aws.tf" : "gcp.tf"] = Hcl.Render(smart);
        }

        var code = model.Platform == Platforms.Aws ? AwsGenerator.CodeFiles(model) : GcpGenerator.CodeFiles(model);
        foreach (var (name, content) in code)
        {
            files[name] = content;
        }

        var resources = GenericGenerator.Resources(model, schemas);
        if (resources.Count > 0)
        {
            files["resources.tf"] = Hcl.Render(resources);
        }

        return files;
    }

    private static List<HclItem> Versions(SketchModel model)
    {
        var cloud = model.Cloud;
        var terraform = new HclBlock("terraform")
            .Str("required_version", ">= 1.10.0")
            .Block("required_providers", providers => providers
                .Map(cloud.Provider, [new("source", Hcl.Str(cloud.Source)), new("version", Hcl.Str(cloud.Version))])
                .Map("random", [new("source", Hcl.Str("hashicorp/random")), new("version", Hcl.Str("~> 3.6"))]));
        if (NeedsArchive(model))
        {
            // Zips the code folders of the functions.
            terraform.Body.OfType<HclBlock>().First(block => block.Type == "required_providers")
                .Map("archive", [new("source", Hcl.Str("hashicorp/archive")), new("version", Hcl.Str("~> 2.7"))]);
        }

        if (model.RemoteState)
        {
            terraform.Comment("The state lives in the cloud; bootstrap-state.ps1 creates its bucket and writes backend.hcl.");
            terraform.Block("backend", _ => { }, model.Platform == Platforms.Aws ? "s3" : "gcs");
        }

        return [terraform];
    }

    private static bool NeedsArchive(SketchModel model) =>
        model.OfKind(CloudKinds.Lambda).Any() || model.OfKind(CloudKinds.CloudFunction).Any();

    private static List<HclItem> Main(SketchModel model)
    {
        var items = new List<HclItem>
        {
            new HclComment($"{model.Settings.Name} ({model.Settings.Environment}) on {model.Cloud.Label}, drawn in Terraform Generator. See README.md for the steps."),
        };

        if (model.Platform == Platforms.Aws)
        {
            items.Add(new HclBlock("provider", "aws")
                .Attr("region", "var.region")
                .Block("default_tags", defaults => defaults
                    .Comment("Every resource gets these tags.")
                    .Attr("tags", "var.tags")));
            if (AwsGenerator.NeedsSecondRegion(model))
            {
                items.Add(new HclComment("The second region, for the copies of the data."));
                items.Add(new HclBlock("provider", "aws")
                    .Str("alias", "second_region")
                    .Str("region", Geo.Second(model))
                    .Block("default_tags", defaults => defaults.Attr("tags", "var.tags")));
            }

            if (AwsGenerator.NeedsUsEast1(model))
            {
                items.Add(new HclComment("CloudFront takes its certificates and its firewall from us-east-1."));
                items.Add(new HclBlock("provider", "aws")
                    .Str("alias", "us_east_1")
                    .Str("region", "us-east-1")
                    .Block("default_tags", defaults => defaults.Attr("tags", "var.tags")));
            }
        }
        else
        {
            var google = new HclBlock("provider", "google")
                .Attr("project", "var.project_id")
                .Attr("region", "var.region");
            if (model.Budget > 0)
            {
                google.Comment("Budgets are created through the project, which pays for the calls.")
                    .Attr("user_project_override", "true")
                    .Attr("billing_project", "var.project_id");
            }

            items.Add(google
                .Comment("Every resource that takes labels gets these.")
                .Attr("default_labels", "var.labels"));
        }

        items.Add(new HclComment("Six characters for names that must be unique everywhere."));
        items.Add(new HclBlock("resource", "random_string", "suffix")
            .Attr("length", "6")
            .Attr("lower", "true")
            .Attr("upper", "false")
            .Attr("numeric", "true")
            .Attr("special", "false"));
        items.Add(new HclBlock("locals").Attr("suffix", "random_string.suffix.result"));
        return items;
    }

    private static HclBlock Variable(string name, string type, string description, string? defaultValue = null)
    {
        var block = new HclBlock("variable", name).Str("description", description).Attr("type", type);
        if (defaultValue is not null)
        {
            block.Attr("default", defaultValue);
        }

        return block;
    }

    private static List<HclItem> Variables(SketchModel model)
    {
        var items = new List<HclItem>();
        if (model.Platform == Platforms.Gcp)
        {
            items.Add(Variable("project_id", "string", "The Google Cloud project every resource goes in.", Hcl.Literal(model.GcpProject)));
        }

        items.Add(Variable("region", "string", "The region.", Hcl.Str(model.Settings.Region)));
        if ((model.Platform == Platforms.Aws && model.AwsOwnNetwork && AwsGenerator.NeedsNetwork(model)) || GcpGenerator.NeedsNetwork(model))
        {
            items.Add(Variable("network_range", "string", "The addresses of the network: a /16 that does not overlap your other networks.", Hcl.Str(model.CloudNetworkRange)));
        }
        items.Add(Variable("name_prefix", "string", "Starts the name of every resource.", Hcl.Str($"{model.Settings.Name}-{model.Settings.Environment}")));

        var tags = new List<KeyValuePair<string, string>>
        {
            new("project", Hcl.Literal(model.Settings.Name)),
            new("environment", Hcl.Literal(model.Settings.Environment)),
            new("managed-by", Hcl.Str("terraform")),
        };
        if (model.Platform == Platforms.Aws)
        {
            tags.AddRange(model.ExtraTags.Select(tag => new KeyValuePair<string, string>(tag.Key, Hcl.Literal(tag.Value))));
            items.Add(new HclBlock("variable", "tags").Str("description", "Tags on every resource.").Attr("type", "map(string)").Map("default", tags));
        }
        else
        {
            // Google labels take lowercase letters, digits, - and _ only.
            tags.AddRange(model.ExtraTags.Select(tag => new KeyValuePair<string, string>(tag.Key, Hcl.Literal(Label(tag.Value)))));
            items.Add(new HclBlock("variable", "labels").Str("description", "Labels on every resource.").Attr("type", "map(string)").Map("default", tags));
        }

        return items;
    }

    private static string Label(string value) =>
        new string(value.ToLowerInvariant().Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '-').Take(63).ToArray());

    private static List<HclItem> Outputs(SketchModel model)
    {
        var items = model.Platform == Platforms.Aws ? AwsGenerator.Outputs(model) : GcpGenerator.Outputs(model);
        foreach (var resource in model.Generics)
        {
            items.Add(new HclBlock("output", $"{resource.Label}_id")
                .Str("description", $"The id of {resource.Name}.")
                .Attr("value", $"{Kinds.TypeOf(resource.Kind)}.{resource.Label}.id"));
        }

        return items;
    }

    private static string TfvarsExample(SketchModel model) => model.Platform == Platforms.Aws
        ? "# Optional: change the region here instead of in Terraform Generator.\n# region = \"" + model.Settings.Region + "\"\n"
        : "# Optional: another project or region than the drawing's.\n# project_id = \"my-project\"\n# region     = \"" + model.Settings.Region + "\"\n";
}
