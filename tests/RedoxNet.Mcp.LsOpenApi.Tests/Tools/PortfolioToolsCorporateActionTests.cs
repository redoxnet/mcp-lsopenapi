using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Portfolio;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

/// <summary>
/// Covers the v0.6 Tier-1 compression: the three v0.5 corporate-action tools
/// merged into one open-enum dispatcher. Service-level math is already tested
/// in PortfolioServiceTests; here we only exercise the dispatcher.
/// </summary>
public sealed class PortfolioToolsCorporateActionTests
{
    [Fact]
    public async Task CorporateAction_SplitDispatchesToSplitMath()
    {
        await using TestEnvironment env = new();
        Account account = await env.Repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);
        await env.Repository.SetHoldingAsync(account.Id, "005930", 10, 2500000, null);

        string result = await PortfolioTools.HoldingCorporateAction(
            env.Service, shcode: "005930", type: "split", ratio: 50, account: null);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("action").GetString().Should().Be("split");
        root.GetProperty("ratio").GetDouble().Should().Be(50);
        JsonElement after = root.GetProperty("applied_to")[0].GetProperty("after");
        after.GetProperty("quantity").GetInt32().Should().Be(500);
        after.GetProperty("avg_price").GetDouble().Should().BeApproximately(50000, 1e-3);
    }

    [Fact]
    public async Task CorporateAction_BonusDispatchesToBonusMath()
    {
        await using TestEnvironment env = new();
        Account account = await env.Repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);
        await env.Repository.SetHoldingAsync(account.Id, "005930", 100, 10000, null);

        string result = await PortfolioTools.HoldingCorporateAction(
            env.Service, shcode: "005930", type: "bonus", ratio: 0.1, account: null);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("action").GetString().Should().Be("bonus");
        JsonElement after = root.GetProperty("applied_to")[0].GetProperty("after");
        after.GetProperty("quantity").GetInt32().Should().Be(110);
    }

    [Fact]
    public async Task CorporateAction_ReverseSplitNonDivisible_ReturnsValidationError()
    {
        await using TestEnvironment env = new();
        Account account = await env.Repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);
        await env.Repository.SetHoldingAsync(account.Id, "005930", 7, 50000, null);

        string result = await PortfolioTools.HoldingCorporateAction(
            env.Service, shcode: "005930", type: "reverse_split", ratio: 3, account: null);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Be("ValidationError");
    }

    [Fact]
    public async Task CorporateAction_FractionalRatioForSplit_Rejected()
    {
        await using TestEnvironment env = new();
        Account account = await env.Repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);
        await env.Repository.SetHoldingAsync(account.Id, "005930", 10, 70000, null);

        string result = await PortfolioTools.HoldingCorporateAction(
            env.Service, shcode: "005930", type: "split", ratio: 1.5, account: null);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Be("ValidationError");
        root.GetProperty("message").GetString().Should().Contain("integer");
    }

    [Fact]
    public async Task CorporateAction_UnknownType_EnvelopeMentionsFutureExtension()
    {
        await using TestEnvironment env = new();
        await env.Repository.UpsertAccountAsync("AAA", "main", null, setDefault: false);

        string result = await PortfolioTools.HoldingCorporateAction(
            env.Service, shcode: "005930", type: "stock_dividend", ratio: 0.05, account: null);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Be("ValidationError");
        string message = root.GetProperty("message").GetString()!;
        message.Should().Contain("split").And.Contain("bonus");
        message.Should().Contain("Additional types"); // signals the open-enum extension policy
    }

    sealed class TestEnvironment : IAsyncDisposable
    {
        readonly string _dir;

        public SqlitePortfolioRepository Repository { get; }
        public PortfolioService Service { get; }

        public TestEnvironment()
        {
            _dir = Path.Combine(Path.GetTempPath(), "mcp-lsopenapi-ca-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            Repository = new SqlitePortfolioRepository(Path.Combine(_dir, "portfolio.db"));
            Service = new PortfolioService(Repository, new NoopQuoteService());
            Repository.InitializeAsync().GetAwaiter().GetResult();
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
