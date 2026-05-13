using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

public class GetChartToolMultiTimeframeTests
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
    public async Task GetChart_Single_IncludesContextBlock()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(DailyBody(30)));

        string result = await GetChartTool.GetChart(
            client, "005930", "day", count: 30, indicators: new[] { "ma:5", "ma:20" });

        JsonElement root = JsonDocument.Parse(result).RootElement;
        root.TryGetProperty("context", out JsonElement context).Should().BeTrue();

        JsonElement divergence = context.GetProperty("divergence_from_ma");
        divergence.TryGetProperty("ma:5", out _).Should().BeTrue();
        divergence.TryGetProperty("ma:20", out _).Should().BeTrue();

        JsonElement volume = context.GetProperty("volume");
        volume.GetProperty("latest").GetInt64().Should().Be(1000);

        JsonElement drawdown = context.GetProperty("drawdown");
        drawdown.GetProperty("period_high").GetDecimal().Should().BeGreaterThan(0);

        JsonElement maTrend = context.GetProperty("ma_trend");
        maTrend.GetProperty("ma:5").GetString().Should().Be("up");
        maTrend.GetProperty("ma:20").GetString().Should().Be("up");

        context.GetProperty("bullish_alignment").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GetChart_MultiPeriod_ReturnsFramesArray()
    {
        int callCount = 0;
        var (client, handler) = TestClientFactory.Create((req, _) =>
        {
            callCount++;
            return Ok(DailyBody(20, startBase: 100 + callCount * 10));
        });

        string result = await GetChartTool.GetChart(
            client, "005930", "day,week,month", count: 20, indicators: new[] { "ma:5" });

        // Three TR calls (day/week/month all map to t8410 with different gubun).
        handler.Requests.Should().HaveCount(3);

        JsonElement root = JsonDocument.Parse(result).RootElement;
        root.GetProperty("shcode").GetString().Should().Be("005930");
        root.GetProperty("period_types").GetArrayLength().Should().Be(3);

        JsonElement frames = root.GetProperty("frames");
        frames.GetArrayLength().Should().Be(3);

        string[] expectedPeriods = { "day", "week", "month" };
        for (int i = 0; i < 3; i++)
        {
            JsonElement frame = frames[i];
            frame.GetProperty("period_type").GetString().Should().Be(expectedPeriods[i]);
            frame.GetProperty("tr_cd").GetString().Should().Be("t8410");
            frame.GetProperty("count").GetInt32().Should().Be(20);
            frame.TryGetProperty("context", out _).Should().BeTrue();
            frame.TryGetProperty("candles", out _).Should().BeTrue();
            frame.TryGetProperty("indicators", out _).Should().BeTrue();
        }
    }

    [Fact]
    public async Task GetChart_MultiPeriod_DispatchesDifferentGubunPerPeriod()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(DailyBody(5)));

        await GetChartTool.GetChart(client, "005930", "day,week,month", count: 5);

        handler.Requests.Should().HaveCount(3);
        string day = await handler.Requests[0].Content!.ReadAsStringAsync();
        string week = await handler.Requests[1].Content!.ReadAsStringAsync();
        string month = await handler.Requests[2].Content!.ReadAsStringAsync();

        day.Should().Contain("\"gubun\":\"2\"");
        week.Should().Contain("\"gubun\":\"3\"");
        month.Should().Contain("\"gubun\":\"4\"");
    }

    [Fact]
    public async Task GetChart_MultiPeriod_DeduplicatesRepeatedPeriods()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(DailyBody(5)));

        string result = await GetChartTool.GetChart(client, "005930", "day,day,week", count: 5);

        handler.Requests.Should().HaveCount(2);
        JsonDocument.Parse(result).RootElement.GetProperty("frames").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task GetChart_MultiPeriod_OneBadPeriod_ReturnsErrorAndDoesNotCall()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(DailyBody(5)));

        string result = await GetChartTool.GetChart(client, "005930", "day,yearly");

        handler.Requests.Should().BeEmpty();
        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString()
            .Should().Contain("yearly");
    }
}
