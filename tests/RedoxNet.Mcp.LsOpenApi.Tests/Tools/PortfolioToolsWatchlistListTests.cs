using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Portfolio;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

/// <summary>
/// Covers v0.7 Tier 2-2 compression: ls_watchlist_groups_list folded into
/// ls_watchlist_list(scope?). The two scopes must return distinct shapes
/// (items vs groups) and validation must reject illegal combinations.
/// </summary>
public sealed class PortfolioToolsWatchlistListTests
{
    [Fact]
    public async Task WatchlistList_DefaultScope_ReturnsItemsShape()
    {
        await using TestEnvironment env = new();
        await env.Repository.AddWatchlistItemAsync("005930", "default", "삼성", CancellationToken.None);

        string result = await PortfolioTools.WatchlistList(env.Service);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.TryGetProperty("groups", out JsonElement groups).Should().BeTrue("WatchlistListResult shape has a `groups` key with per-group item arrays");
        groups.GetArrayLength().Should().BeGreaterThan(0);
        groups.EnumerateArray().Should().Contain(g => g.GetProperty("name").GetString() == "default");
    }

    [Fact]
    public async Task WatchlistList_ScopeGroups_ReturnsFlatGroupMetadataEnvelope()
    {
        await using TestEnvironment env = new();
        await env.Repository.CreateGroupAsync("semis", "반도체");
        await env.Repository.AddWatchlistItemAsync("005930", "semis", null, CancellationToken.None);

        string result = await PortfolioTools.WatchlistList(env.Service, scope: "groups");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("scope").GetString().Should().Be("groups");
        JsonElement groupsArray = root.GetProperty("groups");
        groupsArray.EnumerateArray().Should().Contain(g => g.GetProperty("name").GetString() == "semis"
            && g.GetProperty("item_count").GetInt32() == 1
            && g.GetProperty("description").GetString() == "반도체");
    }

    [Fact]
    public async Task WatchlistList_ScopeGroupsWithGroupName_RejectedAsValidationError()
    {
        await using TestEnvironment env = new();

        string result = await PortfolioTools.WatchlistList(env.Service, group_name: "default", scope: "groups");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("does not accept group_name");
    }

    [Fact]
    public async Task WatchlistList_UnknownScope_ReturnsValidationError()
    {
        await using TestEnvironment env = new();

        string result = await PortfolioTools.WatchlistList(env.Service, scope: "everything");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("not recognized");
    }

    sealed class TestEnvironment : IAsyncDisposable
    {
        readonly string _dir;

        public SqlitePortfolioRepository Repository { get; }
        public PortfolioService Service { get; }

        public TestEnvironment()
        {
            _dir = Path.Combine(Path.GetTempPath(), "mcp-lsopenapi-wl-" + Guid.NewGuid().ToString("N"));
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
