using TerraformGenerator.Domain;

namespace TerraformGenerator.Domain.UnitTests;

// Small drawings for the tests: Add returns the box so arrows can refer to it.
internal sealed class SketchBuilder
{
    public Sketch Sketch { get; } = new();

    public SketchBuilder Environment(string environment)
    {
        Sketch.Settings.Environment = environment;
        return this;
    }

    public SketchNode Add(string kind, string name, Dictionary<string, string>? props = null)
    {
        var node = new SketchNode { Id = $"id-{name}", Kind = kind, Name = name, Props = props ?? [] };
        Sketch.Nodes.Add(node);
        return node;
    }

    public SketchBuilder Link(SketchNode from, SketchNode to)
    {
        Sketch.Edges.Add(new SketchEdge { Id = $"{from.Name}->{to.Name}", From = from.Id, To = to.Id });
        return this;
    }
}
