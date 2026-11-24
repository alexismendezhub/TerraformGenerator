namespace TerraformGenerator.Domain;

// What the AI returns (structured output): a whole drawing plus what the person should check.
public sealed class ProposalDraft
{
    public string Summary { get; set; } = "";
    public ProposalSettings Settings { get; set; } = new();
    public List<ProposalNode> Nodes { get; set; } = [];
    public List<ProposalEdge> Edges { get; set; } = [];
    public List<string> Questions { get; set; } = [];
}

public sealed class ProposalSettings
{
    public string Platform { get; set; } = "";
    public string Name { get; set; } = "";
    public string Environment { get; set; } = "";
    public string Region { get; set; } = "";
    public bool Monitoring { get; set; } = true;

    // The other project settings to change (SettingKeys); the ones not listed keep their current value.
    public List<ProposalProp> Options { get; set; } = [];

    public string Option(string key) => Options.FirstOrDefault(option => option.Key == key)?.Value ?? "";

    public ProposalSettings Set(string key, string value)
    {
        Options.RemoveAll(option => option.Key == key);
        Options.Add(new ProposalProp { Key = key, Value = value });
        return this;
    }
}

public sealed class ProposalNode
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public List<ProposalProp> Props { get; set; } = [];

    // Empty when nothing needs a look; otherwise what the AI assumed and the person should confirm.
    public string Review { get; set; } = "";
}

public sealed class ProposalProp
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class ProposalEdge
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

public enum ChangeType
{
    Added,
    Changed,
    Removed,
}

public sealed record ProposalChange(ChangeType Type, string Text, string? NodeId = null);

public sealed record ReviewItem(string Text, string? NodeId = null);

// Nothing is applied until the person approves: the screen shows the proposed drawing, the changes and the review list.
public sealed record SketchProposal(
    string Summary,
    Sketch Sketch,
    IReadOnlyList<ProposalChange> Changes,
    IReadOnlyList<ReviewItem> Review,
    IReadOnlyList<SketchMessage> Messages);

public static class ProposalBuilder
{
    private const double RowHeight = 110;

    // Columns from left to right: domain, entry, apps, data, then cache, messaging and security.
    private static double Column(string kind) => kind switch
    {
        Kinds.DnsZone => 40,
        Kinds.FrontDoor => 250,
        Kinds.WebApp or Kinds.ContainerApp or Kinds.FunctionApp or Kinds.StaticWebApp => 460,
        Kinds.SqlDatabase or Kinds.Postgres or Kinds.CosmosDb or Kinds.Storage => 680,
        _ => 890,
    };

    // current gives the ids and positions to keep; baseline (the drawing before any proposal) is what the changes compare to;
    // schemas, when loaded, leave out boxes of resource types the provider does not have; layout (a saved sketch.json) puts
    // new boxes where that drawing had a box of the same name.
    public static SketchProposal Build(ProposalDraft draft, Sketch current, Sketch? baseline = null, IResourceSchemas? schemas = null, Sketch? layout = null)
    {
        var review = new List<ReviewItem>();
        var existing = current.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var sketch = new Sketch
        {
            Settings = new SketchSettings
            {
                Platform = Platforms.All.Any(platform => platform.Id == draft.Settings.Platform) ? draft.Settings.Platform : current.Settings.Platform,
                Name = Pick(draft.Settings.Name, current.Settings.Name),
                Environment = Pick(draft.Settings.Environment, current.Settings.Environment),
                Region = Pick(draft.Settings.Region, current.Settings.Region),
                Monitoring = draft.Settings.Monitoring,
                Options = new Dictionary<string, string>(current.Settings.Options, StringComparer.Ordinal),
            },
        };
        foreach (var (key, value) in draft.Settings.Options.Where(option => option.Value.Length > 0).Select(option => (option.Key, option.Value)))
        {
            var setting = Catalog.Instance.Settings.FirstOrDefault(item => item.Key == key);
            if (setting is null)
            {
                continue;
            }

            if (SketchRules.IsValid(setting, value))
            {
                sketch.Settings.Options[key] = value;
            }
            else
            {
                review.Add(new($"Kept the current \"{setting.Label}\": \"{value}\" is not one of its choices."));
            }
        }

        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var proposed in draft.Nodes)
        {
            var kind = Catalog.Find(proposed.Kind);
            if (kind is null && Kinds.IsGeneric(proposed.Kind))
            {
                var cloud = Platforms.Get(sketch.Settings.Platform);
                var type = Kinds.TypeOf(proposed.Kind);
                if (!type.StartsWith(cloud.Prefix, StringComparison.Ordinal) || (schemas is not null && schemas.Find(cloud.Id, type) is null))
                {
                    review.Add(new($"Left out \"{proposed.Name}\": {type} is not a resource of the {cloud.Label} Terraform provider."));
                    continue;
                }

                // A box from the full provider list: its fields as written; references are pointed at the final ids below.
                var genericId = existing.TryGetValue(proposed.Id, out var keptGeneric) && keptGeneric.Kind == proposed.Kind && !ids.ContainsValue(proposed.Id)
                    ? proposed.Id
                    : UniqueId(proposed.Id, ids.Values, existing.Keys);
                ids[proposed.Id] = genericId;
                var generic = new SketchNode
                {
                    Id = genericId, Kind = proposed.Kind, Name = proposed.Name,
                    Props = proposed.Props.Where(prop => prop.Key.Length > 0 && prop.Value.Length > 0).GroupBy(prop => prop.Key).ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal),
                };
                if (existing.TryGetValue(genericId, out var placed))
                {
                    generic.X = placed.X;
                    generic.Y = placed.Y;
                }

                sketch.Nodes.Add(generic);
                if (!string.IsNullOrWhiteSpace(proposed.Review))
                {
                    review.Add(new($"{proposed.Name}: {proposed.Review.Trim()}", genericId));
                }

                continue;
            }

