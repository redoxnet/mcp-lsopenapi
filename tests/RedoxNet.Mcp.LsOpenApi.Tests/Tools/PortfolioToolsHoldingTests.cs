using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Portfolio;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

/// <summary>
/// Covers the v0.10 <c>ls_holding</c> domain dispatcher (SPEC-v0.10 §2.6.5):
/// the five holding-write tools (ls_holdings_set / _buy / _sell / _remove /
/// _corporate_action) folded into one action-routed tool. Service-level math
/// is tested in PortfolioServiceTests; here we exercise routing and per-action
/// argument validation. The read tool ls_holdings_list is unchanged.
/// </summary>
public sealed class PortfolioToolsHoldingTests
{
    [Fact]
    public async Task Holding_Set_RecordsPosition()
    {
        await using TestEnvironment env = new();

        string result = await PortfolioTools.Holding(
            env.Service, action: "set", shcode: "005930", quantity: 10, avg_price: 70000);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("quantity").GetInt32().Should().Be(10);
        root.GetProperty("avg_price").GetDouble().Should().Be(70000);
    }

    [Fact]
    public async Task Holding_Set_MissingQuantityAndAvgPrice_ReturnsValidationError()
    {
        await using TestEnvironment env = new();

        string result = await PortfolioTools.Holding(env.Service, action: "set", shcode: "005930");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("quantity").And.Contain("avg_price");
        root.GetProperty("details").GetProperty("missing").EnumerateArray()
            .Select(e => e.GetString()).Should().BeEquivalentTo(new[] { "quantity", "avg_price" });
    }

    [Fact]
    public async Task Holding_Buy_MergesWeightedAverage()
    {
        await using TestEnvironment env = new();
        await PortfolioTools.Holding(env.Service, action: "buy", shcode: "005930", quantity: 10, price: 70000);

        string result = await PortfolioTools.Holding(
            env.Service, action: "buy", shcode: "005930", quantity: 5, price: 80000);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("quantity").GetInt32().Should().Be(15);
        root.GetProperty("avg_price").GetDouble().Should().BeApproximately((10 * 70000 + 5 * 80000) / 15.0, 1.0);
    }

    [Fact]
    public async Task Holding_Buy_MissingPrice_ReturnsValidationError()
    {
        await using TestEnvironment env = new();

        string result = await PortfolioTools.Holding(
            env.Service, action: "buy", shcode: "005930", quantity: 5);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("price");
        root.GetProperty("details").GetProperty("missing")[0].GetString().Should().Be("price");
    }

    [Fact]
    public async Task Holding_Sell_ReducesQuantity()
    {
        await using TestEnvironment env = new();
        await PortfolioTools.Holding(env.Service, action: "set", shcode: "005930", quantity: 10, avg_price: 70000);

        string result = await PortfolioTools.Holding(
            env.Service, action: "sell", shcode: "005930", quantity: 4);
        JsonDocument.Parse(result).RootElement.GetProperty("quantity").GetInt32().Should().Be(6);
    }

    [Fact]
    public async Task Holding_Sell_MissingQuantity_ReturnsValidationError()
    {
        await using TestEnvironment env = new();

        string result = await PortfolioTools.Holding(env.Service, action: "sell", shcode: "005930");
        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString().Should().Contain("quantity");
    }

