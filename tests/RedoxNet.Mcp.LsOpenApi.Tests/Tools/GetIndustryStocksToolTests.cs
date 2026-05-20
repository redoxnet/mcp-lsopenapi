using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

public sealed class GetIndustryStocksToolTests
{
    const string T8424TwoCodes = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "조회완료",
      "t8424OutBlock": [
        { "upcode": "013", "hname": "전기전자" },
        { "upcode": "017", "hname": "화학" }
      ]
    }
    """;

    const string T8424MultiSemicon = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "조회완료",
      "t8424OutBlock": [
        { "upcode": "013", "hname": "전기전자" },
        { "upcode": "0A1", "hname": "반도체" },
        { "upcode": "0A2", "hname": "반도체 장비" }
      ]
    }
    """;

    static Task<HttpResponseMessage> Ok(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    });

    [Fact]
    public async Task GetIndustryStocks_DirectUpcode_ReturnsIndustryAndStocks()
    {
        const string t1516 = """
        {
          "rsp_cd": "00000",
          "rsp_msg": "조회완료",
          "t1516OutBlock": {
            "shcode": "000660",
            "pricejisu": "24350.50",
            "sign": "2",
            "change": "360.00",
            "jdiff": "1.50"
          },
          "t1516OutBlock1": [
            { "shcode": "005930", "hname": "삼성전자", "price": 71500, "sign": "2", "change": 500, "diff": "0.70",
              "volume": 12000000, "open": 71200, "high": 71600, "low": 71000, "perx": "11.50", "total": 4250000,
              "frgsvolume": 100000, "orgsvolume": 50000, "value": 850000, "sojinrate": "53.10", "beta": "0.9", "diff_vol": "-005.00" },
            { "shcode": "000660", "hname": "SK하이닉스", "price": 138000, "sign": "4", "change": 1500, "diff": "-1.07",
              "volume": 2000000, "open": 139500, "high": 140000, "low": 137500, "perx": "9.30", "total": 1000000,
              "frgsvolume": -50000, "orgsvolume": 0, "value": 270000, "sojinrate": "50.20", "beta": "1.1", "diff_vol": "-002.50" }
          ]
        }
        """;
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(t1516));
        var cache = new IndustryDataCache(client);

        // top_n matches the canned row count exactly, so the loop short-circuits
        // before issuing a second body-paging request — keeps this test focused
        // on response shape (paging has a dedicated test below).
        string result = await GetIndustryStocksTool.GetIndustryStocks(
            client, cache, upcode: "013", industry_keyword: null, market: "1", limit: 2);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t1516");

        JsonElement industry = root.GetProperty("industry");
        industry.GetProperty("upcode").GetString().Should().Be("013");
        industry.GetProperty("value").GetDouble().Should().BeApproximately(24350.50, 1e-2);
        industry.GetProperty("change_pct").GetDouble().Should().BeApproximately(1.50, 1e-2);

        JsonElement stocks = root.GetProperty("stocks");
        stocks.GetArrayLength().Should().Be(2);
        stocks[0].GetProperty("shcode").GetString().Should().Be("005930");
        stocks[0].GetProperty("change_pct").GetDouble().Should().BeApproximately(0.70, 1e-2);
        stocks[1].GetProperty("change").GetInt64().Should().Be(-1500, "sign=4 should flip the magnitude to negative");
        stocks[1].GetProperty("change_pct").GetDouble().Should().BeApproximately(-1.07, 1e-2);
    }

    [Fact]
    public async Task GetIndustryStocks_KeywordSingleMatch_EchoesResolved()
    {
        const string t1516 = """
        {
          "rsp_cd": "00000",
          "rsp_msg": "조회완료",
          "t1516OutBlock": { "shcode": "005930", "pricejisu": "24350.50", "sign": "2", "change": "360.00", "jdiff": "1.50" },
          "t1516OutBlock1": [
            { "shcode": "005930", "hname": "삼성전자", "price": 71500, "sign": "2", "change": 500, "diff": "0.70",
              "volume": 1, "open": 1, "high": 1, "low": 1, "perx": "0", "total": 0, "frgsvolume": 0, "orgsvolume": 0, "value": 0 }
          ]
        }
        """;
        var (client, _) = TestClientFactory.Create((request, _) =>
        {
            string trCode = request.Headers.GetValues("tr_cd").First();
            return trCode == "t8424" ? Ok(T8424TwoCodes) : Ok(t1516);
        });
        var cache = new IndustryDataCache(client);

        string result = await GetIndustryStocksTool.GetIndustryStocks(
            client, cache, upcode: null, industry_keyword: "전기", market: "1", limit: 5);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        JsonElement resolved = root.GetProperty("resolved");
        resolved.GetProperty("upcode").GetString().Should().Be("013");
        resolved.GetProperty("name").GetString().Should().Be("전기전자");
        resolved.GetProperty("matched_via").GetString().Should().Be("keyword");
    }

    [Fact]
    public async Task GetIndustryStocks_KeywordNoMatch_ReturnsIndustryNotFound()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(T8424TwoCodes));
        var cache = new IndustryDataCache(client);

        string result = await GetIndustryStocksTool.GetIndustryStocks(
            client, cache, upcode: null, industry_keyword: "메타버스", market: "1", limit: 5);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("No industry");
        root.GetProperty("details").GetProperty("error_code").GetString().Should().Be("IndustryNotFound");
    }

    [Fact]
    public async Task GetIndustryStocks_KeywordMultipleMatches_ReturnsAmbiguousIndustry()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(T8424MultiSemicon));
        var cache = new IndustryDataCache(client);

        string result = await GetIndustryStocksTool.GetIndustryStocks(
            client, cache, upcode: null, industry_keyword: "반도체", market: "1", limit: 5);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("details").GetProperty("error_code").GetString().Should().Be("AmbiguousIndustry");
        JsonElement candidates = root.GetProperty("details").GetProperty("candidates");
        candidates.GetArrayLength().Should().Be(2);
        candidates[0].GetProperty("upcode").GetString().Should().BeOneOf("0A1", "0A2");
    }

    [Fact]
    public async Task GetIndustryStocks_BodyPaging_FollowsLastShcodeCursorUntilTopN()
    {
        int callCount = 0;
        var (client, handler) = TestClientFactory.Create((request, _) =>
        {
            // Each t1516 call returns 2 rows; cursor advances on each page.
            callCount++;
            string body = request.Content!.ReadAsStringAsync().Result;
            bool firstPage = body.Contains("\"shcode\":\"\"", StringComparison.Ordinal);
            string page = firstPage
                ? """
                {
                  "rsp_cd": "00000",
                  "t1516OutBlock": { "shcode": "AAA002", "pricejisu": "1", "sign": "3", "change": "0", "jdiff": "0" },
                  "t1516OutBlock1": [
                    { "shcode": "AAA001", "hname": "n1", "price": 1, "sign": "3", "change": 0, "diff": "0", "volume": 1, "open": 1, "high": 1, "low": 1, "perx": "0", "total": 0, "frgsvolume": 0, "orgsvolume": 0, "value": 0 },
                    { "shcode": "AAA002", "hname": "n2", "price": 1, "sign": "3", "change": 0, "diff": "0", "volume": 1, "open": 1, "high": 1, "low": 1, "perx": "0", "total": 0, "frgsvolume": 0, "orgsvolume": 0, "value": 0 }
                  ]
                }
                """
                : """
                {
                  "rsp_cd": "00000",
                  "t1516OutBlock": { "shcode": "AAA004", "pricejisu": "1", "sign": "3", "change": "0", "jdiff": "0" },
                  "t1516OutBlock1": [
                    { "shcode": "AAA003", "hname": "n3", "price": 1, "sign": "3", "change": 0, "diff": "0", "volume": 1, "open": 1, "high": 1, "low": 1, "perx": "0", "total": 0, "frgsvolume": 0, "orgsvolume": 0, "value": 0 },
                    { "shcode": "AAA004", "hname": "n4", "price": 1, "sign": "3", "change": 0, "diff": "0", "volume": 1, "open": 1, "high": 1, "low": 1, "perx": "0", "total": 0, "frgsvolume": 0, "orgsvolume": 0, "value": 0 }
                  ]
                }
                """;
            return Ok(page);
        });
        var cache = new IndustryDataCache(client);

        string result = await GetIndustryStocksTool.GetIndustryStocks(
            client, cache, upcode: "013", industry_keyword: null, market: "1", limit: 3);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("count").GetInt32().Should().Be(3);
        JsonElement stocks = root.GetProperty("stocks");
        stocks[0].GetProperty("shcode").GetString().Should().Be("AAA001");
        stocks[2].GetProperty("shcode").GetString().Should().Be("AAA003");
        callCount.Should().Be(2, "first page returned 2 rows, top_n=3 needs one more page");

        // Second page's request body should echo the previous page's cursor.
        string secondBody = await handler.Requests[1].Content!.ReadAsStringAsync();
        secondBody.Should().Contain("\"shcode\":\"AAA002\"");
    }

    [Fact]
    public async Task GetIndustryStocks_UpcodeWinsOverKeyword_NoCatalogCall()
    {
        const string t1516 = """
        {
          "rsp_cd": "00000",
          "t1516OutBlock": { "shcode": "005930", "pricejisu": "1", "sign": "3", "change": "0", "jdiff": "0" },
          "t1516OutBlock1": []
        }
        """;
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(t1516));
        var cache = new IndustryDataCache(client);

        await GetIndustryStocksTool.GetIndustryStocks(
            client, cache, upcode: "013", industry_keyword: "anything", market: "1", limit: 5);

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t1516");
    }
}
