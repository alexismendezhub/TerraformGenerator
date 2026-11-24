using System.IO.Compression;
using System.Text.Json;
using TerraformGenerator.Application.Ai;
using TerraformGenerator.Domain;
using TerraformGenerator.Infrastructure.Terraform;
using TerraformGenerator.Server.Contracts;

namespace TerraformGenerator.Server.Endpoints;

/// <summary>Every <c>/api</c> endpoint of the drawing tool.</summary>
public static class SketchEndpoints
{
    public static IEndpointRouteBuilder MapSketchEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");
        api.MapGet("/catalog", () => Catalog.Instance);
        api.MapGet("/example", (string? name) => name switch
        {
            "everything" => Examples.Everything(),
            "aws" => Examples.Aws(),
            "gcp" => Examples.Gcp(),
            _ => Examples.WebApiSql(),
        });
        // The three clouds, and every resource of each one's Terraform provider (loaded the first time a cloud is used).
        api.MapGet("/platforms", () => Platforms.All);

        api.MapGet("/schemas/{platform}/status", async (string platform, ProviderSchemas schemas, CancellationToken cancellationToken) =>
        {
            await schemas.EnsureAsync(platform, cancellationToken);
            return schemas.Status(platform);
        });
        api.MapPost("/schemas/{platform}/refresh", async (string platform, ProviderSchemas schemas, CancellationToken cancellationToken) =>
        {
            await schemas.RefreshAsync(platform, cancellationToken);
            return schemas.Status(platform);
        });
        api.MapGet("/schemas/{platform}/search", async (string platform, string? q, ProviderSchemas schemas, CancellationToken cancellationToken) =>
        {
            await schemas.EnsureAsync(platform, cancellationToken);
            return schemas.Search(platform, q ?? "", 40);
        });
        api.MapGet("/schemas/{platform}/types/{type}", async (string platform, string type, ProviderSchemas schemas, CancellationToken cancellationToken) =>
        {
            await schemas.EnsureAsync(platform, cancellationToken);
            return schemas.Find(platform, type) is { } schema ? Results.Ok(schema) : Results.NotFound();
        });