    [Fact]
    public async Task Holding_Remove_DropsHolding()
    {
        await using TestEnvironment env = new();
        await PortfolioTools.Holding(env.Service, action: "set", shcode: "005930", quantity: 10, avg_price: 70000);

        string result = await PortfolioTools.Holding(env.Service, action: "remove", shcode: "005930");
        JsonDocument.Parse(result).RootElement.GetProperty("removed").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Holding_Remove_NotHeld_ReturnsRemovedFalse()
    {
        await using TestEnvironment env = new();

        string result = await PortfolioTools.Holding(env.Service, action: "remove", shcode: "000660");
        JsonDocument.Parse(result).RootElement.GetProperty("removed").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Holding_CorporateAction_SplitDispatchesToSplitMath()
    {
        await using TestEnvironment env = new();
        await env.Repository.SetHoldingAsync(env.AccountId, "005930", 10, 2500000, null);

        string result = await PortfolioTools.Holding(
            env.Service, action: "corporate_action", shcode: "005930", type: "split", ratio: 50);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("action").GetString().Should().Be("split");
        root.GetProperty("ratio").GetDouble().Should().Be(50);
        JsonElement after = root.GetProperty("applied_to")[0].GetProperty("after");
        after.GetProperty("quantity").GetInt32().Should().Be(500);
        after.GetProperty("avg_price").GetDouble().Should().BeApproximately(50000, 1e-3);
    }

    [Fact]
    public async Task Holding_CorporateAction_BonusDispatchesToBonusMath()
    {
        await using TestEnvironment env = new();
        await env.Repository.SetHoldingAsync(env.AccountId, "005930", 100, 10000, null);

        string result = await PortfolioTools.Holding(
            env.Service, action: "corporate_action", shcode: "005930", type: "bonus", ratio: 0.1);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("action").GetString().Should().Be("bonus");
        root.GetProperty("applied_to")[0].GetProperty("after").GetProperty("quantity").GetInt32().Should().Be(110);
    }

    [Fact]
    public async Task Holding_CorporateAction_ReverseSplitNonDivisible_ReturnsValidationError()
    {
        await using TestEnvironment env = new();
        await env.Repository.SetHoldingAsync(env.AccountId, "005930", 7, 50000, null);

        string result = await PortfolioTools.Holding(
            env.Service, action: "corporate_action", shcode: "005930", type: "reverse_split", ratio: 3);
        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString().Should().Be("ValidationError");
    }

    [Fact]
    public async Task Holding_CorporateAction_FractionalRatioForSplit_Rejected()
    {
        await using TestEnvironment env = new();
        await env.Repository.SetHoldingAsync(env.AccountId, "005930", 10, 70000, null);

        string result = await PortfolioTools.Holding(
            env.Service, action: "corporate_action", shcode: "005930", type: "split", ratio: 1.5);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Be("ValidationError");
        root.GetProperty("message").GetString().Should().Contain("integer");
    }

    [Fact]
    public async Task Holding_CorporateAction_UnknownType_EnvelopeMentionsFutureExtension()
    {
        await using TestEnvironment env = new();

        string result = await PortfolioTools.Holding(
            env.Service, action: "corporate_action", shcode: "005930", type: "stock_dividend", ratio: 0.05);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Be("ValidationError");
        string message = root.GetProperty("message").GetString()!;
        message.Should().Contain("split").And.Contain("bonus").And.Contain("Additional types");
    }

    [Fact]
    public async Task Holding_CorporateAction_MissingTypeAndRatio_ReturnsValidationError()
    {
        await using TestEnvironment env = new();

        string result = await PortfolioTools.Holding(
            env.Service, action: "corporate_action", shcode: "005930");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("type").And.Contain("ratio");
        root.GetProperty("details").GetProperty("action").GetString().Should().Be("corporate_action");
    }

    [Fact]
    public async Task Holding_UnknownAction_ReturnsValidationErrorWithValidActions()
    {
        await using TestEnvironment env = new();

        string result = await PortfolioTools.Holding(env.Service, action: "trade", shcode: "005930");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("unknown action");
        root.GetProperty("details").GetProperty("valid_actions").EnumerateArray()
            .Select(e => e.GetString())
            .Should().BeEquivalentTo(new[] { "set", "buy", "sell", "remove", "corporate_action" });
    }

    sealed class TestEnvironment : IAsyncDisposable
    {
        readonly string _dir;

        public SqlitePortfolioRepository Repository { get; }
        public PortfolioService Service { get; }

        /// <summary>Id of the single seeded account — write actions auto-resolve to it.</summary>
        public long AccountId { get; }

        public TestEnvironment()
        {
            _dir = Path.Combine(Path.GetTempPath(), "mcp-lsopenapi-hold-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Repository = new SqlitePortfolioRepository(Path.Combine(_dir, "portfolio.db"));
            Service = new PortfolioService(Repository, new NoopQuoteService());
            Repository.InitializeAsync().GetAwaiter().GetResult();
            AccountId = Repository.UpsertAccountAsync("AAA", "main", null, setDefault: false)
                .GetAwaiter().GetResult().Id;
        }

        public ValueTask DisposeAsync()
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch { /* best-effort */ }
            return ValueTask.CompletedTask;
        }

        sealed class NoopQuoteService : IQuoteService
        {
            public Task<QuoteBatchResult<StockQuote>> GetStockQuotesAsync(IReadOnlyCollection<string> symbols, CancellationToken cancellationToken = default) =>
                Task.FromResult(new QuoteBatchResult<StockQuote>(new Dictionary<string, StockQuote?>(), null));
            public Task<QuoteBatchResult<ThemeQuote>> GetThemeQuotesAsync(IReadOnlyCollection<string> themeCodes, CancellationToken cancellationToken = default) =>
                Task.FromResult(new QuoteBatchResult<ThemeQuote>(new Dictionary<string, ThemeQuote?>(), null));
            public Task<ThemeCatalogResult> GetThemeCatalogAsync(CancellationToken cancellationToken = default) =>
                Task.FromResult(new ThemeCatalogResult(Array.Empty<ThemeCatalogRow>(), null));
            public Task<StockThemesFetchResult> GetStockThemesAsync(string symbol, CancellationToken cancellationToken = default) =>
                Task.FromResult(new StockThemesFetchResult(Array.Empty<ThemeCatalogRow>(), null));
            public Task<StockIndustryFetchResult> GetStockIndustryAsync(string symbol, CancellationToken cancellationToken = default) =>
                Task.FromResult(new StockIndustryFetchResult(null, null, null));
        }
    }
}
