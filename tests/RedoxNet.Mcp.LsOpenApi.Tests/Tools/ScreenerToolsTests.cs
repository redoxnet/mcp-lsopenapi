using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

/// <summary>
/// Covers the v1.4 Q-Click signal surface: <c>ls_list_screeners</c>,
/// <c>ls_run_screener</c> (exact + keyword + ambiguity), and
/// <c>ls_combine_screeners</c> (AND / OR / partial-ambiguity).
/// </summary>
/// <remarks>
/// Each test starts by dropping the process-lifetime catalog cache so
/// xUnit's parallel instances don't see each other's seeded data.
/// </remarks>
public sealed class ScreenerToolsTests
{
    public ScreenerToolsTests()
    {
        ScreenerTools.ResetCatalogForTesting();
    }

    // Catalog mock that returns distinct signals per group, mirroring the
    // real LS distribution (core / indicator / market_trend / investor_trend
    // / rapid_change). LS quirk: rsp_cd="" on success.
    static string CatalogBodyForGroup(string group) => group switch
    {
        "0" => """
        { "rsp_cd": "", "rsp_msg": "", "t1826OutBlock": [
            { "search_cd": "6001", "search_nm": "이평밀집정배열" },
            { "search_cd": "6002", "search_nm": "스윙트레이딩매수" }
        ] }
        """,
        "1" => """
        { "rsp_cd": "", "rsp_msg": "", "t1826OutBlock": [
            { "search_cd": "6115", "search_nm": "이평 골든크로스(20,60)" },
            { "search_cd": "6116", "search_nm": "이평 골든크로스(5,20)" },
            { "search_cd": "6120", "search_nm": "이평 정배열(5,20,60)" }
        ] }
        """,
        "2" => """
        { "rsp_cd": "", "rsp_msg": "", "t1826OutBlock": [
            { "search_cd": "6201", "search_nm": "상한가직전" }
        ] }
        """,
        "3" => """
        { "rsp_cd": "", "rsp_msg": "", "t1826OutBlock": [
            { "search_cd": "6310", "search_nm": "외인 3일연속 순매수" }
        ] }
        """,
        "4" => """{ "rsp_cd": "", "rsp_msg": "", "t1826OutBlock": [] }""",
        _ => """{ "rsp_cd": "", "rsp_msg": "", "t1826OutBlock": [] }""",
    };

    // Per-search_cd t1825 result; lets combine tests assemble specific
    // intersection / union outcomes.
    static string T1825Body(string searchCd) => searchCd switch
    {
        "6116" => """
        { "rsp_cd": "", "rsp_msg": "",
          "t1825OutBlock": { "JongCnt": 2 },
          "t1825OutBlock1": [
            { "shcode": "005930", "hname": "삼성전자", "sign": "2", "signcnt": 1, "close": 71800,
              "change": 400, "diff": "0.56", "volume": 4817961, "volumerate": "120.50" },
            { "shcode": "000660", "hname": "SK하이닉스", "sign": "2", "signcnt": 2, "close": 181000,
              "change": 1500, "diff": "0.82", "volume": 3817961, "volumerate": "99.10" }
          ]
        }
        """,
        "6310" => """
        { "rsp_cd": "", "rsp_msg": "",
          "t1825OutBlock": { "JongCnt": 2 },
          "t1825OutBlock1": [
            { "shcode": "005930", "hname": "삼성전자", "sign": "2", "signcnt": 1, "close": 71800,
              "change": 400, "diff": "0.56", "volume": 4817961, "volumerate": "120.50" },
            { "shcode": "035420", "hname": "NAVER", "sign": "2", "signcnt": 1, "close": 215000,
              "change": 2000, "diff": "0.94", "volume": 600000, "volumerate": "110.00" }
          ]
        }
        """,
        _ => """
        { "rsp_cd": "", "rsp_msg": "",
          "t1825OutBlock": { "JongCnt": 0 },
          "t1825OutBlock1": []
        }
        """,
    };

