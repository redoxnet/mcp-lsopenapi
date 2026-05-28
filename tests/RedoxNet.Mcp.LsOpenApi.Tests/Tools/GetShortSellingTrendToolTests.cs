using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

public sealed class GetShortSellingTrendToolTests
{
    const string Sample = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "정상적으로 조회가 완료되었습니다.",
      "t1927OutBlock": { "date": "20230502" },
      "t1927OutBlock1": [
        { "date": "20230601", "price": 70900, "sign": "5", "change": 500, "diff": "-0.70",
          "volume": 14566407, "value": 1034489, "gm_vo": 315193, "gm_va": 22428, "gm_per": "2.16",
          "gm_avg": 71158, "gm_vo_sum": 15254680, "gm_vo1": 207440, "gm_va1": 14743,
          "gm_vo2": 107753, "gm_va2": 1169 },
        { "date": "20230531", "price": 71400, "sign": "5", "change": 900, "diff": "-1.24",
          "volume": 24153085, "value": 1732624, "gm_vo": 856055, "gm_va": 61605, "gm_per": "3.54",
          "gm_avg": 71964, "gm_vo_sum": 14939487, "gm_vo1": 526062, "gm_va1": 37825,
          "gm_vo2": 329993, "gm_va2": 23780 }
      ]
    }
    """;

    static Task<HttpResponseMessage> Ok(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    });

    [Fact]
    public async Task GetShortSellingTrend_Sample_ShapesPayloadAndSummary()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(Sample));

        string result = await GetShortSellingTrendTool.GetShortSellingTrend(client, "005930");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t1927");
        (await handler.Requests[0].Content!.ReadAsStringAsync()).Should().Contain("\"shcode\":\"005930\"");

        root.GetProperty("count").GetInt32().Should().Be(2);
        root.GetProperty("data_as_of").GetString().Should().MatchRegex(@"^\d{8}$");
        // v1.6 §3.2 — query_date_resolution removed; data_as_of is the sole envelope field.
        root.TryGetProperty("query_date_resolution", out _).Should().BeFalse();

        JsonElement first = root.GetProperty("series")[0];
        first.GetProperty("date").GetString().Should().Be("20230601");
        first.GetProperty("close").GetInt64().Should().Be(70900);
        first.GetProperty("change_pct").GetDouble().Should().BeApproximately(-0.70, 1e-2, "sign=5 flips the magnitude");
        first.GetProperty("short_volume").GetInt64().Should().Be(315193);
        first.GetProperty("short_value").GetInt64().Should().Be(22428);
        first.GetProperty("short_ratio_pct").GetDouble().Should().BeApproximately(2.16, 1e-2);

        JsonElement summary = root.GetProperty("summary");
        summary.GetProperty("trading_days").GetInt32().Should().Be(2);
        summary.GetProperty("total_short_volume").GetInt64().Should().Be(315193 + 856055);
        summary.GetProperty("total_short_value").GetInt64().Should().Be(22428 + 61605);
        summary.GetProperty("highest_ratio_day").GetProperty("date").GetString().Should().Be("20230531");
    }

    [Fact]
    public async Task GetShortSellingTrend_ExplicitDates_ForwardedToRequest()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(Sample));

        await GetShortSellingTrendTool.GetShortSellingTrend(client, "005930", from: "20230501", to: "20230601");

        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("\"sdate\":\"20230501\"");
        body.Should().Contain("\"edate\":\"20230601\"");
    }

    [Fact]
    public async Task GetShortSellingTrend_QueryDate_ForwardedToLsVerbatim_AndAnchorsToActualLatestRow()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(Sample));

        // v1.6 §3.1 — WeekendOnlyCalendar removed. LS handles weekend/holiday
        // clamping server-side; the wrapper now forwards query_date verbatim
        // and the response's actual latest row date becomes data_as_of.
        string result = await GetShortSellingTrendTool.GetShortSellingTrend(
            client,
            "005930",
            query_date: "20260524");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("\"edate\":\"20260524\"", "query_date is now forwarded verbatim");
        // data_as_of reflects the actual latest series row. The Sample fixture's
        // latest row is 20230601; data_as_of anchors there.
        root.GetProperty("data_as_of").GetString().Should().Be("20230601");
        root.TryGetProperty("query_date_resolution", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetShortSellingTrend_EmptyShcode_ReturnsValidationError()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(Sample));

        string result = await GetShortSellingTrendTool.GetShortSellingTrend(client, "");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        handler.Requests.Should().BeEmpty();
        root.GetProperty("error").GetString().Should().Contain("shcode");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    public async Task GetShortSellingTrend_CountOutOfRange_ReturnsValidationError(int count)
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(Sample));

        string result = await GetShortSellingTrendTool.GetShortSellingTrend(client, "005930", count: count);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("count");
    }
}
