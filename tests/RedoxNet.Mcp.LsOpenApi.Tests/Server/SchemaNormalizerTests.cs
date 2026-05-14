using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using RedoxNet.Mcp.LsOpenApi.Server;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Server;

public class SchemaNormalizerTests
{
    static Tool BuildTool(string schemaJson)
    {
        return new Tool
        {
            Name = "probe",
            Description = "schema-normalizer test fixture",
            InputSchema = JsonSerializer.Deserialize<JsonElement>(schemaJson),
        };
    }

    [Fact]
    public void NormalizeInputSchema_NullableArrayProperty_CollapsesToPlainArray()
    {
        string schemaJson = """
        {
          "type": "object",
          "properties": {
            "indicators": {
              "type": ["array","null"],
              "items": { "type": ["string","null"] },
              "default": null
            }
          },
          "required": []
        }
        """;
        Tool tool = BuildTool(schemaJson);

        SchemaNormalizer.NormalizeInputSchema(tool);

        JsonElement indicators = tool.InputSchema.GetProperty("properties").GetProperty("indicators");
        indicators.GetProperty("type").GetString().Should().Be("array");
        indicators.GetProperty("items").GetProperty("type").GetString().Should().Be("string");
        indicators.TryGetProperty("default", out _).Should().BeFalse("default:null no longer matches a non-nullable type");
    }

    [Fact]
    public void NormalizeInputSchema_NullableStringProperty_CollapsesToPlainString()
    {
        string schemaJson = """
        {
          "type": "object",
          "properties": {
            "from": { "type": ["string","null"], "default": null }
          },
          "required": []
        }
        """;
        Tool tool = BuildTool(schemaJson);

        SchemaNormalizer.NormalizeInputSchema(tool);

        JsonElement from = tool.InputSchema.GetProperty("properties").GetProperty("from");
        from.GetProperty("type").GetString().Should().Be("string");
        from.TryGetProperty("default", out _).Should().BeFalse();
    }

    [Fact]
    public void NormalizeInputSchema_MultiNonNullType_KeepsArrayMinusNull()
    {
        // ["string","number","null"] becomes ["string","number"]
        string schemaJson = """
        {
          "type": "object",
          "properties": {
            "mixed": { "type": ["string","number","null"] }
          },
          "required": []
        }
        """;
        Tool tool = BuildTool(schemaJson);

        SchemaNormalizer.NormalizeInputSchema(tool);

        JsonElement mixed = tool.InputSchema.GetProperty("properties").GetProperty("mixed");
        mixed.GetProperty("type").ValueKind.Should().Be(JsonValueKind.Array);
        mixed.GetProperty("type").EnumerateArray()
            .Select(e => e.GetString())
            .Should().BeEquivalentTo(new[] { "string", "number" });
    }

    [Fact]
    public void NormalizeInputSchema_PlainTypeProperty_Unchanged()
    {
        string schemaJson = """
        {
          "type": "object",
          "properties": {
            "shcode": { "type": "string", "description": "6-digit code" }
          },
          "required": ["shcode"]
        }
        """;
        Tool tool = BuildTool(schemaJson);
        string before = tool.InputSchema.GetRawText();

        SchemaNormalizer.NormalizeInputSchema(tool);

        // Plain "type": "string" must round-trip unchanged.
        JsonElement shcode = tool.InputSchema.GetProperty("properties").GetProperty("shcode");
        shcode.GetProperty("type").GetString().Should().Be("string");
        shcode.GetProperty("description").GetString().Should().Be("6-digit code");
    }

    [Fact]
    public void NormalizeInputSchema_TypeArrayWithoutNull_Untouched()
    {
        string schemaJson = """
        {
          "type": "object",
          "properties": {
            "mixed": { "type": ["string","number"] }
          },
          "required": []
        }
        """;
        Tool tool = BuildTool(schemaJson);

        SchemaNormalizer.NormalizeInputSchema(tool);

        JsonElement mixed = tool.InputSchema.GetProperty("properties").GetProperty("mixed");
        mixed.GetProperty("type").EnumerateArray()
            .Select(e => e.GetString())
            .Should().BeEquivalentTo(new[] { "string", "number" });
    }

    [Fact]
    public void NormalizeInputSchema_OnlyNullType_LeftAlone()
    {
        // ["null"] is a degenerate schema. Don't pretend to fix it; just don't crash.
        string schemaJson = """
        {
          "type": "object",
          "properties": {
            "void_param": { "type": ["null"] }
          },
          "required": []
        }
        """;
        Tool tool = BuildTool(schemaJson);

        SchemaNormalizer.NormalizeInputSchema(tool);

        JsonElement vp = tool.InputSchema.GetProperty("properties").GetProperty("void_param");
        vp.GetProperty("type").EnumerateArray()
            .Select(e => e.GetString())
            .Should().BeEquivalentTo(new[] { "null" });
    }

    [Fact]
    public void NormalizeInputSchema_DefaultNotNull_Preserved()
    {
        // type rewrite must not nuke an explicit non-null default.
        string schemaJson = """
        {
          "type": "object",
          "properties": {
            "count": { "type": ["integer","null"], "default": 60 }
          },
          "required": []
        }
        """;
        Tool tool = BuildTool(schemaJson);

        SchemaNormalizer.NormalizeInputSchema(tool);

        JsonElement count = tool.InputSchema.GetProperty("properties").GetProperty("count");
        count.GetProperty("type").GetString().Should().Be("integer");
        count.GetProperty("default").GetInt32().Should().Be(60);
    }

    [Fact]
    public void NormalizeInputSchema_NestedItemsRecursion_NormalizesInnerType()
    {
        // Even when the outer "type" is non-nullable, an inner items.type
        // carrying "null" must still get cleaned up.
        string schemaJson = """
        {
          "type": "object",
          "properties": {
            "tags": {
              "type": "array",
              "items": { "type": ["string","null"] }
            }
          },
          "required": []
        }
        """;
        Tool tool = BuildTool(schemaJson);

        SchemaNormalizer.NormalizeInputSchema(tool);

        JsonElement tags = tool.InputSchema.GetProperty("properties").GetProperty("tags");
        tags.GetProperty("type").GetString().Should().Be("array");
        tags.GetProperty("items").GetProperty("type").GetString().Should().Be("string");
    }

    [Fact]
    public void NormalizeInputSchema_NoProperties_NoThrow()
    {
        // The SDK supplies a default {"type":"object"} schema when a tool has
        // no parameters. The normalizer must accept that gracefully.
        Tool tool = new() { Name = "no_params" };

        Action act = () => SchemaNormalizer.NormalizeInputSchema(tool);
        act.Should().NotThrow();
    }
}