    static (RedoxNet.LsOpenApi.Core.Http.LsApiClient client, StubHttpMessageHandler handler) CatalogClient()
    {
        return TestClientFactory.Create(async (req, ct) =>
        {
            string trCd = req.Headers.GetValues("tr_cd").First();
            string body = req.Content is not null ? await req.Content.ReadAsStringAsync(ct) : "";
            if (trCd == "t1826")
            {
                string group = ExtractFieldValue(body, "search_gb") ?? "";
                return Resp(CatalogBodyForGroup(group));
            }
            if (trCd == "t1825")
            {
                string code = ExtractFieldValue(body, "search_cd") ?? "";
                return Resp(T1825Body(code));
            }
            return Resp("""{ "rsp_cd": "99999", "rsp_msg": "unexpected tr" }""");
        });
    }

    static HttpResponseMessage Resp(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    };

    static string? ExtractFieldValue(string body, string field)
    {
        // Tiny parser for the JsonObject our tools serialise (no nested quotes
        // here): looks for "field":"value".
        string key = $"\"{field}\":\"";
        int start = body.IndexOf(key, StringComparison.Ordinal);
        if (start < 0) return null;
        start += key.Length;
        int end = body.IndexOf('"', start);
        return end < 0 ? null : body.Substring(start, end - start);
    }

