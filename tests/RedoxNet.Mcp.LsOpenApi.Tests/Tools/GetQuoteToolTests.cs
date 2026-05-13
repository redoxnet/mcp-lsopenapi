using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

public class GetQuoteToolTests
{
    static Task<HttpResponseMessage> Ok(string json) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
    });

    [Fact]
    public async Task GetQuote_HappyPath_ReturnsShapedPayload()
    {
        const string ls = """
        {
          "t1101OutBlock": {
            "hname": "삼성전자",
            "price": 71500,
            "sign": "2",
            "change": 500,
            "diff": 0.7,
            "volume": 12345678,
            "jnilclose": 71000,
            "open": 71200,
            "high": 71600,
            "low": 71000,
            "offerho1": 71600, "bidho1": 71500, "offerrem1": 1000, "bidrem1": 2000,
            "offerho2": 71700, "bidho2": 71400, "offerrem2": 1100, "bidrem2": 2100,
            "offerho3": 71800, "bidho3": 71300, "offerrem3": 1200, "bidrem3": 2200,
            "offerho4": 71900, "bidho4": 71200, "offerrem4": 1300, "bidrem4": 2300,
            "offerho5": 72000, "bidho5": 71100, "offerrem5": 1400, "bidrem5": 2400,
            "offerho6": 72100, "bidho6": 71000, "offerrem6": 1500, "bidrem6": 2500,
            "offerho7": 72200, "bidho7": 70900, "offerrem7": 1600, "bidrem7": 2600,
            "offerho8": 72300, "bidho8": 70800, "offerrem8": 1700, "bidrem8": 2700,
            "offerho9": 72400, "bidho9": 70700, "offerrem9": 1800, "bidrem9": 2800,
            "offerho10": 72500, "bidho10": 70600, "offerrem10": 1900, "bidrem10": 2900
          },
          "rsp_cd": "00000",
          "rsp_msg": "정상"
        }
        """;
        var (client, _) = TestClientFactory.Create((_, _) => Ok(ls));

        string result = await GetQuoteTool.GetQuote(client, "005930");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("shcode").GetString().Should().Be("005930");
        root.GetProperty("name").GetString().Should().Be("삼성전자");
        root.GetProperty("price").GetInt64().Should().Be(71500);
        root.GetProperty("order_book").GetArrayLength().Should().Be(10);
        JsonElement level1 = root.GetProperty("order_book")[0];
        level1.GetProperty("ask").GetInt64().Should().Be(71600);
        level1.GetProperty("bid").GetInt64().Should().Be(71500);
    }

    [Fact]
    public async Task GetQuote_EmptyShcode_ReturnsError()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok("""{"rsp_cd":"00000"}"""));

        string result = await GetQuoteTool.GetQuote(client, "");

        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString()
            .Should().Contain("shcode");
    }

    [Fact]
    public async Task GetQuote_BusinessError_Surfaces()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok("""{"rsp_cd":"99999","rsp_msg":"잘못된 종목"}"""));

        string result = await GetQuoteTool.GetQuote(client, "999999");

        JsonElement root = JsonDocument.Parse(result).RootElement;
        root.GetProperty("error").GetString().Should().Contain("business-level");
        root.GetProperty("details").GetProperty("rsp_msg").GetString().Should().Be("잘못된 종목");
    }
}
