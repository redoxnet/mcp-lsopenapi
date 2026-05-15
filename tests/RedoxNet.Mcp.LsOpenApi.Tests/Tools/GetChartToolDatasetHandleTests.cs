using System.Net;
using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

[Collection(ChartDatasetCacheCollection.Name)]
public class GetChartToolDatasetHandleTests
{
    static Task<HttpResponseMessage> Ok(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    });

    static string DailyRow(int day, int basePrice)
    {
        DateTime date = new(2024, 1, 1);
        date = date.AddDays(day - 1);
        return $$"""{ "date":"{{date:yyyyMMdd}}", "open":{{basePrice}}, "high":{{basePrice + 10}}, "low":{{basePrice - 10}}, "close":{{basePrice + 5}}, "jdiff_vol":1000, "value":100000 }""";
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

    static JsonElement ParseText(CallToolResult result)
    {
        TextContentBlock text = (TextContentBlock)result.Content[0];
        return JsonDocument.Parse(text.Text).RootElement;
    }

    [Fact]
    public async Task AddIndicator_UsesDatasetIdAndReturnsUpdatedChart()
    {
        int call = 0;
        var (client, handler) = TestClientFactory.Create((_, _) =>
        {
            call++;
            return Ok(call == 1 ? DailyBody(60) : DailyBody(260));
        });

        CallToolResult first = await GetChartTool.GetChart(
            client, "005930", "day", count: 60, output_mode: "analyze");
        string datasetId = ParseText(first).GetProperty("dataset_id").GetString()!;

        CallToolResult updated = await GetChartTool.AddIndicator(client, datasetId, "ma:200");

        handler.Requests.Should().HaveCount(2);
        string secondBody = await handler.Requests[1].Content!.ReadAsStringAsync();
        // count(60) + max(ma:200 warm-up 200, day summary warm-up 240) = 300.
        secondBody.Should().Contain("\"qrycnt\":300");

        JsonElement text = ParseText(updated);
        text.GetProperty("dataset_id").GetString().Should().Be(datasetId);
        text.GetProperty("added_indicator").GetString().Should().Be("ma:200");
        text.GetProperty("indicator").GetProperty("latest").GetProperty("ma:200").ValueKind
            .Should().Be(JsonValueKind.Number);
        text.TryGetProperty("candles", out _).Should().BeFalse();
        text.TryGetProperty("indicators", out _).Should().BeFalse();

        updated.StructuredContent.Should().NotBeNull();
        updated.StructuredContent!.Value.GetProperty("chart")
            .GetProperty("spec").GetProperty("data")
            .GetArrayLength().Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task ReframeChart_RefetchesRequestedPeriodAndKeepsDatasetId()
    {
        int call = 0;
        var (client, handler) = TestClientFactory.Create((_, _) =>
        {
            call++;
            return Ok(call == 1 ? DailyBody(5) : DailyBody(3, startBase: 200));
        });

        CallToolResult first = await GetChartTool.GetChart(
            client, "005930", "day", count: 5, output_mode: "reference");
        string datasetId = ParseText(first).GetProperty("dataset_id").GetString()!;

        CallToolResult reframed = await GetChartTool.ReframeChart(
            client, datasetId, "week", count: 3, include_chart: false);

        handler.Requests.Should().HaveCount(2);
        string secondBody = await handler.Requests[1].Content!.ReadAsStringAsync();
        secondBody.Should().Contain("\"gubun\":\"3\"");
        // count(3) + the week summary warm-up(120) = 123.
        secondBody.Should().Contain("\"qrycnt\":123");

        JsonElement text = ParseText(reframed);
        text.GetProperty("dataset_id").GetString().Should().Be(datasetId);
        text.GetProperty("period_type").GetString().Should().Be("week");
        text.GetProperty("count").GetInt32().Should().Be(3);
        text.TryGetProperty("summary", out _).Should().BeTrue();
        text.TryGetProperty("candles", out _).Should().BeFalse();
        reframed.StructuredContent.Should().BeNull();

        CallToolResult addAfterReframe = await GetChartTool.AddIndicator(
            client, datasetId, "ma:2", include_chart: false);
        JsonElement addText = ParseText(addAfterReframe);
        addText.TryGetProperty("error", out _).Should().BeFalse(
            "reframe should replace the current view, not leave a multi-frame dataset requiring period_type");
        addText.GetProperty("period_type").GetString().Should().Be("week");
    }
}
