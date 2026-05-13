using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

/// <summary>
/// Pins <see cref="GetQuoteTool"/> against a real LS testbed-console
/// response for TR <c>t1101</c> (shcode 078020, captured 2026-05-13).
/// </summary>
/// <remarks>
/// The fixture documents the actual wire format we expect from LS — including
/// the quirks (<c>diff</c>/<c>yediff</c> as numeric-strings, <c>hotime</c> as
/// 8-char timestamp). Regressions in field-name spelling, type readers, or
/// the shaped tool output will surface here.
/// </remarks>
public class GetQuoteToolTestbedFixtureTests
{
    const string TestbedT1101Response = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "정상적으로 조회가 완료되었습니다.",
      "t1101OutBlock": {
        "hname": "LS증권",
        "shcode": "078020",
        "price": 4545,
        "sign": "2",
        "change": 20,
        "diff": "000.44",
        "volume": 4937,
        "jnilclose": 4525,
        "open": 4550,
        "high": 4600,
        "low": 4540,
        "hotime": "10061477",
        "ho_status": "1",
        "offer": 5762,
        "bid": 3750,
        "tmoffer": 0,
        "tmbid": 0,
        "uplmtprice": 5880,
        "dnlmtprice": 3170,
        "yeprice": 0,
        "yevolume": 0,
        "yechange": 0,
        "yediff": "000.00",
        "yesign": "3",
        "preoffercha": -283,
        "prebidcha": -283,
        "offerho1": 4550, "offerho2": 4560, "offerho3": 4565, "offerho4": 4570, "offerho5": 4575,
        "offerho6": 4580, "offerho7": 4585, "offerho8": 4590, "offerho9": 4595, "offerho10": 4600,
        "bidho1": 4545, "bidho2": 4540, "bidho3": 4535, "bidho4": 4530, "bidho5": 4525,
        "bidho6": 4520, "bidho7": 4515, "bidho8": 4510, "bidho9": 4505, "bidho10": 4500,
        "offerrem1": 83, "offerrem2": 126, "offerrem3": 1, "offerrem4": 574, "offerrem5": 759,
        "offerrem6": 459, "offerrem7": 700, "offerrem8": 805, "offerrem9": 884, "offerrem10": 1371,
        "bidrem1": 448, "bidrem2": 1319, "bidrem3": 31, "bidrem4": 312, "bidrem5": 1199,
        "bidrem6": 253, "bidrem7": 5, "bidrem8": 23, "bidrem9": 34, "bidrem10": 126,
        "preoffercha1": -283, "preoffercha2": 0, "preoffercha3": 0, "preoffercha4": 0, "preoffercha5": 0,
        "preoffercha6": 0,    "preoffercha7": 0, "preoffercha8": 0, "preoffercha9": 0, "preoffercha10": 0,
        "prebidcha1": -283,   "prebidcha2": 0,   "prebidcha3": 0,   "prebidcha4": 0,   "prebidcha5": 0,
        "prebidcha6": 0,      "prebidcha7": 0,   "prebidcha8": 0,   "prebidcha9": 0,   "prebidcha10": 0
      }
    }
    """;

    [Fact]
    public async Task GetQuote_TestbedFixture_ParsesAllExpectedFields()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(TestbedT1101Response, System.Text.Encoding.UTF8, "application/json"),
            }));

        string result = await GetQuoteTool.GetQuote(client, "078020");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("shcode").GetString().Should().Be("078020");
        root.GetProperty("name").GetString().Should().Be("LS증권");
        root.GetProperty("price").GetInt64().Should().Be(4545);
        root.GetProperty("sign").GetString().Should().Be("2");
        root.GetProperty("change").GetInt64().Should().Be(20);
        // 'diff' was the string "000.44" — defensive reader must parse to 0.44.
        root.GetProperty("change_percent").GetDouble().Should().BeApproximately(0.44, 0.001);
        root.GetProperty("volume").GetInt64().Should().Be(4937);
        root.GetProperty("previous_close").GetInt64().Should().Be(4525);
        root.GetProperty("open").GetInt64().Should().Be(4550);
        root.GetProperty("high").GetInt64().Should().Be(4600);
        root.GetProperty("low").GetInt64().Should().Be(4540);
    }

    [Fact]
    public async Task GetQuote_TestbedFixture_SurfacesNewlyAddedFields()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(TestbedT1101Response, System.Text.Encoding.UTF8, "application/json"),
            }));

        string result = await GetQuoteTool.GetQuote(client, "078020");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("upper_limit_price").GetInt64().Should().Be(5880);
        root.GetProperty("lower_limit_price").GetInt64().Should().Be(3170);
        root.GetProperty("total_ask_volume").GetInt64().Should().Be(5762);
        root.GetProperty("total_bid_volume").GetInt64().Should().Be(3750);
        root.GetProperty("extended_hours_ask_volume").GetInt64().Should().Be(0);
        root.GetProperty("extended_hours_bid_volume").GetInt64().Should().Be(0);
        root.GetProperty("quote_time").GetString().Should().Be("10061477");
        root.GetProperty("quote_status").GetString().Should().Be("1");
        root.GetProperty("expected_price").GetInt64().Should().Be(0);
        root.GetProperty("expected_change_percent").GetDouble().Should().Be(0.0);
    }

    [Fact]
    public async Task GetQuote_TestbedFixture_OrderBookHasPerLevelChange()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(TestbedT1101Response, System.Text.Encoding.UTF8, "application/json"),
            }));

        string result = await GetQuoteTool.GetQuote(client, "078020");
        JsonElement orderBook = JsonDocument.Parse(result).RootElement.GetProperty("order_book");

        orderBook.GetArrayLength().Should().Be(10);
        JsonElement level1 = orderBook[0];
        level1.GetProperty("level").GetInt32().Should().Be(1);
        level1.GetProperty("ask").GetInt64().Should().Be(4550);
        level1.GetProperty("ask_size").GetInt64().Should().Be(83);
        level1.GetProperty("ask_change").GetInt64().Should().Be(-283);
        level1.GetProperty("bid").GetInt64().Should().Be(4545);
        level1.GetProperty("bid_size").GetInt64().Should().Be(448);
        level1.GetProperty("bid_change").GetInt64().Should().Be(-283);

        JsonElement level10 = orderBook[9];
        level10.GetProperty("ask").GetInt64().Should().Be(4600);
        level10.GetProperty("bid").GetInt64().Should().Be(4500);
    }
}
