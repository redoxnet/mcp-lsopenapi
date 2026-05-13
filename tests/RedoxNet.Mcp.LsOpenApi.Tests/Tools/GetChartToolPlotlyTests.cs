using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

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

    [Fact]
    public async Task GetChart_DefaultIncludeChartFalse_NoChartField()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(DailyBody(10)));

        string result = await GetChartTool.GetChart(client, "005930", "day", count: 10);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        bool hasChart = root.TryGetProperty("chart", out JsonElement chart);
        // The field will exist (anonymous type), but it should be null when include_chart=false.
        (!hasChart || chart.ValueKind == JsonValueKind.Null).Should().BeTrue();
    }

    [Fact]
    public async Task GetChart_IncludeChartTrue_AttachesPlotlySpec()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(DailyBody(20)));

        string result = await GetChartTool.GetChart(
            client, "005930", "day", count: 20,
            indicators: new[] { "ma:5" },
            include_chart: true);

        JsonElement root = JsonDocument.Parse(result).RootElement;
        JsonElement chart = root.GetProperty("chart");
        chart.GetProperty("type").GetString().Should().Be("plotly");
        chart.GetProperty("version").GetString().Should().Be("5");

        JsonElement data = chart.GetProperty("spec").GetProperty("data");
        data.GetArrayLength().Should().BeGreaterThanOrEqualTo(3); // candlestick + ma:5 + volume

        JsonElement layout = chart.GetProperty("spec").GetProperty("layout");
        layout.GetProperty("hovermode").GetString().Should().Be("x unified");
        layout.GetProperty("xaxis").GetProperty("rangeslider").GetProperty("visible").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetChart_MultiTimeframe_IncludeChart_EachFrameHasChart()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(DailyBody(10)));

        string result = await GetChartTool.GetChart(
            client, "005930", "day,week,month", count: 10, include_chart: true);

        JsonElement frames = JsonDocument.Parse(result).RootElement.GetProperty("frames");
        frames.GetArrayLength().Should().Be(3);

        foreach (JsonElement frame in frames.EnumerateArray())
        {
            JsonElement chart = frame.GetProperty("chart");
            chart.GetProperty("type").GetString().Should().Be("plotly");
            chart.GetProperty("spec").GetProperty("data").GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
        }
    }

    [Fact]
    public async Task GetChart_IncludeChartTrue_RespectsKoreanColorConvention()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(DailyBody(5)));

        string result = await GetChartTool.GetChart(
            client, "005930", "day", count: 5, include_chart: true);

        JsonElement candle = JsonDocument.Parse(result).RootElement
            .GetProperty("chart").GetProperty("spec").GetProperty("data")[0];

        candle.GetProperty("increasing").GetProperty("line").GetProperty("color").GetString().Should().Be("#E74C3C");
        candle.GetProperty("decreasing").GetProperty("line").GetProperty("color").GetString().Should().Be("#3498DB");
    }
}
