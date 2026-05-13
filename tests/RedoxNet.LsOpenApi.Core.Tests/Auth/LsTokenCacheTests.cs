using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Auth;
using Xunit;

namespace RedoxNet.LsOpenApi.Core.Tests.Auth;

public class LsTokenCacheTests : IDisposable
{
    readonly string _databasePath;

    public LsTokenCacheTests()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ls_token_cache_tests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _databasePath = Path.Combine(dir, "token.db");
    }

    public void Dispose()
    {
        string? dir = Path.GetDirectoryName(_databasePath);
        if (dir is not null && Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best effort cleanup */ }
        }
    }

    LsTokenCache NewCache() => new(_databasePath);

    static LsAccessToken NewToken(string suffix = "abcd", int ttlMinutes = 60)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new LsAccessToken(
            AccessToken: "access-" + suffix,
            TokenType: "Bearer",
            IssuedAtUtc: now,
            ExpiresAtUtc: now.AddMinutes(ttlMinutes),
            Scope: "oob");
    }

    [Fact]
    public async Task GetAsync_Miss_ReturnsNull()
    {
        LsTokenCache cache = NewCache();
        (await cache.GetAsync("missing-key")).Should().BeNull();
    }

    [Fact]
    public async Task SaveAndGet_RoundTrips()
    {
        LsTokenCache cache = NewCache();
        LsAccessToken original = NewToken();

        await cache.SaveAsync("k1", original);
        LsAccessToken? loaded = await cache.GetAsync("k1");

        loaded.Should().NotBeNull();
        loaded!.AccessToken.Should().Be(original.AccessToken);
        loaded.TokenType.Should().Be(original.TokenType);
        loaded.Scope.Should().Be(original.Scope);
        loaded.IssuedAtUtc.Should().BeCloseTo(original.IssuedAtUtc, TimeSpan.FromMilliseconds(1));
        loaded.ExpiresAtUtc.Should().BeCloseTo(original.ExpiresAtUtc, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task SaveAsync_SecondCall_OverwritesEntry()
    {
        LsTokenCache cache = NewCache();
        await cache.SaveAsync("k1", NewToken(suffix: "one"));
        await cache.SaveAsync("k1", NewToken(suffix: "two"));

        LsAccessToken? loaded = await cache.GetAsync("k1");
        loaded.Should().NotBeNull();
        loaded!.AccessToken.Should().Be("access-two");
    }

    [Fact]
    public async Task RemoveAsync_DropsEntry()
    {
        LsTokenCache cache = NewCache();
        await cache.SaveAsync("k1", NewToken());
        await cache.RemoveAsync("k1");

        (await cache.GetAsync("k1")).Should().BeNull();
    }

    [Fact]
    public async Task RemoveAsync_NonExistentKey_NoThrow()
    {
        LsTokenCache cache = NewCache();
        Func<Task> act = () => cache.RemoveAsync("never-saved");
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task CrossInstance_Persistence_Works()
    {
        // Two LsTokenCache instances against the same file (simulating restart).
        LsTokenCache first = NewCache();
        await first.SaveAsync("k1", NewToken(suffix: "persisted"));

        LsTokenCache second = NewCache();
        LsAccessToken? loaded = await second.GetAsync("k1");

        loaded.Should().NotBeNull();
        loaded!.AccessToken.Should().Be("access-persisted");
    }

    [Fact]
    public async Task MultipleKeys_AreIndependent()
    {
        LsTokenCache cache = NewCache();
        await cache.SaveAsync("real-key", NewToken(suffix: "real"));
        await cache.SaveAsync("virtual-key", NewToken(suffix: "virtual"));

        LsAccessToken? real = await cache.GetAsync("real-key");
        LsAccessToken? virtual_ = await cache.GetAsync("virtual-key");

        real!.AccessToken.Should().Be("access-real");
        virtual_!.AccessToken.Should().Be("access-virtual");
    }
}
