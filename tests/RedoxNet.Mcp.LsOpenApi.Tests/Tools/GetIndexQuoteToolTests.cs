using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

public sealed class GetIndexQuoteToolTests
{
    // Trimmed-down t1511 sample lifted from the LS guide (todo/t1485 t1511.txt).
    // Real-shape JSON so the field mapping logic gets exercised end-to-end.
    const string KospiSample = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "조회완료",
      "t1511OutBlock": {
        "gubun": "1",
        "hname": "종       합",
        "pricejisu": "2610.62",
        "jniljisu": "2601.36",
        "sign": "2",
        "change": "9.26",
        "diffjisu": "0.36",
        "openjisu": "2617.43", "opendiff": "0.62", "opentime": "090030",
        "highjisu": "2617.58", "highdiff": "0.62", "hightime": "090040",
        "lowjisu":  "2610.40", "lowdiff":  "0.35", "lowtime":  "090740",
        "volume": 263165, "jnilvolume": 569620, "volumechange": -306455, "volumerate": "46.20",
        "value": 3884240, "jnilvalue": 9383535, "valuechange": -5499295, "valuerate": "41.39",
        "whjisu": "2662.04", "whchange": "1.93",  "whjday": "20220607",
        "wljisu": "2134.77", "wlchange": "22.29", "wljday": "20220930",
        "yhjisu": "2601.38", "yhchange": "0.36",  "yhjday": "20230602",
        "yljisu": "2180.67", "ylchange": "19.72", "yljday": "20230103",
        "highjo": 606, "upjo": 0, "unchgjo": 91, "lowjo": 253, "downjo": 0,
        "firstjcode":  "001", "firstjname":  "종       합", "firstjisu":  "2610.62", "firsign": "2", "firchange": "9.26",  "firdiff": "0.03",
        "secondjcode": "002", "secondjname": "대   형  주", "secondjisu": "2611.97", "secsign": "2", "secchange": "7.26",  "secdiff": "0.28",
        "thirdjcode":  "003", "thirdjname":  "중   형  주", "thirdjisu":  "2760.88", "thrsign": "2", "thrchange": "22.71", "thrdiff": "0.83",
        "fourthjcode": "004", "fourthjname": "소   형  주", "fourthjisu": "2393.35", "forsign": "2", "forchange": "14.01", "fordiff": "0.59"
      }
    }
    """;

    static Task<HttpResponseMessage> Ok(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    });

    [Fact]
    public async Task GetIndexQuote_KospiAlias_ResolvesTo001AndShapesPayload()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(KospiSample));

        string result = await GetIndexQuoteTool.GetIndexQuote(client, "kospi");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t1511");
        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("\"upcode\":\"001\"");

        root.GetProperty("index_code").GetString().Should().Be("001");
        root.GetProperty("name").GetString().Should().Be("종       합");
        root.GetProperty("value").GetDouble().Should().BeApproximately(2610.62, 1e-2);
        root.GetProperty("previous_close").GetDouble().Should().BeApproximately(2601.36, 1e-2);
        root.GetProperty("change").GetDouble().Should().BeApproximately(9.26, 1e-2);
        root.GetProperty("change_pct").GetDouble().Should().BeApproximately(0.36, 1e-2);

        JsonElement market = root.GetProperty("market_breadth");
        market.GetProperty("up").GetInt64().Should().Be(606);
        market.GetProperty("down").GetInt64().Should().Be(253);
        market.GetProperty("unchanged").GetInt64().Should().Be(91);

        JsonElement related = root.GetProperty("related_indices");
        related.GetArrayLength().Should().Be(4);
        related[0].GetProperty("code").GetString().Should().Be("001");
        related[1].GetProperty("code").GetString().Should().Be("002");
        related[1].GetProperty("change_pct").GetDouble().Should().BeApproximately(0.28, 1e-2);
        related[2].GetProperty("change_pct").GetDouble().Should().BeApproximately(0.83, 1e-2);

        JsonElement r52 = root.GetProperty("range_52w");
        r52.GetProperty("high").GetProperty("value").GetDouble().Should().BeApproximately(2662.04, 1e-2);
        r52.GetProperty("high").GetProperty("date").GetString().Should().Be("20220607");
        r52.GetProperty("low").GetProperty("value").GetDouble().Should().BeApproximately(2134.77, 1e-2);

        JsonElement open = root.GetProperty("open");
        open.GetProperty("value").GetDouble().Should().BeApproximately(2617.43, 1e-2);
        open.GetProperty("time").GetString().Should().Be("090030");
    }

    [Theory]
    [InlineData("kosdaq", "301")]
    [InlineData("kospi200", "101")]
    [InlineData("krx100", "501")]
    [InlineData("002", "002")] // 3-char passthrough
    public async Task GetIndexQuote_AliasMapping(string input, string expectedUpcode)
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(KospiSample));

        await GetIndexQuoteTool.GetIndexQuote(client, input);

        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain($"\"upcode\":\"{expectedUpcode}\"");
    }

    [Fact]
    public async Task GetIndexQuote_UnknownAlias_ReturnsValidationError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(KospiSample));

        string result = await GetIndexQuoteTool.GetIndexQuote(client, "nope");

        JsonElement root = JsonDocument.Parse(result).RootElement;
        root.GetProperty("error").GetString().Should().Contain("not recognized");
    }

    [Fact]
    public async Task GetIndexQuote_BusinessError_SurfacesIndexNotFoundEnvelope()
    {
        const string body = """{"rsp_cd":"99999","rsp_msg":"잘못된 업종"}""";
        var (client, _) = TestClientFactory.Create((_, _) => Ok(body));

        string result = await GetIndexQuoteTool.GetIndexQuote(client, "999");

        JsonElement root = JsonDocument.Parse(result).RootElement;
        root.GetProperty("error").GetString().Should().Contain("business-level");
        root.GetProperty("details").GetProperty("error_code").GetString().Should().Be("IndexNotFound");
        root.GetProperty("details").GetProperty("requested_code").GetString().Should().Be("999");
    }

    [Fact]
    public async Task GetIndexQuote_NegativeSign_FlipsChangeAndChangePct()
    {
        // Replicates the KOSPI sample but flips sign to "4" (LS sign code for "down").
        string ls = KospiSample.Replace("\"sign\": \"2\"", "\"sign\": \"4\"");
        var (client, _) = TestClientFactory.Create((_, _) => Ok(ls));

        string result = await GetIndexQuoteTool.GetIndexQuote(client, "kospi");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("change").GetDouble().Should().BeApproximately(-9.26, 1e-2);
        root.GetProperty("change_pct").GetDouble().Should().BeApproximately(-0.36, 1e-2);
    }
}
