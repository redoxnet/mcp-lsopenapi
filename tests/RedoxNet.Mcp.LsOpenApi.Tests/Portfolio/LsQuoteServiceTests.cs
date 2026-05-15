using System.Net;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Portfolio;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Portfolio;

public sealed class LsQuoteServiceTests
{
    [Fact]
    public async Task GetSectorQuotesAsync_UsesT1531AvgDiffForWatchedThemeCodes()
    {
        const string body = """
        {
          "rsp_cd": "00000",
          "rsp_msg": "조회완료",
          "t1531OutBlock": [
            { "tmname": "반도체 장비", "avgdiff": "-0.59", "tmcode": "0012" },
            { "tmname": "페라이트", "avgdiff": "0.74", "tmcode": "0534" }
          ]
        }
        """;
        var (client, handler) = TestClientFactory.Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        }));
        var service = new LsQuoteService(client);

        QuoteBatchResult<SectorQuote> result = await service.GetSectorQuotesAsync(new[] { "0012", "9999" });

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t1531");
        result.TopLevelError.Should().BeNull();
        result.Quotes["0012"]!.ChangePct.Should().BeApproximately(-0.59, 0.001);
        result.Quotes["0012"]!.IndexValue.Should().BeNull();
        result.Quotes["9999"].Should().BeNull();
    }
    [Fact]
    public async Task GetStockQuotesAsync_ParsesRequestedRowsAndKeepsUnknownSignChange()
    {
        const string body = """
        {
          "rsp_cd": "00000",
          "rsp_msg": "정상",
          "t8407OutBlock1": [
            { "shcode":"005930", "hname":"삼성전자", "price":71700, "sign":"5", "change":500, "diff":"-00.69",
              "volume":12640775, "open":72700, "high":72700, "low":71400 },
            { "shcode":"000660", "hname":"SK하이닉스", "price":108700, "sign":"9", "change":123, "diff":"00.11",
              "volume":3086217, "open":110100, "high":110500, "low":108500 },
            { "shcode":"999999", "hname":"FILLER", "price":0, "sign":"3", "change":0, "diff":"000.00",
              "volume":0, "open":0, "high":0, "low":0 }
          ]
        }
        """;
        var (client, handler) = TestClientFactory.Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        }));
        var service = new LsQuoteService(client);

        QuoteBatchResult<StockQuote> result = await service.GetStockQuotesAsync(new[] { "005930", "000660", "111111" });

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t8407");
        result.TopLevelError.Should().BeNull();
        result.Quotes["005930"]!.Change.Should().Be(-500);
        result.Quotes["000660"]!.Change.Should().Be(123);
        result.Quotes["111111"].Should().BeNull();
    }

    [Fact]
    public async Task GetSectorQuotesAsync_ReturnsBusinessErrorAsTopLevelError()
    {
        const string body = """
        {
          "rsp_cd": "12345",
          "rsp_msg": "업무 오류",
          "t1531OutBlock": []
        }
        """;
        var (client, _) = TestClientFactory.Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        }));
        var service = new LsQuoteService(client);

        QuoteBatchResult<SectorQuote> result = await service.GetSectorQuotesAsync(new[] { "0012" });

        result.TopLevelError.Should().Contain("12345");
        result.Quotes["0012"].Should().BeNull();
    }

    [Fact]
    public async Task GetSectorQuotesAsync_ReusesShortTermThemeCache()
    {
        const string body = """
        {
          "rsp_cd": "00000",
          "rsp_msg": "조회완료",
          "t1531OutBlock": [
            { "tmname": "반도체 장비", "avgdiff": "-0.59", "tmcode": "0012" }
          ]
        }
        """;
        var (client, handler) = TestClientFactory.Create((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        }));
        var service = new LsQuoteService(client);

        await service.GetSectorQuotesAsync(new[] { "0012" });
        await service.GetSectorQuotesAsync(new[] { "0012" });

        handler.Requests.Should().ContainSingle();
    }
}

