using Microsoft.Extensions.Options;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Catalog;
using RedoxNet.LsOpenApi.Core.Http;

namespace RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;

/// <summary>
/// Builds a fully-wired <see cref="LsApiClient"/> around a
/// <see cref="StubHttpMessageHandler"/> for tool-level integration tests.
/// </summary>
internal static class TestClientFactory
{
    sealed class StaticTokenSource : ILsTokenSource
    {
        readonly LsAccessToken _token;

        public StaticTokenSource()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            _token = new LsAccessToken("fake-token", "Bearer", now, now.AddHours(23));
        }

        public Task<LsAccessToken> GetAccessTokenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_token);
    }

    public static (LsApiClient client, StubHttpMessageHandler handler) Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        var handler = new StubHttpMessageHandler(responder);
        var httpClient = new HttpClient(handler);
        var options = Options.Create(new LsApiOptions
        {
            BaseUrl = new Uri("https://openapi.ls-sec.co.kr:8080/"),
        });
        var client = new LsApiClient(
            httpClient,
            options,
            new StaticTokenSource(),
            TrCatalog.Default,
            rateLimiter: null);
        return (client, handler);
    }
}
