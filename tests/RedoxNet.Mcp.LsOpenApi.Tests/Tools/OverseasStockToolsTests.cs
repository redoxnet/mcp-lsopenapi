using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

public sealed class OverseasStockToolsTests
{
    static Task<HttpResponseMessage> Ok(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    });

    static JsonElement ParseText(CallToolResult result)
    {
        TextContentBlock text = (TextContentBlock)result.Content[0];
        return JsonDocument.Parse(text.Text!).RootElement;
    }

    // ============================================================
    // ls_search_overseas_stock
    // ============================================================

    [Fact]
    public async Task SearchOverseasStock_FiltersMasterRowsAndReturnsKeySymbol()
    {
        const string sample = """
        {
          "rsp_cd": "00000",
          "rsp_msg": "조회완료",
          "g3190OutBlock": {
            "delaygb": "R",
            "natcode": "US",
            "exgubun": "2",
            "cts_value": "",
            "rec_count": 2
          },
          "g3190OutBlock1": [
            {
              "keysymbol": "82TSLA",
              "natcode": "US",
              "exchcd": "82",
              "symbol": "TSLA",
              "korname": "테슬라",
              "engname": "TESLA INC",
              "currency": "USD",
              "isin": "US88160R1014",
              "listed_date": "20100629",
              "suspend": "N",
              "sellonly": "0",
              "point": "Y"
            },
            {
              "keysymbol": "82MSFT",
              "natcode": "US",
              "exchcd": "82",
              "symbol": "MSFT",
              "korname": "마이크로소프트",
              "engname": "MICROSOFT CORP",
              "currency": "USD"
            }
          ]
        }
        """;

        var (client, handler) = TestClientFactory.Create((_, _) => Ok(sample));

        string result = await OverseasStockTools.SearchOverseasStock(client, "테슬라", exchange: "nasdaq");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("g3190");
        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("\"exgubun\":\"2\"");

        root.GetProperty("count").GetInt32().Should().Be(1);
        root.GetProperty("matches_scanned").GetInt32().Should().Be(1);
        root.TryGetProperty("total_available", out _).Should().BeFalse(
            because: "the field was renamed to matches_scanned in v1.3");
        JsonElement row = root.GetProperty("results")[0];
        row.GetProperty("keysymbol").GetString().Should().Be("82TSLA");
        row.GetProperty("symbol").GetString().Should().Be("TSLA");
        row.GetProperty("exchange").GetString().Should().Be("nasdaq");
    }

    // ============================================================
    // ls_get_overseas_quote
    // ============================================================

    [Fact]
    public async Task GetOverseasQuote_WithProfileAndOrderBook_CallsThreeTrs()
    {
        const string quote = """
        {
          "rsp_cd": "00000",
          "rsp_msg": "조회완료",
          "g3101OutBlock": {
            "keysymbol": "82TSLA",
            "exchcd": "82",
            "exchange": "0537",
            "suspend": "N",
            "sellonly": "0",
            "symbol": "TSLA",
            "korname": "테슬라",
            "induname": "자동차 및 부품",
            "floatpoint": "4",
            "currency": "USD",
            "price": "283.8200",
            "sign": "5",
            "diff": "1.1300",
            "rate": "0.40",
            "volume": 414175,
            "amount": 117236758,
            "high52p": "488.5399",
            "low52p": "166.3700",
            "uplimit": "0.0000",
            "dnlimit": "0.0000",
            "open": "285.0900",
            "high": "285.3100",
            "low": "281.8400",
            "perv": "142.71",
            "epsv": "1.82"
          }
        }
        """;
        const string profile = """
        {
          "rsp_cd": "00000",
          "rsp_msg": "조회완료",
          "g3104OutBlock": {
            "engname": "TESLA INC",
            "exchange_name": "NASDAQ",
            "nation_name": "United States",
            "instname": "Common Stock",
            "share": 3210000000,
            "shareprc": 910000000000,
            "untprc": "0.0100",
            "bidlotsize2": "1",
            "asklotsize2": "1",
            "pcls": "284.9500",
            "clos": "284.9500",
            "exrate": "1400.10"
          }
        }
        """;
        const string orderbook = """
        {
          "rsp_cd": "00000",
          "rsp_msg": "조회완료",
          "g3106OutBlock": {
            "hotime": "014101",
            "offerho1": "284.0000",
            "bidho1": "283.9500",
            "offercnt1": "3",
            "bidcnt1": "4",
            "offerrem1": 120,
            "bidrem1": 130,
            "offer": 1200,
            "bid": 1300,
            "offercnt": "30",
            "bidcnt": "40"
          }
        }
        """;

        var (client, handler) = TestClientFactory.Create((request, _) =>
        {
            string tr = request.Headers.GetValues("tr_cd").Single();
            return Ok(tr switch
            {
                "g3101" => quote,
                "g3104" => profile,
                "g3106" => orderbook,
                _ => throw new InvalidOperationException(tr),
            });
        });

        string result = await OverseasStockTools.GetOverseasQuote(
            client,
            symbol: "TSLA",
            include_profile: true,
            include_orderbook: true);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        handler.Requests.Select(r => r.Headers.GetValues("tr_cd").Single())
            .Should().Equal("g3101", "g3104", "g3106");
        root.GetProperty("price").GetDecimal().Should().Be(283.8200m);
        root.GetProperty("change").GetDouble().Should().BeApproximately(-1.13, 1e-6);
        root.GetProperty("currency").GetString().Should().Be("USD");
        root.GetProperty("timestamp_tz").GetString().Should().Be("Asia/Seoul");
        root.GetProperty("profile").GetProperty("english_name").GetString().Should().Be("TESLA INC");

        JsonElement levels = root.GetProperty("order_book").GetProperty("levels");
        levels.GetArrayLength().Should().Be(1, because: "the mock only ships level 1 — empty levels should be filtered, not zero-filled");
        levels[0].GetProperty("ask").GetDecimal().Should().Be(284.0000m);
        levels[0].GetProperty("level").GetInt32().Should().Be(1);
    }

    // ============================================================
    // ls_get_overseas_chart
    // ============================================================

    [Fact]
    public async Task GetOverseasChart_DayDisplay_CallsG3204AndShipsPlotlySpec()
    {
        const string sample = """
        {
          "rsp_cd": "00000",
          "rsp_msg": "조회완료",
          "g3204OutBlock": {
            "keysymbol": "82TSLA",
            "exchcd": "82",
            "symbol": "TSLA",
            "cts_date": "",
            "cts_info": "",
            "rec_count": 3
          },
          "g3204OutBlock1": [
            { "date": "20250403", "open": "280.0000", "high": "286.0000", "low": "275.0000", "close": "284.0000", "volume": 1000, "amount": 284000 },
            { "date": "20250401", "open": "270.0000", "high": "281.0000", "low": "269.0000", "close": "280.0000", "volume": 900, "amount": 252000 },
            { "date": "20250402", "open": "281.0000", "high": "285.0000", "low": "278.0000", "close": "282.0000", "volume": 950, "amount": 267900 }
          ]
        }
        """;

        var (client, handler) = TestClientFactory.Create((_, _) => Ok(sample));

        CallToolResult result = await OverseasStockTools.GetOverseasChart(
            client,
            symbol: "TSLA",
            period_type: "day",
            count: 3,
            include_chart: true,
            name: "테슬라");
        JsonElement root = ParseText(result);

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("g3204");
        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("\"keysymbol\":\"82TSLA\"");
        body.Should().Contain("\"sujung\":\"Y\"");

        root.GetProperty("output_mode").GetString().Should().Be("display");
        root.GetProperty("currency").GetString().Should().Be("USD");
        root.GetProperty("bar_timezone").GetString().Should().Be("America/New_York",
            because: "daily overseas bars on a US exchange (exchcd 81/82) are indexed by the NYSE/Nasdaq trading day, not Asia/Seoul calendar dates");
        root.GetProperty("summary").GetProperty("latest_close").GetDecimal().Should().Be(284.0000m);
        result.StructuredContent.Should().NotBeNull();
        result.StructuredContent!.Value.GetProperty("chart").GetProperty("type").GetString().Should().Be("plotly");
    }

    [Fact]
    public async Task GetOverseasChart_LongMaWithShortCount_AppliesSummaryWarmup()
    {
        // 260 ascending daily candles — plenty for SummaryWarmup("day")=240 + count=10 trim.
        string sample = BuildDailyCandleSample("82NVDA", "NVDA", count: 260, startClose: 100.0m, step: 0.5m);

        var (client, handler) = TestClientFactory.Create((_, _) => Ok(sample));

        CallToolResult result = await OverseasStockTools.GetOverseasChart(
            client,
            symbol: "NVDA",
            period_type: "day",
            count: 10,
            indicators: new[] { "ma:50" });
        JsonElement root = ParseText(result);

        handler.Requests.Should().ContainSingle();
        string body = await handler.Requests[0].Content!.ReadAsStringAsync();

        // SummaryWarmup("day") = 240 + count 10 → qrycnt 250 over the wire.
        body.Should().Contain("\"qrycnt\":250",
            because: "ma:50 (warmup 50) plus the daily summary warm-up (240) widens the fetch window");

        // After trim, the display window stays at count=10.
        root.GetProperty("count").GetInt32().Should().Be(10);

        // The summary still sees the warmed-up series, so latest_close is the
        // last bar (260th close = 100 + 259*0.5 = 229.5).
        root.GetProperty("summary").GetProperty("latest_close").GetDecimal().Should().Be(229.5m);

        // The summary should advertise that warm-up is in effect.
        root.GetProperty("summary").GetProperty("coverage").GetProperty("warmup_applied").GetBoolean().Should().BeTrue();
    }

    // ============================================================
    // Follow-up routing: ls_add_indicator / ls_reframe_chart on overseas datasets
    // ============================================================

    [Fact]
    public async Task AddIndicator_OnOverseasDataset_RoutesThroughOverseasFetch()
    {
        string sample = BuildDailyCandleSample("82TSLA", "TSLA", count: 60, startClose: 250.0m, step: 0.25m);
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(sample));

        // Seed an overseas chart dataset.
        CallToolResult chart = await OverseasStockTools.GetOverseasChart(
            client, symbol: "TSLA", period_type: "day", count: 60, name: "테슬라");
        string datasetId = ParseText(chart).GetProperty("dataset_id").GetString()!;

        int beforeFollowup = handler.Requests.Count;

        // Route through GetChartTool.AddIndicator. It must hand the call off to
        // OverseasStockTools.TryAddIndicatorAsync rather than rejecting with
        // "Unknown or expired dataset_id".
        CallToolResult addResult = await GetChartTool.AddIndicator(
            client, datasetId, indicator: "rsi:14", include_chart: false);
        JsonElement added = ParseText(addResult);

        added.GetProperty("dataset_id").GetString().Should().Be(datasetId);
        added.GetProperty("symbol").GetString().Should().Be("TSLA");
        added.GetProperty("added_indicator").GetString().Should().Be("rsi:14");
        added.GetProperty("bar_timezone").GetString().Should().Be("America/New_York");
        addResult.IsError.Should().NotBe(true);

        // The follow-up refetch should also hit g3204 (daily overseas).
        IEnumerable<string> refetched = handler.Requests
            .Skip(beforeFollowup)
            .Select(r => r.Headers.GetValues("tr_cd").Single());
        refetched.Should().AllBe("g3204");
    }

    [Fact]
    public async Task ReframeChart_OnOverseasDataset_SwitchesPeriodViaOverseasFetch()
    {
        string seed = BuildDailyCandleSample("82AAPL", "AAPL", count: 60, startClose: 180.0m, step: 0.1m, gubun: "2");
        string reframed = BuildDailyCandleSample("82AAPL", "AAPL", count: 50, startClose: 175.0m, step: 0.5m, gubun: "3");
        var (client, handler) = TestClientFactory.Create((request, _) =>
        {
            // Distinguish requests by their `gubun` body field.
            string body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Ok(body.Contains("\"gubun\":\"3\"") ? reframed : seed);
        });

        CallToolResult chart = await OverseasStockTools.GetOverseasChart(
            client, symbol: "AAPL", period_type: "day", count: 60);
        string datasetId = ParseText(chart).GetProperty("dataset_id").GetString()!;

        int beforeReframe = handler.Requests.Count;

        CallToolResult reframeResult = await GetChartTool.ReframeChart(
            client, datasetId, period_type: "week", count: 50, include_chart: false);
        JsonElement payload = ParseText(reframeResult);

        payload.GetProperty("dataset_id").GetString().Should().Be(datasetId);
        payload.GetProperty("period_type").GetString().Should().Be("week");
        payload.GetProperty("tr_cd").GetString().Should().Be("g3204");
        payload.GetProperty("symbol").GetString().Should().Be("AAPL");
        payload.GetProperty("bar_timezone").GetString().Should().Be("America/New_York");
        reframeResult.IsError.Should().NotBe(true);

        string reframeBody = await handler.Requests[beforeReframe].Content!.ReadAsStringAsync();
        reframeBody.Should().Contain("\"gubun\":\"3\"", because: "week maps to gubun=3 on g3204");
    }

    [Fact]
    public async Task AddIndicator_OnUnknownDataset_StillReturnsTheNormalError()
    {
        // No dataset seeded — the overseas route should silently decline (return null),
        // letting the existing KR path emit the canonical error.
        var (client, _) = TestClientFactory.Create((_, _) => Ok("{\"rsp_cd\":\"00000\"}"));

        CallToolResult result = await GetChartTool.AddIndicator(
            client, dataset_id: "ds_nonsense00", indicator: "ma:20");
        JsonElement root = ParseText(result);

        result.IsError.Should().Be(true);
        root.GetProperty("error").GetString().Should().Be("Unknown or expired dataset_id.");
    }

    // ============================================================
    // Helpers
    // ============================================================

    /// <summary>
    /// Builds a g3204 daily-bar sample with <paramref name="count"/> ascending
    /// candles. The candles are deliberately out of order so the fetcher's
    /// chronological sort is exercised; closes follow <c>startClose + i*step</c>.
    /// </summary>
    static string BuildDailyCandleSample(
        string keysymbol,
        string symbol,
        int count,
        decimal startClose,
        decimal step,
        string gubun = "2")
    {
        var sb = new StringBuilder();
        sb.Append("{\"rsp_cd\":\"00000\",\"rsp_msg\":\"OK\",\"g3204OutBlock\":{");
        sb.Append($"\"keysymbol\":\"{keysymbol}\",\"exchcd\":\"{keysymbol[..2]}\",\"symbol\":\"{symbol}\",");
        sb.Append("\"cts_date\":\"\",\"cts_info\":\"\",");
        sb.Append($"\"rec_count\":{count}}},\"g3204OutBlock1\":[");

        // Walk backwards from a fixed end date so date strings stay realistic.
        DateTime end = new(2025, 4, 30);
        bool first = true;
        for (int i = count - 1; i >= 0; i--)
        {
            if (!first) sb.Append(',');
            first = false;

            decimal close = startClose + i * step;
            string date = end.AddDays(-(count - 1 - i)).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            sb.Append('{');
            sb.Append($"\"date\":\"{date}\",");
            sb.Append($"\"open\":\"{close - 0.10m:F4}\",");
            sb.Append($"\"high\":\"{close + 0.30m:F4}\",");
            sb.Append($"\"low\":\"{close - 0.30m:F4}\",");
            sb.Append($"\"close\":\"{close:F4}\",");
            sb.Append("\"volume\":1000,\"amount\":1000000");
            sb.Append('}');
        }

        sb.Append("]}");
        _ = gubun; // gubun is requested by the caller for clarity; the sample shape is identical.
        return sb.ToString();
    }
}
