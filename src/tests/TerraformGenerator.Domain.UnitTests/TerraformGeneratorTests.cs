using TerraformGenerator.Domain;

namespace TerraformGenerator.Domain.UnitTests;

public sealed class TerraformGeneratorTests
{
    private static IReadOnlyDictionary<string, string> Files(Sketch sketch)
    {
        var result = SketchGenerator.Generate(sketch);
        Assert.False(result.HasErrors, string.Join("\n", result.Messages.Select(message => message.Text)));
        return result.Files;
    }

    [Fact]
    public void Errors_produce_no_files()
    {
        var result = SketchGenerator.Generate(new Sketch { Settings = { Name = "X" } });

        Assert.True(result.HasErrors);
        Assert.Empty(result.Files);
    }

    [Fact]
    public void An_empty_drawing_still_has_the_base_files()
    {
        var files = Files(new Sketch());

        Assert.Equal([".gitignore", "README.md", "bootstrap-state.ps1", "check-prereqs.ps1", "deploy.ps1", "main.tf", "outputs.tf", "terraform.tfvars.example", "variables.tf", "versions.tf"], files.Keys.Order(StringComparer.Ordinal).ToList());
        Assert.Contains("resource \"azurerm_resource_group\" \"main\" {", files["main.tf"], StringComparison.Ordinal);
        Assert.DoesNotContain("azurerm_client_config", files["main.tf"], StringComparison.Ordinal);
        Assert.Contains("default     = \"myapp-dev\"", files["variables.tf"], StringComparison.Ordinal);
    }

    [Fact]
    public void The_example_generates_every_file()
    {
        var files = Files(Examples.WebApiSql());

        Assert.Equal(
            [".gitignore", "README.md", "apps.tf", "bootstrap-state.ps1", "check-prereqs.ps1", "data.tf", "deploy.ps1", "edge.tf", "main.tf", "monitoring.tf", "network.tf", "outputs.tf", "security.tf", "terraform.tfvars.example", "variables.tf", "versions.tf"],
            files.Keys.Order(StringComparer.Ordinal).ToList());
    }

    [Fact]
    public void Apps_of_the_same_size_share_a_plan()
    {
        var builder = new SketchBuilder();
        builder.Add(Kinds.WebApp, "web");
        builder.Add(Kinds.WebApp, "api");
        builder.Add(Kinds.WebApp, "jobs", new() { ["sku"] = "P1v3" });

        var apps = Files(builder.Sketch)["apps.tf"];
        Assert.Contains("resource \"azurerm_service_plan\" \"plan_b1\" {", apps, StringComparison.Ordinal);
        Assert.Contains("resource \"azurerm_service_plan\" \"plan_p1v3\" {", apps, StringComparison.Ordinal);
        Assert.Contains("# App Service plan B1: web, api.", apps, StringComparison.Ordinal);
        Assert.Equal(3, apps.Split("azurerm_service_plan.plan_b1.id").Length);
    }

    [Theory]
    [InlineData("dotnet8", "dotnet_version = \"8.0\"")]
    [InlineData("dotnet9", "dotnet_version = \"9.0\"")]
    [InlineData("node20", "node_version = \"20-lts\"")]
    [InlineData("node22", "node_version = \"22-lts\"")]
    [InlineData("python312", "python_version = \"3.12\"")]
    [InlineData("docker", "docker_image_name   = \"nginx:latest\"")]
    public void Runtime_sets_the_application_stack(string runtime, string expected)
    {
        var builder = new SketchBuilder();
        builder.Add(Kinds.WebApp, "api", new() { ["runtime"] = runtime });

        Assert.Contains(expected, Files(builder.Sketch)["apps.tf"], StringComparison.Ordinal);
    }

