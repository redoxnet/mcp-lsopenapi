using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

/// <summary>
/// Pins <see cref="GetTopStocksTool"/> against representative LS testbed
/// responses for the top-stock ranking TR family: t1441/t1444/t1463/t1466.
/// </summary>
public class GetTopStocksToolFixtureTests
{
    static Task<HttpResponseMessage> Ok(string body) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        });

    const string T1441Response = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "조회완료",
      "t1441OutBlock": { "idx": 0 },
      "t1441OutBlock1": [
        {
          "hname": "KC코트렐", "price": 2845, "sign": "5", "change": 20, "diff": "-00.70",
          "volume": 514736, "offerrem1": 2780, "offerho1": 2845,
          "bidho1": 2840, "bidrem1": 1307, "updaycnt": 2, "jnildiff": "-12.39",
          "shcode": "119650", "open": 2870, "high": 2890, "low": 2760,
          "voldiff": "00011.86", "value": 1449, "total": 1027, "ex_shcode": "119650"
        }
      ]
    }
    """;

    const string T1463Response = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "정상적으로 조회가 완료되었습니다.",
      "t1463OutBlock": { "idx": 0 },
      "t1463OutBlock1": [
        {
          "hname": "삼성전자", "price": 71800, "sign": "5", "change": 400, "diff": "-00.55",
          "volume": 4817961, "value": 347308, "jnilvalue": 874631, "bef_diff": "0000039.71",
          "shcode": "005930", "filler": "", "jnilvolume": 12161798, "ex_shcode": "005930",
          "total": 4280334
        }
      ]
    }
    """;

    const string T1466Response = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "정상적으로 조회가 완료되었습니다.",
      "t1466OutBlock": { "hhmm": "10:26", "idx": 0 },
      "t1466OutBlock1": [
        {
          "shcode": "123700", "hname": "SJM", "price": 5610, "sign": "5", "change": 230,
          "diff": "-3.94", "stdvolume": 141629, "volume": 953956, "voldiff": "00673.56",
          "open": 5700, "high": 5890, "low": 5550, "ex_shcode": "123700"
        }
      ]
    }
    """;

    [Fact]
    public async Task GetTopStocks_Gainers_DispatchesT1441AndParsesRankingRow()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(T1441Response));

        string result = await GetTopStocksTool.GetTopStocks(
            client,
            kind: "gainers",
            market: "kospi",
            top_n: 10,
            basis: "previous_day");

        handler.Requests.Should().HaveCount(1);
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/stock/market-data");
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t1441");

        string sent = await handler.Requests[0].Content!.ReadAsStringAsync();
        sent.Should().Contain("\"gubun1\":\"1\"");
        sent.Should().Contain("\"gubun2\":\"0\"");
        sent.Should().Contain("\"gubun3\":\"1\"");
        sent.Should().Contain("\"exchgubun\":\"U\"");

        JsonElement root = JsonDocument.Parse(result).RootElement;
        root.GetProperty("kind").GetString().Should().Be("gainers");
        root.GetProperty("market").GetString().Should().Be("kospi");
        root.GetProperty("basis").GetString().Should().Be("previous_day");
        root.GetProperty("count").GetInt32().Should().Be(1);

        JsonElement row = root.GetProperty("rows")[0];
        row.GetProperty("rank").GetInt32().Should().Be(1);
        row.GetProperty("tr_code").GetString().Should().Be("t1441");
        row.GetProperty("shcode").GetString().Should().Be("119650");
        row.GetProperty("name").GetString().Should().Be("KC코트렐");
        row.GetProperty("change_percent").GetDouble().Should().BeApproximately(-0.70, 0.01);
        row.GetProperty("best_ask").GetInt64().Should().Be(2845);
        row.GetProperty("best_bid").GetInt64().Should().Be(2840);
        row.GetProperty("value").GetInt64().Should().Be(1449);
        row.GetProperty("market_cap").GetInt64().Should().Be(1027);
    }

    [Fact]
    public async Task GetTopStocks_Amount_DispatchesT1463WithExchangeAndPriceFilters()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(T1463Response));

        string result = await GetTopStocksTool.GetTopStocks(
            client,
            kind: "amount",
            market: "all",
            top_n: 5,
            basis: "today",
            exchange: "krx",
            min_price: 1000,
            max_price: 100000,
            min_volume: 10000);

        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t1463");
        string sent = await handler.Requests[0].Content!.ReadAsStringAsync();
        sent.Should().Contain("\"gubun\":\"0\"");
        sent.Should().Contain("\"jnilgubun\":\"0\"");
        sent.Should().Contain("\"sprice\":1000");
        sent.Should().Contain("\"eprice\":100000");
        sent.Should().Contain("\"volume\":10000");
        sent.Should().Contain("\"exchgubun\":\"K\"");

        JsonElement row = JsonDocument.Parse(result).RootElement.GetProperty("rows")[0];
        row.GetProperty("shcode").GetString().Should().Be("005930");
        row.GetProperty("value").GetInt64().Should().Be(347308);
        row.GetProperty("previous_value").GetInt64().Should().Be(874631);
        row.GetProperty("value_change_percent").GetDouble().Should().BeApproximately(39.71, 0.01);
        row.GetProperty("previous_volume").GetInt64().Should().Be(12161798);
    }

    [Fact]
    public async Task GetTopStocks_VolumeSurge_ParsesSnapshotTimeAndSurgePercent()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(T1466Response));

        string result = await GetTopStocksTool.GetTopStocks(client, kind: "volume_surge", market: "kosdaq");

        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t1466");
        string sent = await handler.Requests[0].Content!.ReadAsStringAsync();
        sent.Should().Contain("\"gubun\":\"2\"");
        sent.Should().Contain("\"type1\":\"0\"");
        sent.Should().Contain("\"type2\":\"0\"");

        JsonElement row = JsonDocument.Parse(result).RootElement.GetProperty("rows")[0];
        row.GetProperty("snapshot_time").GetString().Should().Be("10:26");
        row.GetProperty("reference_volume").GetInt64().Should().Be(141629);
        row.GetProperty("volume_surge_percent").GetDouble().Should().BeApproximately(673.56, 0.01);
        row.GetProperty("open").GetInt64().Should().Be(5700);
    }

    [Fact]
    public async Task GetTopStocks_MarketCapAll_CallsKospiAndKosdaqThenSortsByMarketCap()
    {
        static Task<HttpResponseMessage> Responder(HttpRequestMessage request, CancellationToken _)
        {
            string body = request.Content!.ReadAsStringAsync().Result;
            string response = body.Contains("\"upcode\":\"301\"")
                ? """
                  {
                    "rsp_cd": "00000",
                    "t1444OutBlock": { "idx": 0 },
                    "t1444OutBlock1": [
                      { "shcode": "035720", "hname": "카카오", "price": 55000, "sign": "2", "change": 1000,
                        "diff": "1.85", "volume": 100, "vol_rate": "0.10", "total": 250000,
                        "rate": "5.00", "for_rate": "26.00" }
                    ]
                  }
                  """
                : """
                  {
                    "rsp_cd": "00000",
                    "t1444OutBlock": { "idx": 0 },
                    "t1444OutBlock1": [
                      { "shcode": "005930", "hname": "삼성전자", "price": 71700, "sign": "5", "change": 500,
                        "diff": "-0.69", "volume": 4817941, "vol_rate": "1.26", "total": 4280334,
                        "rate": "19.67", "for_rate": "52.54" }
                    ]
                  }
                  """;
            return Ok(response);
        }

        var (client, handler) = TestClientFactory.Create(Responder);

        string result = await GetTopStocksTool.GetTopStocks(client, kind: "market_cap", market: "all", top_n: 2);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t1444");
        handler.Requests[1].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t1444");

        JsonElement root = JsonDocument.Parse(result).RootElement;
        JsonElement trCodes = root.GetProperty("tr_codes");
        trCodes.GetArrayLength().Should().Be(1);
        trCodes[0].GetString().Should().Be("t1444");

        JsonElement rows = root.GetProperty("rows");
        rows.GetArrayLength().Should().Be(2);
        rows[0].GetProperty("shcode").GetString().Should().Be("005930");
        rows[1].GetProperty("shcode").GetString().Should().Be("035720");
        rows[0].GetProperty("market_cap").GetInt64().Should().Be(4280334);
        rows[0].GetProperty("foreign_ownership_percent").GetDouble().Should().BeApproximately(52.54, 0.01);
    }

    [Fact]
    public async Task GetTopStocks_InvalidKind_ReturnsError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(T1441Response));

        string result = await GetTopStocksTool.GetTopStocks(client, kind: "mystery");

        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString()
            .Should().Contain("kind must be one of");
    }
}
