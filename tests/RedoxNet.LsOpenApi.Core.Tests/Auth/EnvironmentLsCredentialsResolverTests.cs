using FluentAssertions;
using RedoxNet.LsOpenApi.Core.Auth;
using Xunit;

namespace RedoxNet.LsOpenApi.Core.Tests.Auth;

public class EnvironmentLsCredentialsResolverTests
{
    static ILsCredentialsResolver Build(Dictionary<string, string?> env) =>
        new EnvironmentLsCredentialsResolver(name => env.TryGetValue(name, out string? value) ? value : null);

    [Fact]
    public async Task ResolveAsync_AllSet_ReturnsCredentials()
    {
        var resolver = Build(new()
        {
            [LsCredentials.AppKeyEnvVar] = "appkey-1",
            [LsCredentials.AppSecretKeyEnvVar] = "secret-1",
            [LsCredentials.MarketEnvVar] = "real",
        });

        LsCredentials? creds = await resolver.ResolveAsync();

        creds.Should().NotBeNull();
        creds!.AppKey.Should().Be("appkey-1");
        creds.AppSecretKey.Should().Be("secret-1");
        creds.Market.Should().Be(LsMarket.Real);
    }

    [Fact]
    public async Task ResolveAsync_MarketMissing_DefaultsToVirtual()
    {
        var resolver = Build(new()
        {
            [LsCredentials.AppKeyEnvVar] = "appkey-1",
            [LsCredentials.AppSecretKeyEnvVar] = "secret-1",
        });

        LsCredentials? creds = await resolver.ResolveAsync();

        creds.Should().NotBeNull();
        creds!.Market.Should().Be(LsMarket.Virtual);
    }

    [Fact]
    public async Task ResolveAsync_AppKeyMissing_ReturnsNull()
    {
        var resolver = Build(new()
        {
            [LsCredentials.AppSecretKeyEnvVar] = "secret-1",
        });

        (await resolver.ResolveAsync()).Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_AppSecretMissing_ReturnsNull()
    {
        var resolver = Build(new()
        {
            [LsCredentials.AppKeyEnvVar] = "appkey-1",
        });

        (await resolver.ResolveAsync()).Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_EmptyValues_ReturnsNull()
    {
        var resolver = Build(new()
        {
            [LsCredentials.AppKeyEnvVar] = "  ",
            [LsCredentials.AppSecretKeyEnvVar] = "  ",
        });

        (await resolver.ResolveAsync()).Should().BeNull();
    }
}
