using System.Collections.Concurrent;
using System.Text.Json;
using TerraformGenerator.Domain;

namespace TerraformGenerator.Infrastructure.Terraform;

public sealed record SchemaStatus(string Platform, bool Ready, bool Loading, int Count, string? Problem, string? Version, DateTime? UpdatedUtc);

// Every resource of the Azure, AWS and Google Terraform providers. The first time a cloud is used, terraform downloads
// its provider and prints the provider's list ("terraform providers schema -json"); the slim list is kept in
// %LOCALAPPDATA%\Terraform Generator\schemas and read from there afterwards. Refresh asks for the newest provider the version
// range allows and swaps the list in only when it was read; the old list stays if it fails.
public sealed class ProviderSchemas(ILogger<ProviderSchemas> logger) : IResourceSchemas
{
    private readonly ConcurrentDictionary<string, Dictionary<string, ResourceSchema>> _loaded = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _problems = new(StringComparer.Ordinal);

    public ResourceSchema? Find(string platform, string type) =>
        _loaded.TryGetValue(platform, out var schemas) && schemas.TryGetValue(type, out var schema) ? schema : null;

    public SchemaStatus Status(string platform)
    {
        var (version, updated) = ProviderSchemaFiles.Describe(platform, ProviderSchemaFiles.DefaultFolder(Platforms.Get(platform).Id));
        return new(
            platform,
            _loaded.ContainsKey(platform),
            _gates.TryGetValue(platform, out var gate) && gate.CurrentCount == 0,
            _loaded.TryGetValue(platform, out var schemas) ? schemas.Count : 0,
            _problems.GetValueOrDefault(platform),
            version,
            updated);
    }

    public Task<bool> EnsureAsync(string platformId, CancellationToken cancellationToken) =>
        _loaded.ContainsKey(platformId) ? Task.FromResult(true) : LoadAsync(platformId, refresh: false, cancellationToken);

    public Task<bool> RefreshAsync(string platformId, CancellationToken cancellationToken) => LoadAsync(platformId, refresh: true, cancellationToken);

    private async Task<bool> LoadAsync(string platformId, bool refresh, CancellationToken cancellationToken)
    {
        var platform = Platforms.Get(platformId);
        var gate = _gates.GetOrAdd(platform.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!refresh && _loaded.ContainsKey(platform.Id))
            {
                return true;
            }

            var folder = ProviderSchemaFiles.DefaultFolder(platform.Id);
            var schemas = refresh
                ? await ProviderSchemaFiles.RefreshAsync(platform.Id, folder, cancellationToken)
                : await ProviderSchemaFiles.LoadAsync(platform.Id, folder, cancellationToken);
            _loaded[platform.Id] = schemas;
            _problems.TryRemove(platform.Id, out _);
            logger.LogInformation("Loaded {Count} {Platform} resources", schemas.Count, platform.Id);
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(exception, "Could not load the {Platform} provider list", platform.Id);
            _problems[platform.Id] = exception.Message;
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    // Resources whose type or title holds every word of the query, the closest first.
    public IReadOnlyList<ResourceSummary> Search(string platform, string query, int take)
    {
        if (!_loaded.TryGetValue(platform, out var schemas))
        {
            return [];
        }

        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(word => word.Replace(' ', '_'))
            .ToArray();
        var prefix = Platforms.Get(platform).Prefix;
        return schemas.Keys
            .Select(type => (Type: type, Bare: type[prefix.Length..], Summary: Platforms.Summarize(type)))
            .Where(item => words.All(word => item.Bare.Contains(word, StringComparison.Ordinal) || item.Summary.Title.Contains(word, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(item => words.Length > 0 && item.Bare.StartsWith(words[0], StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(item => item.Bare.Length)
            .ThenBy(item => item.Bare, StringComparer.Ordinal)
            .Take(take)
            .Select(item => item.Summary)
            .ToList();
    }
}
