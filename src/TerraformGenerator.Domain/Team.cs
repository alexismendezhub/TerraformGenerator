using static TerraformGenerator.Domain.Catalog;

namespace TerraformGenerator.Domain;

// Who of the team may enter the cloud and what they may do: administrators, developers and readers. On Azure and
// Google Cloud the person names the team's groups (Microsoft Entra or Google groups); on AWS three IAM groups are
// created to add people to. Developers may change things outside production and only look in production, by default.
public static class Team
{
    public const string AzureAdmins = "azureAdminsGroup";
    public const string AzureDevelopers = "azureDevelopersGroup";
    public const string AzureReaders = "azureReadersGroup";
    public const string GcpAdmins = "gcpAdminsGroup";
    public const string GcpDevelopers = "gcpDevelopersGroup";
    public const string GcpReaders = "gcpReadersGroup";
    public const string AwsGroups = "awsTeamGroups";
    public const string DevelopersAccess = "developersAccess";

    private const string Group = "Team access";

    public static readonly IReadOnlyList<string> Keys = [AzureAdmins, AzureDevelopers, AzureReaders, GcpAdmins, GcpDevelopers, GcpReaders, AwsGroups, DevelopersAccess];

    public static IEnumerable<PropertyDefinition> Settings =>
    [
        new(AzureAdmins, "Administrators group (object id)", PropertyType.Text, "", [], GuidPattern,
            "The Microsoft Entra group whose members manage everything here (Owner). Empty: nobody besides you.", Group, "platform=azure"),
        new(AzureDevelopers, "Developers group (object id)", PropertyType.Text, "", [], GuidPattern, "The Microsoft Entra group of the developers.", Group, "platform=azure"),
        new(AzureReaders, "Readers group (object id)", PropertyType.Text, "", [], GuidPattern, "The Microsoft Entra group that only looks (Reader).", Group, "platform=azure"),
        new(GcpAdmins, "Administrators group (email)", PropertyType.Text, "", [], EmailPattern,
            "The Google group whose members manage the project (Owner). Empty: nobody besides you.", Group, "platform=gcp"),
        new(GcpDevelopers, "Developers group (email)", PropertyType.Text, "", [], EmailPattern, "The Google group of the developers.", Group, "platform=gcp"),
        new(GcpReaders, "Readers group (email)", PropertyType.Text, "", [], EmailPattern, "The Google group that only looks (Viewer).", Group, "platform=gcp"),
        new(AwsGroups, "Team groups", PropertyType.Select, "create",
            [O("create", "Create administrators, developers and readers groups in IAM"), O("none", "None (I use IAM Identity Center)")], null,
            "Add people to them afterwards: aws iam add-user-to-group.", Group, "platform=aws"),
        new(DevelopersAccess, "What developers may do", PropertyType.Select, "auto",
        [
            O("auto", "Change things outside production; only look in production"), O("write", "Change things in every environment"), O("read", "Only look"),
        ], null, null, Group),
    ];

    public static bool DevelopersWrite(SketchModel model) => model.Setting(DevelopersAccess) switch
    {
        "write" => true,
        "read" => false,
        _ => !model.IsProduction,
    };

    // The named groups and the role each gets.
    private static IEnumerable<(string Label, string Group, string Role)> Named(SketchModel model, string admins, string developers, string readers,
        string owner, string writer, string reader)
    {
        if (model.Setting(admins) is { Length: > 0 } adminGroup)
        {
            yield return ("admins", adminGroup, owner);
        }

        if (model.Setting(developers) is { Length: > 0 } developerGroup)
        {
            yield return ("developers", developerGroup, DevelopersWrite(model) ? writer : reader);
        }

        if (model.Setting(readers) is { Length: > 0 } readerGroup)
        {
            yield return ("readers", readerGroup, reader);
        }
    }

    public static IEnumerable<(string Label, string Group, string Role)> Azure(SketchModel model) =>
        Named(model, AzureAdmins, AzureDevelopers, AzureReaders, "Owner", "Contributor", "Reader");

