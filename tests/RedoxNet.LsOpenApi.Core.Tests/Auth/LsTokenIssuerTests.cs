using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Http;
using RedoxNet.LsOpenApi.Core.Tests.TestSupport;
using Xunit;

namespace RedoxNet.LsOpenApi.Core.Tests.Auth;

public class LsTokenIssuerTests
{
    static readonly LsCredentials FakeCredentials = new("PSappkey1234", "PSsecret-abcdwxyz", LsMarket.Virtual);

    sealed class InMemoryTokenStore : ITokenStore
    {
        readonly Dictionary<string, LsAccessToken> _store = new(StringComparer.Ordinal);

        public Task<LsAccessToken?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            _store.TryGetValue(key, out LsAccessToken? token);
            return Task.FromResult(token);
        }

        public Task SaveAsync(string key, LsAccessToken token, CancellationToken cancellationToken = default)
        {
            _store[key] = token;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            _store.Remove(key);
            return Task.CompletedTask;
        }

        public int Count => _store.Count;
    }

    sealed class StaticResolver(LsCredentials? credentials) : ILsCredentialsResolver
    {
        public Task<LsCredentials?> ResolveAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(credentials);
    }

    static LsTokenIssuer NewIssuer(
        StubHttpMessageHandler handler,
        ITokenStore? store = null,
        ILsCredentialsResolver? resolver = null,
        TimeSpan? refreshWindow = null)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://openapi.ls-sec.co.kr:8080/"),
        };
        var options = Options.Create(new LsApiOptions
        {
            BaseUrl = new Uri("https://openapi.ls-sec.co.kr:8080/"),
            TokenRefreshWindow = refreshWindow ?? TimeSpan.FromMinutes(5),
        });
        return new LsTokenIssuer(
            httpClient,
            options,
            store ?? new InMemoryTokenStore(),
            resolver ?? new StaticResolver(FakeCredentials));
    }

    static string CannedTokenJson(string accessToken = "issued-access-token", int expiresIn = 3600) =>
        JsonSerializer.Serialize(new
        {
            access_token = accessToken,
            token_type = "Bearer",
            expires_in = expiresIn,
            scope = "oob",
        });

    [Fact]
    public async Task GetAccessTokenAsync_FirstCall_HitsTokenEndpoint()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, CannedTokenJson());
        LsTokenIssuer issuer = NewIssuer(handler);

        LsAccessToken token = await issuer.GetAccessTokenAsync();

        token.AccessToken.Should().Be("issued-access-token");
        token.TokenType.Should().Be("Bearer");
        token.Scope.Should().Be("oob");
        handler.Requests.Should().HaveCount(1);
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/oauth2/token");
        handler.Requests[0].Method.Should().Be(HttpMethod.Post);
    }

    [Fact]
    public async Task GetAccessTokenAsync_PostsClientCredentialsForm()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, CannedTokenJson());
        LsTokenIssuer issuer = NewIssuer(handler);

        await issuer.GetAccessTokenAsync();

        string body = await handler.Requests[0].Content!.ReadAsStringAsync();
        body.Should().Contain("grant_type=client_credentials");
        body.Should().Contain("appkey=PSappkey1234");
        body.Should().Contain("appsecretkey=PSsecret-abcdwxyz");
        body.Should().Contain("scope=oob");
    }

    [Fact]
    public async Task GetAccessTokenAsync_SecondCall_ServedFromMemoryCache()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, CannedTokenJson());
        LsTokenIssuer issuer = NewIssuer(handler);

        await issuer.GetAccessTokenAsync();
        await issuer.GetAccessTokenAsync();

        handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetAccessTokenAsync_PersistsToStore()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, CannedTokenJson());
        var store = new InMemoryTokenStore();
        LsTokenIssuer issuer = NewIssuer(handler, store: store);

        await issuer.GetAccessTokenAsync();

        store.Count.Should().Be(1);
    }

    [Fact]
    public async Task GetAccessTokenAsync_PrefersStoredTokenAcrossInstances()
    {
        var store = new InMemoryTokenStore();
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, CannedTokenJson("first-token"));
        LsTokenIssuer firstIssuer = NewIssuer(handler, store: store);
        await firstIssuer.GetAccessTokenAsync();

        // New issuer instance, same store, second handler should not be invoked.
        var secondHandler = new StubHttpMessageHandler(HttpStatusCode.OK, CannedTokenJson("second-token"));
        LsTokenIssuer secondIssuer = NewIssuer(secondHandler, store: store);

        LsAccessToken token = await secondIssuer.GetAccessTokenAsync();

        token.AccessToken.Should().Be("first-token");
        secondHandler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAccessTokenAsync_RefreshesWhenInsideRefreshWindow()
    {
        // Token whose expiry is only 1 minute away; refresh window is 5 minutes.
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, CannedTokenJson(expiresIn: 60));
        LsTokenIssuer issuer = NewIssuer(handler, refreshWindow: TimeSpan.FromMinutes(5));

        await issuer.GetAccessTokenAsync();
        await issuer.GetAccessTokenAsync();

        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAccessTokenAsync_NonSuccess_ThrowsLsAuthException()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.Unauthorized, "{\"error\":\"invalid_client\"}");
        LsTokenIssuer issuer = NewIssuer(handler);

        Func<Task> act = () => issuer.GetAccessTokenAsync();

        LsAuthException ex = (await act.Should().ThrowAsync<LsAuthException>()).Which;
        ex.StatusCode.Should().Be(401);
        ex.ResponseBody.Should().Contain("invalid_client");
    }

    [Fact]
    public async Task GetAccessTokenAsync_MalformedJson_ThrowsLsAuthException()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "not-json");
        LsTokenIssuer issuer = NewIssuer(handler);

        Func<Task> act = () => issuer.GetAccessTokenAsync();

        await act.Should().ThrowAsync<LsAuthException>();
    }

    [Fact]
    public async Task GetAccessTokenAsync_MissingAccessToken_ThrowsLsAuthException()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, "{\"token_type\":\"Bearer\",\"expires_in\":3600}");
        LsTokenIssuer issuer = NewIssuer(handler);

        Func<Task> act = () => issuer.GetAccessTokenAsync();

        await act.Should().ThrowAsync<LsAuthException>();
    }

    [Fact]
    public async Task GetAccessTokenAsync_NoCredentials_ThrowsLsAuthException()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, CannedTokenJson());
        LsTokenIssuer issuer = NewIssuer(handler, resolver: new StaticResolver(null));

        Func<Task> act = () => issuer.GetAccessTokenAsync();
        (await act.Should().ThrowAsync<LsAuthException>())
            .Which.Message.Should().Contain(LsCredentials.AppKeyEnvVar);
    }

    [Fact]
    public async Task InvalidateAsync_DropsCachedTokenAndForcesReissue()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, CannedTokenJson());
        LsTokenIssuer issuer = NewIssuer(handler);

        await issuer.GetAccessTokenAsync();
        await issuer.InvalidateAsync(FakeCredentials);
        await issuer.GetAccessTokenAsync();

        handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetAccessTokenAsync_ConcurrentCallers_ShareSingleIssuance()
    {
        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, CannedTokenJson());
        LsTokenIssuer issuer = NewIssuer(handler);

        Task<LsAccessToken>[] tasks = Enumerable.Range(0, 16)
            .Select(_ => issuer.GetAccessTokenAsync())
            .ToArray();
        await Task.WhenAll(tasks);

        handler.Requests.Should().HaveCount(1);
        tasks.Select(t => t.Result.AccessToken).Distinct().Should().ContainSingle();
    }
}
