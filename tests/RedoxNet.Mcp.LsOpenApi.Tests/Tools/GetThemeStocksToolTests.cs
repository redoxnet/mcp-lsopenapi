using System.Net;
using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Portfolio;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

public sealed class GetThemeStocksToolTests
{
    // Hand-rolled IQuoteService stub. Only the catalog method matters for
    // these tests; the quote method is satisfied with empty results.
    sealed class FakeQuoteService : IQuoteService
    {
        readonly IReadOnlyList<ThemeCatalogRow> _catalog;
        readonly string? _catalogError;

        public FakeQuoteService(IReadOnlyList<ThemeCatalogRow>? catalog = null, string? catalogError = null)
        {
            _catalog = catalog ?? Array.Empty<ThemeCatalogRow>();
            _catalogError = catalogError;
        }

        public Task<QuoteBatchResult<StockQuote>> GetStockQuotesAsync(IReadOnlyCollection<string> symbols, CancellationToken cancellationToken = default) =>
            Task.FromResult(new QuoteBatchResult<StockQuote>(new Dictionary<string, StockQuote?>(), null));

        public Task<QuoteBatchResult<ThemeQuote>> GetThemeQuotesAsync(IReadOnlyCollection<string> themeCodes, CancellationToken cancellationToken = default) =>
            Task.FromResult(new QuoteBatchResult<ThemeQuote>(new Dictionary<string, ThemeQuote?>(), null));

        public Task<ThemeCatalogResult> GetThemeCatalogAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ThemeCatalogResult(_catalog, _catalogError));

