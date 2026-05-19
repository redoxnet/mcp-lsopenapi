using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

public sealed class GetGlobalMarketQuoteToolTests
{
    const string DowSample = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "조회완료",
      "t3521OutBlock": {
        "date": "20230602",
        "symbol": "DJI@DJI",
        "change": "701.19",
        "sign": "2",
        "diff": "2.12",
        "close": "33762.76",
        "hname": "다우 산업"
      }
    }
    """;

    static Task<HttpResponseMessage> Ok(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    });

    [Fact]
    public async Task GetGlobalMarketQuote_NasdaqAlias_CallsT3521AndShapesPayload()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(DowSample));

        string result = await GetGlobalMarketQuoteTool.GetGlobalMarketQuote(client, kind: "index", symbol: "nasdaq");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t3521");
        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("\"kind\":\"S\"");
        body.Should().Contain("\"symbol\":\"NAS@IXIC\"");

        root.GetProperty("kind").GetString().Should().Be("index");
        root.GetProperty("kind_code").GetString().Should().Be("S");
        root.GetProperty("symbol").GetString().Should().Be("DJI@DJI");
        root.GetProperty("name").GetString().Should().Be("다우 산업");
        root.GetProperty("date").GetString().Should().Be("20230602");
        root.GetProperty("close").GetDouble().Should().BeApproximately(33762.76, 1e-2);
        root.GetProperty("change").GetDouble().Should().BeApproximately(701.19, 1e-2);
        root.GetProperty("change_pct").GetDouble().Should().BeApproximately(2.12, 1e-2);
        root.GetProperty("source_tr").GetString().Should().Be("t3521");
    }

    [Theory]
    [InlineData("usd/krw", "USDKRWSMBS")]
    [InlineData("usd_krw", "USDKRWSMBS")]
    [InlineData("sp500", "SPI@SPX")]
    [InlineData("SOXX", "USI@SOXX")]
    [InlineData("NAS@IXIC", "NAS@IXIC")]
    public void NormalizeSymbol_HandlesAliasesAndRawSymbols(string input, string expected)
    {
        GetGlobalMarketQuoteTool.NormalizeSymbol(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("fx", "R")]
    [InlineData("환율", "R")]
    [InlineData("futures", "F")]
    [InlineData("index", "S")]
    public void TryNormalizeKind_MapsCommonAliases(string input, string expectedCode)
    {
        GetGlobalMarketQuoteTool.TryNormalizeKind(input, out string code, out _).Should().BeTrue();
        code.Should().Be(expectedCode);
    }

    [Fact]
    public async Task GetGlobalMarketQuote_NegativeSign_FlipsChangeAndPct()
    {
        string sample = DowSample.Replace("\"sign\": \"2\"", "\"sign\": \"5\"");
        var (client, _) = TestClientFactory.Create((_, _) => Ok(sample));

        string result = await GetGlobalMarketQuoteTool.GetGlobalMarketQuote(client, symbol: "dow");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("change").GetDouble().Should().BeApproximately(-701.19, 1e-2);
        root.GetProperty("change_pct").GetDouble().Should().BeApproximately(-2.12, 1e-2);
    }

    [Fact]
    public async Task GetGlobalMarketQuote_InvalidKind_ReturnsValidationError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(DowSample));

        string result = await GetGlobalMarketQuoteTool.GetGlobalMarketQuote(client, kind: "crypto", symbol: "btc");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("kind");
    }
}
