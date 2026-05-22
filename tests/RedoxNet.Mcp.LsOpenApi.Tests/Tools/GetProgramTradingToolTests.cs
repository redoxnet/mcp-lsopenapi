using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

/// <summary>
/// Tool-level tests for <see cref="GetProgramTradingTool"/> — covers the t1662
/// summary / key-point payload and the <c>include_chart</c> / <c>chart_view</c>
/// wiring that ships a Plotly spec under <c>structuredContent.chart</c>.
/// </summary>
[Collection(ChartDatasetCacheCollection.Name)]
public class GetProgramTradingToolTests
{
    static Task<HttpResponseMessage> Ok(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    });

    /// <summary>One t1662 row: cumulative nets plus the gross buy / sell legs.</summary>
    static string Row(
        string time, string k200, string basis, long tot3,
        long cha1, long cha2, long cha3, long bcha1, long bcha2, long bcha3) =>
        $$"""{"time":"{{time}}","k200jisu":"{{k200}}","sign":"2","change":"50.00","k200basis":"{{basis}}","tot3":{{tot3}},"cha1":{{cha1}},"cha2":{{cha2}},"cha3":{{cha3}},"bcha1":{{bcha1}},"bcha2":{{bcha2}},"bcha3":{{bcha3}}}""";

    /// <summary>
    /// A small synthetic t1662 response. LS ships rows newest-first, so the
    /// chronological rows are emitted in reverse — the tool flips them back.
    /// Cumulative fields rise monotonically (gross buy / sell legs included).
    /// </summary>
    static string T1662Body()
    {
        string[] chronological =
        [
            Row("090100", "1175.45", "0.45", -274000, 12000, 6000, 6000, 500000, 780000, -280000),
            Row("100000", "1195.98", "1.42", 130000, 90000, 60000, 30000, 3600000, 3500000, 100000),
            Row("110000", "1202.51", "1.79", 400000, 200000, 100000, 100000, 5500000, 5200000, 300000),
            Row("120000", "1211.01", "2.84", 630000, 300000, 170000, 130000, 6900000, 6400000, 500000),
            Row("130000", "1217.58", "2.57", 890000, 380000, 190000, 190000, 8100000, 7400000, 700000),
            Row("140000", "1221.12", "1.93", 1230000, 470000, 340000, 130000, 9500000, 8400000, 1100000),
            Row("150000", "1223.89", "1.36", 1725000, 490000, 465000, 25000, 11500000, 9800000, 1700000),
            Row("153000", "1224.78", "1.62", 2120000, 600000, 480000, 120000, 13700000, 11700000, 2000000),
        ];

        var sb = new StringBuilder();
        for (int i = chronological.Length - 1; i >= 0; i--)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(chronological[i]);
        }
        return $$"""{"rsp_cd":"00000","rsp_msg":"정상","t1662OutBlock":[{{sb}}]}""";
    }

    /// <summary>Extracts the model-facing text body from a CallToolResult.</summary>
    static JsonElement ParseTextContent(CallToolResult result)
    {
        TextContentBlock text = (TextContentBlock)result.Content[0];
        return JsonDocument.Parse(text.Text).RootElement;
    }

    [Fact]
    public async Task GetProgramTrading_ReturnsSummaryKeyPointsAndDatasetHandle()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(T1662Body()));

        CallToolResult result = await GetProgramTradingTool.GetProgramTrading(client);

        result.IsError.Should().NotBe(true);
        JsonElement text = ParseTextContent(result);
        text.GetProperty("tr_cd").GetString().Should().Be("t1662");
        text.GetProperty("market").GetString().Should().Be("kospi");
        text.GetProperty("dataset_id").GetString().Should().StartWith("ds_");
        text.GetProperty("total_minutes").GetInt32().Should().Be(8);
        text.GetProperty("key_points").GetArrayLength().Should().BeGreaterThan(0);
        text.GetProperty("summary").GetProperty("net").GetInt64().Should().Be(2120000);
    }

    [Fact]
    public async Task GetProgramTrading_DefaultIncludeChartFalse_NoStructuredContent()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(T1662Body()));

        CallToolResult result = await GetProgramTradingTool.GetProgramTrading(client);

        result.StructuredContent.Should().BeNull(
            "include_chart=false must not ship an inline chart spec");

        JsonElement text = ParseTextContent(result);
        text.GetProperty("chart_available").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetProgramTrading_IncludeChartTrue_DefaultView_ShipsFlowOverviewScatterSpec()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(T1662Body()));

        CallToolResult result = await GetProgramTradingTool.GetProgramTrading(
            client, include_chart: true);

        // Model-facing text: flag only, no spec leaked into context.
        JsonElement text = ParseTextContent(result);
        text.GetProperty("chart_available").GetBoolean().Should().BeTrue();
        text.GetProperty("chart_view").GetString().Should().Be("flow_overview");
        text.TryGetProperty("chart", out _).Should().BeFalse(
            "the Plotly spec must not leak into the model's text context");

        // structuredContent: a Plotly v5 spec for the iframe.
        result.StructuredContent.Should().NotBeNull();
        JsonElement chart = result.StructuredContent!.Value.GetProperty("chart");
        chart.GetProperty("type").GetString().Should().Be("plotly");
        chart.GetProperty("version").GetString().Should().Be("5");

        JsonElement data = chart.GetProperty("spec").GetProperty("data");
        data.GetArrayLength().Should().Be(4); // K200 + 전체 + 비차익 + 차익
        data[0].GetProperty("type").GetString().Should().Be("scatter");
    }

    [Fact]
    public async Task GetProgramTrading_IncludeChartTrue_GrossFlow_ShipsTwinPanelBarSpec()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(T1662Body()));

        CallToolResult result = await GetProgramTradingTool.GetProgramTrading(
            client, include_chart: true, chart_view: "gross_flow");

        JsonElement text = ParseTextContent(result);
        text.GetProperty("chart_view").GetString().Should().Be("gross_flow");

        JsonElement spec = result.StructuredContent!.Value.GetProperty("chart").GetProperty("spec");
        JsonElement data = spec.GetProperty("data");
        data.GetArrayLength().Should().Be(4); // 비차익 매수/매도 + 차익 매수/매도
        foreach (JsonElement trace in data.EnumerateArray())
            trace.GetProperty("type").GetString().Should().Be("bar");

        JsonElement layout = spec.GetProperty("layout");
        layout.GetProperty("barmode").GetString().Should().Be("relative");
        // Twin panels: 비차익 on top, 차익 on the bottom, non-overlapping.
        layout.GetProperty("yaxis").GetProperty("domain")[0].GetDouble()
            .Should().BeGreaterThan(layout.GetProperty("yaxis2").GetProperty("domain")[1].GetDouble());
    }

    [Fact]
    public async Task GetProgramTrading_UnknownChartView_ReturnsErrorBeforeTrCall()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(T1662Body()));

        CallToolResult result = await GetProgramTradingTool.GetProgramTrading(
            client, include_chart: true, chart_view: "nope");

        result.IsError.Should().BeTrue();
        handler.Requests.Should().BeEmpty("an unknown chart_view must fail before the t1662 call");
    }

    [Fact]
    public async Task GetProgramTrading_BaselineComparisonView_ReturnsDeferredError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(T1662Body()));

        CallToolResult result = await GetProgramTradingTool.GetProgramTrading(
            client, include_chart: true, chart_view: "baseline_comparison");

        result.IsError.Should().BeTrue();
        ParseTextContent(result).GetProperty("error").GetString()
            .Should().Contain("baseline_comparison");
    }

    // ───────────────────────────── ranking scope (t1636) ────────────────────────────

    /// <summary>One t1636 ranking row (gubun1=금액: amount fields filled, volume fields 0).</summary>
    static string RankRow(int rank, string name, string shcode, long svalue, string mkcap) =>
        $$"""{"rank":{{rank}},"hname":"{{name}}","shcode":"{{shcode}}","price":100000,"sign":"2","change":1000,"diff":"1.50","volume":50000,"svalue":{{svalue}},"offervalue":1000000,"stksvalue":{{svalue + 1000000}},"svolume":0,"offervolume":0,"stksvolume":0,"sgta":500000,"rate":"0.50","ex_shcode":"{{shcode}}","mkcap_cmpr_val":"{{mkcap}}"}""";

    /// <summary>A synthetic t1636 response with <paramref name="rowCount"/> rank-descending rows.</summary>
    static string T1636Body(int rowCount)
    {
        string[] names = ["셀트리온", "SK", "KB금융", "한화오션", "LG"];
        var sb = new StringBuilder();
        for (int i = 0; i < rowCount; i++)
        {
            if (sb.Length > 0) sb.Append(',');
            long svalue = (rowCount - i) * 5_000_000L;            // descending net buy
            sb.Append(RankRow(i + 1, names[i % names.Length], $"00{1000 + i}", svalue, "0.0" + ((i % 9) + 1)));
        }
        return $$"""{"t1636OutBlock":{"cts_idx":{{rowCount}}},"rsp_cd":"00000","rsp_msg":"정상","t1636OutBlock1":[{{sb}}]}""";
    }

    [Fact]
    public async Task GetProgramTrading_RankingScope_ReturnsRankedRowsInEokwon()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(T1636Body(5)));

        CallToolResult result = await GetProgramTradingTool.GetProgramTrading(client, scope: "ranking");

        result.IsError.Should().NotBe(true);
        JsonElement text = ParseTextContent(result);
        text.GetProperty("tr_cd").GetString().Should().Be("t1636");
        text.GetProperty("scope").GetString().Should().Be("ranking");
        text.GetProperty("value_unit").GetString().Should().Be("억원");
        text.GetProperty("count").GetInt32().Should().Be(5);

        JsonElement rows = text.GetProperty("rows");
        rows[0].GetProperty("rank").GetInt32().Should().Be(1);
        // svalue 25,000,000 천원 ÷ 100,000 = 250 억원.
        rows[0].GetProperty("net").GetDouble().Should().Be(250.0);
        result.StructuredContent.Should().BeNull("include_chart defaults to false");
    }

    [Fact]
    public async Task GetProgramTrading_RankingScope_RespectsLimit()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(T1636Body(15)));

        CallToolResult result = await GetProgramTradingTool.GetProgramTrading(
            client, scope: "ranking", limit: 8);

        ParseTextContent(result).GetProperty("count").GetInt32().Should().Be(8);
    }

    [Fact]
    public async Task GetProgramTrading_RankingScope_IncludeChart_ShipsHorizontalBarSpec()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(T1636Body(10)));

        CallToolResult result = await GetProgramTradingTool.GetProgramTrading(
            client, scope: "ranking", include_chart: true);

        ParseTextContent(result).GetProperty("chart_available").GetBoolean().Should().BeTrue();

        JsonElement spec = result.StructuredContent!.Value.GetProperty("chart").GetProperty("spec");
        JsonElement trace = spec.GetProperty("data")[0];
        trace.GetProperty("type").GetString().Should().Be("bar");
        trace.GetProperty("orientation").GetString().Should().Be("h");
        spec.GetProperty("layout").GetProperty("showlegend").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetProgramTrading_RankingScope_UnknownSort_ReturnsErrorBeforeTrCall()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(T1636Body(5)));

        CallToolResult result = await GetProgramTradingTool.GetProgramTrading(
            client, scope: "ranking", sort: "nope");

        result.IsError.Should().BeTrue();
        handler.Requests.Should().BeEmpty("an unknown sort must fail before the t1636 call");
    }

    [Fact]
    public async Task GetProgramTrading_UnknownScope_ReturnsError()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(T1662Body()));

        CallToolResult result = await GetProgramTradingTool.GetProgramTrading(client, scope: "nope");

        result.IsError.Should().BeTrue();
        handler.Requests.Should().BeEmpty("an unknown scope must fail before any TR call");
    }

    // ───────────────────────────── stock scope (t1637) ──────────────────────────────

    /// <summary>One t1637 intraday row (cumulative svalue, 천원; gross legs zero).</summary>
    static string IntradayRow1637(string time, long price, long svalue) =>
        $$"""{"date":"20260522","time":"{{time}}","price":{{price}},"svalue":{{svalue}},"offervalue":0,"stksvalue":0,"svolume":0,"offervolume":0,"stksvolume":0,"diff":"0","sign":"","change":0,"volume":0,"shcode":"005930","ex_shcode":"005930"}""";

    /// <summary>One t1637 daily row (per-day svalue + buy/sell legs, 천원).</summary>
    static string DailyRow1637(string date, long price, long svalue) =>
        $$"""{"date":"{{date}}","time":"","price":{{price}},"svalue":{{svalue}},"offervalue":1000000,"stksvalue":{{svalue + 1000000}},"svolume":0,"offervolume":0,"stksvolume":0,"diff":"1.50","sign":"2","change":1000,"volume":50000,"shcode":"005930","ex_shcode":"005930"}""";

    /// <summary>A synthetic t1637 intraday response — 5 newest-first cumulative rows.</summary>
    static string T1637IntradayBody()
    {
        string[] rows =
        [
            IntradayRow1637("090500", 298000, -5000000),
            IntradayRow1637("090400", 298500, -4000000),
            IntradayRow1637("090300", 299000, -3000000),
            IntradayRow1637("090200", 299500, -2000000),
            IntradayRow1637("090100", 300000, -1000000),
        ];
        return $$"""{"t1637OutBlock":{"cts_idx":0},"rsp_cd":"00000","rsp_msg":"조회완료","t1637OutBlock1":[{{string.Join(",", rows)}}]}""";
    }

    /// <summary>A synthetic t1637 daily response with <paramref name="rowCount"/> newest-first rows.</summary>
    static string T1637DailyBody(int rowCount)
    {
        var sb = new StringBuilder();
        var d = new DateTime(2026, 5, 22);
        for (int i = 0; i < rowCount; i++)
        {
            if (sb.Length > 0) sb.Append(',');
            long svalue = (i % 2 == 0 ? 1 : -1) * (i + 1) * 100_000_000L;
            sb.Append(DailyRow1637(d.ToString("yyyyMMdd"), 290000 + i * 500, svalue));
            d = d.AddDays(-1);
        }
        return $$"""{"t1637OutBlock":{"cts_idx":0},"rsp_cd":"00000","rsp_msg":"조회완료","t1637OutBlock1":[{{sb}}]}""";
    }

    [Fact]
    public async Task GetProgramTrading_StockScope_Intraday_ReturnsCumulativeSeriesInEokwon()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(T1637IntradayBody()));

        CallToolResult result = await GetProgramTradingTool.GetProgramTrading(
            client, scope: "stock", shcode: "005930");

        result.IsError.Should().NotBe(true);
        JsonElement text = ParseTextContent(result);
        text.GetProperty("tr_cd").GetString().Should().Be("t1637");
        text.GetProperty("scope").GetString().Should().Be("stock");
        text.GetProperty("period").GetString().Should().Be("intraday");
        text.GetProperty("total_minutes").GetInt32().Should().Be(5);
        // svalue -5,000,000 천원 ÷ 100,000 = -50 억원 (last, chronological).
        text.GetProperty("summary").GetProperty("latest_net").GetDouble().Should().Be(-50.0);
        result.StructuredContent.Should().BeNull("include_chart defaults to false");
    }

    [Fact]
    public async Task GetProgramTrading_StockScope_Intraday_IncludeChart_ShipsDualAxisLineSpec()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(T1637IntradayBody()));

        CallToolResult result = await GetProgramTradingTool.GetProgramTrading(
            client, scope: "stock", shcode: "005930", name: "삼성전자", include_chart: true);

        ParseTextContent(result).GetProperty("chart_available").GetBoolean().Should().BeTrue();

        JsonElement spec = result.StructuredContent!.Value.GetProperty("chart").GetProperty("spec");
        JsonElement data = spec.GetProperty("data");
        data.GetArrayLength().Should().Be(2);   // price + cumulative net
        data[0].GetProperty("type").GetString().Should().Be("scatter");
        data[1].GetProperty("yaxis").GetString().Should().Be("y2");
    }

    [Fact]
    public async Task GetProgramTrading_StockScope_Daily_RespectsLimitAndShipsBarChart()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(T1637DailyBody(40)));

        CallToolResult result = await GetProgramTradingTool.GetProgramTrading(
            client, scope: "stock", shcode: "005930", period: "daily", limit: 12, include_chart: true);

        JsonElement text = ParseTextContent(result);
        text.GetProperty("period").GetString().Should().Be("daily");
        text.GetProperty("count").GetInt32().Should().Be(12);
        text.GetProperty("rows").GetArrayLength().Should().Be(12);

        JsonElement trace = result.StructuredContent!.Value
            .GetProperty("chart").GetProperty("spec").GetProperty("data")[0];
        trace.GetProperty("type").GetString().Should().Be("bar");
    }

    [Fact]
    public async Task GetProgramTrading_StockScope_MissingShcode_ReturnsErrorBeforeTrCall()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(T1637IntradayBody()));

        CallToolResult result = await GetProgramTradingTool.GetProgramTrading(client, scope: "stock");

        result.IsError.Should().BeTrue();
        handler.Requests.Should().BeEmpty("a missing shcode must fail before the t1637 call");
    }

    [Fact]
    public async Task GetProgramTrading_StockScope_UnknownPeriod_ReturnsErrorBeforeTrCall()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(T1637IntradayBody()));

        CallToolResult result = await GetProgramTradingTool.GetProgramTrading(
            client, scope: "stock", shcode: "005930", period: "weekly");

        result.IsError.Should().BeTrue();
        handler.Requests.Should().BeEmpty("an unknown period must fail before the t1637 call");
    }

    // ───────────────────────── market scope, daily period (t1633) ───────────────────

    /// <summary>One t1633 daily row (per-day 차익 / 비차익 net buying, 백만원).</summary>
    static string Row1633(string date, string jisu, long cha3, long bcha3) =>
        $$"""{"date":"{{date}}","jisu":"{{jisu}}","sign":"2","change":"1.00","tot3":{{cha3 + bcha3}},"tot1":0,"tot2":0,"cha3":{{cha3}},"cha1":0,"cha2":0,"bcha3":{{bcha3}},"bcha1":0,"bcha2":0,"volume":100000}""";

    /// <summary>A synthetic t1633 daily response with <paramref name="rowCount"/> newest-first rows.</summary>
    static string T1633Body(int rowCount)
    {
        var sb = new StringBuilder();
        var d = new DateTime(2026, 5, 22);
        for (int i = 0; i < rowCount; i++)
        {
            if (sb.Length > 0) sb.Append(',');
            long bcha3 = (i % 2 == 0 ? -1 : 1) * (i + 1) * 100_000L;
            sb.Append(Row1633(d.ToString("yyyyMMdd"), (1200 - i).ToString(), 10_000, bcha3));
            d = d.AddDays(-1);
        }
        return $$"""{"t1633OutBlock":{"date":"20260101","idx":{{rowCount}}},"rsp_cd":"00000","rsp_msg":"정상","t1633OutBlock1":[{{sb}}]}""";
    }

    [Fact]
    public async Task GetProgramTrading_MarketScope_Daily_ReturnsDailyRowsInEokwon()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(T1633Body(20)));

        CallToolResult result = await GetProgramTradingTool.GetProgramTrading(client, period: "daily");

        result.IsError.Should().NotBe(true);
        JsonElement text = ParseTextContent(result);
        text.GetProperty("tr_cd").GetString().Should().Be("t1633");
        text.GetProperty("scope").GetString().Should().Be("market");
        text.GetProperty("period").GetString().Should().Be("daily");
        text.GetProperty("count").GetInt32().Should().Be(20);
        text.GetProperty("rows").GetArrayLength().Should().Be(20);
        // cha3 10,000 백만원 ÷ 100 = 100 억원.
        text.GetProperty("rows")[0].GetProperty("arbitrage").GetDouble().Should().Be(100.0);
        result.StructuredContent.Should().BeNull("include_chart defaults to false");
    }

    [Fact]
    public async Task GetProgramTrading_MarketScope_Daily_RespectsLimitAndShipsStackedBarChart()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(T1633Body(30)));

        CallToolResult result = await GetProgramTradingTool.GetProgramTrading(
            client, period: "daily", limit: 15, include_chart: true);

        ParseTextContent(result).GetProperty("count").GetInt32().Should().Be(15);

        JsonElement spec = result.StructuredContent!.Value.GetProperty("chart").GetProperty("spec");
        spec.GetProperty("data").GetArrayLength().Should().Be(3);   // 비차익 + 차익 + index
        spec.GetProperty("layout").GetProperty("barmode").GetString().Should().Be("relative");
    }
}
