using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

public sealed class GetFundamentalsRankToolTests
{
    const string TwoRowSample = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "OK",
      "t3341OutBlock": { "cnt": 1341, "idx": 0 },
      "t3341OutBlock1": [
        {
          "rank": 1, "shcode": "007700", "hname": "F&F홀딩스",
          "per": "33.62", "pbr": "0.41", "peg": "0.00",
          "eps": 606.83, "bps": 49296.05, "roe": 1.24,
          "salesgrowth": 1358.39, "operatingincomegrowt": 2452.03, "ordinaryincomegrowth": 77.98,
          "liabilitytoequity": 0.44, "enterpriseratio": 9759.21
        },
        {
          "rank": 2, "shcode": "138040", "hname": "메리츠금융지주",
          "per": "55.83", "pbr": "3.31", "peg": "0.00",
          "eps": 831.04, "bps": 14035.43, "roe": 6.72,
          "salesgrowth": 639.2, "operatingincomegrowt": -47.11, "ordinaryincomegrowth": -47.18,
          "liabilitytoequity": 66.05, "enterpriseratio": 2406.23
        }
      ]
    }
    """;

    static Task<HttpResponseMessage> Ok(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    });

    [Fact]
    public async Task GetFundamentalsRank_DefaultBoth_PerField_MapsCorrectGubunsAndShapesPayload()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(TwoRowSample));

        string result = await GetFundamentalsRankTool.GetFundamentalsRank(client, field: "per", count: 2);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t3341");
        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("\"gubun\":\"0\"", "market 'both' maps to gubun=0");
        body.Should().Contain("\"gubun1\":\"9\"", "field 'per' maps to gubun1=9");

        root.GetProperty("field").GetString().Should().Be("per");
        root.GetProperty("market").GetString().Should().Be("both");
        root.GetProperty("count").GetInt32().Should().Be(2);
        root.GetProperty("total_available").GetInt32().Should().Be(1341);

        JsonElement rows = root.GetProperty("rows");
        rows.GetArrayLength().Should().Be(2);
        rows[0].GetProperty("shcode").GetString().Should().Be("007700");
        rows[0].GetProperty("name").GetString().Should().Be("F&F홀딩스");
        rows[0].GetProperty("per").GetDouble().Should().BeApproximately(33.62, 1e-2);
        rows[0].GetProperty("operating_income_growth").GetDouble().Should().BeApproximately(2452.03, 1e-2,
            "LS field is operatingincomegrowt (sic) — wrapper translates the typo to a clean key");
    }

    [Theory]
    [InlineData("per", "9")]
    [InlineData("pbr", "a")]
    [InlineData("peg", "b")]
    [InlineData("eps", "6")]
    [InlineData("bps", "7")]
    [InlineData("roe", "8")]
    [InlineData("sales_growth", "1")]
    [InlineData("operating_income_growth", "2")]
    [InlineData("ordinary_income_growth", "3")]
    [InlineData("debt_to_equity", "4")]
    [InlineData("retained_earnings_ratio", "5")]
    public async Task GetFundamentalsRank_FieldAliases(string input, string expectedGubun1)
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(TwoRowSample));

        await GetFundamentalsRankTool.GetFundamentalsRank(client, field: input, count: 1);

        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain($"\"gubun1\":\"{expectedGubun1}\"");
    }

    [Theory]
    [InlineData("kospi", "1")]
    [InlineData("kosdaq", "2")]
    [InlineData("both", "0")]
    public async Task GetFundamentalsRank_MarketMapping(string input, string expectedGubun)
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(TwoRowSample));

        await GetFundamentalsRankTool.GetFundamentalsRank(client, field: "per", market: input, count: 1);

        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain($"\"gubun\":\"{expectedGubun}\"");
    }

    [Fact]
    public async Task GetFundamentalsRank_UnknownField_ReturnsValidationError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(TwoRowSample));

        string result = await GetFundamentalsRankTool.GetFundamentalsRank(client, field: "nonsense");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("not recognized");
    }

    [Fact]
    public async Task GetFundamentalsRank_CountOutOfRange_ReturnsValidationError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(TwoRowSample));

        string result = await GetFundamentalsRankTool.GetFundamentalsRank(client, field: "per", count: 0);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("count");
    }

    [Fact]
    public async Task GetFundamentalsRank_BusinessError_SurfacesEnvelope()
    {
        const string body = """{"rsp_cd":"99999","rsp_msg":"필수항목 누락"}""";
        var (client, _) = TestClientFactory.Create((_, _) => Ok(body));

        string result = await GetFundamentalsRankTool.GetFundamentalsRank(client, field: "per");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("business-level");
        root.GetProperty("details").GetProperty("rsp_cd").GetString().Should().Be("99999");
    }
}