    public static IEnumerable<(string Label, string Group, string Role)> Gcp(SketchModel model) =>
        Named(model, GcpAdmins, GcpDevelopers, GcpReaders, "roles/owner", "roles/editor", "roles/viewer");

    public static List<HclItem> AzureTerraform(SketchModel model)
    {
        var items = new List<HclItem>();
        foreach (var (label, group, role) in Azure(model))
        {
            items.Add(new HclComment($"The {label} group: {role} on the resource group."));
            items.Add(new HclBlock("resource", "azurerm_role_assignment", $"team_{label}")
                .Attr("scope", "local.resource_group_id")
                .Str("role_definition_name", role)
                .Attr("principal_id", Hcl.Literal(group))
                .Str("principal_type", "Group"));
        }

        return items;
    }

    private static readonly Dictionary<string, string> AzureRoleIds = new(StringComparer.Ordinal)
    {
        ["Owner"] = "8e3af657-a8ff-443c-a75c-2fe8c4bcb635",
        ["Contributor"] = "b24988ac-6180-42a0-ab88-20f7382dd24c",
        ["Reader"] = "acdd72a7-3385-48ef-bd42-f606fba81ae7",
    };

    public static void AzureBicep(SketchModel model, BicepFile file)
    {
        foreach (var (label, group, role) in Azure(model))
        {
            var id = AzureRoleIds[role];
            file.Resource($"team{char.ToUpperInvariant(label[0])}{label[1..]}", "Microsoft.Authorization/roleAssignments@2022-04-01", Bicep.Object(body => body
                .Add("name", $"guid(resourceGroup().id, '{group}', '{id}')")
                .Obj("properties", properties => properties
                    .Add("roleDefinitionId", $"subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '{id}')")
                    .Add("principalId", Bicep.Literal(group))
                    .Str("principalType", "Group"))),
                $"The {label} group: {role} on the resource group.");
        }
    }

    public static IEnumerable<HclItem> AwsTerraform(SketchModel model)
    {
        if (model.Setting(AwsGroups) != "create")
        {
            yield break;
        }

        yield return new HclComment("The team's groups: add people with aws iam add-user-to-group --group-name <group> --user-name <user>.");
        foreach (var (label, policy) in new[]
        {
            ("admins", "AdministratorAccess"), ("developers", DevelopersWrite(model) ? "PowerUserAccess" : "ReadOnlyAccess"), ("readers", "ReadOnlyAccess"),
        })
        {
            yield return new HclBlock("resource", "aws_iam_group", $"team_{label}").Attr("name", Hcl.Str($"${{var.name_prefix}}-{label}"));
            yield return new HclBlock("resource", "aws_iam_group_policy_attachment", $"team_{label}")
                .Attr("group", $"aws_iam_group.team_{label}.name")
                .Str("policy_arn", $"arn:aws:iam::aws:policy/{policy}");
        }
    }

    public static IEnumerable<HclItem> GcpTerraform(SketchModel model)
    {
        foreach (var (label, group, role) in Gcp(model))
        {
            yield return new HclComment($"The {label} group: {role} on the project.");
            yield return new HclBlock("resource", "google_project_iam_member", $"team_{label}")
                .Attr("project", "var.project_id")
                .Str("role", role)
                .Attr("member", Hcl.Literal($"group:{group}"));
        }
    }

    public static IEnumerable<SketchMessage> Check(SketchModel model)
    {
        var named = model.Platform switch
        {
            Platforms.Azure => Azure(model).Any(),
            Platforms.Gcp => Gcp(model).Any(),
            _ => model.Setting(AwsGroups) == "create",
        };
        if (!named && model.Resources.Count > 0)
        {
            yield return new(model.IsProduction ? MessageLevel.Warning : MessageLevel.Info,
                "Nobody besides you gets access: name your team's groups in Team access (administrators, developers, readers).");
        }

        if (model.IsProduction && model.Setting(DevelopersAccess) == "write")
        {
            yield return new(MessageLevel.Warning, "Developers can change production: changes there usually go only through the pipeline.");
        }
    }
}
