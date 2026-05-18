using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

public sealed class GetIndexHistoryToolTests
{
    // KOSPI two-day sample combining the guide's single-bar response (todo/t1514.txt)
    // with a synthetic earlier bar so we can verify the array maps multiple rows.
    // cts_date is set so we exercise the pagination cursor in the payload.
    const string KospiTwoDaySample = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "정상적으로 조회가 완료되었습니다.",
      "t1514OutBlock": { "cts_date": "20230602" },
      "t1514OutBlock1": [
        {
          "date": "20230605",
          "jisu": "2610.62",
          "sign": "2",
          "change": "9.26",
          "diff": "0.36",
          "volume": 263165,
          "diff_vol": "46.20",
          "value1": 3884240,
          "value2": 3884240,
          "high": 606, "unchg": 91, "low": 253, "up": 0, "down": 0, "totjo": 950,
          "uprate": "63.79",
          "openjisu": "2617.43",
          "highjisu": "2617.58",
          "lowjisu":  "2610.40",
          "frgsvolume": 351,
          "orgsvolume": 1210,
          "upcode": "001",
          "rate": "0.00",
          "divrate": "0.00"
        },
        {
          "date": "20230602",
          "jisu": "2601.36",
          "sign": "5",
          "change": "12.50",
          "diff": "0.48",
          "volume": 569620,
          "diff_vol": "12.10",
          "value1": 9383535,
          "value2": 9383535,
          "high": 400, "unchg": 80, "low": 470, "up": 1, "down": 2, "totjo": 950,
          "uprate": "42.11",
          "openjisu": "2614.00",
          "highjisu": "2620.10",
          "lowjisu":  "2598.20",
          "frgsvolume": -120,
          "orgsvolume": 890,
          "upcode": "001",
          "rate": "0.00",
          "divrate": "0.00"
        }
      ]
    }
    """;

    static Task<HttpResponseMessage> Ok(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    });

    [Fact]
    public async Task GetIndexHistory_DefaultDayKospi_ShapesPayloadAndPropagatesGubun2()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(KospiTwoDaySample));

        string result = await GetIndexHistoryTool.GetIndexHistory(client, "kospi");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t1514");
        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("\"upcode\":\"001\"");
        body.Should().Contain("\"gubun2\":\"1\"", "period_type=day maps to gubun2=1");
        body.Should().Contain("\"rate_gbn\":\"1\"");

        root.GetProperty("index_code").GetString().Should().Be("001");
        root.GetProperty("period_type").GetString().Should().Be("day");
        root.GetProperty("count").GetInt32().Should().Be(2);
        root.GetProperty("cts_date").GetString().Should().Be("20230602", "pagination cursor surfaces when LS reports more pages");

        JsonElement points = root.GetProperty("points");
        points.GetArrayLength().Should().Be(2);

        JsonElement first = points[0];
        first.GetProperty("date").GetString().Should().Be("20230605");
        first.GetProperty("close").GetDouble().Should().BeApproximately(2610.62, 1e-2);
        first.GetProperty("change").GetDouble().Should().BeApproximately(9.26, 1e-2, "sign=2 (up) keeps change positive");
        first.GetProperty("change_pct").GetDouble().Should().BeApproximately(0.36, 1e-2);
        first.GetProperty("open").GetDouble().Should().BeApproximately(2617.43, 1e-2);
        first.GetProperty("volume").GetInt64().Should().Be(263165);
        first.GetProperty("value").GetInt64().Should().Be(3884240, "value1 is the canonical 거래대금");

        JsonElement firstBreadth = first.GetProperty("breadth");
        firstBreadth.GetProperty("advance").GetInt64().Should().Be(606);
        firstBreadth.GetProperty("decline").GetInt64().Should().Be(253);
        firstBreadth.GetProperty("unchanged").GetInt64().Should().Be(91);

        JsonElement firstFlows = first.GetProperty("flows");
        firstFlows.GetProperty("foreign_net").GetInt64().Should().Be(351);
        firstFlows.GetProperty("institution_net").GetInt64().Should().Be(1210);

        // Second bar carries sign=5 (down) — ApplySign must flip the change values.
        JsonElement second = points[1];
        second.GetProperty("change").GetDouble().Should().BeApproximately(-12.50, 1e-2, "sign=5 flips the unsigned magnitude to negative");
        second.GetProperty("change_pct").GetDouble().Should().BeApproximately(-0.48, 1e-2);
        second.GetProperty("flows").GetProperty("foreign_net").GetInt64().Should().Be(-120);
    }

    [Theory]
    [InlineData("day", "1")]
    [InlineData("week", "2")]
    [InlineData("month", "3")]
    [InlineData("일봉", "1")]
    [InlineData("주봉", "2")]
    [InlineData("월봉", "3")]
    public async Task GetIndexHistory_PeriodTypeMapping(string input, string expectedGubun2)
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(KospiTwoDaySample));

        await GetIndexHistoryTool.GetIndexHistory(client, "kospi", input);

        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain($"\"gubun2\":\"{expectedGubun2}\"");
    }

    [Fact]
    public async Task GetIndexHistory_MinPeriodType_RejectedWithValidationError()
    {
        // Minute-level index series are intentionally out of scope for the v0.7
        // wrapper — the t1514 gubun2=4 minute mode opens a different cadence
        // contract and is being deferred to a later milestone. Document the
        // refusal via the envelope so callers don't silently fall through.
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(KospiTwoDaySample));

        string result = await GetIndexHistoryTool.GetIndexHistory(client, "kospi", "min");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        handler.Requests.Should().BeEmpty("validation error short-circuits before any TR call");
        root.GetProperty("error").GetString().Should().Contain("period_type");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(501)]
    public async Task GetIndexHistory_CountOutOfRange_ReturnsValidationError(int count)
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(KospiTwoDaySample));

        string result = await GetIndexHistoryTool.GetIndexHistory(client, "kospi", "day", count);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("count");
    }

    [Fact]
    public async Task GetIndexHistory_NoCtsDate_OmittedFromPayload()
    {
        // When LS reports no further pages, t1514OutBlock.cts_date arrives as
        // an empty string. The wrapper should omit cts_date from the envelope
        // so callers can use `?? null` semantics on the JSON property.
        const string lastPage = """
        {
          "rsp_cd": "00000",
          "rsp_msg": "OK",
          "t1514OutBlock": { "cts_date": "        " },
          "t1514OutBlock1": [
            { "date": "20230601", "jisu": "2589.0", "sign": "3", "change": "0", "diff": "0",
              "volume": 100, "value1": 100, "value2": 100, "openjisu": "2589", "highjisu": "2590", "lowjisu": "2588",
              "high": 100, "low": 100, "unchg": 750, "up": 0, "down": 0, "totjo": 950, "uprate": "10.5",
              "frgsvolume": 0, "orgsvolume": 0, "upcode": "001", "rate": "0", "divrate": "0", "diff_vol": "0" }
          ]
        }
        """;
        var (client, _) = TestClientFactory.Create((_, _) => Ok(lastPage));

        string result = await GetIndexHistoryTool.GetIndexHistory(client, "kospi");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.TryGetProperty("cts_date", out _).Should().BeFalse("empty/whitespace cts_date is omitted from the envelope");
    }

    [Fact]
    public async Task GetIndexHistory_CtsDateCursor_ForwardedToLsRequest()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(KospiTwoDaySample));

        await GetIndexHistoryTool.GetIndexHistory(client, "kospi", "day", count: 60, cts_date: "20230601");

        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("\"cts_date\":\"20230601\"", "cursor flows through to t1514 as-is");
    }

    [Fact]
    public async Task GetIndexHistory_UnknownAlias_ReturnsValidationError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(KospiTwoDaySample));

        string result = await GetIndexHistoryTool.GetIndexHistory(client, "nope");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("not recognized");
    }

    [Fact]
    public async Task GetIndexHistory_BusinessError_SurfacesLsCode()
    {
        const string body = """{"rsp_cd":"99999","rsp_msg":"잘못된 업종"}""";
        var (client, _) = TestClientFactory.Create((_, _) => Ok(body));

        string result = await GetIndexHistoryTool.GetIndexHistory(client, "999");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("business-level");
        root.GetProperty("details").GetProperty("rsp_cd").GetString().Should().Be("99999");
        root.GetProperty("details").GetProperty("requested_code").GetString().Should().Be("999");
    }
}
