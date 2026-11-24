using static TerraformGenerator.Domain.Catalog;

namespace TerraformGenerator.Domain;

// Rules for the whole account, so nothing can be created outside them: on Azure, policies on the resource group
// (allowed regions, storage over HTTPS only); on AWS, account settings (no public buckets, disks encrypted by
// default, a password policy). Google Cloud's equivalents are organization policies, set at the organization.
public static class Guardrails
{
    public const string Key = "accountPolicies";

    public static PropertyDefinition Setting() =>
        new(Key, "Rules for the whole account", PropertyType.Select, "off",
            [O("off", "No"), O("on", "Yes: allowed regions, no public storage, encryption by default")], null,
            "Azure: policies on the resource group. AWS: account settings (they apply to every project in the account).", "Security");

    public static bool On(SketchModel model) => model.Setting(Key) == "on" && model.Resources.Count > 0;

    private const string AllowedLocations = "e56962a6-4747-49cd-b67b-bf8b01975c4c";
    private const string SecureTransfer = "404c3081-a854-4457-ae30-26a93ef643f9";

    // The regions the drawing uses: the region, "global" (Front Door, DNS) and the few services that run elsewhere.
    public static IEnumerable<string> AzureLocations(SketchModel model)
    {
        var locations = new SortedSet<string>(StringComparer.Ordinal) { model.Settings.Region, "global" };
        if (model.StaticSites.Any())
        {
            locations.Add(model.StaticSiteRegion);
        }

        if (model.OfKind(AzureServices.Maps).Any())
        {
            locations.Add(AzureServices.MapsLocation(model));
        }

        return locations;
    }

    public static List<HclItem> AzureTerraform(SketchModel model)
    {
        var items = new List<HclItem>();
        if (!On(model))
        {
            return items;
        }

        var locations = string.Join(", ", AzureLocations(model).Select(location => $"\"{location}\""));
        items.Add(new HclComment("Policies on the resource group: nothing outside these regions, storage only over HTTPS."));
        items.Add(new HclBlock("resource", "azurerm_resource_group_policy_assignment", "allowed_locations")
            .Str("name", "allowed-locations")
            .Attr("resource_group_id", "local.resource_group_id")
            .Str("policy_definition_id", $"/providers/Microsoft.Authorization/policyDefinitions/{AllowedLocations}")
            .Attr("parameters", $"jsonencode({{ listOfAllowedLocations = {{ value = [{locations}] }} }})"));
        items.Add(new HclBlock("resource", "azurerm_resource_group_policy_assignment", "secure_transfer")
            .Str("name", "storage-https-only")
            .Attr("resource_group_id", "local.resource_group_id")
            .Str("policy_definition_id", $"/providers/Microsoft.Authorization/policyDefinitions/{SecureTransfer}")
            .Attr("parameters", "jsonencode({ effect = { value = \"Deny\" } })"));
        return items;
    }

    public static void AzureBicep(SketchModel model, BicepFile file)
    {
        if (!On(model))
        {
            return;
        }

        file.Resource("policyAllowedLocations", "Microsoft.Authorization/policyAssignments@2024-04-01", Bicep.Object(body => body
            .Str("name", "allowed-locations")
            .Obj("properties", properties => properties
                .Add("policyDefinitionId", $"tenantResourceId('Microsoft.Authorization/policyDefinitions', '{AllowedLocations}')")
                .Obj("parameters", parameters => parameters.Obj("listOfAllowedLocations", allowed => allowed
                    .Add("value", Bicep.Array(AzureLocations(model).Select(location => (BicepValue)Bicep.Raw(Bicep.Str(location))).ToArray())))))),
            "Policies on the resource group: nothing outside these regions, storage only over HTTPS.");
        file.Resource("policySecureTransfer", "Microsoft.Authorization/policyAssignments@2024-04-01", Bicep.Object(body => body
            .Str("name", "storage-https-only")
            .Obj("properties", properties => properties
                .Add("policyDefinitionId", $"tenantResourceId('Microsoft.Authorization/policyDefinitions', '{SecureTransfer}')")
                .Obj("parameters", parameters => parameters.Obj("effect", effect => effect.Str("value", "Deny"))))));
    }

    public static IEnumerable<HclItem> Aws(SketchModel model)
    {
        if (!On(model))
        {
            yield break;
        }

        yield return new HclComment("Account settings: no bucket of the account can be public, new disks are encrypted, and people's passwords are strong.");
        yield return new HclBlock("resource", "aws_s3_account_public_access_block", "main")
            .Attr("block_public_acls", "true")
            .Attr("block_public_policy", "true")
            .Attr("ignore_public_acls", "true")
            .Attr("restrict_public_buckets", "true");
        yield return new HclBlock("resource", "aws_ebs_encryption_by_default", "main").Attr("enabled", "true");
        yield return new HclBlock("resource", "aws_iam_account_password_policy", "main")
            .Attr("minimum_password_length", "14")
            .Attr("require_lowercase_characters", "true")
            .Attr("require_uppercase_characters", "true")
            .Attr("require_numbers", "true")
            .Attr("require_symbols", "true")
            .Attr("password_reuse_prevention", "24")
            .Attr("max_password_age", "90");
    }

    public static IEnumerable<SketchMessage> Check(SketchModel model)
    {
        if (model.Setting(Key) != "on")
        {
            yield break;
        }

        yield return new(MessageLevel.Info, model.Platform switch
        {
            Platforms.Aws => "The account rules apply to every project of the AWS account; turn them on in one drawing only.",
            Platforms.Gcp => "On Google Cloud these rules are organization policies, set at the organization (constraints like storage.publicAccessPrevention and gcp.resourceLocations); a project cannot set them for itself.",
            _ => "Policies deny anything outside the allowed regions and storage without HTTPS in this resource group.",
        });
    }
}
