using System.Text;

namespace TerraformGenerator.Domain;

// A small HCL writer whose output matches terraform fmt: two-space indents and the = of consecutive one-line
// attributes aligned. Multi-line values (maps, nested blocks) stand on their own between blank lines.
public abstract class HclItem;

public sealed class HclAttribute(string name, string expression) : HclItem
{
    public string Name { get; } = name;
    public string Expression { get; } = expression;
}

public sealed class HclMap(string name, IReadOnlyList<KeyValuePair<string, string>> entries) : HclItem
{
    public string Name { get; } = name;
    public IReadOnlyList<KeyValuePair<string, string>> Entries { get; } = entries;
}

public sealed class HclComment(string text) : HclItem
{
    public string Text { get; } = text;
}

public sealed class HclBlank : HclItem;

// Lines written as they are (HCL the person typed for a field Terraform Generator does not list), indented to the block.
public sealed class HclRawText(string text) : HclItem
{
    public string Text { get; } = text;
}

public sealed class HclBlock(string type, params string[] labels) : HclItem
{
    public string Type { get; } = type;
    public IReadOnlyList<string> Labels { get; } = labels;
    public List<HclItem> Body { get; } = [];

    public HclBlock Attr(string name, string expression)
    {
        Body.Add(new HclAttribute(name, expression));
        return this;
    }

    public HclBlock Str(string name, string value) => Attr(name, Hcl.Str(value));

    public HclBlock Map(string name, IEnumerable<KeyValuePair<string, string>> entries)
    {
        Body.Add(new HclMap(name, entries.ToList()));
        return this;
    }

    public HclBlock Block(string type, Action<HclBlock> build, params string[] labels)
    {
        var block = new HclBlock(type, labels);
        build(block);
        Body.Add(block);
        return this;
    }

    public HclBlock Comment(string text)
    {
        Body.Add(new HclComment(text));
        return this;
    }

    // Adds what a choice adds, in the middle of a chain.
    public HclBlock With(Action<HclBlock> build)
    {
        build(this);
        return this;
    }
}

public static class Hcl
{
    // A quoted string. ${...} stays an interpolation; quotes, backslashes and %{ are escaped.
    public static string Str(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("%{", "%%{", StringComparison.Ordinal) + "\"";

    // A literal value with no interpolation at all (for text the person typed).
    public static string Literal(string value) => Str(value.Replace("${", "$${", StringComparison.Ordinal));

    public static string List(IEnumerable<string> expressions) => "[" + string.Join(", ", expressions) + "]";

    public static string Bool(bool value) => value ? "true" : "false";

    public static string Render(IEnumerable<HclItem> topLevel)
    {
        var builder = new StringBuilder();
        RenderBody(builder, topLevel.ToList(), 0);
        return builder.ToString().TrimEnd('\n') + "\n";
    }

    private static void RenderBody(StringBuilder builder, List<HclItem> items, int depth)
    {
        var indent = new string(' ', depth * 2);
        var previousWasMultiline = false;
        var index = 0;
        while (index < items.Count)
        {
            var item = items[index];
            var multiline = item is HclBlock or HclMap;
            var afterSeparator = index > 0 && items[index - 1] is HclComment or HclBlank;
            if (index > 0 && !afterSeparator && (multiline || previousWasMultiline))
            {
                builder.Append('\n');
            }

            switch (item)
            {
                case HclAttribute:
                    var run = new List<HclAttribute>();
                    while (index < items.Count && items[index] is HclAttribute attribute)
                    {
                        run.Add(attribute);
                        index++;
                    }

                    var width = run.Max(attribute => attribute.Name.Length);
                    foreach (var attribute in run)
                    {
                        builder.Append(indent).Append(attribute.Name.PadRight(width)).Append(" = ").Append(attribute.Expression).Append('\n');
                    }

                    previousWasMultiline = false;
                    continue;
                case HclBlank:
                    builder.Append('\n');
                    break;
                case HclComment comment:
                    builder.Append(indent).Append("# ").Append(comment.Text).Append('\n');
                    break;
                case HclMap map:
                    RenderMap(builder, map, indent);
                    break;
                case HclRawText raw:
                    foreach (var line in raw.Text.Replace("\r", "", StringComparison.Ordinal).Split('\n'))
                    {
                        builder.Append(line.Trim().Length == 0 ? "" : indent + line.TrimEnd()).Append('\n');
                    }

                    break;
                case HclBlock block:
                    builder.Append(indent).Append(block.Type);
                    foreach (var label in block.Labels)
                    {
                        builder.Append(" \"").Append(label).Append('"');
                    }

                    if (block.Body.Count == 0)
                    {
                        builder.Append(" {}\n");
                    }
                    else
                    {
                        builder.Append(" {\n");
                        RenderBody(builder, block.Body, depth + 1);
                        builder.Append(indent).Append("}\n");
                    }

                    break;
            }

            previousWasMultiline = multiline;
            index++;
        }
    }

    private static void RenderMap(StringBuilder builder, HclMap map, string indent)
    {
        if (map.Entries.Count == 0)
        {
            builder.Append(indent).Append(map.Name).Append(" = {}\n");
            return;
        }

        builder.Append(indent).Append(map.Name).Append(" = {\n");
        var width = map.Entries.Max(entry => Str(entry.Key).Length);
        foreach (var entry in map.Entries)
        {
            builder.Append(indent).Append("  ").Append(Str(entry.Key).PadRight(width)).Append(" = ").Append(entry.Value).Append('\n');
        }

        builder.Append(indent).Append("}\n");
    }
}
