using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

/// <summary>
/// Pins <see cref="GetEtfHoldingsTool"/> against a real LS testbed
/// response for TR <c>t1904</c>. The captured PDF has two heterogeneous
/// holdings (an equity 005930 and a treasury bond
/// KR103501GC90), which exercises the bond-shcode path the tool must
/// pass through verbatim.
/// </summary>
public class GetEtfHoldingsToolFixtureTests
{
    static Task<HttpResponseMessage> Ok(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    });

    // Note: the garbled `upname` field from the live response is omitted —
    // the tool does not consume it and including it here only confuses test
    // setup. Everything the tool reads is preserved.
    const string TestbedT1904Response = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "정상적으로 조회가 완료되었습니다.",
      "t1904OutBlock": {
        "date": "20230104", "chk_tday": "0",
        "price": 10690, "sign": "3", "change": 0, "diff": "0.00",
        "volume": 0,
        "nav": "0.00", "navsign": "3", "navchange": "0.00", "navdiff": "0.00",
        "jnilnav": "10689.10", "jnilnavsign": "5", "jnilnavchange": "-3.50", "jnilnavdiff": "-0.03",
        "etfnum": 7, "etfcunum": 21, "etftotcap": 224,
        "tot_pval": 1008302135, "tot_sigatval": 401022935, "cash": 0,
        "futcode": "101T9000", "futname": "F 202309", "futprice": "351.70",
        "futsign": "3", "futchange": "0.00", "futdiff": "0.00",
        "upcode": "000"
      },
      "t1904OutBlock1": [
        {
          "shcode": "005930", "hname": "삼성전자",
          "weight": "27.94", "price": 57800, "sign": "2", "change": 2400, "diff": "4.33", "diff2": "4.33",
          "volume": 20188071, "value": 1151474,
          "pvalue": 281709000, "sigatvalue": 293913000,
          "parprice": 0, "profitdate": "00000000", "icux": 5085
        },
        {
          "shcode": "KR103501GC90", "hname": "국고03125-2709(22-8)",
          "weight": "19.57", "price": 0, "sign": "", "change": 0, "diff": "0", "diff2": "0",
          "volume": 0, "value": 0,
          "pvalue": 0, "sigatvalue": 0,
          "parprice": 0, "profitdate": "", "icux": 0
        }
      ]
    }
    """;

    [Fact]
    public async Task GetEtfHoldings_LsTestbedResponse_ParsesSummaryAndPdf()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(TestbedT1904Response));

        string result = await GetEtfHoldingsTool.GetEtfHoldings(client, "069500");

        handler.Requests.Should().HaveCount(1);
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/stock/etf");
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t1904");

        JsonElement root = JsonDocument.Parse(result).RootElement;

        // Summary
        root.GetProperty("shcode").GetString().Should().Be("069500");
        root.GetProperty("date").GetString().Should().Be("20230104");
        root.GetProperty("confirmed_today").GetBoolean().Should().BeFalse(); // chk_tday="0"
        root.GetProperty("price").GetInt64().Should().Be(10690);
        root.GetProperty("previous_nav").GetDouble().Should().BeApproximately(10689.10, 0.01);
        root.GetProperty("holdings_count").GetInt64().Should().Be(7);
        root.GetProperty("cu_units").GetInt64().Should().Be(21);
        root.GetProperty("total_assets").GetInt64().Should().Be(224);
        root.GetProperty("total_market_value").GetInt64().Should().Be(401022935);
        root.GetProperty("total_valuation").GetInt64().Should().Be(1008302135);
        root.GetProperty("cash").GetInt64().Should().Be(0);

        // Holdings — must pass both equity shcode (6-digit) and bond ISIN through verbatim.
        JsonElement holdings = root.GetProperty("holdings");
        holdings.GetArrayLength().Should().Be(2);

        JsonElement samsung = holdings[0];
        samsung.GetProperty("shcode").GetString().Should().Be("005930");
        samsung.GetProperty("name").GetString().Should().Be("삼성전자");
        samsung.GetProperty("weight_percent").GetDouble().Should().BeApproximately(27.94, 0.01);
        samsung.GetProperty("price").GetInt64().Should().Be(57800);
        samsung.GetProperty("change").GetInt64().Should().Be(2400);
        samsung.GetProperty("change_percent").GetDouble().Should().BeApproximately(4.33, 0.01);
        samsung.GetProperty("market_value").GetInt64().Should().Be(293913000);
        samsung.GetProperty("etf_valuation").GetInt64().Should().Be(281709000);

        JsonElement bond = holdings[1];
        bond.GetProperty("shcode").GetString().Should().Be("KR103501GC90");
        bond.GetProperty("name").GetString().Should().Be("국고03125-2709(22-8)");
        bond.GetProperty("weight_percent").GetDouble().Should().BeApproximately(19.57, 0.01);
    }

    [Fact]
    public async Task GetEtfHoldings_EmptyShcode_ReturnsError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok("""{"rsp_cd":"00000"}"""));

        string result = await GetEtfHoldingsTool.GetEtfHoldings(client, "");

        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString().Should().Contain("shcode");
    }

    [Fact]
    public async Task GetEtfHoldings_MissingPdfArray_ReturnsEmptyHoldings()
    {
        // Some odd cases return the summary block but no t1904OutBlock1.
        string body = """
        {
          "rsp_cd": "00000",
          "t1904OutBlock": {
            "date": "20230104", "chk_tday": "1",
            "price": 10000, "nav": "10000.00",
            "etfnum": 0, "etftotcap": 100
          }
        }
        """;
        var (client, _) = TestClientFactory.Create((_, _) => Ok(body));

        string result = await GetEtfHoldingsTool.GetEtfHoldings(client, "069500");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("confirmed_today").GetBoolean().Should().BeTrue();
        root.GetProperty("holdings").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task GetEtfHoldings_BusinessError_ReturnsErrorEnvelope()
    {
        string body = """
        { "rsp_cd": "IGW00501", "rsp_msg": "잘못된 종목코드입니다." }
        """;
        var (client, _) = TestClientFactory.Create((_, _) => Ok(body));

        string result = await GetEtfHoldingsTool.GetEtfHoldings(client, "999999");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.TryGetProperty("error", out _).Should().BeTrue();
        root.GetProperty("details").GetProperty("rsp_cd").GetString().Should().Be("IGW00501");
    }

    [Fact]
    public async Task GetEtfHoldings_ForeignAssetEtf_SuccessButBlockMissing_HintsUserClearly()
    {
        // Reproduces the 2026-05-13 TIGER 미국S&P500 E2E observation: LS
        // returns rsp_cd "00000" but the OutBlock is absent for ETFs that
        // hold foreign assets. The tool must produce a user-friendly hint,
        // not the raw LS-internal "t1904OutBlock was missing" string.
        string body = """
        {
          "rsp_cd": "00000",
          "rsp_msg": "정상적으로 조회가 완료되었습니다."
        }
        """;
        var (client, _) = TestClientFactory.Create((_, _) => Ok(body));

        string result = await GetEtfHoldingsTool.GetEtfHoldings(client, "360750");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        string error = root.GetProperty("error").GetString()!;
        error.Should().Contain("PDF");
        error.Should().Contain("foreign assets");
        root.GetProperty("details").GetProperty("shcode").GetString().Should().Be("360750");
    }
}
