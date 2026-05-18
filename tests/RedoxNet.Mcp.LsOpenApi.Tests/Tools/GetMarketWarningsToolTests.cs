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
    public async Task GetMarketWarnings_DefaultKinds_QueriesFiveCriticalSets()
    {
        var responses = new Dictionary<string, string>
        {
            ["t1404"] = T1404AdminSample,
            ["t1405"] = T1405HaltSample,
        };
        var (client, handler) = TestClientFactory.Create((req, _) => RouteByTrCode(req, responses));

        string result = await GetMarketWarningsTool.GetMarketWarnings(client);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        // 5 default kinds: 1 t1404 + 4 t1405
        handler.Requests.Should().HaveCount(5);
        JsonElement queried = root.GetProperty("queried_kinds");
        queried.GetArrayLength().Should().Be(5);
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
}
