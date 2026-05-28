using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

public sealed class GetMarketFundsTrendToolTests
{
    const string Sample = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "정상적으로 조회가 완료되었습니다.",
      "t8428OutBlock": { "date": "20260420", "idx": 1 },
      "t8428OutBlock1": [
        { "date": "20260518", "jisu": "7516.04", "sign": "2", "change": "22.86", "diff": "0.31",
          "volume": 566618, "custmoney": 1329853, "yecha": 1256, "vol": "44.43", "outmoney": 14601,
          "trjango": 363967, "futymoney": 417855, "stkmoney": 3451859, "mstkmoney": 115555,
          "mbndmoney": 160565, "bndmoney": 1227608, "bndsmoney": 0, "mmfmoney": 1757638 },
        { "date": "20260515", "jisu": "7493.18", "sign": "5", "change": "23.10", "diff": "0.30",
          "volume": 500000, "custmoney": 1328597, "yecha": -6491, "vol": "40.00", "outmoney": 14000,
          "trjango": 365675, "futymoney": 410000, "stkmoney": 3400000, "mstkmoney": 115000,
          "mbndmoney": 160000, "bndmoney": 1220000, "bndsmoney": 0, "mmfmoney": 1750000 }
      ]
    }
    """;

    static Task<HttpResponseMessage> Ok(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    });

    [Fact]
    public async Task GetMarketFundsTrend_Sample_ShapesPayloadAndSummary()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(Sample));

        string result = await GetMarketFundsTrendTool.GetMarketFundsTrend(client);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t8428");
        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("\"upcode\":\"001\"", "kospi maps to upcode 001");
        body.Should().Contain("\"gubun\":\"1\"");

        root.GetProperty("market").GetString().Should().Be("kospi");
        root.GetProperty("data_as_of").GetString().Should().MatchRegex(@"^\d{8}$");
        // v1.6 §3.2 — query_date_resolution removed; data_as_of stays as the
        // sole envelope field (factual, not classification).
        root.TryGetProperty("query_date_resolution", out _).Should().BeFalse();
        root.GetProperty("count").GetInt32().Should().Be(2);

        JsonElement first = root.GetProperty("series")[0];
        first.GetProperty("date").GetString().Should().Be("20260518");
        first.GetProperty("index").GetDouble().Should().BeApproximately(7516.04, 1e-2);
        first.GetProperty("change_pct").GetDouble().Should().BeApproximately(0.31, 1e-2);
        first.GetProperty("investor_deposit").GetInt64().Should().Be(1329853);
        first.GetProperty("credit_balance").GetInt64().Should().Be(363967);

        // series[1] sign=5 flips the change.
        root.GetProperty("series")[1].GetProperty("change_pct").GetDouble()
            .Should().BeApproximately(-0.30, 1e-2);

        // summary = latest (series[0]) minus oldest (series[^1]).
        JsonElement summary = root.GetProperty("summary");
        summary.GetProperty("investor_deposit_change").GetInt64().Should().Be(1329853 - 1328597);
        summary.GetProperty("credit_balance_change").GetInt64().Should().Be(363967 - 365675);
    }

    [Fact]
    public async Task GetMarketFundsTrend_Kosdaq_MapsUpcode301()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(Sample));

        await GetMarketFundsTrendTool.GetMarketFundsTrend(client, market: "kosdaq");

        (await handler.Requests[0].Content!.ReadAsStringAsync()).Should().Contain("\"upcode\":\"301\"");
    }

    [Fact]
    public async Task GetMarketFundsTrend_QueryDate_ForwardedToLsVerbatim_AndAnchorsToActualLatestRow()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(Sample));

        // v1.6 §3.1 — the WeekendOnlyCalendar / DateEnvelope.Resolve abstractions
        // were deleted; we no longer clamp weekends client-side. LS handles the
        // weekend / holiday rollback server-side. The wrapper forwards the
        // user's query_date verbatim and the response's actual latest row date
        // becomes data_as_of (the latest publishing date that LS could honor).
        string result = await GetMarketFundsTrendTool.GetMarketFundsTrend(client, query_date: "20260524");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("\"tdate\":\"20260524\"", "query_date is now forwarded to LS verbatim");
        // data_as_of still reflects the actual latest series row in the response;
        // the Sample fixture's latest row is 20260518.
        root.GetProperty("data_as_of").GetString().Should().Be("20260518");
        root.TryGetProperty("query_date_resolution", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetMarketFundsTrend_InvalidMarket_ReturnsValidationError()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(Sample));

        string result = await GetMarketFundsTrendTool.GetMarketFundsTrend(client, market: "konex");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        handler.Requests.Should().BeEmpty();
        root.GetProperty("error").GetString().Should().Contain("market");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(121)]
    public async Task GetMarketFundsTrend_CountOutOfRange_ReturnsValidationError(int count)
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(Sample));

        string result = await GetMarketFundsTrendTool.GetMarketFundsTrend(client, count: count);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("count");
    }
}
