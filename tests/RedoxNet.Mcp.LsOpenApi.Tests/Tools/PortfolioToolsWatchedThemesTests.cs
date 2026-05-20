using System.Text.Json;
using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Portfolio;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

/// <summary>
/// Covers the v0.10 <c>ls_watched_themes</c> domain dispatcher
/// (SPEC-v0.10 §2.6.3): ls_watched_themes_add / _remove / _list folded
/// into one action-routed tool. Exercises routing and per-action
/// argument validation.
/// </summary>
public sealed class PortfolioToolsWatchedThemesTests
{
    [Fact]
    public async Task WatchedThemes_Add_ThenList_RoundTrips()
    {
        await using TestEnvironment env = new();

        string added = await PortfolioTools.WatchedThemes(
            env.Service, action: "add", theme_code: "0064", theme_name: "2차전지", note: "watch");
        JsonDocument.Parse(added).RootElement.GetProperty("theme_code").GetString().Should().Be("0064");

        string listed = await PortfolioTools.WatchedThemes(env.Service, action: "list");
        JsonElement items = JsonDocument.Parse(listed).RootElement.GetProperty("items");
        items.EnumerateArray().Should().ContainSingle(t => t.GetProperty("theme_code").GetString() == "0064");
    }

    [Fact]
    public async Task WatchedThemes_Remove_DropsTheme()
    {
        await using TestEnvironment env = new();
        await env.Repository.WatchThemeAsync("0064", "2차전지", null, CancellationToken.None);

        string result = await PortfolioTools.WatchedThemes(env.Service, action: "remove", theme_code: "0064");
        JsonDocument.Parse(result).RootElement.GetProperty("removed").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task WatchedThemes_Add_MissingThemeCode_ReturnsValidationError()
    {
        await using TestEnvironment env = new();

        string result = await PortfolioTools.WatchedThemes(env.Service, action: "add", theme_name: "2차전지");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("theme_code");
        root.GetProperty("details").GetProperty("action").GetString().Should().Be("add");
    }

    [Fact]
    public async Task WatchedThemes_Remove_MissingThemeCode_ReturnsValidationError()
    {
        await using TestEnvironment env = new();

        string result = await PortfolioTools.WatchedThemes(env.Service, action: "remove");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("theme_code");
    }

    [Fact]
    public async Task WatchedThemes_UnknownAction_ReturnsValidationErrorWithValidActions()
    {
        await using TestEnvironment env = new();

        string result = await PortfolioTools.WatchedThemes(env.Service, action: "subscribe");
        JsonElement root = JsonDocument.Parse(result).RootElement;

        root.GetProperty("error").GetString().Should().Contain("unknown action");
        root.GetProperty("details").GetProperty("valid_actions").EnumerateArray()
            .Select(e => e.GetString()).Should().BeEquivalentTo(new[] { "list", "add", "remove" });
    }

    sealed class TestEnvironment : IAsyncDisposable
    {
        readonly string _dir;

        public SqlitePortfolioRepository Repository { get; }
        public PortfolioService Service { get; }

        public TestEnvironment()
        {
            _dir = Path.Combine(Path.GetTempPath(), "mcp-lsopenapi-wt-" + Guid.NewGuid().ToString("N"));
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
