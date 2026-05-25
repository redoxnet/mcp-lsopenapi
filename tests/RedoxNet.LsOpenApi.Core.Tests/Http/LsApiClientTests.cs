using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Options;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Catalog;
using RedoxNet.LsOpenApi.Core.Http;
using RedoxNet.LsOpenApi.Core.Tests.TestSupport;
using Xunit;

namespace RedoxNet.LsOpenApi.Core.Tests.Http;

public class LsApiClientTests
{
    sealed class StaticTokenSource : ILsTokenSource
    {
        readonly LsAccessToken _token;

        public StaticTokenSource(string accessToken = "fake-access-token")
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            _token = new LsAccessToken(accessToken, "Bearer", now, now.AddHours(23));
        }

        public Task<LsAccessToken> GetAccessTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_token);
    }

    static (LsApiClient client, StubHttpMessageHandler handler) NewClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder,
        TrRateLimiter? rateLimiter = null)
    {
        var handler = new StubHttpMessageHandler(responder);
        var httpClient = new HttpClient(handler);
        var options = Options.Create(new LsApiOptions
        {
            BaseUrl = new Uri("https://openapi.ls-sec.co.kr:8080/"),
            TokenRefreshWindow = TimeSpan.FromMinutes(5),
        });

        var client = new LsApiClient(
            httpClient,
            options,
            new StaticTokenSource(),
            catalog: TrCatalog.Default,
            rateLimiter: rateLimiter);
        return (client, handler);
    }

    static Task<HttpResponseMessage> Json(HttpStatusCode status, string body, bool trCont = false, string? trContKey = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
        };
        response.Headers.TryAddWithoutValidation("tr_cont", trCont ? "Y" : "N");
        if (trContKey is not null)
            response.Headers.TryAddWithoutValidation("tr_cont_key", trContKey);
        return Task.FromResult(response);
    }

    [Fact]
    public async Task CallTrAsync_BuildsCanonicalRequestShape()
    {
        const string okBody = """{ "t1101OutBlock": { "hname": "삼성전자" }, "rsp_cd": "00000", "rsp_msg": "정상" }""";
        var (client, handler) = NewClient((req, _) => Json(HttpStatusCode.OK, okBody));

        var inBlock = new JsonObject { ["shcode"] = "005930" };
        LsTrResponse response = await client.CallTrAsync("t1101", inBlock);

        handler.Requests.Should().HaveCount(1);
        HttpRequestMessage sent = handler.Requests[0];
        sent.Method.Should().Be(HttpMethod.Post);
        sent.RequestUri!.AbsolutePath.Should().Be("/stock/market-data");
        sent.Headers.Authorization!.Scheme.Should().Be("Bearer");
        sent.Headers.Authorization.Parameter.Should().Be("fake-access-token");
        sent.Headers.GetValues("tr_cd").Should().ContainSingle().Which.Should().Be("t1101");
        sent.Headers.GetValues("tr_cont").Should().ContainSingle().Which.Should().Be("N");

        string body = await sent.Content!.ReadAsStringAsync();
        JsonElement parsed = JsonDocument.Parse(body).RootElement;
        parsed.GetProperty("t1101InBlock").GetProperty("shcode").GetString().Should().Be("005930");

        response.IsSuccess.Should().BeTrue();
        response.RspCode.Should().Be("00000");
        response.OutBlockNames.Should().ContainSingle().Which.Should().Be("t1101OutBlock");
    }

    [Fact]
    public async Task CallTrAsync_WithContinuation_SendsKeyHeader()
    {
        var (client, handler) = NewClient((req, _) => Json(HttpStatusCode.OK,
            """{ "t8410OutBlock1": [], "rsp_cd": "00000", "rsp_msg": "ok" }"""));

        await client.CallTrAsync("t8410",
            new JsonObject { ["shcode"] = "005930", ["gubun"] = "2", ["qrycnt"] = 100 },
            continuationKey: "20240101");

        handler.Requests[0].Headers.GetValues("tr_cont").Should().ContainSingle().Which.Should().Be("Y");
        handler.Requests[0].Headers.GetValues("tr_cont_key").Should().ContainSingle().Which.Should().Be("20240101");
    }

    [Fact]
    public async Task CallTrAsync_ParsesContinuationFromResponseHeaders()
    {
        var (client, _) = NewClient((req, _) => Json(HttpStatusCode.OK,
            """{ "t8410OutBlock1": [], "rsp_cd": "00000", "rsp_msg": "ok" }""",
            trCont: true,
            trContKey: "20240115"));

        LsTrResponse response = await client.CallTrAsync("t8410",
            new JsonObject { ["shcode"] = "005930", ["gubun"] = "2", ["qrycnt"] = 100 });

        response.HasContinuation.Should().BeTrue();
        response.ContinuationKey.Should().Be("20240115");
    }

    [Fact]
    public async Task CallTrAsync_BusinessFailure_DoesNotThrow()
    {
        var (client, _) = NewClient((req, _) => Json(HttpStatusCode.OK,
            """{ "rsp_cd": "00001", "rsp_msg": "잘못된 종목" }"""));

        LsTrResponse response = await client.CallTrAsync("t1101",
            new JsonObject { ["shcode"] = "999999" });

        response.IsSuccess.Should().BeFalse();
        response.RspCode.Should().Be("00001");
        response.RspMessage.Should().Be("잘못된 종목");
    }

    [Fact]
    public async Task CallTrAsync_HttpError_ThrowsLsTrException()
    {
        var (client, handler) = NewClient((req, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("nope"),
            }));

        Func<Task> act = () => client.CallTrAsync("t1101", new JsonObject { ["shcode"] = "005930" });

        LsTrException ex = (await act.Should().ThrowAsync<LsTrException>()).Which;
        ex.StatusCode.Should().Be(401);
        ex.TrCode.Should().Be("t1101");
        handler.Requests.Should().HaveCount(1); // 401 is not retried
    }

    [Fact]
    public async Task CallTrAsync_TransientFailure_RetriesUntilSuccess()
    {
        int callCount = 0;
        var (client, handler) = NewClient((req, _) =>
        {
            callCount++;
            if (callCount < 3)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("retry me"),
                });
            return Json(HttpStatusCode.OK,
                """{ "t1101OutBlock": {}, "rsp_cd": "00000", "rsp_msg": "정상" }""");
        });

        LsTrResponse response = await client.CallTrAsync("t1101", new JsonObject { ["shcode"] = "005930" });

        response.IsSuccess.Should().BeTrue();
        handler.Requests.Should().HaveCount(3);
    }

    [Fact]
    public async Task CallTrAsync_Http500_RetriesUntilSuccess()
    {
        // v1.4-dev: LS occasionally returns HTTP 500 for known-good TRs
        // (observed on t1825 inside ls_combine_screeners, and on g3204
        // overseas chart). 500 was added to the transient retry list so
        // these one-shot blips don't surface to the user.
        int callCount = 0;
        var (client, handler) = NewClient((req, _) =>
        {
            callCount++;
            if (callCount == 1)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("LS 500"),
                });
            return Json(HttpStatusCode.OK,
                """{ "t1101OutBlock": {}, "rsp_cd": "00000", "rsp_msg": "정상" }""");
        });

        LsTrResponse response = await client.CallTrAsync("t1101", new JsonObject { ["shcode"] = "005930" });

        response.IsSuccess.Should().BeTrue();
        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task CallTrAsync_TransientFailure_GivesUpAfterMaxRetries()
    {
        var (client, handler) = NewClient((req, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("nope"),
            }));

        Func<Task> act = () => client.CallTrAsync("t1101", new JsonObject { ["shcode"] = "005930" });

        await act.Should().ThrowAsync<LsTrException>();
        // 1 initial + 3 retries
        handler.Requests.Should().HaveCount(4);
    }

    [Fact]
    public async Task CallTrAsync_MalformedJson_ThrowsLsTrException()
    {
        var (client, _) = NewClient((req, _) => Json(HttpStatusCode.OK, "not json"));

        Func<Task> act = () => client.CallTrAsync("t1101", new JsonObject { ["shcode"] = "005930" });

        await act.Should().ThrowAsync<LsTrException>();
    }

    [Fact]
    public async Task CallTrAsync_UnknownTrCode_Throws()
    {
        var (client, _) = NewClient((req, _) => Json(HttpStatusCode.OK, "{}"));

        Func<Task> act = () => client.CallTrAsync("tNOPE", new JsonObject());

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task CallTrAsync_GetBlock_ReturnsTypedElement()
    {
        var (client, _) = NewClient((req, _) => Json(HttpStatusCode.OK,
            """{ "t1101OutBlock": { "hname": "삼성전자", "price": 71500 }, "rsp_cd": "00000", "rsp_msg": "정상" }"""));

        LsTrResponse response = await client.CallTrAsync("t1101", new JsonObject { ["shcode"] = "005930" });

        JsonElement? block = response.GetBlock("t1101OutBlock");
        block.Should().NotBeNull();
        block!.Value.GetProperty("hname").GetString().Should().Be("삼성전자");
        block.Value.GetProperty("price").GetInt32().Should().Be(71500);
    }
}