    static Task<HttpResponseMessage> Ok(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    });

    // ---------- ls_list_screeners ----------

    [Fact]
    public async Task ListScreeners_All_FetchesAllGroupsAndReturnsCatalog()
    {
        var (client, handler) = CatalogClient();

        string result = await ScreenerTools.ListScreeners(client, search_group: "all");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("search_group").GetString().Should().Be("all");
        root.GetProperty("count").GetInt32().Should().Be(7, "2+3+1+1+0 across the five groups");
        // Five t1826 calls — one per group — and search_gb=4 is included as a probe.
        handler.Requests.Should().HaveCount(5);
        handler.Requests.All(r => r.Headers.GetValues("tr_cd").Single() == "t1826").Should().BeTrue();
    }

    [Fact]
    public async Task ListScreeners_FilterByGroup_SeedsCacheButReturnsOnlyThatGroup()
    {
        var (client, _) = CatalogClient();

        string result = await ScreenerTools.ListScreeners(client, search_group: "indicator");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("search_group").GetString().Should().Be("indicator");
        root.GetProperty("count").GetInt32().Should().Be(3);
        root.GetProperty("results")[0].GetProperty("id").GetString().Should().Be("6115");
        root.GetProperty("results").EnumerateArray()
            .Should().OnlyContain(r => r.GetProperty("group").GetString() == "indicator");
    }

    [Fact]
    public async Task ListScreeners_Empty_ReturnsGuidanceNote()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok("""
            { "rsp_cd": "", "rsp_msg": "", "t1826OutBlock": [] }
            """));

        string result = await ScreenerTools.ListScreeners(client, search_group: "core");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("count").GetInt32().Should().Be(0);
        root.GetProperty("note").GetString().Should().Contain("empty Q-Click signal catalog");
    }

    [Fact]
    public async Task ListScreeners_StillAcceptsStandardSuccessCode()
    {
        // Defensive: if LS ever fixes the quirk and starts returning "00000",
        // the tool must still treat the response as success.
        var (client, _) = TestClientFactory.Create((_, _) => Ok("""
            { "rsp_cd": "00000", "rsp_msg": "정상", "t1826OutBlock": [
                { "search_cd": "6001", "search_nm": "test" }
            ] }
            """));

        string result = await ScreenerTools.ListScreeners(client, search_group: "core");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("count").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ListScreeners_NonEmptyErrorCode_StillSurfacesAsError()
    {
        // The quirk only relaxes the empty-rsp_cd case; real LS error codes
        // must still bubble up.
        var (client, _) = TestClientFactory.Create((_, _) => Ok("""
            { "rsp_cd": "00040", "rsp_msg": "조회 권한 없음" }
            """));

        string result = await ScreenerTools.ListScreeners(client, search_group: "core");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("business-level");
    }

    // ---------- ls_run_screener ----------

    [Fact]
    public async Task RunScreener_ExactName_ResolvesAndExecutes()
    {
        var (client, handler) = CatalogClient();

        string result = await ScreenerTools.RunScreener(
            client, "이평 골든크로스(5,20)", market: "kospi", limit: 1);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("screener").GetProperty("id").GetString().Should().Be("6116");
        root.GetProperty("count").GetInt32().Should().Be(1);
        root.GetProperty("data_as_of").GetString().Should().MatchRegex(@"^\d{8}$");

        // 5 catalog fetches (cache seed) + 1 t1825 = 6 calls.
        handler.Requests.Should().HaveCount(6);
        handler.Requests[^1].Headers.GetValues("tr_cd").Single().Should().Be("t1825");
        string body = await handler.Requests[^1].Content!.ReadAsStringAsync();
        body.Should().Contain("\"search_cd\":\"6116\"").And.Contain("\"gubun\":\"1\"");
    }

    [Fact]
    public async Task RunScreener_ExactId_ResolvesViaCatalog()
    {
        var (client, _) = CatalogClient();

        string result = await ScreenerTools.RunScreener(client, "6116", limit: 5);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("screener").GetProperty("id").GetString().Should().Be("6116");
        root.GetProperty("screener").GetProperty("name").GetString().Should().Be("이평 골든크로스(5,20)");
    }

    [Fact]
    public async Task RunScreener_SingleKeywordMatch_Executes()
    {
        var (client, _) = CatalogClient();

        // "외인" matches only "외인 3일연속 순매수" in our mock catalog.
        string result = await ScreenerTools.RunScreener(client, "외인");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("screener").GetProperty("id").GetString().Should().Be("6310");
        root.GetProperty("count").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task RunScreener_AmbiguousKeyword_ReturnsCandidatesAndGroupCatalog()
    {
        var (client, _) = CatalogClient();

        // "골든크로스" matches both (5,20) and (20,60) → ambiguity.
        string result = await ScreenerTools.RunScreener(client, "골든크로스");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("ambiguously");

        JsonElement details = root.GetProperty("details");
        details.GetProperty("tool").GetString().Should().Be("ls_run_screener");
        details.GetProperty("ambiguous").GetProperty("골든크로스").GetArrayLength().Should().Be(2);

        // β policy: the full indicator group catalog (3 signals) is included
        // so the model can pick a related signal without an extra list call.
        JsonElement groupCatalogs = details.GetProperty("group_catalogs");
        JsonElement indicator = groupCatalogs.GetProperty("indicator");
        indicator.GetArrayLength().Should().Be(3);
        indicator.EnumerateArray().Select(e => e.GetProperty("id").GetString())
            .Should().BeEquivalentTo(["6115", "6116", "6120"]);
    }

    [Fact]
    public async Task RunScreener_UnknownName_ReturnsError()
    {
        var (client, _) = CatalogClient();

        string result = await ScreenerTools.RunScreener(client, "전혀 없는 시그널 이름");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("not found");
    }

    [Fact]
    public async Task RunScreener_FourDigitIdNotInCatalog_PassthroughExecutes()
    {
        // If LS adds a new signal that our cached catalog hasn't seen, a
        // direct 4-digit id should still be sent to t1825 rather than erroring.
        var (client, handler) = CatalogClient();

        string result = await ScreenerTools.RunScreener(client, "9999", limit: 1);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        // The mock returns an empty result for unknown ids, but the tool
        // still issued a t1825 call.
        root.GetProperty("screener").GetProperty("id").GetString().Should().Be("9999");
        handler.Requests.Last().Headers.GetValues("tr_cd").Single().Should().Be("t1825");
    }

    // ---------- ls_combine_screeners ----------

    [Fact]
    public async Task CombineScreeners_And_IntersectsByShcode()
    {
        var (client, handler) = CatalogClient();

        string result = await ScreenerTools.CombineScreeners(
            client,
            signals: new[] { "이평 골든크로스(5,20)", "외인 3일연속 순매수" },
            mode: "and");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("mode").GetString().Should().Be("and");
        root.GetProperty("signals_resolved").GetArrayLength().Should().Be(2);
        // 005930 삼성전자 is the only stock in BOTH signal results.
        root.GetProperty("count").GetInt32().Should().Be(1);
        root.GetProperty("total_in_combination").GetInt32().Should().Be(1);
        JsonElement first = root.GetProperty("results")[0];
        first.GetProperty("shcode").GetString().Should().Be("005930");
        first.GetProperty("signals_matched").EnumerateArray()
            .Select(e => e.GetString()).Should().BeEquivalentTo(["6116", "6310"]);

        // 5 catalog + 2 t1825 = 7 calls.
        handler.Requests.Should().HaveCount(7);
    }

    [Fact]
    public async Task CombineScreeners_Or_UnionsByShcode()
    {
        var (client, _) = CatalogClient();

        string result = await ScreenerTools.CombineScreeners(
            client,
            signals: new[] { "6116", "6310" },
            mode: "or");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("mode").GetString().Should().Be("or");
        // Union: {005930, 000660, 035420}
        root.GetProperty("total_in_combination").GetInt32().Should().Be(3);

        // signals_matched on a stock present in both contains both ids.
        JsonElement samsung = root.GetProperty("results").EnumerateArray()
            .Single(r => r.GetProperty("shcode").GetString() == "005930");
        samsung.GetProperty("signals_matched").EnumerateArray()
            .Select(e => e.GetString()).Should().BeEquivalentTo(["6116", "6310"]);

        JsonElement naver = root.GetProperty("results").EnumerateArray()
            .Single(r => r.GetProperty("shcode").GetString() == "035420");
        naver.GetProperty("signals_matched").EnumerateArray()
            .Should().ContainSingle().Which.GetString().Should().Be("6310");
    }

    [Fact]
    public async Task CombineScreeners_AmbiguousKeyword_ReturnsAmbiguityEnvelope()
    {
        var (client, _) = CatalogClient();

        string result = await ScreenerTools.CombineScreeners(
            client,
            signals: new[] { "골든크로스", "외인 3일연속 순매수" },
            mode: "and");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("ambiguously");
        JsonElement details = root.GetProperty("details");
        details.GetProperty("tool").GetString().Should().Be("ls_combine_screeners");

        // The unambiguous signal is reported as resolved, the keyword shows
        // up under ambiguous with its candidates, and the indicator group
        // catalog accompanies the response.
        details.GetProperty("resolved").GetArrayLength().Should().Be(1);
        details.GetProperty("resolved")[0].GetProperty("id").GetString().Should().Be("6310");
        details.GetProperty("ambiguous").GetProperty("골든크로스").GetArrayLength().Should().Be(2);
        details.GetProperty("group_catalogs").GetProperty("indicator").GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task CombineScreeners_NotFound_SurfacesInEnvelope()
    {
        var (client, _) = CatalogClient();

        string result = await ScreenerTools.CombineScreeners(
            client,
            signals: new[] { "이평 골든크로스(5,20)", "전혀 없는 시그널" },
            mode: "and");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("ambiguously");
        root.GetProperty("details").GetProperty("not_found").EnumerateArray()
            .Should().ContainSingle().Which.GetString().Should().Be("전혀 없는 시그널");
    }

    [Fact]
    public async Task CombineScreeners_TooFewSignals_ReturnsValidationError()
    {
        var (client, _) = CatalogClient();

        string result = await ScreenerTools.CombineScreeners(
            client, signals: new[] { "이평 골든크로스(5,20)" });
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("at least 2");
    }

    [Fact]
    public async Task CombineScreeners_TooManySignals_ReturnsValidationError()
    {
        var (client, _) = CatalogClient();

        string result = await ScreenerTools.CombineScreeners(
            client,
            signals: new[] { "a", "b", "c", "d", "e", "f", "g", "h", "i" });
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("at most");
    }

    [Theory]
    [InlineData("intersection", "and")]
    [InlineData("union", "or")]
    [InlineData("교집합", "and")]
    [InlineData("합집합", "or")]
    public async Task CombineScreeners_AliasedMode_NormalizesToCanonical(string input, string expected)
    {
        var (client, _) = CatalogClient();

        string result = await ScreenerTools.CombineScreeners(
            client,
            signals: new[] { "6116", "6310" },
            mode: input);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("mode").GetString().Should().Be(expected);
    }

    [Fact]
    public async Task CombineScreeners_InvalidMode_ReturnsValidationError()
    {
        var (client, _) = CatalogClient();

        string result = await ScreenerTools.CombineScreeners(
            client,
            signals: new[] { "6116", "6310" },
            mode: "xor");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("'and' or 'or'");
    }
}
