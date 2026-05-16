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

        string result = await GetIndustryIndicesTool.GetIndustryIndices(cache, market: "kospi", top_n: 30);
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
    public async Task GetIndustryIndices_TopN_SlicesWithoutRefetching()
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

        string first = await GetIndustryIndicesTool.GetIndustryIndices(cache, market: "kospi", top_n: 1);
        int reqsAfterFirst = handler.Requests.Count;
        string second = await GetIndustryIndicesTool.GetIndustryIndices(cache, market: "kospi", top_n: 3);

        JsonElement firstRoot = JsonDocument.Parse(first).RootElement;
        firstRoot.GetProperty("count").GetInt32().Should().Be(1);
        firstRoot.GetProperty("total_available").GetInt32().Should().Be(3);
        firstRoot.GetProperty("rows")[0].GetProperty("upcode").GetString().Should().Be("013");

        JsonElement secondRoot = JsonDocument.Parse(second).RootElement;
        secondRoot.GetProperty("count").GetInt32().Should().Be(3);

        handler.Requests.Count.Should().Be(reqsAfterFirst, "60s cache means top_n=3 reuses the fanout from top_n=1");
    }

    [Fact]
    public async Task GetIndustryIndices_TopNOutOfRange_ReturnsValidationError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(T8424ThreeCodes));
        var cache = new IndustryDataCache(client);

        string result = await GetIndustryIndicesTool.GetIndustryIndices(cache, market: "kospi", top_n: 0);

        JsonElement root = JsonDocument.Parse(result).RootElement;
        root.GetProperty("error").GetString().Should().Contain("top_n");
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
}
