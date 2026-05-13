using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Catalog;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

/// <summary>
/// Pins <see cref="GetMultiQuoteTool"/> against a real LS testbed-console
/// response for TR <c>t8407</c> (captured 2026-05-13).
/// </summary>
/// <remarks>
/// t8407 ("API용 주식 멀티 현재가 조회") returns 22 fields per row including
/// best ask/bid (1 level only), 총잔량, 체결강도. Notable: <c>change</c> is
/// unsigned magnitude — direction is in <c>sign</c>, signed % is in <c>diff</c>.
/// </remarks>
public class GetMultiQuoteToolFixtureTests
{
    const string TestbedT8407Response = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "정상적으로 조회가 완료되었습니다.",
      "t8407OutBlock1": [
        {
          "shcode": "078020", "hname": "이베스트투자증권",
          "price": 4530, "sign": "2", "change": 5, "diff": "000.11",
          "volume": 33764, "cvolume": 202, "value": 153,
          "jnilclose": 4525, "open": 4550, "high": 4600, "low": 4520,
          "uplmtprice": 5880, "dnlmtprice": 3170,
          "offerho": 4540, "bidho": 4530, "offerrem": 57, "bidrem": 143,
          "totofferrem": 3928, "totbidrem": 5901,
          "chdegree": "000020.91"
        },
        {
          "shcode": "000660", "hname": "SK하이닉스",
          "price": 108700, "sign": "5", "change": 1600, "diff": "-01.45",
          "volume": 3086217, "cvolume": 459, "value": 337018,
          "jnilclose": 110300, "open": 110100, "high": 110500, "low": 108500,
          "uplmtprice": 143300, "dnlmtprice": 77300,
          "offerho": 108800, "bidho": 108700, "offerrem": 248, "bidrem": 25011,
          "totofferrem": 126172, "totbidrem": 172000,
          "chdegree": "000072.05"
        },
        {
          "shcode": "005930", "hname": "삼성전자",
          "price": 71700, "sign": "5", "change": 500, "diff": "-00.69",
          "volume": 12640775, "cvolume": 25, "value": 908016,
          "jnilclose": 72200, "open": 72700, "high": 72700, "low": 71400,
          "uplmtprice": 93800, "dnlmtprice": 50600,
          "offerho": 71700, "bidho": 71600, "offerrem": 58968, "bidrem": 31934,
          "totofferrem": 1498765, "totbidrem": 880412,
          "chdegree": "000056.80"
        }
      ]
    }
    """;

    static Task<HttpResponseMessage> Ok(string body) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });

    [Fact]
    public async Task GetMultiQuote_DispatchesT8407AndConcatenatesShcodes()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(TestbedT8407Response));

        await GetMultiQuoteTool.GetMultiQuote(client, new[] { "078020", "000660", "005930" });

        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/stock/market-data");
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t8407");

        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("\"qrycnt\":3");
        body.Should().Contain("\"shcode\":\"078020000660005930\"");
    }

    [Fact]
    public async Task GetMultiQuote_TestbedFixture_ParsesAllRows()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(TestbedT8407Response));

        string result = await GetMultiQuoteTool.GetMultiQuote(client, new[] { "078020", "000660", "005930" });
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("count").GetInt32().Should().Be(3);
        JsonElement quotes = root.GetProperty("quotes");

        JsonElement samsung = quotes[2];
        samsung.GetProperty("shcode").GetString().Should().Be("005930");
        samsung.GetProperty("name").GetString().Should().Be("삼성전자");
        samsung.GetProperty("price").GetInt64().Should().Be(71700);
        samsung.GetProperty("sign").GetString().Should().Be("5");
        samsung.GetProperty("change").GetInt64().Should().Be(500);
        // diff is the signed string "-00.69" → parsed to -0.69
        samsung.GetProperty("change_percent").GetDouble().Should().BeApproximately(-0.69, 0.01);
        samsung.GetProperty("volume").GetInt64().Should().Be(12640775);
        samsung.GetProperty("best_ask").GetInt64().Should().Be(71700);
        samsung.GetProperty("best_bid").GetInt64().Should().Be(71600);
        samsung.GetProperty("total_ask_size").GetInt64().Should().Be(1498765);
        samsung.GetProperty("total_bid_size").GetInt64().Should().Be(880412);
        samsung.GetProperty("chdegree").GetDouble().Should().BeApproximately(56.80, 0.01);
    }

    [Fact]
    public async Task GetMultiQuote_EmptyShcodes_ReturnsError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(TestbedT8407Response));

        string result = await GetMultiQuoteTool.GetMultiQuote(client, Array.Empty<string>());

        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString()
            .Should().Contain("shcodes is required");
    }

    [Fact]
    public async Task GetMultiQuote_BadShcode_ReturnsError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(TestbedT8407Response));

        string result = await GetMultiQuoteTool.GetMultiQuote(client, new[] { "005930", "ABC" });

        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString()
            .Should().Contain("ABC");
    }

    [Fact]
    public async Task GetMultiQuote_TooManyShcodes_ReturnsError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(TestbedT8407Response));

        string[] tooMany = Enumerable.Range(0, GetMultiQuoteTool.MaxStocks + 1)
            .Select(i => i.ToString("D6"))
            .ToArray();

        string result = await GetMultiQuoteTool.GetMultiQuote(client, tooMany);

        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString()
            .Should().Contain("At most");
    }
}
