using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Catalog;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

/// <summary>
/// Pins <see cref="GetChartTool"/> and <c>ls_call_tr</c> against a real LS
/// testbed-console response for TR <c>t8412</c> (shcode 078020, minute chart).
/// </summary>
/// <remarks>
/// Notable invariants:
/// <list type="bullet">
///   <item><description><c>t8412</c> pages via two body keys: <c>cts_date</c> + <c>cts_time</c>. Both must be echoed back on the next call.</description></item>
///   <item><description><c>rate</c> in <c>t8412OutBlock1</c> may render as <c>"0.00"</c> or just <c>"0"</c> — the defensive reader must handle both.</description></item>
/// </list>
/// </remarks>
public class GetChartToolT8412FixtureTests
{
    const string TestbedT8412Response = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "정상적으로 조회가 완료되었습니다.",
      "t8412OutBlock": {
        "shcode": "078020",
        "cts_date": "20240906",
        "cts_time": "111200",
        "jisiga": 4550, "jihigh": 4610, "jilow": 4320, "jiclose": 4555, "jivolume": 49742,
        "disiga": 4495, "dihigh": 4540, "dilow": 4280, "diclose": 4515,
        "highend": 5920, "lowend": 3190,
        "s_time": "090000", "e_time": "153000", "dshmin": "10",
        "rec_count": 500
      },
      "t8412OutBlock1": [
        { "date":"20240906", "time":"111300", "open":4445, "high":4470, "low":4445, "close":4470, "jdiff_vol":2317, "value":10, "sign":"5", "rate":"0.00", "jongchk":0 },
        { "date":"20240906", "time":"111400", "open":4470, "high":4470, "low":4445, "close":4470, "jdiff_vol":8301, "value":37, "sign":"5", "rate":"0.00", "jongchk":0 },
        { "date":"20240909", "time":"131000", "open":4515, "high":4515, "low":4515, "close":4515, "jdiff_vol":0,    "value":0,  "sign":"5", "rate":"0",    "jongchk":0 }
      ]
    }
    """;

    static Task<HttpResponseMessage> Ok(string body) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });

    [Fact]
    public async Task GetChart_T8412Fixture_DispatchesT8412AndParsesMinuteCandles()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(TestbedT8412Response));

        string result = await GetChartTool.GetChart(
            client, "078020", "min", count: 3, minute_unit: 1);

        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/stock/chart");
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t8412");

        JsonElement root = JsonDocument.Parse(result).RootElement;
        root.GetProperty("tr_cd").GetString().Should().Be("t8412");
        root.GetProperty("count").GetInt32().Should().Be(3);

        // Each candle date should be a full datetime, parsed from date+time fields.
        JsonElement first = root.GetProperty("candles")[0];
        first.GetProperty("date").GetString().Should().Be("2024-09-06T11:13:00");
        first.GetProperty("close").GetDecimal().Should().Be(4470m);
        first.GetProperty("volume").GetInt64().Should().Be(2317);
    }

    [Fact]
    public async Task CallTr_T8412Fixture_SurfacesBothBodyKeys()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(TestbedT8412Response));

        JsonElement inBlock = JsonDocument.Parse("""
            { "shcode":"078020", "ncnt":1, "qrycnt":500, "nday":"0", "comp_yn":"N" }
            """).RootElement;

        string result = await CallTrTool.CallTr(client, TrCatalog.Default, "t8412", inBlock);
        JsonElement cont = JsonDocument.Parse(result).RootElement.GetProperty("continuation");

        cont.GetProperty("has_more").GetBoolean().Should().BeTrue();
        JsonElement keys = cont.GetProperty("keys");
        keys.GetProperty("cts_date").GetString().Should().Be("20240906");
        keys.GetProperty("cts_time").GetString().Should().Be("111200");
    }
}
