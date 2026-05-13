using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Catalog;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

/// <summary>
/// Pins <see cref="SearchStockTool"/> against the LS testbed-console response
/// for TR <c>t8436</c> ("주식종목조회 API용", captured 2026-05-13), and the
/// related catalog seeds for both <c>t8430</c> (legacy) and <c>t8436</c>.
/// </summary>
/// <remarks>
/// <para>
/// The <c>/stock/etc</c> path was the highest-risk seed guess. The testbed
/// confirms both <c>t8430</c> and <c>t8436</c> use it, and that <c>t8436</c>
/// adds <c>spac_gubun</c>, <c>bu12gubun</c>, and <c>filler</c> on top of the
/// 10 fields shared with <c>t8430</c>.
/// </para>
/// <para>
/// <see cref="SearchStockTool"/> ships pointed at <c>t8436</c> per LS's "API용"
/// label; this fixture protects that wiring.
/// </para>
/// </remarks>
public class SearchStockToolTestbedFixtureTests
{
    const string TestbedT8436Response = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "정상적으로 조회가 완료되었습니다.",
      "t8436OutBlock": [
        {
          "hname": "동화약품",   "shcode": "000020", "expcode": "KR7000020008",
          "etfgubun": "0", "memedan": "00001", "recprice": 10550,
          "uplmtprice": 13710, "dnlmtprice": 7390, "jnilclose": 10550,
          "gubun": "1", "spac_gubun": "N", "bu12gubun": "01", "filler": ""
        },
        {
          "hname": "현대차우",   "shcode": "005385", "expcode": "KR7005381009",
          "etfgubun": "0", "memedan": "00001", "recprice": 107900,
          "uplmtprice": 140200, "dnlmtprice": 75600, "jnilclose": 107900,
          "gubun": "1", "spac_gubun": "N", "bu12gubun": "01", "filler": ""
        }
      ]
    }
    """;

    static Task<HttpResponseMessage> Ok(string body) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });

    [Fact]
    public void Catalog_T8430_ShapeMatchesTestbed()
    {
        TrMeta meta = TrCatalog.Default.Get("t8430");
        meta.Path.Should().Be("/stock/etc");
        meta.OutBlocks.Should().ContainSingle().Which.IsArray.Should().BeTrue();
        meta.OutBlocks[0].Fields.Select(f => f.Name).Should().Contain(new[]
        {
            "hname", "shcode", "expcode", "etfgubun", "memedan",
            "recprice", "uplmtprice", "dnlmtprice", "jnilclose", "gubun",
        });
    }

    [Fact]
    public void Catalog_T8436_AddsSpacAndStatusFields()
    {
        TrMeta meta = TrCatalog.Default.Get("t8436");
        meta.Path.Should().Be("/stock/etc");
        meta.OutBlocks.Should().ContainSingle().Which.IsArray.Should().BeTrue();

        IEnumerable<string> fields = meta.OutBlocks[0].Fields.Select(f => f.Name);
        fields.Should().Contain(new[] { "spac_gubun", "bu12gubun", "filler" });
        // And still carry the t8430 base set.
        fields.Should().Contain(new[]
        {
            "hname", "shcode", "expcode", "etfgubun", "memedan",
            "recprice", "uplmtprice", "dnlmtprice", "jnilclose", "gubun",
        });
    }

    [Fact]
    public async Task SearchStock_ShipsAgainstT8436_NotT8430()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(TestbedT8436Response));

        await SearchStockTool.SearchStock(client, "동화");

        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/stock/etc");
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t8436");
    }

    [Fact]
    public async Task SearchStock_SurfacesSpacAndStatusCode()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(TestbedT8436Response));

        string result = await SearchStockTool.SearchStock(client, "동화");
        JsonElement first = JsonDocument.Parse(result).RootElement.GetProperty("results")[0];

        first.GetProperty("shcode").GetString().Should().Be("000020");
        first.GetProperty("name").GetString().Should().Be("동화약품");
        first.GetProperty("is_spac").GetBoolean().Should().BeFalse();
        first.GetProperty("status_code").GetString().Should().Be("01");
    }

    [Fact]
    public async Task SearchStock_IsSpac_FlagsSpacEntries()
    {
        const string spacBody = """
        {
          "rsp_cd": "00000",
          "rsp_msg": "정상",
          "t8436OutBlock": [
            {
              "hname": "테스트스팩",  "shcode": "999999", "expcode": "KR7999999999",
              "etfgubun": "0", "memedan": "00001", "recprice": 2000,
              "uplmtprice": 2600, "dnlmtprice": 1400, "jnilclose": 2000,
              "gubun": "2", "spac_gubun": "Y", "bu12gubun": "01", "filler": ""
            }
          ]
        }
        """;
        var (client, _) = TestClientFactory.Create((_, _) => Ok(spacBody));

        string result = await SearchStockTool.SearchStock(client, "스팩");
        JsonElement first = JsonDocument.Parse(result).RootElement.GetProperty("results")[0];

        first.GetProperty("is_spac").GetBoolean().Should().BeTrue();
        first.GetProperty("market").GetString().Should().Be("kosdaq");
    }
}
