using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

public class GetTopStocksToolTests
{
    static (RedoxNet.LsOpenApi.Core.Http.LsApiClient client, StubHttpMessageHandler handler) CreateNoCallClient()
    {
        return TestClientFactory.Create((_, _) =>
            throw new InvalidOperationException("Validation failure should not call LS."));
    }

    static Task<HttpResponseMessage> Ok(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    });

    [Fact]
    public async Task GetTopStocks_InvalidKind_ReturnsErrorWithoutCallingLs()
    {
        var (client, handler) = CreateNoCallClient();

        string result = await GetTopStocksTool.GetTopStocks(client, kind: "mystery");

        handler.Requests.Should().BeEmpty();
        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString()
            .Should().Contain("kind must be one of");
    }

    [Fact]
    public async Task GetTopStocks_InvalidMarket_ReturnsErrorWithoutCallingLs()
    {
        var (client, handler) = CreateNoCallClient();

        string result = await GetTopStocksTool.GetTopStocks(client, kind: "volume", market: "konex");

        handler.Requests.Should().BeEmpty();
        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString()
            .Should().Contain("market");
    }

    [Fact]
    public async Task GetTopStocks_InvalidBasis_ReturnsErrorWithoutCallingLs()
    {
        var (client, handler) = CreateNoCallClient();

        string result = await GetTopStocksTool.GetTopStocks(client, kind: "amount", basis: "weekly");

        handler.Requests.Should().BeEmpty();
        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString()
            .Should().Contain("basis");
    }

    [Fact]
    public async Task GetTopStocks_InvalidExchange_ReturnsErrorWithoutCallingLs()
    {
        var (client, handler) = CreateNoCallClient();

        string result = await GetTopStocksTool.GetTopStocks(client, kind: "gainers", exchange: "darkpool");

        handler.Requests.Should().BeEmpty();
        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString()
            .Should().Contain("exchange");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task GetTopStocks_LimitOutOfRange_ReturnsErrorWithoutCallingLs(int topN)
    {
        var (client, handler) = CreateNoCallClient();

        string result = await GetTopStocksTool.GetTopStocks(client, kind: "volume", limit: topN);

        handler.Requests.Should().BeEmpty();
        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString()
            .Should().Contain("limit");
    }

    [Fact]
    public async Task GetTopStocks_MaxPriceBelowMinPrice_ReturnsErrorWithoutCallingLs()
    {
        var (client, handler) = CreateNoCallClient();

        string result = await GetTopStocksTool.GetTopStocks(
            client,
            kind: "volume",
            min_price: 10000,
            max_price: 5000);

        handler.Requests.Should().BeEmpty();
        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString()
            .Should().Contain("max_price");
    }

    [Theory]
    [InlineData("gainer", "t1441", "\"gubun2\":\"0\"")]
    [InlineData("loser", "t1441", "\"gubun2\":\"1\"")]
    [InlineData("flat", "t1441", "\"gubun2\":\"2\"")]
    [InlineData("marketcap", "t1444", "\"upcode\":\"001\"")]
    [InlineData("trading_value", "t1463", "\"jnilgubun\":\"0\"")]
    [InlineData("volume_spike", "t1466", "\"type1\":\"0\"")]
    public async Task GetTopStocks_KindAliases_DispatchToExpectedTr(
        string alias,
        string expectedTr,
        string expectedBodyFragment)
    {
        const string emptyRanking = """
        {
          "rsp_cd": "00000",
          "rsp_msg": "정상",
          "t1441OutBlock": { "idx": 0 },
          "t1441OutBlock1": [],
          "t1444OutBlock": { "idx": 0 },
          "t1444OutBlock1": [],
          "t1463OutBlock": { "idx": 0 },
          "t1463OutBlock1": [],
          "t1466OutBlock": { "idx": 0 },
          "t1466OutBlock1": []
        }
        """;
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(emptyRanking));

        string result = await GetTopStocksTool.GetTopStocks(client, alias, market: "kospi");

        JsonDocument.Parse(result).RootElement.GetProperty("count").GetInt32().Should().Be(0);
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be(expectedTr);
        string sent = await handler.Requests[0].Content!.ReadAsStringAsync();
        sent.Should().Contain(expectedBodyFragment);
    }

    [Fact]
    public async Task GetTopStocks_BusinessError_ReturnsBusinessErrorEnvelope()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok("""
        { "rsp_cd": "IGW00501", "rsp_msg": "잘못된 요청입니다." }
        """));

        string result = await GetTopStocksTool.GetTopStocks(client, kind: "volume");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("business-level");
        root.GetProperty("details").GetProperty("rsp_cd").GetString().Should().Be("IGW00501");
        root.GetProperty("details").GetProperty("rsp_msg").GetString().Should().Be("잘못된 요청입니다.");
    }

    [Fact]
    public async Task GetTopStocks_NonSurgeRows_OmitNullSnapshotTime()
    {
        const string body = """
        {
          "rsp_cd": "00000",
          "rsp_msg": "정상",
          "t1463OutBlock": { "idx": 0 },
          "t1463OutBlock1": [
            {
              "hname": "삼성전자", "price": 71800, "sign": "5", "change": 400,
              "diff": "-00.55", "volume": 4817961, "value": 347308,
              "jnilvalue": 874631, "bef_diff": "0000039.71",
              "shcode": "005930", "jnilvolume": 12161798, "total": 4280334
            }
          ]
        }
        """;
        var (client, _) = TestClientFactory.Create((_, _) => Ok(body));

        string result = await GetTopStocksTool.GetTopStocks(client, kind: "amount");
        JsonElement row = JsonDocument.Parse(result).RootElement.GetProperty("rows")[0];

        row.TryGetProperty("snapshot_time", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetTopStocks_Volume_PaginatesWithIdxUntilEnoughRows()
    {
        int call = 0;
        var (client, handler) = TestClientFactory.Create((_, _) =>
        {
            call++;
            string body = call == 1
                ? """
                  {
                    "rsp_cd": "00000",
                    "rsp_msg": "정상",
                    "t1452OutBlock": { "idx": 40 },
                    "t1452OutBlock1": [
                      { "hname": "A", "price": 1000, "sign": "2", "change": 10, "diff": "1.00",
                        "volume": 100000, "vol": "1.00", "jnilvolume": 50000,
                        "bef_diff": "200.00", "shcode": "000001" }
                    ]
                  }
                  """
                : """
                  {
                    "rsp_cd": "00000",
                    "rsp_msg": "정상",
                    "t1452OutBlock": { "idx": 0 },
                    "t1452OutBlock1": [
                      { "hname": "B", "price": 2000, "sign": "5", "change": 20, "diff": "-1.00",
                        "volume": 90000, "vol": "0.80", "jnilvolume": 45000,
                        "bef_diff": "200.00", "shcode": "000002" }
                    ]
                  }
                  """;
            return Ok(body);
        });

        string result = await GetTopStocksTool.GetTopStocks(client, kind: "volume", limit: 2);

        handler.Requests.Should().HaveCount(2);
        string first = await handler.Requests[0].Content!.ReadAsStringAsync();
        string second = await handler.Requests[1].Content!.ReadAsStringAsync();
        first.Should().Contain("\"idx\":0");
        second.Should().Contain("\"idx\":40");

        JsonElement rows = JsonDocument.Parse(result).RootElement.GetProperty("rows");
        rows.GetArrayLength().Should().Be(2);
        rows[0].GetProperty("rank").GetInt32().Should().Be(1);
        rows[0].GetProperty("shcode").GetString().Should().Be("000001");
        rows[1].GetProperty("rank").GetInt32().Should().Be(2);
        rows[1].GetProperty("shcode").GetString().Should().Be("000002");
    }
}
