using System.Net;
using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

/// <summary>
/// End-to-end tests for the <c>include_chart=true</c> path of
/// <see cref="GetEtfHoldingsTool"/>, asserting that structuredContent
/// carries the treemap spec and the side panel produced by
/// <see cref="RedoxNet.LsOpenApi.Core.Charting.EtfHoldingsChartBuilder"/>.
/// </summary>
public class GetEtfHoldingsToolPlotlyTests
{
    static Task<HttpResponseMessage> Ok(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    });

    /// <summary>Synthetic 4-holding ETF response. Weights are
    /// 50 / 25 / 15 / 10 → top-5 cumulative = 100 → 초집중형 badge.</summary>
    const string T1904FourHoldings = """
    {
      "rsp_cd": "00000", "rsp_msg": "정상",
      "t1904OutBlock": {
        "date": "20260514", "chk_tday": "1",
        "price": 12000, "sign": "2", "change": 100, "diff": "0.83",
        "volume": 1000,
        "nav": "12000.00", "navsign": "2", "navchange": "100.00", "navdiff": "0.83",
        "jnilnav": "11900.00", "jnilnavsign": "5", "jnilnavchange": "0", "jnilnavdiff": "0",
        "etfnum": 4, "etfcunum": 100, "etftotcap": 500,
        "tot_pval": 1000000, "tot_sigatval": 50000000, "cash": 500000,
        "upcode": "000"
      },
      "t1904OutBlock1": [
        {
          "shcode": "373220", "hname": "LG에너지솔루션",
          "weight": "50.00", "price": 400000, "sign": "2", "change": 0, "diff": "0", "diff2": "0",
          "volume": 0, "value": 250,
          "pvalue": 0, "sigatvalue": 0, "parprice": 0, "profitdate": "", "icux": 0
        },
        {
          "shcode": "005930", "hname": "삼성전자",
          "weight": "25.00", "price": 70000, "sign": "2", "change": 0, "diff": "0", "diff2": "0",
          "volume": 0, "value": 125,
          "pvalue": 0, "sigatvalue": 0, "parprice": 0, "profitdate": "", "icux": 0
        },
        {
          "shcode": "000660", "hname": "SK하이닉스",
          "weight": "15.00", "price": 200000, "sign": "2", "change": 0, "diff": "0", "diff2": "0",
          "volume": 0, "value": 75,
          "pvalue": 0, "sigatvalue": 0, "parprice": 0, "profitdate": "", "icux": 0
        },
        {
          "shcode": "207940", "hname": "삼성바이오로직스",
          "weight": "10.00", "price": 900000, "sign": "2", "change": 0, "diff": "0", "diff2": "0",
          "volume": 0, "value": 50,
          "pvalue": 0, "sigatvalue": 0, "parprice": 0, "profitdate": "", "icux": 0
        }
      ]
    }
    """;

    [Fact]
    public async Task IncludeChartFalse_NoStructuredContent()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(T1904FourHoldings));

        CallToolResult result = await GetEtfHoldingsTool.GetEtfHoldings(client, "305720");

        result.StructuredContent.Should().BeNull();

        JsonElement text = JsonDocument.Parse(result.TextContent()).RootElement;
        text.GetProperty("chart_available").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task IncludeChartTrue_ShipsTreemapAsStructuredContent()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(T1904FourHoldings));

        CallToolResult result = await GetEtfHoldingsTool.GetEtfHoldings(
            client, "305720", include_chart: true);

        result.StructuredContent.Should().NotBeNull();
        JsonElement sc = result.StructuredContent!.Value;

        JsonElement chart = sc.GetProperty("chart");
        chart.GetProperty("type").GetString().Should().Be("plotly");
        chart.GetProperty("version").GetString().Should().Be("5");

        JsonElement trace = chart.GetProperty("spec").GetProperty("data")[0];
        trace.GetProperty("type").GetString().Should().Be("treemap");
        trace.GetProperty("labels").GetArrayLength().Should().Be(4);
        // First label is the heaviest holding.
        trace.GetProperty("labels")[0].GetString().Should().Be("LG에너지솔루션");
    }

    [Fact]
    public async Task IncludeChartTrue_PanelHasTopHoldingsAndBadge()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(T1904FourHoldings));

        CallToolResult result = await GetEtfHoldingsTool.GetEtfHoldings(
            client, "305720", include_chart: true);

        JsonElement panel = result.StructuredContent!.Value.GetProperty("panel");
        panel.GetProperty("kind").GetString().Should().Be("etf_holdings");
        panel.GetProperty("title").GetString().Should().Contain("305720");

        // Top-5 cumulative = 100% → 초집중형
        panel.GetProperty("concentration").GetProperty("badge").GetString().Should().Be("초집중형");

        JsonElement top = panel.GetProperty("top_holdings");
        top.GetArrayLength().Should().Be(4);
        top[0].GetProperty("name").GetString().Should().Be("LG에너지솔루션");
        top[0].GetProperty("weight_pct").GetDouble().Should().Be(50.0);
        top[0].GetProperty("cumulative_pct").GetDouble().Should().Be(50.0);
        top[1].GetProperty("cumulative_pct").GetDouble().Should().Be(75.0);
    }

    [Fact]
    public async Task IncludeChartTrue_FlagsHeavyweightOver30Percent()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(T1904FourHoldings));

        CallToolResult result = await GetEtfHoldingsTool.GetEtfHoldings(
            client, "305720", include_chart: true);

        JsonElement notes = result.StructuredContent!.Value
            .GetProperty("panel").GetProperty("notes");

        bool hasHeavyweightNote = false;
        foreach (JsonElement n in notes.EnumerateArray())
            if (n.GetString()!.Contains("LG에너지솔루션") && n.GetString()!.Contains("30%"))
                hasHeavyweightNote = true;

        hasHeavyweightNote.Should().BeTrue();
    }

    [Fact]
    public async Task IncludeChartTrue_TextHasChartAvailableFlag()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(T1904FourHoldings));

        CallToolResult result = await GetEtfHoldingsTool.GetEtfHoldings(
            client, "305720", include_chart: true);

        JsonElement text = JsonDocument.Parse(result.TextContent()).RootElement;
        text.GetProperty("chart_available").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task IncludeChartTrue_EmptyHoldings_NoStructuredContent()
    {
        // LS sometimes returns rsp_cd=00000 with an empty OutBlock1 (esp. on
        // the virtual server). The tool already surfaces that as a text-side
        // error; structuredContent should stay null.
        const string EmptyResponse = """
        {
          "rsp_cd": "00000", "rsp_msg": "해당자료가 없습니다",
          "t1904OutBlock1": []
        }
        """;
        var (client, _) = TestClientFactory.Create((_, _) => Ok(EmptyResponse));

        CallToolResult result = await GetEtfHoldingsTool.GetEtfHoldings(
            client, "305720", include_chart: true);

        result.StructuredContent.Should().BeNull();
    }
}
