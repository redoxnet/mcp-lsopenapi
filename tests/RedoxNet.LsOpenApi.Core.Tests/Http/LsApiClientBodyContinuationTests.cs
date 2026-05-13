using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Options;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Catalog;
using RedoxNet.LsOpenApi.Core.Http;
using RedoxNet.LsOpenApi.Core.Tests.TestSupport;
using Xunit;

namespace RedoxNet.LsOpenApi.Core.Tests.Http;

public class LsApiClientBodyContinuationTests
{
    sealed class StaticTokenSource : ILsTokenSource
    {
        readonly LsAccessToken _token;
        public StaticTokenSource()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            _token = new LsAccessToken("fake", "Bearer", now, now.AddHours(23));
        }
        public Task<LsAccessToken> GetAccessTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_token);
    }

    static (LsApiClient client, StubHttpMessageHandler handler) NewClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        var handler = new StubHttpMessageHandler(responder);
        var http = new HttpClient(handler);
        var options = Options.Create(new LsApiOptions
        {
            BaseUrl = new Uri("https://openapi.ls-sec.co.kr:8080/"),
        });
        var client = new LsApiClient(http, options, new StaticTokenSource(), TrCatalog.Default);
        return (client, handler);
    }

    [Fact]
    public async Task CallTrAsync_T8410_NonEmptyCtsDate_ReportsBodyBasedContinuation()
    {
        const string okJson = """
        {
          "rsp_cd": "00000",
          "rsp_msg": "정상",
          "t8410OutBlock": { "shcode": "078020", "cts_date": "20230601" },
          "t8410OutBlock1": []
        }
        """;
        var (client, _) = NewClient((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(okJson, System.Text.Encoding.UTF8, "application/json"),
            }));

        LsTrResponse response = await client.CallTrAsync(
            "t8410",
            new JsonObject { ["shcode"] = "078020", ["gubun"] = "2", ["qrycnt"] = 100 });

        response.HasContinuation.Should().BeTrue();
        response.ContinuationKeys.Should().ContainKey("cts_date").WhoseValue.Should().Be("20230601");
    }

    [Fact]
    public async Task CallTrAsync_T8410_EmptyCtsDate_ReportsNoContinuation()
    {
        const string okJson = """
        {
          "rsp_cd": "00000",
          "rsp_msg": "정상",
          "t8410OutBlock": { "shcode": "078020", "cts_date": "" },
          "t8410OutBlock1": []
        }
        """;
        var (client, _) = NewClient((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(okJson, System.Text.Encoding.UTF8, "application/json"),
            }));

        LsTrResponse response = await client.CallTrAsync(
            "t8410",
            new JsonObject { ["shcode"] = "078020", ["gubun"] = "2", ["qrycnt"] = 100 });

        response.HasContinuation.Should().BeFalse();
        response.ContinuationKey.Should().BeNull();
        response.ContinuationKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task CallTrAsync_HeaderTakesPrecedenceOverBody()
    {
        const string okJson = """
        {
          "rsp_cd": "00000",
          "rsp_msg": "정상",
          "t8410OutBlock": { "shcode": "078020", "cts_date": "20230601" },
          "t8410OutBlock1": []
        }
        """;
        var (client, _) = NewClient((_, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(okJson, System.Text.Encoding.UTF8, "application/json"),
            };
            resp.Headers.TryAddWithoutValidation("tr_cont", "N");
            return Task.FromResult(resp);
        });

        LsTrResponse response = await client.CallTrAsync(
            "t8410",
            new JsonObject { ["shcode"] = "078020", ["gubun"] = "2", ["qrycnt"] = 100 });

        // Server explicitly said tr_cont=N — trust it, ignore body.
        response.HasContinuation.Should().BeFalse();
        response.ContinuationKey.Should().BeNull();
        response.ContinuationKeys.Should().BeEmpty();
    }

    [Fact]
    public async Task CallTrAsync_T8412_MultipleKeyFields_AllRead()
    {
        const string okJson = """
        {
          "rsp_cd": "00000",
          "rsp_msg": "정상",
          "t8412OutBlock": { "shcode": "078020", "cts_date": "20240906", "cts_time": "111200" },
          "t8412OutBlock1": []
        }
        """;
        var (client, _) = NewClient((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(okJson, System.Text.Encoding.UTF8, "application/json"),
            }));

        LsTrResponse response = await client.CallTrAsync(
            "t8412",
            new JsonObject { ["shcode"] = "078020", ["ncnt"] = 1, ["qrycnt"] = 500 });

        response.HasContinuation.Should().BeTrue();
        response.ContinuationKeys.Should().HaveCount(2);
        response.ContinuationKeys["cts_date"].Should().Be("20240906");
        response.ContinuationKeys["cts_time"].Should().Be("111200");
    }

    [Fact]
    public async Task CallTrAsync_T1101_NonContinuationTr_NoKeyField()
    {
        const string okJson = """
        {
          "rsp_cd": "00000",
          "rsp_msg": "정상",
          "t1101OutBlock": { "hname": "LS증권", "price": 4545 }
        }
        """;
        var (client, _) = NewClient((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(okJson, System.Text.Encoding.UTF8, "application/json"),
            }));

        LsTrResponse response = await client.CallTrAsync(
            "t1101",
            new JsonObject { ["shcode"] = "078020" });

        response.HasContinuation.Should().BeFalse();
        response.ContinuationKeys.Should().BeEmpty();
    }
}
