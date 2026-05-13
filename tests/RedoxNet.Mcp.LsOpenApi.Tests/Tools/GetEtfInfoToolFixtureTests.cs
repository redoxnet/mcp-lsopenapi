using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

/// <summary>
/// Pins <see cref="GetEtfInfoTool"/> against a real LS testbed response for
/// TR <c>t1901</c>. Many ETF-specific fields are zero in this sample
/// because the captured shcode is a regular equity, but the parsing path
/// is exercised end-to-end (NAV / kasis / cocrate / LP list / futures).
/// </summary>
public class GetEtfInfoToolFixtureTests
{
    static Task<HttpResponseMessage> Ok(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    });

    const string TestbedT1901Response = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "정상적으로 조회가 완료되었습니다.",
      "t1901OutBlock": {
        "hname": "유진투자증권", "shcode": "001200",
        "price": 3685, "sign": "2", "change": 25, "diff": "000.68",
        "open": 3660, "high": 3750, "low": 3645,
        "volume": "000000322192", "value": 1192, "vol": "000.33",
        "jnilvolume": "000001274097", "volumediff": 951905,
        "recprice": 3660, "uplmtprice": 4755, "dnlmtprice": 2565, "subprice": 2560,
        "nav": "00000.00", "navsign": "3", "navchange": "00000.00", "navdiff": "000.00",
        "jnilnav": "0", "jnilnavsign": "", "jnilnavchange": "0", "jnilnavdiff": "0",
        "kasis": "0", "cocrate": "0", "exhratio": "007.17", "spread": "000.14",
        "etftotcap": 0, "listing": 96866, "leverage": 0, "leverage2": "000.00",
        "taxgubun": "0", "etf_kind": "", "etp_gb": "", "etn_kind_cd": "", "etn_elback_yn": "",
        "idx_asset_class1": "", "issuernmk": "", "opcom_nmk": "",
        "lp_holdvol": "000000000000",
        "lp_nm1": "신영증권", "lp_nm2": "eBEST 증권", "lp_nm3": "", "lp_nm4": "", "lp_nm5": "",
        "high52w": 3750, "high52wdate": "20230605",
        "low52w": 2185, "low52wdate": "20220930",
        "highyear": 3750, "highyeardate": "20230605",
        "lowyear": 2230, "lowyeardate": "20230103",
        "opentime": "090013", "hightime": "091719", "lowtime": "090057",
        "futcode": "101T6000", "futname": "F 202306", "futprice": "343.70",
        "futsign": "2", "futchange": "000.75", "futdiff": "000.22",
        "vi_gubun": "", "payday": "", "listdate": "19870824"
      }
    }
    """;

    [Fact]
    public async Task GetEtfInfo_LsTestbedResponse_ParsesCleanly()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(TestbedT1901Response));

        string result = await GetEtfInfoTool.GetEtfInfo(client, "001200");

        handler.Requests.Should().HaveCount(1);
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/stock/etf");
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t1901");

        JsonElement root = JsonDocument.Parse(result).RootElement;

        // Identification
        root.GetProperty("shcode").GetString().Should().Be("001200");
        root.GetProperty("name").GetString().Should().Be("유진투자증권");
        root.GetProperty("listing_shares").GetInt64().Should().Be(96866);
        root.GetProperty("listing_date").GetString().Should().Be("19870824");

        // Price block — verify string-typed numerics are parsed
        root.GetProperty("price").GetInt64().Should().Be(3685);
        root.GetProperty("change").GetInt64().Should().Be(25);
        root.GetProperty("change_percent").GetDouble().Should().BeApproximately(0.68, 0.01);
        root.GetProperty("volume").GetInt64().Should().Be(322192); // "000000322192"
        root.GetProperty("turnover_percent").GetDouble().Should().BeApproximately(0.33, 0.01);
        root.GetProperty("upper_limit_price").GetInt64().Should().Be(4755);
        root.GetProperty("lower_limit_price").GetInt64().Should().Be(2565);

        // NAV / divergence block
        root.GetProperty("nav").GetDouble().Should().Be(0.0);
        root.GetProperty("divergence_percent").GetDouble().Should().Be(0.0);
        root.GetProperty("foreign_ownership_percent").GetDouble().Should().BeApproximately(7.17, 0.01);
        root.GetProperty("spread_percent").GetDouble().Should().BeApproximately(0.14, 0.01);
        root.TryGetProperty("tracking_basis", out _).Should().BeFalse("kasis is too noisy on the virtual server to surface as a curated field");
        root.TryGetProperty("exchange_divergence_percent", out _).Should().BeFalse("renamed to foreign_ownership_percent to reflect actual semantics");

        // 52-week / year range
        root.GetProperty("high_52w").GetInt64().Should().Be(3750);
        root.GetProperty("high_52w_date").GetString().Should().Be("20230605");
        root.GetProperty("low_52w").GetInt64().Should().Be(2185);
        root.GetProperty("low_52w_date").GetString().Should().Be("20220930");

        // LPs — filter empties
        JsonElement lps = root.GetProperty("liquidity_providers");
        lps.GetArrayLength().Should().Be(2);
        lps[0].GetString().Should().Be("신영증권");
        lps[1].GetString().Should().Be("eBEST 증권");

        // Futures sub-object — present because futcode is non-empty
        JsonElement futures = root.GetProperty("futures");
        futures.ValueKind.Should().Be(JsonValueKind.Object);
        futures.GetProperty("code").GetString().Should().Be("101T6000");
        futures.GetProperty("name").GetString().Should().Be("F 202306");
        futures.GetProperty("price").GetDouble().Should().BeApproximately(343.70, 0.01);
    }

    [Fact]
    public async Task GetEtfInfo_EmptyShcode_ReturnsError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok("""{"rsp_cd":"00000"}"""));

        string result = await GetEtfInfoTool.GetEtfInfo(client, "");

        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString().Should().Contain("shcode");
    }

    [Fact]
    public async Task GetEtfInfo_BusinessError_ReturnsLsErrorEnvelope()
    {
        string body = """
        { "rsp_cd": "IGW00501", "rsp_msg": "잘못된 종목코드입니다." }
        """;
        var (client, _) = TestClientFactory.Create((_, _) => Ok(body));

        string result = await GetEtfInfoTool.GetEtfInfo(client, "999999");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.TryGetProperty("error", out _).Should().BeTrue();
        root.GetProperty("details").GetProperty("rsp_cd").GetString().Should().Be("IGW00501");
    }

    [Fact]
    public async Task GetEtfInfo_FuturesMissing_ReturnsNull()
    {
        // Strip futcode → tool should emit futures=null.
        string body = """
        {
          "rsp_cd": "00000",
          "t1901OutBlock": {
            "hname": "TestETF", "shcode": "069500",
            "price": 30000, "sign": "2", "change": 100, "diff": "000.33",
            "nav": "29950.00", "kasis": "0", "cocrate": "0.10",
            "futcode": "", "futname": "", "futprice": ""
          }
        }
        """;
        var (client, _) = TestClientFactory.Create((_, _) => Ok(body));

        string result = await GetEtfInfoTool.GetEtfInfo(client, "069500");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("futures").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("nav").GetDouble().Should().BeApproximately(29950.0, 0.01);
    }

    [Fact]
    public async Task GetEtfInfo_KodexLikePayload_SurfacesForeignOwnershipNotDivergence()
    {
        // Reproduces the 2026-05-13 KODEX 200 E2E observation: cocrate is the
        // real NAV divergence (~0.01%) while exhratio is foreign ownership (~23%).
        // Earlier drafts mis-labeled exhratio as "exchange_divergence_percent".
        string body = """
        {
          "rsp_cd": "00000",
          "t1901OutBlock": {
            "hname": "KODEX 200", "shcode": "069500",
            "price": 122655, "sign": "2", "change": 3555, "diff": "002.98",
            "nav": "122737.70", "navsign": "2", "navchange": "3708.75", "navdiff": "003.12",
            "jnilnav": "119028.95",
            "kasis": "0", "cocrate": "0.01", "exhratio": "023.40", "spread": "000.07",
            "etftotcap": 7654321, "listing": 100000,
            "futcode": "", "futname": "", "futprice": ""
          }
        }
        """;
        var (client, _) = TestClientFactory.Create((_, _) => Ok(body));

        string result = await GetEtfInfoTool.GetEtfInfo(client, "069500");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("divergence_percent").GetDouble().Should().BeApproximately(0.01, 0.001);
        root.GetProperty("foreign_ownership_percent").GetDouble().Should().BeApproximately(23.40, 0.01);
        root.TryGetProperty("tracking_basis", out _).Should().BeFalse();
        root.TryGetProperty("exchange_divergence_percent", out _).Should().BeFalse();
    }
}
