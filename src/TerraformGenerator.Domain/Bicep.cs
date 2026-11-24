using System.Text;
using System.Text.RegularExpressions;

namespace TerraformGenerator.Domain;

// A small Bicep writer: values are objects, arrays or raw expressions; resources, params, vars and outputs are
// top-level statements. Output follows the Bicep formatter (two-space indents, one property per line).
public abstract class BicepValue;

public sealed class BicepRaw(string expression) : BicepValue
{
    public string Expression { get; } = expression;
}

public sealed class BicepObject : BicepValue
{
    public List<(string Key, BicepValue Value)> Properties { get; } = [];

    public BicepObject Add(string key, BicepValue value)
    {
        Properties.Add((key, value));
        return this;
    }

    public BicepObject Add(string key, string expression) => Add(key, new BicepRaw(expression));

    public BicepObject Str(string key, string value) => Add(key, Bicep.Str(value));

    // Adds what a choice adds, in the middle of a chain.
    public BicepObject With(Action<BicepObject> build)
    {
        build(this);
        return this;
    }

    public BicepObject Obj(string key, Action<BicepObject> build)
    {
        var child = new BicepObject();
        build(child);
        return Add(key, child);
    }
}

public sealed class BicepArray(IEnumerable<BicepValue> items) : BicepValue
{
    public List<BicepValue> Items { get; } = items.ToList();
}

public static partial class Bicep
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex Identifier();

    // A string with ${...} interpolations kept; quotes and backslashes escaped.
    public static string Str(string value) => "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";

    // A string the person typed: never interpolated.
    public static string Literal(string value) => Str(value).Replace("${", "\\${", StringComparison.Ordinal);

    public static BicepRaw Raw(string expression) => new(expression);

    public static BicepArray Array(IEnumerable<BicepValue> items) => new(items);

    public static BicepArray Array(params BicepValue[] items) => new(items);

    public static BicepArray Array(params string[] expressions) => new(expressions.Select(expression => new BicepRaw(expression)));

    public static BicepObject Object(Action<BicepObject> build)
    {
        var value = new BicepObject();
        build(value);
        return value;
    }

    public static string Bool(bool value) => value ? "true" : "false";

    public static string Render(BicepValue value, int depth)
    {
        var builder = new StringBuilder();
        Write(builder, value, depth);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, BicepValue value, int depth)
    {
        var indent = new string(' ', (depth + 1) * 2);
        var closing = new string(' ', depth * 2);
        switch (value)
        {
            case BicepRaw raw:
                builder.Append(raw.Expression);
                break;
            case BicepObject obj when obj.Properties.Count == 0:
                builder.Append("{}");
                break;
            case BicepObject obj:
                builder.Append("{\n");
                foreach (var (key, item) in obj.Properties)
                {
                    builder.Append(indent).Append(Identifier().IsMatch(key) || key.StartsWith('\'') ? key : Str(key)).Append(": ");
                    Write(builder, item, depth + 1);
                    builder.Append('\n');
                }

                builder.Append(closing).Append('}');
                break;
            case BicepArray array when array.Items.Count == 0:
                builder.Append("[]");
                break;
            case BicepArray array:
                builder.Append("[\n");
                foreach (var item in array.Items)
                {
                    builder.Append(indent);
                    Write(builder, item, depth + 1);
                    builder.Append('\n');
                }

                builder.Append(closing).Append(']');
                break;
        }
    }
}

// The statements of one Bicep file, in order, separated by blank lines.
public sealed class BicepFile
{
    private readonly List<string> _statements = [];

    public void Comment(string text) => _statements.Add("// " + text);

    public void Param(string name, string type, string description, string? defaultValue = null) =>
        _statements.Add($"@description({Bicep.Str(description)})\nparam {name} {type}" + (defaultValue is null ? "" : $" = {defaultValue}"));

    public void Var(string name, string expression) => _statements.Add($"var {name} = {expression}");

    public void Output(string name, string type, string expression) => _statements.Add($"output {name} {type} = {expression}");

    // A type ending in " existing" refers to a resource that is already there.
    public void Resource(string symbol, string type, BicepObject body, string? comment = null)
    {
        var existing = type.EndsWith(" existing", StringComparison.Ordinal);
        var name = existing ? type[..^" existing".Length] : type;
        var text = $"resource {symbol} '{name}'{(existing ? " existing" : "")} = {Bicep.Render(body, 0)}";
        _statements.Add(comment is null ? text : "// " + comment + "\n" + text);
    }

    public void Module(string symbol, string path, BicepObject body, string? comment = null)
    {
        var text = $"module {symbol} '{path}' = {Bicep.Render(body, 0)}";
        _statements.Add(comment is null ? text : "// " + comment + "\n" + text);
    }

    public string Render() => string.Join("\n\n", _statements) + "\n";
}
