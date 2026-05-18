using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

public sealed class GetMarketWarningsToolTests
{
    // Sample shaped after t1404 guide example.
    const string T1404AdminSample = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "OK",
      "t1404OutBlock": { "cts_shcode": "" },
      "t1404OutBlock1": [
        {
          "date": "20230102", "reason": "5102",
          "tprice": 16200, "change": 260, "shcode": "000547", "sign": "5",
          "tdiff": "001.85", "diff": "-01.55", "tchange": 300, "edate": "",
          "volume": 216, "price": 16500, "hname": "흥국화재2우B"
        },
        {
          "date": "20220530", "reason": "6024",
          "tprice": 3780, "change": 70, "shcode": "950170", "sign": "2",
          "tdiff": "003.70", "diff": "001.82", "tchange": 140, "edate": "",
          "volume": 5492, "price": 3920, "hname": "JTC"
        }
      ]
    }
    """;

    // Sample shaped after t1405 guide example.
    const string T1405HaltSample = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "OK",
      "t1405OutBlock": { "cts_shcode": "" },
      "t1405OutBlock1": [
        {
          "volume": 27964262, "date": "20230525",
          "price": 2215, "change": 35, "shcode": "001470", "sign": "2",
          "diff": "001.61", "edate": "", "hname": "삼부토건"
        },
        {
          "volume": 195211, "date": "20230518",
          "price": 22750, "change": 550, "shcode": "290690", "sign": "5",
          "diff": "-02.36", "edate": "", "hname": "소룩스"
        }
      ]
    }
    """;

    static Task<HttpResponseMessage> Ok(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    });

    static Task<HttpResponseMessage> RouteByTrCode(HttpRequestMessage request, IDictionary<string, string> responses)
    {
        string tr = request.Headers.GetValues("tr_cd").First();
        if (!responses.TryGetValue(tr, out string? body))
            body = """{"rsp_cd":"00000","rsp_msg":"empty"}""";
        return Ok(body);
    }

    [Fact]
    public async Task GetMarketWarnings_SingleAdminKind_OneTrCall_ShapesPayload()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(T1404AdminSample));

        string result = await GetMarketWarningsTool.GetMarketWarnings(client, kinds: new[] { "designated_admin" });
        JsonElement root = JsonDocument.Parse(result).RootElement;

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t1404");
        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("\"gubun\":\"0\"");
        body.Should().Contain("\"jongchk\":\"1\"");

        root.GetProperty("market").GetString().Should().Be("all");
        root.GetProperty("count").GetInt32().Should().Be(2);
        JsonElement rows = root.GetProperty("rows");
        rows[0].GetProperty("kind").GetString().Should().Be("designated_admin");
        rows[0].GetProperty("korean_label").GetString().Should().Be("관리종목");
        rows[0].GetProperty("source_tr").GetString().Should().Be("t1404");
        rows[0].GetProperty("jongchk").GetString().Should().Be("1");
        rows[0].GetProperty("shcode").GetString().Should().Be("000547");
        rows[0].GetProperty("since").GetString().Should().Be("20230102");
        rows[0].GetProperty("reason_code").GetString().Should().Be("5102");
    }

    [Fact]
    public async Task GetMarketWarnings_MixedKinds_FansOutToBothTrs()
    {
        var responses = new Dictionary<string, string>
        {
            ["t1404"] = T1404AdminSample,
            ["t1405"] = T1405HaltSample,
        };
        var (client, handler) = TestClientFactory.Create((req, _) => RouteByTrCode(req, responses));

        string result = await GetMarketWarningsTool.GetMarketWarnings(client, kinds: new[] { "관리", "매매정지" });
        JsonElement root = JsonDocument.Parse(result).RootElement;

        handler.Requests.Should().HaveCount(2);
        handler.Requests.Select(r => r.Headers.GetValues("tr_cd").Single()).Should().BeEquivalentTo(new[] { "t1404", "t1405" });
        root.GetProperty("count").GetInt32().Should().Be(4);
        // Sorted by kind (alphabetical): designated_admin first, trading_halt second.
        JsonElement rows = root.GetProperty("rows");
        rows[0].GetProperty("kind").GetString().Should().Be("designated_admin");
        rows[2].GetProperty("kind").GetString().Should().Be("trading_halt");
    }

    [Fact]
    public async Task GetMarketWarnings_ShcodeFilter_KeepsOnlyMatches()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(T1404AdminSample));

        string result = await GetMarketWarningsTool.GetMarketWarnings(
            client,
            kinds: new[] { "관리" },
            shcodes: new[] { "950170" });
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("count").GetInt32().Should().Be(1);
        root.GetProperty("rows")[0].GetProperty("shcode").GetString().Should().Be("950170");
    }

    [Fact]
    public async Task GetMarketWarnings_DefaultKind_QueriesOnlyDesignatedAdmin()
    {
        // v0.7 E2E reduced the default from 5 kinds to 1 (관리) after observing
        // the model fan out 5 TR calls for the common "오늘 관리종목" question.
        // Models that want comprehensive surveillance coverage pass an explicit
        // list — default-narrow keeps the floor cheap.
        var responses = new Dictionary<string, string>
        {
            ["t1404"] = T1404AdminSample,
            ["t1405"] = T1405HaltSample,
        };
        var (client, handler) = TestClientFactory.Create((req, _) => RouteByTrCode(req, responses));

        string result = await GetMarketWarningsTool.GetMarketWarnings(client);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t1404");
        JsonElement queried = root.GetProperty("queried_kinds");
        queried.GetArrayLength().Should().Be(1);
        queried[0].GetString().Should().Be("designated_admin");
    }

    [Theory]
    [InlineData("kospi", "1")]
    [InlineData("kosdaq", "2")]
    [InlineData("all", "0")]
    public async Task GetMarketWarnings_MarketMapping(string market, string expectedGubun)
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(T1404AdminSample));

        await GetMarketWarningsTool.GetMarketWarnings(client, kinds: new[] { "관리" }, market: market);

        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain($"\"gubun\":\"{expectedGubun}\"");
    }

    [Theory]
    [InlineData("trading_halt", "t1405", "2")]
    [InlineData("매매정지", "t1405", "2")]
    [InlineData("t1405:2", "t1405", "2")]
    [InlineData("short_term_overheating", "t1405", "7")]
    [InlineData("단기과열", "t1405", "7")]
    [InlineData("designated_admin", "t1404", "1")]
    [InlineData("관리", "t1404", "1")]
    [InlineData("관리종목", "t1404", "1")]
    [InlineData("liquidation_trading", "t1405", "3")]
    [InlineData("정리매매", "t1405", "3")]
    [InlineData("delisting_trade", "t1405", "3")] // legacy alias from v0.7-prep
    public async Task GetMarketWarnings_KindAliases_AllResolveCorrectly(string alias, string expectedTr, string expectedJongchk)
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(T1404AdminSample));

        await GetMarketWarningsTool.GetMarketWarnings(client, kinds: new[] { alias });

        handler.Requests[0].Headers.GetValues("tr_cd").Single().Should().Be(expectedTr);
        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain($"\"jongchk\":\"{expectedJongchk}\"");
    }

    [Fact]
    public async Task GetMarketWarnings_UnknownKind_ReturnsValidationError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(T1404AdminSample));

        string result = await GetMarketWarningsTool.GetMarketWarnings(client, kinds: new[] { "nonsense_kind" });
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("not recognized");
    }

    [Fact]
    public async Task GetMarketWarnings_UnknownMarket_ReturnsValidationError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(T1404AdminSample));

        string result = await GetMarketWarningsTool.GetMarketWarnings(client, market: "nasdaq");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("market");
    }

    [Fact]
    public async Task GetMarketWarnings_BusinessError_SurfacesEnvelope()
    {
        const string body = """{"rsp_cd":"99999","rsp_msg":"필수항목 누락"}""";
        var (client, _) = TestClientFactory.Create((_, _) => Ok(body));

        string result = await GetMarketWarningsTool.GetMarketWarnings(client, kinds: new[] { "관리" });
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("business-level");
        root.GetProperty("details").GetProperty("rsp_cd").GetString().Should().Be("99999");
        root.GetProperty("details").GetProperty("source_tr").GetString().Should().Be("t1404");
        root.GetProperty("details").GetProperty("kind").GetString().Should().Be("designated_admin");
    }

    // E2E v0.7.0 caught LS echoing cts_shcode without advancing the cursor — the
    // wrapper kept paging 6× and shipped ~600 duplicate rows for a 100-row
    // screen. This pins the dedup + cursor-advance check so the bug stays fixed.
    [Fact]
    public async Task GetMarketWarnings_PaginationStops_WhenCursorDoesNotAdvance()
    {
        // Same OutBlock cts_shcode echoed back on every page — LS reuses the
        // last shcode of the response when there are no more records.
        const string echoingSample = """
        {
          "rsp_cd": "00000",
          "rsp_msg": "OK",
          "t1404OutBlock": { "cts_shcode": "950170" },
          "t1404OutBlock1": [
            { "date": "20230102", "reason": "5102", "shcode": "000547", "hname": "흥국화재2우B", "price": 16500, "diff": "-01.55", "volume": 216, "edate": "" },
            { "date": "20220530", "reason": "6024", "shcode": "950170", "hname": "JTC", "price": 3920, "diff": "001.82", "volume": 5492, "edate": "" }
          ]
        }
        """;
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(echoingSample));

        string result = await GetMarketWarningsTool.GetMarketWarnings(client, kinds: new[] { "관리" });
        JsonElement root = JsonDocument.Parse(result).RootElement;

        // Before the fix this called 6 times (MaxPagesPerKind) and emitted 12 rows.
        // After the fix: page 1 collects both rows, page 2 sees same cursor + no
        // new shcodes → break. Exactly 2 LS calls (page 1 always fires; page 2
        // fires once to discover non-advance) or 1 if the seen-set short-circuits;
        // the assertion bounds it sanely either way.
        handler.Requests.Should().HaveCountLessThanOrEqualTo(2);
        root.GetProperty("count").GetInt32().Should().Be(2);
        JsonElement rows = root.GetProperty("rows");
        rows.EnumerateArray().Select(r => r.GetProperty("shcode").GetString())
            .Should().BeEquivalentTo(new[] { "000547", "950170" });
    }

    [Fact]
    public async Task GetMarketWarnings_LiquidationTradingCanonical_EmittedAsKind()
    {
        // t1405 jongchk=3 (정리매매) → canonical English now "liquidation_trading".
        const string liquidationSample = """
        {
          "rsp_cd": "00000",
          "rsp_msg": "OK",
          "t1405OutBlock": { "cts_shcode": "" },
          "t1405OutBlock1": [
            { "volume": 100, "date": "20260101", "price": 1000, "change": 0, "shcode": "999990", "sign": "3", "diff": "0", "edate": "", "hname": "정리매매테스트" }
          ]
        }
        """;
        var (client, _) = TestClientFactory.Create((_, _) => Ok(liquidationSample));

        string result = await GetMarketWarningsTool.GetMarketWarnings(client, kinds: new[] { "정리매매" });
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("queried_kinds")[0].GetString().Should().Be("liquidation_trading");
        root.GetProperty("rows")[0].GetProperty("kind").GetString().Should().Be("liquidation_trading");
    }
}
