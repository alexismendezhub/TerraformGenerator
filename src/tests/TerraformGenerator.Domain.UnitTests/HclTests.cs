using TerraformGenerator.Domain;

namespace TerraformGenerator.Domain.UnitTests;

public sealed class HclTests
{
    [Fact]
    public void Aligns_consecutive_attributes_and_separates_blocks()
    {
        var text = Hcl.Render(
        [
            new HclBlock("resource", "a", "b")
                .Str("name", "x")
                .Attr("location", "var.location")
                .Block("identity", identity => identity.Str("type", "SystemAssigned"))
                .Attr("tags", "var.tags"),
            new HclBlock("locals").Attr("a", "1"),
        ]);

        Assert.Equal(
            "resource \"a\" \"b\" {\n" +
            "  name     = \"x\"\n" +
            "  location = var.location\n" +
            "\n" +
            "  identity {\n" +
            "    type = \"SystemAssigned\"\n" +
            "  }\n" +
            "\n" +
            "  tags = var.tags\n" +
            "}\n" +
            "\n" +
            "locals {\n" +
            "  a = 1\n" +
            "}\n",
            text);
    }

    [Fact]
    public void Renders_maps_with_aligned_quoted_keys()
    {
        var text = Hcl.Render([new HclBlock("x").Attr("a", "1").Map("settings", [new("A", "1"), new("LONGER", "2")])]);

        Assert.Equal("x {\n  a = 1\n\n  settings = {\n    \"A\"      = 1\n    \"LONGER\" = 2\n  }\n}\n", text);
    }

    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("c:\\x", "\"c:\\\\x\"")]
    [InlineData("%{if}", "\"%%{if}\"")]
    public void Escapes_strings(string value, string expected) => Assert.Equal(expected, Hcl.Str(value));

    [Fact]
    public void Literal_never_interpolates() => Assert.Equal("\"$${var.x}\"", Hcl.Literal("${var.x}"));

    [Fact]
    public void Empty_blocks_and_maps_stay_on_one_line()
    {
        var text = Hcl.Render([new HclBlock("data", "azurerm_client_config", "current"), new HclBlock("x").Map("m", [])]);

        Assert.Equal("data \"azurerm_client_config\" \"current\" {}\n\nx {\n  m = {}\n}\n", text);
    }
}