    [Fact]
    public void App_to_database_uses_the_identity_and_a_connection_string()
    {
        var builder = new SketchBuilder();
        var dotnet = builder.Add(Kinds.WebApp, "api");
        var node = builder.Add(Kinds.WebApp, "worker", new() { ["runtime"] = "node22" });
        var database = builder.Add(Kinds.SqlDatabase, "orders");
        builder.Link(dotnet, database).Link(node, database);

        var files = Files(builder.Sketch);
        Assert.Contains("\"ConnectionStrings__orders\"", files["apps.tf"], StringComparison.Ordinal);
        Assert.Contains("\"SQL_ORDERS_CONNECTION_STRING\"", files["apps.tf"], StringComparison.Ordinal);
        Assert.Contains("Authentication=Active Directory Managed Identity", files["apps.tf"], StringComparison.Ordinal);
        Assert.Contains("azuread_authentication_only = true", files["data.tf"], StringComparison.Ordinal);
        Assert.Contains("resource \"azurerm_mssql_firewall_rule\" \"azure_services\"", files["data.tf"], StringComparison.Ordinal);
        Assert.DoesNotContain("network.tf", files.Keys);
        Assert.Contains("-- In the database orders, for worker (output worker_identity):", files["README.md"], StringComparison.Ordinal);
    }

    [Fact]
    public void Private_database_adds_network_endpoint_and_integration()
    {
        var builder = new SketchBuilder();
        var app = builder.Add(Kinds.WebApp, "api");
        builder.Add(Kinds.WebApp, "web");
        builder.Link(app, builder.Add(Kinds.SqlDatabase, "orders", new() { ["privateOnly"] = "true" }));

        var files = Files(builder.Sketch);
        Assert.Contains("public_network_access_enabled = false", files["data.tf"], StringComparison.Ordinal);
        Assert.Contains("resource \"azurerm_private_endpoint\" \"sql\"", files["data.tf"], StringComparison.Ordinal);
        Assert.DoesNotContain("azurerm_mssql_firewall_rule", files["data.tf"], StringComparison.Ordinal);
        Assert.Contains("privatelink.database.windows.net", files["network.tf"], StringComparison.Ordinal);
        Assert.Contains("resource \"azurerm_subnet\" \"plan_b1\"", files["network.tf"], StringComparison.Ordinal);
        Assert.Contains("Microsoft.Web/serverFarms", files["network.tf"], StringComparison.Ordinal);

        // Only the app that reaches the private database joins the network.
        Assert.Single(files["apps.tf"].Split('\n'), line => line.Contains("virtual_network_subnet_id", StringComparison.Ordinal));
        Assert.Contains("vnet_route_all_enabled = true", files["apps.tf"], StringComparison.Ordinal);
    }

    [Fact]
    public void Serverless_database_pauses_and_production_keeps_geo_backups()
    {
        var builder = new SketchBuilder().Environment("prod");
        builder.Link(builder.Add(Kinds.WebApp, "api"), builder.Add(Kinds.SqlDatabase, "orders", new() { ["sku"] = "GP_S_Gen5_1", ["backupDays"] = "35" }));

        var data = Files(builder.Sketch)["data.tf"];
        Assert.Contains("auto_pause_delay_in_minutes = 60", data, StringComparison.Ordinal);
        Assert.Contains("storage_account_type        = \"Geo\"", data, StringComparison.Ordinal);
        Assert.Contains("retention_days = 35", data, StringComparison.Ordinal);
    }

