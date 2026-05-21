using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

public sealed class GetIndustryIndicesToolTests
{
    const string T8424ThreeCodes = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "조회완료",
      "t8424OutBlock": [
        { "upcode": "001", "hname": "종       합" },
        { "upcode": "013", "hname": "전기전자" },
        { "upcode": "017", "hname": "화학" }
      ]
    }
    """;

    // t8424 gubun1="1" — KOSPI side. Carries real 업종 (incl. "IT서비스", a
    // Latin-starting industry that must survive the filter) plus LS index
    // products (KP-family, KOSPI composites, VKOSPI) that must be dropped.
    const string T8424Kospi = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "조회완료",
      "t8424OutBlock": [
        { "upcode": "001", "hname": "종       합" },
        { "upcode": "013", "hname": "전기전자" },
        { "upcode": "029", "hname": "IT서비스" },
        { "upcode": "115", "hname": "KP200정보기술" },
        { "upcode": "202", "hname": "KOSPI50" },
        { "upcode": "208", "hname": "KP100동일가중" },
        { "upcode": "205", "hname": "VKOSPI" }
      ]
    }
    """;

    // t8424 gubun1="2" — KOSDAQ industries only.
    const string T8424Kosdaq = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "조회완료",
      "t8424OutBlock": [
        { "upcode": "301", "hname": "코스닥종합" }
      ]
    }
    """;

    // t8424 with an empty OutBlock — a transient empty-leg response.
    const string T8424Empty = """
    { "rsp_cd": "00000", "rsp_msg": "조회완료", "t8424OutBlock": [] }
    """;

    // Minimal t1511 sample factory — provides pricejisu/sign/change/diffjisu/hname
    // varying by upcode so the sort/slice logic can be verified.
    static string T1511Response(string upcode, double price, string sign, double change, double diff, string hname) => $$"""
    {
      "rsp_cd": "00000",
      "rsp_msg": "조회완료",
      "t1511OutBlock": {
        "gubun": "1",
        "hname": "{{hname}}",
        "pricejisu": "{{price}}",
        "jniljisu": "{{price - change}}",
        "sign": "{{sign}}",
        "change": "{{change}}",
        "diffjisu": "{{diff}}"
      }
    }
    """;

    static Task<HttpResponseMessage> Ok(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    });

    static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> RouteByTrCode(
        Dictionary<string, string> bodyByTrCode)
    {
        return (request, _) =>
        {
            string trCode = request.Headers.TryGetValues("tr_cd", out var values) ? values.First() : "";
            return Ok(bodyByTrCode.TryGetValue(trCode, out string? body) ? body : "{\"rsp_cd\":\"99999\"}");
        };
    }

    [Fact]
    public async Task GetIndustryIndices_SortsByChangePctDescAndShapesPayload()
    {
        // 013 전기전자: +1.5%, 017 화학: -0.8%, 001 종합: +0.3%. Expected order: 013, 001, 017.
        string lastSentUpcode = "";
        var (client, handler) = TestClientFactory.Create((request, _) =>
        {
            string trCode = request.Headers.GetValues("tr_cd").First();
            if (trCode == "t8424")
                return Ok(T8424ThreeCodes);
            // For t1511, decode the body to pick the upcode.
            string body = request.Content!.ReadAsStringAsync().Result;
            string upcode = ExtractUpcodeFromBody(body);
            lastSentUpcode = upcode;
            return upcode switch
            {
                "001" => Ok(T1511Response("001", 2610, "2", 7.83, 0.30, "종       합")),
                "013" => Ok(T1511Response("013", 24500, "2", 360, 1.50, "전기전자")),
                "017" => Ok(T1511Response("017", 5400, "4", 43, 0.80, "화학")),
                _ => Ok("{\"rsp_cd\":\"99999\"}"),
            };
        });
        var cache = new IndustryDataCache(client);

        string result = await GetIndustryIndicesTool.GetIndustryIndices(cache, market: "kospi", limit: 30);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("market").GetString().Should().Be("kospi");
        root.GetProperty("count").GetInt32().Should().Be(3);
        root.GetProperty("total_available").GetInt32().Should().Be(3);

        JsonElement rows = root.GetProperty("rows");
        rows.GetArrayLength().Should().Be(3);
        rows[0].GetProperty("upcode").GetString().Should().Be("013");
        rows[0].GetProperty("rank").GetInt32().Should().Be(1);
        rows[0].GetProperty("change_pct").GetDouble().Should().BeApproximately(1.50, 1e-2);
        rows[1].GetProperty("upcode").GetString().Should().Be("001");
        rows[2].GetProperty("upcode").GetString().Should().Be("017");
        rows[2].GetProperty("change_pct").GetDouble().Should().BeApproximately(-0.80, 1e-2);

        // 1 t8424 + 3 t1511 = 4 outgoing requests
        handler.Requests.Should().HaveCount(4);
    }

    [Fact]
    public async Task GetIndustryIndices_Limit_SlicesWithoutRefetching()
    {
        var (client, handler) = TestClientFactory.Create((request, _) =>
        {
            string trCode = request.Headers.GetValues("tr_cd").First();
            if (trCode == "t8424")
                return Ok(T8424ThreeCodes);
            string body = request.Content!.ReadAsStringAsync().Result;
            string upcode = ExtractUpcodeFromBody(body);
            return upcode switch
            {
                "001" => Ok(T1511Response("001", 2610, "2", 7.83, 0.30, "종       합")),
                "013" => Ok(T1511Response("013", 24500, "2", 360, 1.50, "전기전자")),
                "017" => Ok(T1511Response("017", 5400, "4", 43, 0.80, "화학")),
                _ => Ok("{\"rsp_cd\":\"99999\"}"),
            };
        });
        var cache = new IndustryDataCache(client);

        string first = await GetIndustryIndicesTool.GetIndustryIndices(cache, market: "kospi", limit: 1);
        int reqsAfterFirst = handler.Requests.Count;
        string second = await GetIndustryIndicesTool.GetIndustryIndices(cache, market: "kospi", limit: 3);

        JsonElement firstRoot = JsonDocument.Parse(first).RootElement;
        firstRoot.GetProperty("count").GetInt32().Should().Be(1);
        firstRoot.GetProperty("total_available").GetInt32().Should().Be(3);
        firstRoot.GetProperty("rows")[0].GetProperty("upcode").GetString().Should().Be("013");

        JsonElement secondRoot = JsonDocument.Parse(second).RootElement;
        secondRoot.GetProperty("count").GetInt32().Should().Be(3);

        handler.Requests.Count.Should().Be(reqsAfterFirst, "60s cache means limit=3 reuses the fanout from limit=1");
    }

    [Fact]
    public async Task GetIndustryIndices_LimitOutOfRange_ReturnsValidationError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(T8424ThreeCodes));
        var cache = new IndustryDataCache(client);

        string result = await GetIndustryIndicesTool.GetIndustryIndices(cache, market: "kospi", limit: 0);

        JsonElement root = JsonDocument.Parse(result).RootElement;
        root.GetProperty("error").GetString().Should().Contain("limit");
    }

    [Fact]
    public async Task GetIndustryIndices_AllMarket_MergesRealIndustriesAndDropsIndexProducts()
    {
        // "all" must fetch the two catalogs via gubun1 "1" + "2" (never
        // gubun1="" — the 250+ index zoo) and drop LS index products: the
        // KP200 sector index (115) and the KOSPI50 composite (202) here.
        var gubunsSeen = new List<string>();
        var (client, _) = TestClientFactory.Create((request, _) =>
        {
            string trCode = request.Headers.GetValues("tr_cd").First();
            string body = request.Content!.ReadAsStringAsync().Result;
            if (trCode == "t8424")
            {
                string gubun = ExtractGubun1FromBody(body);
                gubunsSeen.Add(gubun);
                return gubun switch
                {
                    "1" => Ok(T8424Kospi),
                    "2" => Ok(T8424Kosdaq),
                    _ => Ok("{\"rsp_cd\":\"99999\"}"),
                };
            }
            string upcode = ExtractUpcodeFromBody(body);
            return Ok(T1511Response(upcode, 100, "2", 1, 1.0, "ind" + upcode));
        });
        var cache = new IndustryDataCache(client);

        string result = await GetIndustryIndicesTool.GetIndustryIndices(cache, market: "all", limit: 30);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        gubunsSeen.Should().BeEquivalentTo(new[] { "1", "2" });
        gubunsSeen.Should().NotContain("", "the gubun1=\"\" index zoo must never be requested");
        root.GetProperty("market").GetString().Should().Be("all");
        var upcodes = root.GetProperty("rows").EnumerateArray()
            .Select(r => r.GetProperty("upcode").GetString())
            .ToList();
        upcodes.Should().BeEquivalentTo(new[] { "001", "013", "029", "301" },
            "real 업종 merge (incl. the Latin-starting IT서비스); index products drop");
        upcodes.Should().NotContain("115").And.NotContain("202")
            .And.NotContain("208").And.NotContain("205");
    }

    [Fact]
    public async Task GetIndustryIndices_TruncatedName_DropsReplacementCharacter()
    {
        // LS truncates a long hname mid-character; the dangling byte decodes
        // to U+FFFD. It must be stripped, not shipped to the model.
        string brokenName = "정보기술 레버" + (char)0xFFFD;
        var (client, _) = TestClientFactory.Create((request, _) =>
        {
            string trCode = request.Headers.GetValues("tr_cd").First();
            if (trCode == "t8424")
                return Ok(T8424ThreeCodes);
            string upcode = ExtractUpcodeFromBody(request.Content!.ReadAsStringAsync().Result);
            return Ok(T1511Response(upcode, 100, "2", 1, 1.0, brokenName));
        });
        var cache = new IndustryDataCache(client);

        string result = await GetIndustryIndicesTool.GetIndustryIndices(cache, market: "kospi", limit: 30);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        foreach (JsonElement row in root.GetProperty("rows").EnumerateArray())
        {
            string name = row.GetProperty("name").GetString()!;
            name.Should().NotContain(((char)0xFFFD).ToString());
            name.Should().Be("정보기술레버");
        }
    }

    [Fact]
    public async Task GetIndustryIndices_StaleChangeField_DerivesChangeFromValueAndPercent()
    {
        // LS t1511 occasionally reports 'change' against a frozen base
        // (observed on 전기전자/013), inconsistent with pricejisu/diffjisu.
        // The tool must derive change from value × pct/(100+pct) and discard
        // the stale field: value 24416.59 + pct 8.63 -> change ~1939.7.
        var (client, _) = TestClientFactory.Create((request, _) =>
        {
            string trCode = request.Headers.GetValues("tr_cd").First();
            if (trCode == "t8424")
                return Ok(T8424ThreeCodes);
            string upcode = ExtractUpcodeFromBody(request.Content!.ReadAsStringAsync().Result);
            return Ok(T1511Response(upcode, 24416.59, "2", 9880.66, 8.63, "ind" + upcode));
        });
        var cache = new IndustryDataCache(client);

        string result = await GetIndustryIndicesTool.GetIndustryIndices(cache, market: "kospi", limit: 30);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        foreach (JsonElement row in root.GetProperty("rows").EnumerateArray())
        {
            double value = row.GetProperty("value").GetDouble();
            double change = row.GetProperty("change").GetDouble();
            double pct = row.GetProperty("change_pct").GetDouble();
            change.Should().BeApproximately(1939.7, 1.0, "change is derived, not the stale 9880.66 field");
            (change / (value - change) * 100).Should().BeApproximately(pct, 0.05,
                "derived change must stay consistent with value and change_pct");
        }
    }

    [Fact]
    public async Task GetIndustryIndices_AllMarket_RetriesAnEmptyCatalogLeg()
    {
        // The KOSDAQ t8424 leg returns empty on the first call and data on
        // the retry — the merged board must recover KOSDAQ and report no
        // partial_error.
        int kosdaqCalls = 0;
        var (client, _) = TestClientFactory.Create((request, _) =>
        {
            string trCode = request.Headers.GetValues("tr_cd").First();
            string body = request.Content!.ReadAsStringAsync().Result;
            if (trCode == "t8424")
            {
                if (ExtractGubun1FromBody(body) == "1")
                    return Ok(T8424Kospi);
                return Ok(++kosdaqCalls == 1 ? T8424Empty : T8424Kosdaq);
            }
            return Ok(T1511Response(ExtractUpcodeFromBody(body), 100, "2", 1, 1.0, "ind"));
        });
        var cache = new IndustryDataCache(client);

        string result = await GetIndustryIndicesTool.GetIndustryIndices(cache, market: "all", limit: 30);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        kosdaqCalls.Should().Be(2, "the empty KOSDAQ leg must be retried once");
        root.GetProperty("rows").EnumerateArray()
            .Select(r => r.GetProperty("upcode").GetString())
            .Should().Contain("301", "the retry recovered the KOSDAQ catalog");
        root.GetProperty("partial_error").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task GetIndustryIndices_AllMarket_PersistentEmptyLeg_SurfacesPartialError()
    {
        // KOSDAQ stays empty through the retry — the board is KOSPI-only and
        // partial_error must say so rather than the gap being silent.
        var (client, _) = TestClientFactory.Create((request, _) =>
        {
            string trCode = request.Headers.GetValues("tr_cd").First();
            string body = request.Content!.ReadAsStringAsync().Result;
            if (trCode == "t8424")
                return Ok(ExtractGubun1FromBody(body) == "1" ? T8424Kospi : T8424Empty);
            return Ok(T1511Response(ExtractUpcodeFromBody(body), 100, "2", 1, 1.0, "ind"));
        });
        var cache = new IndustryDataCache(client);

        string result = await GetIndustryIndicesTool.GetIndustryIndices(cache, market: "all", limit: 30);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("rows").GetArrayLength().Should().BeGreaterThan(0, "the KOSPI board still returns");
        root.GetProperty("partial_error").GetString().Should().Contain("KOSDAQ");
    }

    static string ExtractUpcodeFromBody(string body)
    {
        // Tiny string-search to avoid pulling in System.Text.Json for the stub.
        const string key = "\"upcode\":\"";
        int start = body.IndexOf(key, StringComparison.Ordinal);
        if (start < 0) return "";
        start += key.Length;
        int end = body.IndexOf('"', start);
        return end < 0 ? "" : body[start..end];
    }

    static string ExtractGubun1FromBody(string body)
    {
        const string key = "\"gubun1\":\"";
        int start = body.IndexOf(key, StringComparison.Ordinal);
        if (start < 0) return "?";
        start += key.Length;
        int end = body.IndexOf('"', start);
        return end < 0 ? "?" : body[start..end];
    }
}
