using System.Text.Json;
using System.Text.Json.Serialization;
using TerraformGenerator.Infrastructure;
using TerraformGenerator.Server.Endpoints;

var builder = WebApplication.CreateBuilder(args);
if (string.IsNullOrEmpty(builder.Configuration["urls"]) && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    // The tool runs terraform on this computer, so it listens only here.
    builder.WebHost.UseUrls("http://localhost:5080");
}

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapSketchEndpoints();
app.MapFallback("/api/{**rest}", () => Results.NotFound());
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program;
