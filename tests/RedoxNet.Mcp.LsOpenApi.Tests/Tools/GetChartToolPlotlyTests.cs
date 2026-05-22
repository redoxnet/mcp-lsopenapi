using System.Net;
using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

[Collection(ChartDatasetCacheCollection.Name)]
public class GetChartToolPlotlyTests
{
    static Task<HttpResponseMessage> Ok(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    });

    static string DailyRow(int day, int basePrice) =>
        $$"""{ "date":"202401{{day:D2}}", "open":{{basePrice}}, "high":{{basePrice + 10}}, "low":{{basePrice - 10}}, "close":{{basePrice + 5}}, "jdiff_vol":1000, "value":100000 }""";

    static string DailyBody(int rows, int startBase = 100)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < rows; i++)
        {
            if (sb.Length > 0) sb.Append(',');
            sb.Append(DailyRow(i + 1, startBase + i));
        }
        return $$"""{ "t8410OutBlock": { "shcode":"005930" }, "t8410OutBlock1": [ {{sb}} ], "rsp_cd":"00000", "rsp_msg":"정상" }""";
    }

    /// <summary>Extracts the text body that the model sees from a CallToolResult.</summary>
    static JsonElement ParseTextContent(CallToolResult result)
    {
        TextContentBlock text = (TextContentBlock)result.Content[0];
        return JsonDocument.Parse(text.Text).RootElement;
    }

    [Fact]
    public async Task GetChart_DefaultIncludeChartFalse_NoStructuredContent()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(DailyBody(10)));

        CallToolResult result = await GetChartTool.GetChart(client, "005930", "day", count: 10);

        result.StructuredContent.Should().BeNull(
            "include_chart=false must not ship an inline chart spec");

        JsonElement text = ParseTextContent(result);
        text.TryGetProperty("chart_available", out _).Should().BeFalse(
            "the chart_available marker was removed in v1.2");
    }

    [Fact]
    public async Task GetChart_IncludeChartTrue_ShipsPlotlySpecAsStructuredContent()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(DailyBody(20)));

        CallToolResult result = await GetChartTool.GetChart(
            client, "005930", "day", count: 20,
            indicators: new[] { "ma:5" },
            include_chart: true);

        // Model-facing text: summary + dataset_id, no spec, no marker.
        JsonElement text = ParseTextContent(result);
        text.TryGetProperty("chart_available", out _).Should().BeFalse(
            "the chart_available marker was removed in v1.2 — the host decides chart visibility");
        text.GetProperty("output_mode").GetString().Should().Be("display");
        text.GetProperty("dataset_id").GetString().Should().StartWith("ds_");
        text.TryGetProperty("summary", out _).Should().BeTrue();
        text.TryGetProperty("candles", out _).Should().BeFalse(
            "display mode keeps raw OHLCV out of the model-facing text");
        text.TryGetProperty("indicators", out _).Should().BeFalse(
            "display mode keeps full indicator arrays out of the model-facing text");
        text.TryGetProperty("chart", out _).Should().BeFalse(
            "the Plotly spec must not leak into the model's text context");

        // structuredContent: full Plotly v5 spec for the iframe to render.
        result.StructuredContent.Should().NotBeNull();
        JsonElement chart = result.StructuredContent!.Value.GetProperty("chart");
        chart.GetProperty("type").GetString().Should().Be("plotly");
        chart.GetProperty("version").GetString().Should().Be("5");

        JsonElement data = chart.GetProperty("spec").GetProperty("data");
        data.GetArrayLength().Should().BeGreaterThanOrEqualTo(3); // candlestick + ma:5 + volume

        JsonElement layout = chart.GetProperty("spec").GetProperty("layout");
        layout.GetProperty("hovermode").GetString().Should().Be("x unified");
        layout.GetProperty("xaxis").GetProperty("rangeslider").GetProperty("visible").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetChart_MultiTimeframe_IncludeChartTrue_NoStructuredContent_TextNotesLimitation()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(DailyBody(10)));

        CallToolResult result = await GetChartTool.GetChart(
            client, "005930", "day,week,month", count: 10, include_chart: true);

        result.StructuredContent.Should().BeNull(
            "multi-timeframe inline rendering is not supported in v0.2.0 — users call once per period_type");

        JsonElement text = ParseTextContent(result);
        text.GetProperty("frames").GetArrayLength().Should().Be(3);
        text.GetProperty("chart_note").GetString()
            .Should().Contain("single-timeframe only");

        // No `chart` key leaks into any frame either.
        foreach (JsonElement frame in text.GetProperty("frames").EnumerateArray())
            frame.TryGetProperty("chart", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetChart_IncludeChartTrue_RespectsKoreanColorConvention()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(DailyBody(5)));

        CallToolResult result = await GetChartTool.GetChart(
            client, "005930", "day", count: 5, include_chart: true);

        JsonElement candle = result.StructuredContent!.Value
            .GetProperty("chart").GetProperty("spec").GetProperty("data")[0];

        candle.GetProperty("increasing").GetProperty("line").GetProperty("color").GetString().Should().Be("#E74C3C");
        candle.GetProperty("decreasing").GetProperty("line").GetProperty("color").GetString().Should().Be("#3498DB");
    }
}
