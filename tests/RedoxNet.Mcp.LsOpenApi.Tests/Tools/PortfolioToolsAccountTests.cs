using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Portfolio;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

/// <summary>
/// Covers the v0.10 <c>ls_account</c> domain dispatcher (SPEC-v0.10 §2.6.1):
/// ls_accounts_list / ls_account_upsert / ls_account_remove folded into one
/// action-routed tool. Service-level math is tested in PortfolioServiceTests;
/// here we exercise routing, the two upsert sub-modes, and per-action
/// argument validation.
/// </summary>
public sealed class PortfolioToolsAccountTests
{
    [Fact]
    public async Task Account_List_ReturnsRegisteredAccounts()
    {
        await using TestEnvironment env = new();
        await env.Repository.UpsertAccountAsync("AAA", "한투", null, setDefault: false);

        string result = await PortfolioTools.Account(env.Service, action: "list");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.ValueKind.Should().Be(JsonValueKind.Array);
        root.EnumerateArray().Should().ContainSingle(a => a.GetProperty("nickname").GetString() == "한투");
    }

    [Fact]
    public async Task Account_Upsert_DefaultMode_CreatesAccount()
    {
        await using TestEnvironment env = new();

        string result = await PortfolioTools.Account(
            env.Service, action: "upsert", account_number: "AAA", nickname: "한투", broker: "한국투자");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("account_number").GetString().Should().Be("AAA");
        root.GetProperty("broker").GetString().Should().Be("한국투자");
    }

    [Fact]
    public async Task Account_Upsert_RenameBrokerMode_UpdatesAllMatchingAccountsAndReportsCount()
    {
        await using TestEnvironment env = new();
        await env.Repository.UpsertAccountAsync("AAA", "한투-주식", "한투", setDefault: false);
        await env.Repository.UpsertAccountAsync("BBB", "한투-ISA", "한투", setDefault: false);
        await env.Repository.UpsertAccountAsync("CCC", "KB", "KB증권", setDefault: false);

        string result = await PortfolioTools.Account(
            env.Service, action: "upsert", broker: "한국투자증권", rename_broker_from: "한투");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("from").GetString().Should().Be("한투");
        root.GetProperty("to").GetString().Should().Be("한국투자증권");
        root.GetProperty("accounts_affected").GetInt32().Should().Be(2);
        IReadOnlyList<Account> accounts = await env.Repository.ListAccountsAsync();
        accounts.Where(a => a.Broker == "한국투자증권").Should().HaveCount(2);
        accounts.Single(a => a.AccountNo == "CCC").Broker.Should().Be("KB증권");
    }

    [Fact]
    public async Task Account_Upsert_RenameBrokerMode_MissingBroker_ReturnsValidationError()
    {
        await using TestEnvironment env = new();

        string result = await PortfolioTools.Account(
            env.Service, action: "upsert", rename_broker_from: "한투");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("broker");
        root.GetProperty("details").GetProperty("missing")[0].GetString().Should().Be("broker");
    }

    [Fact]
    public async Task Account_Upsert_DefaultMode_MissingAccountNumber_ReturnsValidationError()
    {
        await using TestEnvironment env = new();

        string result = await PortfolioTools.Account(env.Service, action: "upsert", nickname: "한투");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("account_number");
        root.GetProperty("details").GetProperty("action").GetString().Should().Be("upsert");
    }

    [Fact]
    public async Task Account_Remove_MissingAccount_ReturnsValidationError()
    {
        await using TestEnvironment env = new();

        string result = await PortfolioTools.Account(env.Service, action: "remove");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("account");
        root.GetProperty("details").GetProperty("missing")[0].GetString().Should().Be("account");
    }

    [Fact]
    public async Task Account_Remove_CascadesWithConfirm()
    {
        await using TestEnvironment env = new();
        await env.Repository.UpsertAccountAsync("AAA", "한투", null, setDefault: false);

        string result = await PortfolioTools.Account(
            env.Service, action: "remove", account: "한투", confirm: true);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("removed").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Account_UnknownAction_ReturnsValidationErrorWithValidActions()
    {
        await using TestEnvironment env = new();

        string result = await PortfolioTools.Account(env.Service, action: "frobnicate");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("unknown action");
        root.GetProperty("details").GetProperty("valid_actions").EnumerateArray()
            .Select(e => e.GetString()).Should().BeEquivalentTo(new[] { "list", "upsert", "remove" });
    }

    sealed class TestEnvironment : IAsyncDisposable
    {
        readonly string _dir;

        public SqlitePortfolioRepository Repository { get; }
        public PortfolioService Service { get; }

        public TestEnvironment()
        {
            _dir = Path.Combine(Path.GetTempPath(), "mcp-lsopenapi-acct-" + Guid.NewGuid().ToString("N"));
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
