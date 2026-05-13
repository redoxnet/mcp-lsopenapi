using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

public class SearchStockToolTests
{
    static Task<HttpResponseMessage> Ok(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    });

    const string SampleBody = """
    {
      "t8436OutBlock": [
        { "hname": "삼성전자",     "shcode": "005930", "expcode": "KR7005930003", "gubun": "1", "etfgubun": "0", "spac_gubun": "N", "bu12gubun": "01" },
        { "hname": "삼성SDI",      "shcode": "006400", "expcode": "KR7006400006", "gubun": "1", "etfgubun": "0", "spac_gubun": "N", "bu12gubun": "01" },
        { "hname": "현대자동차",   "shcode": "005380", "expcode": "KR7005380001", "gubun": "1", "etfgubun": "0", "spac_gubun": "N", "bu12gubun": "01" },
        { "hname": "카카오",       "shcode": "035720", "expcode": "KR7035720002", "gubun": "1", "etfgubun": "0", "spac_gubun": "N", "bu12gubun": "01" }
      ],
      "rsp_cd": "00000",
      "rsp_msg": "정상"
    }
    """;

    [Fact]
    public async Task SearchStock_KoreanQuery_MatchesByName()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(SampleBody));

        string result = await SearchStockTool.SearchStock(client, "삼성");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("count").GetInt32().Should().Be(2);
        JsonElement first = root.GetProperty("results")[0];
        first.GetProperty("shcode").GetString().Should().Be("005930");
        first.GetProperty("name").GetString().Should().Be("삼성전자");
    }

    [Fact]
    public async Task SearchStock_LimitClamped()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(SampleBody));

        string result = await SearchStockTool.SearchStock(client, "전자", limit: 0);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("limit").GetInt32().Should().Be(1);
        root.GetProperty("count").GetInt32().Should().BeLessThanOrEqualTo(1);
    }

    [Fact]
    public async Task SearchStock_EmptyQuery_ReturnsError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(SampleBody));

        string result = await SearchStockTool.SearchStock(client, "");
        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString()
            .Should().Contain("query");
    }

    [Fact]
    public async Task SearchStock_MarketFilter_PropagatesToInBlock()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(SampleBody));

        await SearchStockTool.SearchStock(client, "삼성", market: "kospi");

        string sent = await handler.Requests[0].Content!.ReadAsStringAsync();
        sent.Should().Contain("\"gubun\":\"1\"");
    }
}
