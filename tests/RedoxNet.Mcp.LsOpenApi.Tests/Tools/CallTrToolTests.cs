using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Catalog;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

/// <summary>
/// Pins <see cref="CallTrTool"/> against the two shapes the MCP C# SDK 1.2
/// can produce for a <c>JsonElement</c> parameter: a real object payload,
/// and a JSON-stringified payload that some client/LLM combos emit when
/// the auto-generated schema is ambiguous about object vs. string.
/// </summary>
public class CallTrToolTests
{
    static Task<HttpResponseMessage> Ok(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    });

    const string DummyT1101Response = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "정상",
      "t1101OutBlock": { "shcode": "005930", "price": 70000 }
    }
    """;

    [Fact]
    public async Task CallTr_InBlockAsObject_ForwardsAndReturnsRawBody()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(DummyT1101Response));
        JsonElement inBlock = JsonDocument.Parse("""{ "shcode": "005930" }""").RootElement;

        string result = await CallTrTool.CallTr(client, TrCatalog.Default, "t1101", inBlock);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("rsp_cd").GetString().Should().Be("00000");
        root.GetProperty("is_success").GetBoolean().Should().BeTrue();
        root.GetProperty("body").GetProperty("t1101OutBlock").GetProperty("price").GetInt64().Should().Be(70000);

        string sentBody = await handler.Requests[0].Content!.ReadAsStringAsync();
        sentBody.Should().Contain("\"t1101InBlock\":{\"shcode\":\"005930\"}");
    }

    [Fact]
    public async Task CallTr_InBlockAsJsonEncodedString_ParsedAndForwarded()
    {
        // Simulates the client-side serialization the Claude Code E2E session
        // exhibited on 2026-05-13 — the LLM emitted a stringified object
        // ("{\"shcode\":\"069500\"}") and the previous tool implementation
        // rejected it with "in_block must be a JSON object."
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(DummyT1101Response));
        JsonElement inBlock = JsonDocument.Parse(
            JsonSerializer.Serialize("""{ "shcode": "005930" }""")).RootElement;
        inBlock.ValueKind.Should().Be(JsonValueKind.String); // pre-condition

        string result = await CallTrTool.CallTr(client, TrCatalog.Default, "t1101", inBlock);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("rsp_cd").GetString().Should().Be("00000");
        string sentBody = await handler.Requests[0].Content!.ReadAsStringAsync();
        sentBody.Should().Contain("\"shcode\":\"005930\"");
    }

    [Fact]
    public async Task CallTr_InBlockAsStringNotJson_ReturnsParseError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(DummyT1101Response));
        JsonElement inBlock = JsonDocument.Parse("\"not-json\"").RootElement;

        string result = await CallTrTool.CallTr(client, TrCatalog.Default, "t1101", inBlock);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.TryGetProperty("error", out _).Should().BeTrue();
    }

    [Fact]
    public async Task CallTr_InBlockAsArray_ReturnsTypeError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(DummyT1101Response));
        JsonElement inBlock = JsonDocument.Parse("[1,2,3]").RootElement;

        string result = await CallTrTool.CallTr(client, TrCatalog.Default, "t1101", inBlock);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        string error = root.GetProperty("error").GetString()!;
        error.Should().Contain("Array");
    }

    [Fact]
    public async Task CallTr_UnknownTr_RejectsBeforeHttpCall()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(DummyT1101Response));
        JsonElement inBlock = JsonDocument.Parse("""{ "shcode": "005930" }""").RootElement;

        string result = await CallTrTool.CallTr(client, TrCatalog.Default, "tBOGUS", inBlock);

        handler.Requests.Should().BeEmpty();
        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString()
            .Should().Contain("tBOGUS");
    }
}
