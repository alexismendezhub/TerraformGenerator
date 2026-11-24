using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TerraformGenerator.Server.IntegrationTests;

/// <summary>Real HTTP requests to every endpoint group, with the AI off.</summary>
public sealed class SketchEndpointsTests : IDisposable
{
    private readonly WebApplicationFactory<Program> _factory;

    public SketchEndpointsTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Ai:Provider", "None");
        });
    }

    [Fact]
    public async Task Catalog_and_example_are_served()
    {
        using var client = _factory.CreateClient();
        using var catalog = await client.GetAsync(new Uri("/api/catalog", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);

        var example = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/example", UriKind.Relative));
        Assert.True(example.GetProperty("nodes").GetArrayLength() > 0);
    }

    [Fact]
    public async Task The_example_generates_terraform_and_downloads_as_a_zip()
    {
        using var client = _factory.CreateClient();
        var example = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/example", UriKind.Relative));

        using var generated = await client.PostAsJsonAsync(new Uri("/api/generate", UriKind.Relative), example);
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        var files = (await generated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("files");
        Assert.Contains(files.EnumerateObject(), file => file.Name.EndsWith(".tf", StringComparison.Ordinal));

        using var export = await client.PostAsJsonAsync(new Uri("/api/export", UriKind.Relative), example);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("application/zip", export.Content.Headers.ContentType?.MediaType);
        using var zip = new ZipArchive(await export.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        Assert.Contains(zip.Entries, entry => entry.FullName == "sketch.json");
    }

    [Fact]
    public async Task There_is_no_preferences_endpoint_because_the_palette_lives_in_the_browser()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(new Uri("/api/preferences/azure", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task With_the_ai_off_the_status_says_so_and_a_proposal_answers_502()
    {
        using var client = _factory.CreateClient();
        var status = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/ai/status", UriKind.Relative));
        Assert.False(status.GetProperty("enabled").GetBoolean());

        var example = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/example", UriKind.Relative));
        using var proposal = await client.PostAsJsonAsync(new Uri("/api/ai/propose", UriKind.Relative),
            new { description = "A web app with a database", current = example, startFresh = false });
        Assert.Equal(HttpStatusCode.BadGateway, proposal.StatusCode);
    }

    [Fact]
    public async Task An_empty_description_is_refused()
    {
        using var client = _factory.CreateClient();
        var example = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/example", UriKind.Relative));
        using var proposal = await client.PostAsJsonAsync(new Uri("/api/ai/propose", UriKind.Relative),
            new { description = " ", current = example, startFresh = false });
        Assert.Equal(HttpStatusCode.BadRequest, proposal.StatusCode);
    }

    [Fact]
    public async Task Unknown_api_paths_answer_404()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(new Uri("/api/does-not-exist", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public void Dispose() => _factory.Dispose();
}
