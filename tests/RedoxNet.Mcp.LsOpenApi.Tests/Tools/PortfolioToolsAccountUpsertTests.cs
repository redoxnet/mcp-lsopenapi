using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Portfolio;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

/// <summary>
/// Covers v0.7 Tier 2-3 compression: ls_broker_rename folded into
/// ls_account_upsert(rename_broker_from?). Two modes must remain
/// distinguishable and the rename mode must guard its required parameters.
/// </summary>
public sealed class PortfolioToolsAccountUpsertTests
{
    [Fact]
    public async Task AccountUpsert_DefaultMode_CreatesAccount()
    {
        await using TestEnvironment env = new();

        string result = await PortfolioTools.AccountUpsert(
            env.Service, account_number: "AAA", nickname: "한투", broker: "한국투자");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("account_number").GetString().Should().Be("AAA");
        root.GetProperty("broker").GetString().Should().Be("한국투자");
    }

    [Fact]
    public async Task AccountUpsert_RenameBrokerMode_UpdatesAllMatchingAccountsAndReportsCount()
    {
        await using TestEnvironment env = new();
        await env.Repository.UpsertAccountAsync("AAA", "한투-주식", "한투", setDefault: false);
        await env.Repository.UpsertAccountAsync("BBB", "한투-ISA", "한투", setDefault: false);
        await env.Repository.UpsertAccountAsync("CCC", "KB", "KB증권", setDefault: false);

        string result = await PortfolioTools.AccountUpsert(
            env.Service, broker: "한국투자증권", rename_broker_from: "한투");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("from").GetString().Should().Be("한투");
        root.GetProperty("to").GetString().Should().Be("한국투자증권");
        root.GetProperty("accounts_affected").GetInt32().Should().Be(2);
        IReadOnlyList<Account> accounts = await env.Repository.ListAccountsAsync();
        accounts.Where(a => a.Broker == "한국투자증권").Should().HaveCount(2);
        accounts.Single(a => a.AccountNo == "CCC").Broker.Should().Be("KB증권");
    }

    [Fact]
    public async Task AccountUpsert_RenameBrokerMode_MissingBroker_ReturnsValidationError()
    {
        await using TestEnvironment env = new();

        string result = await PortfolioTools.AccountUpsert(
            env.Service, rename_broker_from: "한투");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("broker");
    }

    [Fact]
    public async Task AccountUpsert_DefaultMode_MissingAccountNumber_ReturnsValidationError()
    {
        await using TestEnvironment env = new();

        string result = await PortfolioTools.AccountUpsert(
            env.Service, nickname: "한투");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("account_number");
    }

    sealed class TestEnvironment : IAsyncDisposable
    {
        readonly string _dir;

        public SqlitePortfolioRepository Repository { get; }
        public PortfolioService Service { get; }

        public TestEnvironment()
        {
            _dir = Path.Combine(Path.GetTempPath(), "mcp-lsopenapi-au-" + Guid.NewGuid().ToString("N"));
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