    [Fact]
    public void Storage_and_key_vault_give_roles_and_settings()
    {
        var builder = new SketchBuilder();
        var app = builder.Add(Kinds.WebApp, "api");
        builder.Link(app, builder.Add(Kinds.Storage, "files", new() { ["containers"] = "uploads, invoices", ["privateOnly"] = "true" }));
        builder.Link(app, builder.Add(Kinds.KeyVault, "secrets", new() { ["purgeProtection"] = "true", ["retentionDays"] = "90" }));

        var files = Files(builder.Sketch);
        Assert.Contains("for_each              = toset([\"uploads\", \"invoices\"])", files["data.tf"], StringComparison.Ordinal);
        Assert.Contains("resource \"azurerm_private_endpoint\" \"files_blob\"", files["data.tf"], StringComparison.Ordinal);
        Assert.Contains("\"STORAGE_FILES_BLOB_ENDPOINT\"", files["apps.tf"], StringComparison.Ordinal);
        Assert.Contains("\"KEYVAULT_SECRETS_URI\"", files["apps.tf"], StringComparison.Ordinal);
        Assert.Contains("role_definition_name = \"Storage Blob Data Contributor\"", files["security.tf"], StringComparison.Ordinal);
        Assert.Contains("role_definition_name = \"Key Vault Secrets User\"", files["security.tf"], StringComparison.Ordinal);
        Assert.Matches("purge_protection_enabled += true", files["security.tf"]);
        Assert.Matches("soft_delete_retention_days += 90", files["security.tf"]);
        Assert.Contains("purge_soft_delete_on_destroy = false", files["main.tf"], StringComparison.Ordinal);
    }

    [Fact]
    public void Front_door_with_a_zone_restricts_the_app_and_adds_records()
    {
        var builder = new SketchBuilder();
        var frontDoor = builder.Add(Kinds.FrontDoor, "edge", new() { ["subdomain"] = "app" });
        builder.Link(frontDoor, builder.Add(Kinds.WebApp, "web"));
        builder.Link(builder.Add(Kinds.DnsZone, "domain", new() { ["zone"] = "contoso.org" }), frontDoor);

        var files = Files(builder.Sketch);
        Assert.Contains("ip_restriction_default_action = \"Deny\"", files["apps.tf"], StringComparison.Ordinal);
        Assert.Contains("x_azure_fdid = [azurerm_cdn_frontdoor_profile.edge.resource_guid]", files["apps.tf"], StringComparison.Ordinal);
        Assert.Contains("host_name                = \"app.contoso.org\"", files["edge.tf"], StringComparison.Ordinal);
        Assert.Contains("name                = \"_dnsauth.app\"", files["edge.tf"], StringComparison.Ordinal);
        Assert.Contains("cdn_frontdoor_custom_domain_ids = [azurerm_cdn_frontdoor_custom_domain.edge__domain.id]", files["edge.tf"], StringComparison.Ordinal);
        Assert.Contains("output \"domain_name_servers\"", files["outputs.tf"], StringComparison.Ordinal);
    }

    [Fact]
    public void App_to_app_passes_the_url()
    {
        var builder = new SketchBuilder();
        builder.Link(builder.Add(Kinds.WebApp, "web"), builder.Add(Kinds.WebApp, "order-api"));
        builder.Sketch.Settings.Monitoring = false;

        Assert.Contains("\"ORDER_API_URL\" = \"https://${azurerm_linux_web_app.order_api.default_hostname}\"", Files(builder.Sketch)["apps.tf"], StringComparison.Ordinal);
    }

    [Fact]
    public void Typed_text_cannot_inject_interpolation()
    {
        var builder = new SketchBuilder();
        builder.Add(Kinds.WebApp, "api", new() { ["runtime"] = "docker", ["dockerImage"] = "nginx:1.27" });

        Assert.Contains("docker_image_name   = \"nginx:1.27\"", Files(builder.Sketch)["apps.tf"], StringComparison.Ordinal);
        Assert.Equal("\"$${x}\"", Hcl.Literal("${x}"));
    }

    [Fact]
    public void Monitoring_off_removes_application_insights()
    {
        var builder = new SketchBuilder();
        builder.Add(Kinds.WebApp, "api");
        builder.Sketch.Settings.Monitoring = false;

        var files = Files(builder.Sketch);
        Assert.DoesNotContain("azurerm_application_insights", files["main.tf"], StringComparison.Ordinal);
        Assert.DoesNotContain("app_settings", files["apps.tf"], StringComparison.Ordinal);
    }
}
