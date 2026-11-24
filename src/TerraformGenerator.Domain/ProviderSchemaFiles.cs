using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerraformGenerator.Domain;

// Downloads a cloud's Terraform provider once and keeps the slim list of its resources in a folder
// (resources.json). The server and the tests both read it from there.
public static partial class ProviderSchemaFiles
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex(@"provider ""registry\.terraform\.io/([^""]+)""\s*\{\s*version\s*=\s*""([^""]+)""")]
    private static partial Regex LockedProvider();

    public static string DefaultFolder(string platform) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TerraformGenerator", "schemas", platform);

    public static async Task<Dictionary<string, ResourceSchema>> LoadAsync(string platformId, string folder, CancellationToken cancellationToken)
    {
        var platform = Platforms.Get(platformId);
        var slim = Path.Combine(folder, "resources.json");
        if (!File.Exists(slim))
        {
            await DownloadAsync(platform, folder, slim, cancellationToken);
        }

        await using var stream = File.OpenRead(slim);
        var schemas = await JsonSerializer.DeserializeAsync<Dictionary<string, ResourceSchema>>(stream, Json, cancellationToken) ?? [];
        return new Dictionary<string, ResourceSchema>(schemas, StringComparer.Ordinal);
    }

    // Asks terraform for the newest provider the version range allows and writes the list again. The list already
    // in the folder is replaced only once the new one has been read, so a failed refresh keeps it.
    public static async Task<Dictionary<string, ResourceSchema>> RefreshAsync(string platformId, string folder, CancellationToken cancellationToken)
    {
        var platform = Platforms.Get(platformId);
        await DownloadAsync(platform, folder, Path.Combine(folder, "resources.json"), cancellationToken, upgrade: true);
        return await LoadAsync(platform.Id, folder, cancellationToken);
    }

    // The provider version the list in the folder came from (terraform's lock file), and when the list was written.
    public static (string? Version, DateTime? UpdatedUtc) Describe(string platformId, string folder)
    {
        var platform = Platforms.Get(platformId);
        var slim = Path.Combine(folder, "resources.json");
        var updated = File.Exists(slim) ? File.GetLastWriteTimeUtc(slim) : (DateTime?)null;
        var lockFile = Path.Combine(folder, ".terraform.lock.hcl");
        if (!File.Exists(lockFile))
        {
            return (null, updated);
        }

        var version = LockedProvider().Matches(File.ReadAllText(lockFile))
            .FirstOrDefault(match => match.Groups[1].Value == platform.Source)?.Groups[2].Value;
        return (version, updated);
    }

    private static async Task DownloadAsync(Platform platform, string folder, string slim, CancellationToken cancellationToken, bool upgrade = false)
    {
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "versions.tf"),
            $"terraform {{\n  required_providers {{\n    {platform.Provider} = {{\n      source  = \"{platform.Source}\"\n      version = \"{platform.Version}\"\n    }}\n  }}\n}}\n",
            cancellationToken);
        await RunAsync(folder, upgrade ? "init -input=false -no-color -upgrade" : "init -input=false -no-color", null, cancellationToken);
        var raw = Path.Combine(folder, "schema.json");
        await RunAsync(folder, "providers schema -json", raw, cancellationToken);
        using (var document = JsonDocument.Parse(await File.ReadAllBytesAsync(raw, cancellationToken)))
        {
            var schemas = SchemaReader.Read(platform.Id, document);
            await using var output = File.Create(slim);
            await JsonSerializer.SerializeAsync(output, schemas, Json, cancellationToken);
        }

        File.Delete(raw);
    }

    private static async Task RunAsync(string folder, string arguments, string? outputFile, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("terraform", arguments)
        {
            WorkingDirectory = folder,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TerraformGenerator", "plugin-cache");
        Directory.CreateDirectory(cache);
        start.Environment["TF_PLUGIN_CACHE_DIR"] = cache;
        start.Environment["CHECKPOINT_DISABLE"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("terraform did not start");
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        if (outputFile is not null)
        {
            await using var file = File.Create(outputFile);
            await process.StandardOutput.BaseStream.CopyToAsync(file, cancellationToken);
        }
        else
        {
            await process.StandardOutput.ReadToEndAsync(cancellationToken);
        }

        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"terraform {arguments} failed: {(await error).Trim()}");
        }
    }
}