        api.MapPost("/generate", (Sketch sketch, ProviderSchemas schemas, TerraformFormatter formatter, CancellationToken cancellationToken) => Generate(sketch, schemas, formatter, cancellationToken));
        api.MapPost("/validate", async (Sketch sketch, TerraformRunner runner, ProviderSchemas schemas, TerraformFormatter formatter, CancellationToken cancellationToken) =>
        {
            var result = await Generate(sketch, schemas, formatter, cancellationToken);
            if (result.HasErrors)
            {
                return Results.BadRequest(result.Messages);
            }

            return Results.Ok(await runner.CheckAsync(result.Files, cancellationToken));
        });
        api.MapPost("/export", async (Sketch sketch, ProviderSchemas schemas, TerraformFormatter formatter, CancellationToken cancellationToken) =>
        {
            var result = await Generate(sketch, schemas, formatter, cancellationToken);
            if (result.HasErrors)
            {
                return Results.BadRequest(result.Messages);
            }

            var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                // The drawing goes next to the code, and into each environment's folder so importing that folder keeps the positions.
                var drawing = JsonSerializer.Serialize(sketch, SketchJson.Indented);
                var folders = result.Files.Keys
                    .Where(name => name.StartsWith("environments/", StringComparison.Ordinal))
                    .Select(name => string.Join('/', name.Split('/').Take(2)) + "/")
                    .Distinct(StringComparer.Ordinal);
                foreach (var (name, content) in result.Files.Append(new("sketch.json", drawing)).Concat(folders.Select(folder => new KeyValuePair<string, string>($"{folder}sketch.json", drawing))))
                {
                    using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                    writer.Write(content);
                }
            }

            buffer.Position = 0;
            return Results.File(buffer, "application/zip", $"{sketch.Settings.Name}-{sketch.Settings.Environment}-infrastructure.zip");
        });

        // The AI proposes a drawing; nothing changes until the person approves it on the screen.
        api.MapGet("/ai/status", (ISketchProposer proposer) => proposer.Status);
        api.MapPost("/ai/propose", async (ProposalRequest request, ISketchProposer proposer, ProviderSchemas schemas, CancellationToken cancellationToken) =>
        {
            var description = request.Description?.Trim() ?? "";
            if (description.Length is 0 or > ProposalRequest.MaxDescription)
            {
                return Results.BadRequest(new { problem = $"Describe what you need in 1 to {ProposalRequest.MaxDescription} characters." });
            }

            var current = request.StartFresh ? new Sketch { Settings = request.Current.Settings } : request.Current;
            try
            {
                var draft = await proposer.ProposeAsync(description, current, cancellationToken);
                return Results.Ok(ProposalBuilder.Build(draft, current, request.Baseline, await DraftSchemas(draft, current, schemas, cancellationToken)));
            }
            catch (ProposalException exception)
            {
                return Results.Json(new { problem = exception.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // The AI reviews the drawing and suggests improvements; one with a change is applied as a proposal on the screen.
        api.MapPost("/ai/recommend", async (RecommendRequest request, ISketchProposer proposer, ProviderSchemas schemas, CancellationToken cancellationToken) =>
        {
            try
            {
                var language = string.IsNullOrWhiteSpace(request.Language) ? "English" : request.Language.Trim()[..Math.Min(40, request.Language.Trim().Length)];
                var ready = request.Sketch.Nodes.Any(node => Kinds.IsGeneric(node.Kind)) && await schemas.EnsureAsync(request.Sketch.Settings.Platform, cancellationToken);
                return Results.Ok(await proposer.RecommendAsync(request.Sketch, SketchRules.Check(request.Sketch, ready ? schemas : null), language, cancellationToken));
            }
            catch (ProposalException exception)
            {
                return Results.Json(new { problem = exception.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Existing Terraform becomes a proposed drawing, read by rules or by the AI; the person approves it like any proposal.
        api.MapPost("/import", async (ImportRequest request, ISketchProposer proposer, ProviderSchemas schemas, CancellationToken cancellationToken) =>
        {
            var terraform = request.Terraform ?? "";
            if (terraform.Trim().Length == 0 || terraform.Length > ImportRequest.MaxLength)
            {
                return Results.BadRequest(new { problem = $"Choose .tf files with Terraform, up to {ImportRequest.MaxLength / 1_000_000} MB in all." });
            }

            var empty = new Sketch { Settings = request.Current.Settings };
            try
            {
                var draft = request.UseAi ? await proposer.ImportAsync(terraform, empty, cancellationToken) : TerraformImporter.Read(terraform);
                return Results.Ok(ProposalBuilder.Build(draft, empty, request.Current, await DraftSchemas(draft, empty, schemas, cancellationToken), request.Layout));
            }
            catch (ProposalException exception)
            {
                return Results.Json(new { problem = exception.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        return app;
    }

    // Generating needs the provider list when the drawing has boxes from it.
    private static async Task<GenerationResult> Generate(Sketch sketch, ProviderSchemas schemas, TerraformFormatter formatter, CancellationToken cancellationToken)
    {
        var generic = sketch.Nodes.Any(node => Kinds.IsGeneric(node.Kind));
        var ready = generic && await schemas.EnsureAsync(sketch.Settings.Platform, cancellationToken);
        var result = SketchGenerator.Generate(sketch, ready ? schemas : null);
        return await formatter.FormatAsync(result, cancellationToken);
    }

    // The provider list of the cloud a proposal is for, when it has boxes from it and the list loads.
    private static async Task<IResourceSchemas?> DraftSchemas(ProposalDraft draft, Sketch current, ProviderSchemas schemas, CancellationToken cancellationToken)
    {
        var platform = Platforms.All.Any(item => item.Id == draft.Settings.Platform) ? draft.Settings.Platform : current.Settings.Platform;
        return draft.Nodes.Any(node => Kinds.IsGeneric(node.Kind)) && await schemas.EnsureAsync(platform, cancellationToken) ? schemas : null;
    }
}
