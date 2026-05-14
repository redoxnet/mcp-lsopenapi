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
    public async Task SearchStock_EmptyKeyword_ReturnsError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(SampleBody));

        string result = await SearchStockTool.SearchStock(client, "");
        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString()
            .Should().Contain("keyword");
    }

    [Fact]
    public async Task SearchStock_MarketFilter_PropagatesToInBlock()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(SampleBody));

        await SearchStockTool.SearchStock(client, "삼성", market: "kospi");

        string sent = await handler.Requests[0].Content!.ReadAsStringAsync();
        sent.Should().Contain("\"gubun\":\"1\"");
    }

    const string MixedBody = """
    {
      "t8436OutBlock": [
        { "hname": "바이오노트",       "shcode": "377740", "expcode": "KR7377740004", "gubun": "1", "etfgubun": "0", "spac_gubun": "N", "bu12gubun": "01" },
        { "hname": "KODEX 바이오",     "shcode": "244620", "expcode": "KR7244620008", "gubun": "1", "etfgubun": "1", "spac_gubun": "N", "bu12gubun": "01" },
        { "hname": "TIGER 헬스케어",   "shcode": "143860", "expcode": "KR7143860007", "gubun": "1", "etfgubun": "1", "spac_gubun": "N", "bu12gubun": "01" },
        { "hname": "바이오플러스",     "shcode": "099430", "expcode": "KR7099430002", "gubun": "2", "etfgubun": "0", "spac_gubun": "N", "bu12gubun": "01" }
      ],
      "rsp_cd": "00000",
      "rsp_msg": "정상"
    }
    """;

    [Fact]
    public async Task SearchStock_InstrumentAll_ReturnsEverything()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(MixedBody));

        string result = await SearchStockTool.SearchStock(client, "바이오");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("instrument_filter").GetString().Should().Be("all");
        // "바이오" matches "바이오노트", "KODEX 바이오", "바이오플러스" (not "TIGER 헬스케어").
        root.GetProperty("count").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task SearchStock_InstrumentStock_ExcludesEtfs()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(MixedBody));

        string result = await SearchStockTool.SearchStock(client, "바이오", instrument: "stock");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("instrument_filter").GetString().Should().Be("stock");
        root.GetProperty("count").GetInt32().Should().Be(2);
        foreach (JsonElement r in root.GetProperty("results").EnumerateArray())
            r.GetProperty("etf").GetString().Should().Be("0");
    }

    [Fact]
    public async Task SearchStock_InstrumentEtf_KeepsOnlyEtfs()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(MixedBody));

        string result = await SearchStockTool.SearchStock(client, "바이오", instrument: "etf");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("instrument_filter").GetString().Should().Be("etf");
        root.GetProperty("count").GetInt32().Should().Be(1);
        root.GetProperty("results")[0].GetProperty("name").GetString().Should().Be("KODEX 바이오");
    }

    [Fact]
    public async Task SearchStock_UnknownInstrument_ReturnsError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(MixedBody));

        string result = await SearchStockTool.SearchStock(client, "바이오", instrument: "weird");
        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString()
            .Should().Contain("instrument");
    }
}
