using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

/// <summary>
/// Pins <see cref="GetChartTool"/> and <c>ls_call_tr</c> against a real LS
/// testbed-console response for TR <c>t8410</c> (shcode 078020, captured 2026-05-13).
/// </summary>
/// <remarks>
/// Notable invariants:
/// <list type="bullet">
///   <item><description><c>t8410</c> pages via the request body — the continuation key <c>cts_date</c> appears inside <c>t8410OutBlock</c>, not in response headers.</description></item>
///   <item><description><c>rate</c> in <c>t8410OutBlock1</c> is a numeric-string (e.g. <c>"000.00"</c>) like t1101's <c>diff</c>.</description></item>
///   <item><description><c>value</c> is denominated in million won (단위 백만원).</description></item>
/// </list>
/// </remarks>
public class GetChartToolT8410FixtureTests
{
    const string TestbedT8410Response = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "정상적으로 조회가 완료되었습니다.",
      "t8410OutBlock": {
        "shcode": "078020",
        "cts_date": "",
        "jisiga": 4500, "jihigh": 4565, "jilow": 4470, "jiclose": 4525, "jivolume": 32336,
        "disiga": 4550, "dihigh": 4600, "dilow": 4520, "diclose": 4530,
        "highend": 5880, "lowend": 3170,
        "svi_uplmtprice": 5010, "svi_dnlmtprice": 4095,
        "s_time": "090000", "e_time": "153000", "dshmin": "10",
        "rec_count": 1
      },
      "t8410OutBlock1": [
        {
          "date": "20230605",
          "open": 4550, "high": 4600, "low": 4520, "close": 4530,
          "jdiff_vol": 33764,
          "value": 153,
          "sign": "2", "rate": "000.00", "ratevalue": 0,
          "jongchk": 0, "pricechk": 0
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
    public async Task GetChart_T8410Fixture_DispatchesToStockChartAndParsesCandle()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(TestbedT8410Response));

        string result = await GetChartTool.GetChart(client, "078020", "day", count: 1);

        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/stock/chart");
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t8410");

        JsonElement root = JsonDocument.Parse(result).RootElement;
        root.GetProperty("tr_cd").GetString().Should().Be("t8410");
        root.GetProperty("count").GetInt32().Should().Be(1);

        JsonElement candle = root.GetProperty("candles")[0];
        candle.GetProperty("date").GetString().Should().Be("2023-06-05");
        candle.GetProperty("open").GetDecimal().Should().Be(4550m);
        candle.GetProperty("high").GetDecimal().Should().Be(4600m);
        candle.GetProperty("low").GetDecimal().Should().Be(4520m);
        candle.GetProperty("close").GetDecimal().Should().Be(4530m);
        candle.GetProperty("volume").GetInt64().Should().Be(33764);
        candle.GetProperty("value").GetInt64().Should().Be(153);
    }

    [Theory]
    [InlineData("day", "2")]
    [InlineData("week", "3")]
    [InlineData("month", "4")]
    [InlineData("year", "5")]
    public async Task GetChart_T8410_DispatchesGubunByPeriodType(string periodType, string expectedGubun)
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(TestbedT8410Response));

        await GetChartTool.GetChart(client, "078020", periodType, count: 1);

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t8410");

        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain($"\"gubun\":\"{expectedGubun}\"");
    }

    [Fact]
    public async Task CallTr_T8410Fixture_SurfacesBodyBasedContinuationKeys()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(TestbedT8410Response));
        // Override body's cts_date to a non-empty value to simulate "more pages".
        string pagingBody = TestbedT8410Response.Replace("\"cts_date\": \"\"", "\"cts_date\": \"20230531\"");
        var (clientPaging, _) = TestClientFactory.Create((_, _) => Ok(pagingBody));

        JsonElement inBlock = JsonDocument.Parse("""
            { "shcode":"078020", "gubun":"2", "qrycnt":1, "comp_yn":"N" }
            """).RootElement;

        string emptyResult = await CallTrTool.CallTr(client, RedoxNet.LsOpenApi.Core.Catalog.TrCatalog.Default,
            "t8410", inBlock);
        JsonElement emptyCont = JsonDocument.Parse(emptyResult).RootElement.GetProperty("continuation");
        emptyCont.GetProperty("has_more").GetBoolean().Should().BeFalse();
        emptyCont.GetProperty("keys").EnumerateObject().Should().BeEmpty();

        string pagingResult = await CallTrTool.CallTr(clientPaging, RedoxNet.LsOpenApi.Core.Catalog.TrCatalog.Default,
            "t8410", inBlock);
        JsonElement pagingCont = JsonDocument.Parse(pagingResult).RootElement.GetProperty("continuation");
        pagingCont.GetProperty("has_more").GetBoolean().Should().BeTrue();
        pagingCont.GetProperty("keys").GetProperty("cts_date").GetString().Should().Be("20230531");
    }
}
