using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Portfolio;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Portfolio;

public sealed class PortfolioServiceTests
{
    [Fact]
    public async Task ListWatchlistAsync_ReturnsMetadataWhenQuoteServiceFails()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        await repository.AddWatchlistItemAsync("005930", "default", "watch", CancellationToken.None);
        var service = new PortfolioService(repository, new FakeQuoteService("no credentials"));

        WatchlistListResult result = await service.ListWatchlistAsync("default");

        result.QuoteError.Should().Be("no credentials");
        WatchlistGroupItems group = result.Groups.Should().ContainSingle().Subject;
        group.Items.Should().ContainSingle();
        group.Items[0].Shcode.Should().Be("005930");
        group.Items[0].Quote.Should().BeNull();
    }

    [Fact]
    public async Task ListWatchlistAsync_WithoutGroupIncludesEmptyGroups()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        await repository.CreateGroupAsync("empty", null, CancellationToken.None);
        var service = new PortfolioService(repository, new FakeQuoteService());

        WatchlistListResult result = await service.ListWatchlistAsync(null);

        result.Groups.Should().NotBeNull();
        result.Groups.Should().Contain(g => g.Name == "default");
        result.Groups.Should().Contain(g => g.Name == "empty" && g.Items.Count == 0);
    }


    [Fact]
    public async Task DeleteGroupAsync_ReportsEmptyGroupDeleted()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        await repository.CreateGroupAsync("empty", null, CancellationToken.None);
        var service = new PortfolioService(repository, new FakeQuoteService());

        DeleteGroupResult result = await service.DeleteGroupAsync("empty");
        string json = System.Text.Json.JsonSerializer.Serialize(result, McpJson.Tool);
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json);

        document.RootElement.GetProperty("deleted").GetBoolean().Should().BeTrue();
        document.RootElement.GetProperty("cascaded_items").GetInt32().Should().Be(0);
    }
    [Fact]
    public async Task ListHoldingsAsync_ComputesSummaryWhenQuotesAreAvailable()
    {
        await using TestDatabase db = new();
        var repository = new SqlitePortfolioRepository(db.Path);
        await repository.InitializeAsync();
        Account account = await repository.GetDefaultAccountAsync();
        await repository.UpsertHoldingAsync(account.Id, "005930", 10, 70000, null, CancellationToken.None);
        var service = new PortfolioService(repository, new FakeQuoteService(quotes: new Dictionary<string, StockQuote?>
        {
            ["005930"] = new StockQuote(Price: 75000, Change: 1000, ChangePct: 1.35, Open: 74000, High: 76000, Low: 73500, Volume: 1000, Timestamp: "2026-05-15T09:00:00+09:00")
            {
                Name = "삼성전자",
            },
        }));

        HoldingListResult result = await service.ListHoldingsAsync();

        result.Items.Should().ContainSingle();
        result.Items[0].Name.Should().Be("삼성전자");
        result.Items[0].CurrentValue.Should().Be(750000);
        result.Items[0].Pnl.Should().Be(50000);
        result.Summary.TotalCost.Should().Be(700000);
        result.Summary.TotalValue.Should().Be(750000);
        result.Summary.TotalPnl.Should().Be(50000);
    }

    sealed class FakeQuoteService : IQuoteService
    {
        readonly string? _stockError;
        readonly IReadOnlyDictionary<string, StockQuote?> _quotes;

        public FakeQuoteService(string? stockError = null, IReadOnlyDictionary<string, StockQuote?>? quotes = null)
        {
            _stockError = stockError;
            _quotes = quotes ?? new Dictionary<string, StockQuote?>();
        }

        public Task<QuoteBatchResult<StockQuote>> GetStockQuotesAsync(IReadOnlyCollection<string> symbols, CancellationToken cancellationToken = default)
        {
            var result = symbols.Distinct(StringComparer.Ordinal).ToDictionary(
                s => s,
                s => _quotes.TryGetValue(s, out StockQuote? quote) ? quote : null,
                StringComparer.Ordinal);
            return Task.FromResult(new QuoteBatchResult<StockQuote>(result, _stockError));
        }

        public Task<QuoteBatchResult<SectorQuote>> GetSectorQuotesAsync(IReadOnlyCollection<string> sectorCodes, CancellationToken cancellationToken = default)
        {
            var result = sectorCodes.Distinct(StringComparer.Ordinal).ToDictionary(
                s => s,
                _ => (SectorQuote?)null,
                StringComparer.Ordinal);
            return Task.FromResult(new QuoteBatchResult<SectorQuote>(result, null));
        }
    }

    sealed class TestDatabase : IAsyncDisposable
    {
        readonly string _directory;

        public TestDatabase()
        {
            _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcp-lsopenapi-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "portfolio.db");
        }

        public string Path { get; }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (Directory.Exists(_directory))
                    Directory.Delete(_directory, recursive: true);
            }
            catch
            {
            }
            return ValueTask.CompletedTask;
        }
    }
}

