using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

[Collection(ChartDatasetCacheCollection.Name)]
public class GetChartToolTests
{
    static Task<HttpResponseMessage> Ok(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    });

    static string DailyRow(int day, int basePrice) =>
        $$"""{ "date":"202401{{day:D2}}", "open":{{basePrice}}, "high":{{basePrice + 10}}, "low":{{basePrice - 10}}, "close":{{basePrice + 5}}, "jdiff_vol":1000, "value":100000 }""";

    [Fact]
    public async Task GetChart_Day_ReturnsCandlesAndDispatchesT8410()
    {
        string body = $$"""
        {
          "t8410OutBlock": { "shcode":"005930" },
          "t8410OutBlock1": [
            {{DailyRow(2, 100)}},
            {{DailyRow(3, 102)}},
            {{DailyRow(4, 104)}}
          ],
          "rsp_cd": "00000",
          "rsp_msg": "정상"
        }
        """;
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(body));

        string result = await GetChartTool.GetChart(
            client, "005930", "day", count: 3, output_mode: "export").TextContent();

        handler.Requests.Should().HaveCount(1);
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/stock/chart");
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t8410");

        JsonElement root = JsonDocument.Parse(result).RootElement;
        root.GetProperty("tr_cd").GetString().Should().Be("t8410");
        root.GetProperty("count").GetInt32().Should().Be(3);
        root.GetProperty("candles").GetArrayLength().Should().Be(3);
        root.GetProperty("candles")[0].GetProperty("close").GetDecimal().Should().Be(105m);
    }

    [Fact]
    public async Task GetChart_Min_DispatchesT8412WithMinuteUnit()
    {
        string body = $$"""
        {
          "t8412OutBlock": { "shcode":"005930" },
          "t8412OutBlock1": [
            { "date":"20240102", "time":"090000", "open":100, "high":110, "low":90, "close":105, "jdiff_vol":1000, "value":100000 }
          ],
          "rsp_cd": "00000", "rsp_msg": "정상"
        }
        """;
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(body));

        string result = await GetChartTool.GetChart(client, "005930", "min", count: 1, minute_unit: 3).TextContent();

        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t8412");
        string sent = await handler.Requests[0].Content!.ReadAsStringAsync();
        sent.Should().Contain("\"ncnt\":3");

        JsonDocument.Parse(result).RootElement.GetProperty("tr_cd").GetString().Should().Be("t8412");
    }

    [Fact]
    public async Task GetChart_UnknownPeriod_ReturnsError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok("""{"rsp_cd":"00000"}"""));

        string result = await GetChartTool.GetChart(client, "005930", "yearly").TextContent();

        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString()
            .Should().Contain("period_type");
    }

    [Fact]
    public async Task GetChart_WithIndicators_AddsAlignedSeries()
    {
        var rows = new System.Text.StringBuilder();
        for (int day = 1; day <= 30; day++)
        {
            if (rows.Length > 0) rows.Append(',');
            rows.Append(DailyRow(day, 100 + day));
        }
        string body = $$"""
        {
          "t8410OutBlock": { "shcode":"005930" },
          "t8410OutBlock1": [ {{rows}} ],
          "rsp_cd": "00000", "rsp_msg": "정상"
        }
        """;
        var (client, _) = TestClientFactory.Create((_, _) => Ok(body));

        string result = await GetChartTool.GetChart(
            client, "005930", "day", count: 30, indicators: new[] { "ma:5" }, output_mode: "export").TextContent();

        JsonElement root = JsonDocument.Parse(result).RootElement;
        root.GetProperty("indicators").TryGetProperty("ma:5", out JsonElement ma).Should().BeTrue();
        ma.GetArrayLength().Should().Be(30);
        ma[0].ValueKind.Should().Be(JsonValueKind.Null);
        ma[4].ValueKind.Should().Be(JsonValueKind.Number);
    }

    [Fact]
    public async Task GetChart_InvalidIndicator_ReturnsError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok("""{"rsp_cd":"00000"}"""));

        string result = await GetChartTool.GetChart(
            client, "005930", "day", indicators: new[] { "bogus:1" }).TextContent();

        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString()
            .Should().Contain("bogus");
    }

    [Fact]
    public async Task GetChart_InvalidOutputMode_ReturnsError()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok("""{"rsp_cd":"00000"}"""));

        string result = await GetChartTool.GetChart(
            client, "005930", "day", output_mode: "full").TextContent();

        handler.Requests.Should().BeEmpty();
        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString()
            .Should().Contain("output_mode");
    }

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
    public async Task GetChart_SummaryOnly_TrimsCandlesAndCollapsesIndicators()
    {
        // 30 candles, ma:5 indicator. summary_only must:
        //  - keep only the last 5 candles
        //  - collapse 'indicators' to a dict of scalar last values (not arrays)
        //  - keep 'count' as the full underlying count
        //  - keep 'context' intact
        var (client, _) = TestClientFactory.Create((_, _) => Ok(DailyBody(30)));

        string result = await GetChartTool.GetChart(
            client, "005930", "day", count: 30, indicators: new[] { "ma:5" }, summary_only: true, output_mode: "export").TextContent();

        JsonElement root = JsonDocument.Parse(result).RootElement;
        root.GetProperty("summary_only").GetBoolean().Should().BeTrue();
        root.GetProperty("count").GetInt32().Should().Be(30);
        root.GetProperty("candles").GetArrayLength().Should().Be(5);

        JsonElement indicators = root.GetProperty("indicators");
        indicators.TryGetProperty("ma:5", out JsonElement ma).Should().BeTrue();
        ma.ValueKind.Should().Be(JsonValueKind.Number);

        root.TryGetProperty("context", out JsonElement context).Should().BeTrue();
        context.GetProperty("volume").GetProperty("latest").GetInt64().Should().Be(1000);
    }

    [Fact]
    public async Task GetChart_SummaryOnly_MultiTimeframe_TrimsEachFrame()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(DailyBody(30)));

        string result = await GetChartTool.GetChart(
            client, "005930", "day,week", count: 30, indicators: new[] { "ma:5" }, summary_only: true, output_mode: "export").TextContent();

        JsonElement root = JsonDocument.Parse(result).RootElement;
        root.GetProperty("summary_only").GetBoolean().Should().BeTrue();

        JsonElement frames = root.GetProperty("frames");
        frames.GetArrayLength().Should().Be(2);
        foreach (JsonElement frame in frames.EnumerateArray())
        {
            frame.GetProperty("count").GetInt32().Should().Be(30);
            frame.GetProperty("candles").GetArrayLength().Should().Be(5);
            frame.GetProperty("indicators").GetProperty("ma:5").ValueKind.Should().Be(JsonValueKind.Number);
            frame.TryGetProperty("context", out _).Should().BeTrue();
        }
    }

    [Fact]
    public async Task GetChart_ExportMode_ReturnsFullSeries()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(DailyBody(30)));

        string result = await GetChartTool.GetChart(
            client, "005930", "day", count: 30, indicators: new[] { "ma:5" }, output_mode: "export").TextContent();

        JsonElement root = JsonDocument.Parse(result).RootElement;
        root.GetProperty("output_mode").GetString().Should().Be("export");
        root.GetProperty("candles").GetArrayLength().Should().Be(30);
        root.GetProperty("indicators").GetProperty("ma:5").GetArrayLength().Should().Be(30);
    }

    [Fact]
    public async Task GetChart_DefaultAnalyzeMode_ReturnsSummaryAndNoRawArrays()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(DailyBody(30)));

        string result = await GetChartTool.GetChart(
            client, "005930", "day", count: 30, indicators: new[] { "ma:5" }).TextContent();

        JsonElement root = JsonDocument.Parse(result).RootElement;
        root.GetProperty("output_mode").GetString().Should().Be("analyze");
        root.GetProperty("dataset_id").GetString().Should().StartWith("ds_");
        root.TryGetProperty("summary", out JsonElement summary).Should().BeTrue();
        summary.GetProperty("latest_close").GetDecimal().Should().BeGreaterThan(0);
        root.TryGetProperty("context", out _).Should().BeTrue();
        root.TryGetProperty("candles", out _).Should().BeFalse();
        root.TryGetProperty("indicators", out _).Should().BeFalse();
    }
}
