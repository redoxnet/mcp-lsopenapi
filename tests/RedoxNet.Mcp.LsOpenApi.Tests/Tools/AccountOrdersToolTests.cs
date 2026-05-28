using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.Mcp.LsOpenApi.Portfolio;
using RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

/// <summary>
/// Pins <see cref="AccountInquiryTools.Orders"/> (TR t0425) against a
/// real-shaped sample response. Fixture adapted from the LS docs
/// example in <c>todo/[주식] 계좌_t0425.txt</c>.
/// </summary>
public sealed class AccountOrdersToolTests
{
    const string SampleT0425Response = """
        {
          "rsp_cd": "00000",
          "rsp_msg": "조회가 완료되었습니다.",
          "t0425OutBlock": {
            "tcheqty": 0,
            "tamt": 0,
            "tqty": 2,
            "cmss": 0,
            "tmsamt": 0,
            "tax": 0,
            "tmdamt": 0,
            "cts_ordno": "",
            "tordrem": 2
          },
          "t0425OutBlock1": [
            {
              "orgordno": 0,
              "ordrem": 2,
              "cfmqty": 0,
              "ordgb": "보통",
              "cheqty": 0,
              "orggb": "02",
              "ordno": 84,
              "loandt": "",
              "price": 60000,
              "sysprocseq": 88,
              "singb": "00",
              "qty": 2,
              "hogagb": "00",
              "expcode": "005930",
              "medosu": "매수",
              "cheprice": 0,
              "ordtime": "08410730",
              "ordermtd": "씽(Xing)-F",
              "price1": 71900,
              "status": "접수"
            }
          ]
        }
        """;

    [Fact]
    public async Task Orders_ParsesSampleResponse_AndAttachesMeta()
    {
        await using AccountScratch scratch = new();
        await scratch.SeedDefaultAccount("12345-01", "주식");

        var (client, handler) = TestClientFactory.Create((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SampleT0425Response, System.Text.Encoding.UTF8, "application/json"),
            }));

        string result = await AccountInquiryTools.Orders(client, scratch.Resolver);
        JsonElement root = JsonDocument.Parse(result).RootElement;

        JsonElement filter = root.GetProperty("filter");
        filter.GetProperty("status").GetString().Should().Be("all");
        filter.GetProperty("side").GetString().Should().Be("all");
        filter.GetProperty("sort").GetString().Should().Be("asc");

        JsonElement summary = root.GetProperty("summary");
        summary.GetProperty("total_order_quantity").GetInt64().Should().Be(2);
        summary.GetProperty("total_filled_quantity").GetInt64().Should().Be(0);
        summary.GetProperty("total_pending_quantity").GetInt64().Should().Be(2);

        root.GetProperty("count").GetInt32().Should().Be(1);
        JsonElement row = root.GetProperty("orders")[0];
        row.GetProperty("order_no").GetInt64().Should().Be(84);
        row.GetProperty("symbol").GetString().Should().Be("005930");
        row.GetProperty("side").GetString().Should().Be("매수");
        row.GetProperty("order_type").GetString().Should().Be("보통");
        row.GetProperty("order_quantity").GetInt64().Should().Be(2);
        row.GetProperty("order_price").GetInt64().Should().Be(60000);
        row.GetProperty("filled_quantity").GetInt64().Should().Be(0);
        row.GetProperty("pending_quantity").GetInt64().Should().Be(2);
        row.GetProperty("status").GetString().Should().Be("접수");
        // ordtime 08410730 → "08:41:07.30" so the LLM doesn't try to parse it as a date.
        row.GetProperty("order_time").GetString().Should().Be("08:41:07.30");
        row.GetProperty("current_price").GetInt64().Should().Be(71900);

        JsonElement meta = root.GetProperty("_meta");
        meta.GetProperty("tr_code").GetString().Should().Be("t0425");
        meta.GetProperty("source").GetString().Should().Be("live");
    }

    [Fact]
    public async Task Orders_RejectsUnknownStatus()
    {
        await using AccountScratch scratch = new();
        await scratch.SeedDefaultAccount("12345-01", "주식");

        var (client, _) = TestClientFactory.Create((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }));

        string result = await AccountInquiryTools.Orders(client, scratch.Resolver, status: "weird");
        JsonElement root = JsonDocument.Parse(result).RootElement;
        root.GetProperty("error").GetString().Should().Contain("status 'weird' is not recognized");
    }

    [Fact]
    public async Task Orders_MapsStatusAndSideFiltersToInBlock()
    {
        await using AccountScratch scratch = new();
        await scratch.SeedDefaultAccount("12345-01", "주식");

        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;
        var (client, _) = TestClientFactory.Create(async (req, _) =>
        {
            capturedRequest = req;
            capturedBody = req.Content is null ? null : await req.Content.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"rsp_cd":"00000","rsp_msg":"ok","t0425OutBlock":{},"t0425OutBlock1":[]}""",
                    System.Text.Encoding.UTF8, "application/json"),
            };
        });

        await AccountInquiryTools.Orders(client, scratch.Resolver,
            status: "pending", side: "buy", symbol: "005930", sort: "desc");

        capturedRequest.Should().NotBeNull();
        capturedRequest!.RequestUri!.AbsolutePath.Should().Be("/stock/accno");
        capturedBody.Should().NotBeNull();
        capturedBody!.Should().Contain("\"chegb\":\"2\"");   // pending
        capturedBody.Should().Contain("\"medosu\":\"2\"");   // buy
        capturedBody.Should().Contain("\"sortgb\":\"1\"");   // desc
        capturedBody.Should().Contain("\"expcode\":\"005930\"");
    }

    sealed class AccountScratch : IAsyncDisposable
    {
        readonly string _directory;
        readonly SqlitePortfolioRepository _repository;

        public AccountScratch()
        {
            _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mcp-lsopenapi-o-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            DbPath = System.IO.Path.Combine(_directory, "portfolio.db");
            _repository = new SqlitePortfolioRepository(DbPath, "real");
            Resolver = new LsAccountResolver(_repository, LsMarket.Real);
        }

        public string DbPath { get; }
        public LsAccountResolver Resolver { get; }

        public async Task SeedDefaultAccount(string accountNumber, string nickname)
        {
            await _repository.InitializeAsync();
            await _repository.UpsertAccountAsync(accountNumber, nickname, null, setDefault: true);
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(_directory))
                    Directory.Delete(_directory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup.
            }
            return ValueTask.CompletedTask;
        }
    }
}
