using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

public sealed class GetStockThemesToolTests
{
    static Task<HttpResponseMessage> Ok(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    });

    [Fact]
    public async Task GetStockThemes_HappyPath_ReturnsThemeArray()
    {
        const string body = """
        {
          "rsp_cd": "00000",
          "rsp_msg": "조회완료",
          "t1532OutBlock": [
            { "tmname": "반도체", "avgdiff": "1.25", "tmcode": "0011" },
            { "tmname": "AI",     "avgdiff": "0.65", "tmcode": "0100" }
          ]
        }
        """;
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(body));

        string result = await GetStockThemesTool.GetStockThemes(client, "005930");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t1532");
        (await handler.Requests[0].Content!.ReadAsStringAsync()).Should().Contain("\"shcode\":\"005930\"");

        root.GetProperty("shcode").GetString().Should().Be("005930");
        root.GetProperty("count").GetInt32().Should().Be(2);
        JsonElement themes = root.GetProperty("themes");
        themes.GetArrayLength().Should().Be(2);
        themes[0].GetProperty("theme_code").GetString().Should().Be("0011");
        themes[0].GetProperty("theme_name").GetString().Should().Be("반도체");
        themes[0].GetProperty("avg_change_pct").GetDouble().Should().BeApproximately(1.25, 1e-2);
    }

    [Fact]
    public async Task GetStockThemes_EmptyArray_IsValid()
    {
        const string body = """{"rsp_cd":"00000","rsp_msg":"조회완료","t1532OutBlock":[]}""";
        var (client, _) = TestClientFactory.Create((_, _) => Ok(body));

        string result = await GetStockThemesTool.GetStockThemes(client, "005930");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("count").GetInt32().Should().Be(0);
        root.GetProperty("themes").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task GetStockThemes_InvalidShcode_ReturnsValidationError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok("{\"rsp_cd\":\"00000\"}"));

        string result = await GetStockThemesTool.GetStockThemes(client, "12");

        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString()
            .Should().Contain("6-character");
    }

    [Fact]
    public async Task GetStockThemes_BusinessError_Surfaces()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok("""{"rsp_cd":"99999","rsp_msg":"잘못된 종목"}"""));

        string result = await GetStockThemesTool.GetStockThemes(client, "999999");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("business-level");
        root.GetProperty("details").GetProperty("rsp_msg").GetString().Should().Be("잘못된 종목");
    }
}
