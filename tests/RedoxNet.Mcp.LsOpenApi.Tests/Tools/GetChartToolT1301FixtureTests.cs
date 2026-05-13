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
/// testbed-console response for TR <c>t1301</c> (시간대별 체결조회 / tick).
/// </summary>
/// <remarks>
/// Notable invariants:
/// <list type="bullet">
///   <item><description><c>t1301</c> pages via a single body key <c>cts_time</c> (10-char "1013130002" format).</description></item>
///   <item><description>Per-tick: <c>mdvolume</c>/<c>msvolume</c> (매도/매수 누적량), <c>mdchecnt</c>/<c>mschecnt</c> (체결건수), <c>chdegree</c> (체결강도, 문자열).</description></item>
/// </list>
/// </remarks>
public class GetChartToolT1301FixtureTests
{
    const string TestbedT1301Response = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "정상적으로 조회가 완료되었습니다.",
      "t1301OutBlock": { "cts_time": "1013130002" },
      "t1301OutBlock1": [
        {
          "chetime": "102626",
          "price": 3685,
          "sign": "2",
          "change": 25,
          "diff": "000.68",
          "cvolume": 5,
          "volume": 321201,
          "mdvolume": 119531,
          "mdchecnt": 256,
          "msvolume": 195608,
          "mschecnt": 239,
          "revolume": 76077,
          "rechecnt": -17,
          "chdegree": "00163.65"
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
    public async Task GetChart_T1301Fixture_DispatchesT1301AndParsesTick()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(TestbedT1301Response));

        string result = await GetChartTool.GetChart(client, "078020", "tick", count: 1);

        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/stock/market-data");
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t1301");

        JsonElement root = JsonDocument.Parse(result).RootElement;
        root.GetProperty("tr_cd").GetString().Should().Be("t1301");
        root.GetProperty("count").GetInt32().Should().Be(1);

        JsonElement candle = root.GetProperty("candles")[0];
        candle.GetProperty("close").GetDecimal().Should().Be(3685m);
        candle.GetProperty("volume").GetInt64().Should().Be(5);
    }

    [Fact]
    public async Task CallTr_T1301Fixture_SurfacesCtsTimeKey()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(TestbedT1301Response));

        JsonElement inBlock = JsonDocument.Parse("""
            { "shcode":"078020", "cvolume":0 }
            """).RootElement;

        string result = await CallTrTool.CallTr(client, TrCatalog.Default, "t1301", inBlock);
        JsonElement cont = JsonDocument.Parse(result).RootElement.GetProperty("continuation");

        cont.GetProperty("has_more").GetBoolean().Should().BeTrue();
        cont.GetProperty("keys").GetProperty("cts_time").GetString().Should().Be("1013130002");
    }

    [Fact]
    public async Task CallTr_T1301Fixture_FullTickBlockIsAccessible()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(TestbedT1301Response));

        JsonElement inBlock = JsonDocument.Parse("""{"shcode":"078020"}""").RootElement;
        string result = await CallTrTool.CallTr(client, TrCatalog.Default, "t1301", inBlock);

        JsonElement tick = JsonDocument.Parse(result).RootElement
            .GetProperty("body").GetProperty("t1301OutBlock1")[0];

        tick.GetProperty("chdegree").GetString().Should().Be("00163.65");
        tick.GetProperty("mdvolume").GetInt64().Should().Be(119531);
        tick.GetProperty("msvolume").GetInt64().Should().Be(195608);
        tick.GetProperty("mdchecnt").GetInt64().Should().Be(256);
        tick.GetProperty("mschecnt").GetInt64().Should().Be(239);
        tick.GetProperty("rechecnt").GetInt64().Should().Be(-17);
    }
}
