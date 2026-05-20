using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

public sealed class GetHighLowStocksToolTests
{
    const string Sample = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "조회완료",
      "t1442OutBlock": { "idx": 0 },
      "t1442OutBlock1": [
        { "shcode": "001820", "hname": "삼화콘덴서", "price": 75500, "sign": "2", "change": 11500,
          "diff": "17.97", "volume": 1200000, "pastprice": 64000, "pastsign": "2",
          "pastchange": 0, "pastdiff": "0.00" }
      ]
    }
    """;

    static Task<HttpResponseMessage> Ok(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    });

    [Fact]
    public async Task GetHighLowStocks_Defaults_ShapePayloadAndInBlock()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(Sample));

        string result = await GetHighLowStocksTool.GetHighLowStocks(client);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t1442");
        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("\"type1\":\"0\"", "direction=high maps to type1=0");
        body.Should().Contain("\"type2\":\"6\"", "period=52w maps to type2=6");
        body.Should().Contain("\"type3\":\"1\"", "maintained=true maps to type3=1");
        body.Should().Contain("\"jc_num2\":9", "exclude_etf=true sends the ETF+ETN bitmask");

        root.GetProperty("direction").GetString().Should().Be("high");
        root.GetProperty("period").GetString().Should().Be("52w");
        root.GetProperty("maintained").GetBoolean().Should().BeTrue();
        root.GetProperty("count").GetInt32().Should().Be(1);

        JsonElement stock = root.GetProperty("stocks")[0];
        stock.GetProperty("shcode").GetString().Should().Be("001820");
        stock.GetProperty("name").GetString().Should().Be("삼화콘덴서");
        stock.GetProperty("price").GetInt64().Should().Be(75500);
        stock.GetProperty("change_pct").GetDouble().Should().BeApproximately(17.97, 1e-2);
        stock.GetProperty("past_price").GetInt64().Should().Be(64000);
    }

    [Theory]
    [InlineData("low", "\"type1\":\"1\"")]
    [InlineData("신저가", "\"type1\":\"1\"")]
    [InlineData("high", "\"type1\":\"0\"")]
    public async Task GetHighLowStocks_DirectionMapping(string direction, string expectedFragment)
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(Sample));

        await GetHighLowStocksTool.GetHighLowStocks(client, direction: direction);

        (await handler.Requests[0].Content!.ReadAsStringAsync()).Should().Contain(expectedFragment);
    }

    [Fact]
    public async Task GetHighLowStocks_ExcludeEtfFalse_SendsZeroMask()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(Sample));

        await GetHighLowStocksTool.GetHighLowStocks(client, exclude_etf: false);

        (await handler.Requests[0].Content!.ReadAsStringAsync()).Should().Contain("\"jc_num2\":0");
    }

    [Fact]
    public async Task GetHighLowStocks_InvalidPeriod_ReturnsValidationError()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(Sample));

        string result = await GetHighLowStocksTool.GetHighLowStocks(client, period: "decade");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        handler.Requests.Should().BeEmpty();
        root.GetProperty("error").GetString().Should().Contain("period");
    }

    [Fact]
    public async Task GetHighLowStocks_PaginatesWithIdxUntilTopN()
    {
        int call = 0;
        var (client, handler) = TestClientFactory.Create((_, _) =>
        {
            call++;
            string body = call == 1
                ? """
                  {
                    "rsp_cd": "00000", "rsp_msg": "조회완료",
                    "t1442OutBlock": { "idx": 20 },
                    "t1442OutBlock1": [
                      { "shcode": "000001", "hname": "A", "price": 1000, "sign": "2", "change": 10,
                        "diff": "1.00", "volume": 100, "pastprice": 900, "pastsign": "2",
                        "pastchange": 0, "pastdiff": "0.00" }
                    ]
                  }
                  """
                : """
                  {
                    "rsp_cd": "00000", "rsp_msg": "조회완료",
                    "t1442OutBlock": { "idx": 0 },
                    "t1442OutBlock1": [
                      { "shcode": "000002", "hname": "B", "price": 2000, "sign": "2", "change": 20,
                        "diff": "1.00", "volume": 200, "pastprice": 1800, "pastsign": "2",
                        "pastchange": 0, "pastdiff": "0.00" }
                    ]
                  }
                  """;
            return Ok(body);
        });

        string result = await GetHighLowStocksTool.GetHighLowStocks(client, top_n: 2);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        handler.Requests.Should().HaveCount(2);
        (await handler.Requests[0].Content!.ReadAsStringAsync()).Should().Contain("\"idx\":0");
        (await handler.Requests[1].Content!.ReadAsStringAsync()).Should().Contain("\"idx\":20");

        root.GetProperty("count").GetInt32().Should().Be(2);
        root.GetProperty("stocks")[1].GetProperty("rank").GetInt32().Should().Be(2);
        root.GetProperty("stocks")[1].GetProperty("shcode").GetString().Should().Be("000002");
    }
}
