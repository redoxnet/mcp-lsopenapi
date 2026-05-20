using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Portfolio;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

/// <summary>
/// Covers the v0.10 <c>ls_watchlist</c> domain dispatcher (SPEC-v0.10 §2.6.2):
/// ls_watchlist_add / _remove / _list / _group_create / _group_delete folded
/// into one action-routed tool. Exercises routing, the list scope shapes,
/// the folded group rename path, and per-action argument validation.
/// </summary>
public sealed class PortfolioToolsWatchlistTests
{
    [Fact]
    public async Task Watchlist_List_DefaultScope_ReturnsItemsShape()
    {
        await using TestEnvironment env = new();
        await env.Repository.AddWatchlistItemAsync("005930", "default", "삼성", CancellationToken.None);

        string result = await PortfolioTools.Watchlist(env.Service, action: "list");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.TryGetProperty("groups", out JsonElement groups).Should().BeTrue();
        groups.EnumerateArray().Should().Contain(g => g.GetProperty("name").GetString() == "default");
    }

    [Fact]
    public async Task Watchlist_List_ScopeGroups_ReturnsGroupMetadataEnvelope()
    {
        await using TestEnvironment env = new();
        await env.Repository.CreateGroupAsync("semis", "반도체");
        await env.Repository.AddWatchlistItemAsync("005930", "semis", null, CancellationToken.None);

        string result = await PortfolioTools.Watchlist(env.Service, action: "list", scope: "groups");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("scope").GetString().Should().Be("groups");
        root.GetProperty("groups").EnumerateArray().Should().Contain(g =>
            g.GetProperty("name").GetString() == "semis"
            && g.GetProperty("item_count").GetInt32() == 1
            && g.GetProperty("description").GetString() == "반도체");
    }

    [Fact]
    public async Task Watchlist_List_ScopeGroupsWithGroupName_RejectedAsValidationError()
    {
        await using TestEnvironment env = new();

        string result = await PortfolioTools.Watchlist(
            env.Service, action: "list", group_name: "default", scope: "groups");
        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString()
            .Should().Contain("does not accept group_name");
    }

    [Fact]
    public async Task Watchlist_List_UnknownScope_ReturnsValidationError()
    {
        await using TestEnvironment env = new();

        string result = await PortfolioTools.Watchlist(env.Service, action: "list", scope: "everything");
        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString()
            .Should().Contain("not recognized");
    }

    [Fact]
    public async Task Watchlist_Add_ThenList_RoundTrips()
    {
        await using TestEnvironment env = new();

        await PortfolioTools.Watchlist(env.Service, action: "add", shcode: "005930", note: "core");

        string listed = await PortfolioTools.Watchlist(env.Service, action: "list");
        JsonElement groups = JsonDocument.Parse(listed).RootElement.GetProperty("groups");
        groups.EnumerateArray()
            .SelectMany(g => g.GetProperty("items").EnumerateArray())
            .Should().Contain(i => i.GetProperty("shcode").GetString() == "005930");
    }

    [Fact]
    public async Task Watchlist_Add_MissingShcode_ReturnsValidationError()
    {
        await using TestEnvironment env = new();

        string result = await PortfolioTools.Watchlist(env.Service, action: "add");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("shcode");
        root.GetProperty("details").GetProperty("action").GetString().Should().Be("add");
    }

    [Fact]
    public async Task Watchlist_Remove_DropsItem()
    {
        await using TestEnvironment env = new();
        await env.Repository.AddWatchlistItemAsync("005930", "default", null, CancellationToken.None);

        string result = await PortfolioTools.Watchlist(env.Service, action: "remove", shcode: "005930");
        JsonDocument.Parse(result).RootElement.GetProperty("removed").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Watchlist_Remove_MissingShcode_ReturnsValidationError()
    {
        await using TestEnvironment env = new();

        string result = await PortfolioTools.Watchlist(env.Service, action: "remove");
        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString().Should().Contain("shcode");
    }

    [Fact]
    public async Task Watchlist_GroupUpsert_CreatesGroup()
    {
        await using TestEnvironment env = new();

        await PortfolioTools.Watchlist(
            env.Service, action: "group_upsert", name: "semis", description: "반도체");

        string listed = await PortfolioTools.Watchlist(env.Service, action: "list", scope: "groups");
        JsonDocument.Parse(listed).RootElement.GetProperty("groups").EnumerateArray()
            .Should().Contain(g => g.GetProperty("name").GetString() == "semis");
    }

    [Fact]
    public async Task Watchlist_GroupUpsert_RenamesViaRenameFrom()
    {
        await using TestEnvironment env = new();
        await env.Repository.CreateGroupAsync("semis", null);

        await PortfolioTools.Watchlist(
            env.Service, action: "group_upsert", name: "semiconductors", rename_from: "semis");

        string listed = await PortfolioTools.Watchlist(env.Service, action: "list", scope: "groups");
        IEnumerable<string?> names = JsonDocument.Parse(listed).RootElement.GetProperty("groups")
            .EnumerateArray().Select(g => g.GetProperty("name").GetString());
        names.Should().Contain("semiconductors").And.NotContain("semis");
    }

    [Fact]
    public async Task Watchlist_GroupUpsert_MissingName_ReturnsValidationError()
    {
        await using TestEnvironment env = new();

        string result = await PortfolioTools.Watchlist(env.Service, action: "group_upsert");
        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString().Should().Contain("name");
    }

    [Fact]
    public async Task Watchlist_GroupDelete_DropsGroup()
    {
        await using TestEnvironment env = new();
        await env.Repository.CreateGroupAsync("semis", null);

        await PortfolioTools.Watchlist(env.Service, action: "group_delete", name: "semis");

        string listed = await PortfolioTools.Watchlist(env.Service, action: "list", scope: "groups");
        JsonDocument.Parse(listed).RootElement.GetProperty("groups").EnumerateArray()
            .Select(g => g.GetProperty("name").GetString()).Should().NotContain("semis");
    }

    [Fact]
    public async Task Watchlist_GroupDelete_MissingName_ReturnsValidationError()
    {
        await using TestEnvironment env = new();

        string result = await PortfolioTools.Watchlist(env.Service, action: "group_delete");
        JsonDocument.Parse(result).RootElement.GetProperty("error").GetString().Should().Contain("name");
    }

    [Fact]
    public async Task Watchlist_UnknownAction_ReturnsValidationErrorWithValidActions()
    {
        await using TestEnvironment env = new();

        string result = await PortfolioTools.Watchlist(env.Service, action: "star");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("unknown action");
        root.GetProperty("details").GetProperty("valid_actions").EnumerateArray()
            .Select(e => e.GetString())
            .Should().BeEquivalentTo(new[] { "list", "add", "remove", "group_upsert", "group_delete" });
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