            if (kind is null)
            {
                review.Add(new($"Left out \"{proposed.Name}\": Terraform Generator has no {proposed.Kind} box yet."));
                continue;
            }

            var id = existing.TryGetValue(proposed.Id, out var kept) && kept.Kind == proposed.Kind && !ids.ContainsValue(proposed.Id)
                ? proposed.Id
                : UniqueId(proposed.Id, ids.Values, existing.Keys);
            ids[proposed.Id] = id;

            var props = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var prop in proposed.Props)
            {
                var definition = kind.Properties.FirstOrDefault(property => property.Key == prop.Key);
                if (definition is not null && prop.Value != definition.Default)
                {
                    props[prop.Key] = prop.Value;
                }
            }

            var node = new SketchNode { Id = id, Kind = kind.Kind, Name = proposed.Name, Props = props };
            if (existing.TryGetValue(id, out var before))
            {
                node.X = before.X;
                node.Y = before.Y;
            }

            sketch.Nodes.Add(node);
            if (!string.IsNullOrWhiteSpace(proposed.Review))
            {
                review.Add(new($"{proposed.Name}: {proposed.Review.Trim()}", id));
            }
        }

        var added = sketch.Nodes.Where(node => !existing.ContainsKey(node.Id)).ToList();
        var restored = layout is null ? [] : Restore(added, layout);
        Place(added.Except(restored).ToList(), sketch.Nodes);

        // References written with the AI's ids ("ref:new-2.id") point at the final ids.
        foreach (var node in sketch.Nodes.Where(node => Kinds.IsGeneric(node.Kind)))
        {
            foreach (var (key, value) in node.Props.ToList())
            {
                if (GenericGenerator.ParseReference(value) is { } reference && ids.TryGetValue(reference.NodeId, out var finalId))
                {
                    node.Props[key] = $"{GenericGenerator.ReferencePrefix}{finalId}.{reference.Attribute}";
                }
            }
        }

        var pairs = new HashSet<(string, string)>();
        foreach (var edge in draft.Edges)
        {
            if (ids.TryGetValue(edge.From, out var from) && ids.TryGetValue(edge.To, out var to) && pairs.Add((from, to)))
            {
                var kept = current.Edges.FirstOrDefault(item => item.From == from && item.To == to);
                sketch.Edges.Add(new SketchEdge { Id = kept?.Id ?? $"ai-e{sketch.Edges.Count + 1}-{from}-{to}", From = from, To = to });
            }
        }

        review.AddRange(draft.Questions.Where(question => !string.IsNullOrWhiteSpace(question)).Select(question => new ReviewItem(question.Trim())));
        var summary = layout is null
            ? draft.Summary.Trim()
            : $"{draft.Summary.Trim()} {restored.Count} of {added.Count} boxes placed as in sketch.json.".TrimStart();
        return new SketchProposal(summary, sketch, Changes(baseline ?? current, sketch), review, SketchRules.Check(sketch));
    }

    private static string Pick(string proposed, string current) => string.IsNullOrWhiteSpace(proposed) ? current : proposed;

    private static string UniqueId(string wanted, IEnumerable<string> used, IEnumerable<string> existing)
    {
        var taken = used.Concat(existing).ToHashSet(StringComparer.Ordinal);
        var stem = "ai-" + new string((wanted.Length > 0 ? wanted : "box").Where(char.IsLetterOrDigit).Take(20).ToArray());
        var id = stem;
        for (var index = 2; taken.Contains(id); index++)
        {
            id = $"{stem}-{index}";
        }

        return id;
    }

    // Each box goes where the saved drawing had the box of the same name (and kind, when both have one of that name).
    private static List<SketchNode> Restore(List<SketchNode> added, Sketch layout)
    {
        var unused = layout.Nodes.ToList();
        var restored = new List<SketchNode>();
        foreach (var node in added)
        {
            var saved = unused.FirstOrDefault(item => item.Name == node.Name && item.Kind == node.Kind)
                ?? unused.FirstOrDefault(item => item.Name == node.Name);
            if (saved is not null)
            {
                unused.Remove(saved);
                node.X = saved.X;
                node.Y = saved.Y;
                restored.Add(node);
            }
        }

        return restored;
    }

    // New boxes go to their kind's column, below whatever is already there.
    private static void Place(List<SketchNode> added, List<SketchNode> all)
    {
        var waiting = added.ToHashSet();
        foreach (var node in added)
        {
            waiting.Remove(node);
            node.X = Column(node.Kind);
            var y = 40.0;
            while (all.Any(other => other != node && !waiting.Contains(other) && Math.Abs(other.X - node.X) < 190 && Math.Abs(other.Y - y) < 80))
            {
                y += RowHeight;
            }

            node.Y = y;
        }
    }

    public static List<ProposalChange> Changes(Sketch before, Sketch after)
    {
        var changes = new List<ProposalChange>();
        var old = before.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var names = before.Nodes.Concat(after.Nodes).GroupBy(node => node.Id).ToDictionary(group => group.Key, group => group.Last().Name, StringComparer.Ordinal);
        foreach (var node in after.Nodes)
        {
            var label = Kinds.IsGeneric(node.Kind) ? Platforms.Summarize(Kinds.TypeOf(node.Kind)).Title : Catalog.Find(node.Kind)?.Label ?? node.Kind;
            if (!old.TryGetValue(node.Id, out var previous))
            {
                changes.Add(new(ChangeType.Added, $"{label} {node.Name}", node.Id));
                continue;
            }

            var differences = new List<string>();
            if (previous.Name != node.Name)
            {
                differences.Add($"renamed from {previous.Name}");
            }

            var beforeProps = Kinds.IsGeneric(node.Kind) ? new Dictionary<string, string>(previous.Props, StringComparer.Ordinal) : Catalog.Resolve(previous);
            var afterProps = Kinds.IsGeneric(node.Kind) ? new Dictionary<string, string>(node.Props, StringComparer.Ordinal) : Catalog.Resolve(node);
            foreach (var removed in beforeProps.Keys.Where(key => !afterProps.ContainsKey(key)))
            {
                differences.Add($"{removed} cleared");
            }

            var kind = Catalog.Find(node.Kind);
            foreach (var (key, value) in afterProps.Where(pair => beforeProps.GetValueOrDefault(pair.Key) != pair.Value))
            {
                var property = kind?.Properties.FirstOrDefault(item => item.Key == key);
                var shown = property?.Options.FirstOrDefault(option => option.Value == value)?.Label ?? value;
                differences.Add($"{property?.Label ?? key}: {shown}");
            }

            if (differences.Count > 0)
            {
                changes.Add(new(ChangeType.Changed, $"{label} {node.Name}: {string.Join("; ", differences)}", node.Id));
            }
        }

        foreach (var node in before.Nodes.Where(node => after.Nodes.All(item => item.Id != node.Id)))
        {
            changes.Add(new(ChangeType.Removed, $"{(Kinds.IsGeneric(node.Kind) ? Platforms.Summarize(Kinds.TypeOf(node.Kind)).Title : Catalog.Find(node.Kind)?.Label ?? node.Kind)} {node.Name}", node.Id));
        }

        foreach (var edge in after.Edges.Where(edge => !before.Edges.Any(item => item.From == edge.From && item.To == edge.To)))
        {
            changes.Add(new(ChangeType.Added, $"Connection {names[edge.From]} -> {names[edge.To]}"));
        }

        foreach (var edge in before.Edges.Where(edge => !after.Edges.Any(item => item.From == edge.From && item.To == edge.To)))
        {
            changes.Add(new(ChangeType.Removed, $"Connection {names.GetValueOrDefault(edge.From, "?")} -> {names.GetValueOrDefault(edge.To, "?")}"));
        }

        if (before.Settings.Name != after.Settings.Name || before.Settings.Environment != after.Settings.Environment
            || before.Settings.Region != after.Settings.Region || before.Settings.Monitoring != after.Settings.Monitoring)
        {
            changes.Add(new(ChangeType.Changed, $"Project: {after.Settings.Name}, {after.Settings.Environment}, {after.Settings.Region}, monitoring {(after.Settings.Monitoring ? "on" : "off")}"));
        }

        foreach (var setting in Catalog.Instance.Settings)
        {
            var value = Catalog.Setting(after.Settings, setting.Key);
            if (Catalog.Setting(before.Settings, setting.Key) != value)
            {
                var shown = setting.Options.FirstOrDefault(option => option.Value == value)?.Label ?? value;
                changes.Add(new(ChangeType.Changed, $"Project: {setting.Label}: {shown}"));
            }
        }

        return changes;
    }
}