        public Task<StockThemesFetchResult> GetStockThemesAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StockThemesFetchResult(Array.Empty<ThemeCatalogRow>(), null));
        public Task<StockIndustryFetchResult> GetStockIndustryAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult(new StockIndustryFetchResult(null, null, null));
    }

    static Task<HttpResponseMessage> Ok(string json, (string name, string value)[]? headers = null)
    {
        var msg = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        if (headers is not null)
        {
            foreach ((string name, string value) in headers)
                msg.Headers.TryAddWithoutValidation(name, value);
        }
        return Task.FromResult(msg);
    }

    const string T1537Page = """
    {
      "rsp_cd": "00000",
      "rsp_msg": "조회완료",
      "t1537OutBlock": {
        "tmname": "2차전지",
        "tmcnt": 30,
        "upcnt": 18,
        "uprate": 60
      },
      "t1537OutBlock1": [
        { "shcode": "373220", "hname": "LG에너지솔루션", "price": 380000, "sign": "2", "change": 5000, "diff": "1.33",
          "volume": 1500000, "open": 376000, "high": 382000, "low": 374500, "value": 580000, "marketcap": 88000000, "yeprice": 0, "jniltime": "0" },
        { "shcode": "006400", "hname": "삼성SDI", "price": 425000, "sign": "4", "change": 3000, "diff": "-0.70",
          "volume": 800000, "open": 428000, "high": 430000, "low": 423000, "value": 340000, "marketcap": 30000000, "yeprice": 0, "jniltime": "0" }
      ]
    }
    """;

    [Fact]
    public async Task GetThemeStocks_DirectCode_ReturnsThemeAndStocks()
    {
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(T1537Page));
        var quoteService = new FakeQuoteService();

        string result = await GetThemeStocksTool.GetThemeStocks(
            client, quoteService, theme_code: "0064", theme_keyword: null, limit: 2);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t1537");
        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("\"tmcode\":\"0064\"");

        JsonElement theme = root.GetProperty("theme");
        theme.GetProperty("code").GetString().Should().Be("0064");
        theme.GetProperty("name").GetString().Should().Be("2차전지");
        theme.GetProperty("stock_count").GetInt32().Should().Be(30);
        theme.GetProperty("up_count").GetInt32().Should().Be(18);
        theme.GetProperty("up_rate").GetDouble().Should().BeApproximately(60.0, 1e-2);

        JsonElement stocks = root.GetProperty("stocks");
        stocks.GetArrayLength().Should().Be(2);
        stocks[0].GetProperty("shcode").GetString().Should().Be("373220");
        stocks[0].GetProperty("change_pct").GetDouble().Should().BeApproximately(1.33, 1e-2);
        stocks[1].GetProperty("change").GetInt64().Should().Be(-3000, "sign=4 should flip the magnitude to negative");
        stocks[1].GetProperty("change_pct").GetDouble().Should().BeApproximately(-0.70, 1e-2);
    }

    [Fact]
    public async Task GetThemeStocks_KeywordSingleMatch_EchoesResolved()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(T1537Page));
        var quoteService = new FakeQuoteService(catalog: new[]
        {
            new ThemeCatalogRow("0064", "2차전지"),
            new ThemeCatalogRow("0012", "반도체 장비"),
        });

        string result = await GetThemeStocksTool.GetThemeStocks(
            client, quoteService, theme_code: null, theme_keyword: "2차전지", limit: 2);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        JsonElement resolved = root.GetProperty("resolved");
        resolved.GetProperty("theme_code").GetString().Should().Be("0064");
        resolved.GetProperty("theme_name").GetString().Should().Be("2차전지");
        resolved.GetProperty("matched_via").GetString().Should().Be("keyword");
    }

    [Fact]
    public async Task GetThemeStocks_KeywordNoMatch_ReturnsThemeNotFound()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(T1537Page));
        var quoteService = new FakeQuoteService(catalog: new[]
        {
            new ThemeCatalogRow("0064", "2차전지"),
            new ThemeCatalogRow("0012", "반도체 장비"),
        });

        string result = await GetThemeStocksTool.GetThemeStocks(
            client, quoteService, theme_code: null, theme_keyword: "메타버스", limit: 5);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("No theme");
        root.GetProperty("details").GetProperty("error_code").GetString().Should().Be("ThemeNotFound");
    }

    [Fact]
    public async Task GetThemeStocks_KeywordMultipleMatches_ReturnsAmbiguousTheme()
    {
        var (client, _) = TestClientFactory.Create((_, _) => Ok(T1537Page));
        var quoteService = new FakeQuoteService(catalog: new[]
        {
            new ThemeCatalogRow("0064", "2차전지"),
            new ThemeCatalogRow("0065", "2차전지 소재"),
            new ThemeCatalogRow("0066", "2차전지 장비"),
        });

        string result = await GetThemeStocksTool.GetThemeStocks(
            client, quoteService, theme_code: null, theme_keyword: "2차전지", limit: 5);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("details").GetProperty("error_code").GetString().Should().Be("AmbiguousTheme");
        JsonElement candidates = root.GetProperty("details").GetProperty("candidates");
        candidates.GetArrayLength().Should().Be(3);
    }

    [Fact]
    public async Task GetThemeStocks_HeaderPaging_EchoesTrContKeyOnNextCall()
    {
        // Two pages: first responds with tr_cont=Y + tr_cont_key=PAGE2, second
        // returns tr_cont=N to terminate the loop.
        const string firstPage = """
        {
          "rsp_cd": "00000",
          "t1537OutBlock": { "tmname": "BIG", "tmcnt": 4, "upcnt": 2, "uprate": 50 },
          "t1537OutBlock1": [
            { "shcode": "AAA001", "hname": "n1", "price": 1, "sign": "3", "change": 0, "diff": "0", "volume": 1, "open": 1, "high": 1, "low": 1, "value": 0, "marketcap": 0, "yeprice": 0, "jniltime": "0" },
            { "shcode": "AAA002", "hname": "n2", "price": 1, "sign": "3", "change": 0, "diff": "0", "volume": 1, "open": 1, "high": 1, "low": 1, "value": 0, "marketcap": 0, "yeprice": 0, "jniltime": "0" }
          ]
        }
        """;
        const string secondPage = """
        {
          "rsp_cd": "00000",
          "t1537OutBlock": { "tmname": "BIG", "tmcnt": 4, "upcnt": 2, "uprate": 50 },
          "t1537OutBlock1": [
            { "shcode": "AAA003", "hname": "n3", "price": 1, "sign": "3", "change": 0, "diff": "0", "volume": 1, "open": 1, "high": 1, "low": 1, "value": 0, "marketcap": 0, "yeprice": 0, "jniltime": "0" },
            { "shcode": "AAA004", "hname": "n4", "price": 1, "sign": "3", "change": 0, "diff": "0", "volume": 1, "open": 1, "high": 1, "low": 1, "value": 0, "marketcap": 0, "yeprice": 0, "jniltime": "0" }
          ]
        }
        """;
        int callCount = 0;
        var (client, handler) = TestClientFactory.Create((_, _) =>
        {
            callCount++;
            if (callCount == 1)
                return Ok(firstPage, new[] { ("tr_cont", "Y"), ("tr_cont_key", "PAGE2") });
            return Ok(secondPage, new[] { ("tr_cont", "N") });
        });
        var quoteService = new FakeQuoteService();

        string result = await GetThemeStocksTool.GetThemeStocks(
            client, quoteService, theme_code: "0064", theme_keyword: null, limit: 3);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("count").GetInt32().Should().Be(3);
        callCount.Should().Be(2, "first page returned 2 of 3 requested rows; second page provides the third");

        // Second outgoing request must echo tr_cont_key=PAGE2 in the header.
        handler.Requests.Count.Should().Be(2);
        handler.Requests[1].Headers.GetValues("tr_cont_key").Should().ContainSingle().Which.Should().Be("PAGE2");
        handler.Requests[1].Headers.GetValues("tr_cont").Should().ContainSingle().Which.Should().Be("Y");
        // First request: no continuation.
        handler.Requests[0].Headers.GetValues("tr_cont").Should().ContainSingle().Which.Should().Be("N");
    }

    [Fact]
    public async Task GetThemeStocks_ThemeCodeWinsOverKeyword_NoCatalogCall()
    {
        var quoteService = new FakeQuoteService();
        var (client, handler) = TestClientFactory.Create((_, _) => Ok(T1537Page));

        await GetThemeStocksTool.GetThemeStocks(
            client, quoteService, theme_code: "0064", theme_keyword: "anything", limit: 5);

        // Only the t1537 outbound request — no catalog lookup since code wins.
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t1537");
    }

    [Fact]
    public async Task GetThemeStocks_InvalidCode_ReturnsValidationError()
    {
        var quoteService = new FakeQuoteService();
        var (client, _) = TestClientFactory.Create((_, _) => Ok(T1537Page));

        string result = await GetThemeStocksTool.GetThemeStocks(
            client, quoteService, theme_code: "00", theme_keyword: null, limit: 5);

        JsonElement root = JsonDocument.Parse(result).RootElement;
        root.GetProperty("error").GetString().Should().Contain("4-character");
    }
}
