using FluentAssertions;
using RedoxNet.Mcp.LsOpenApi.Tools;
using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

/// <summary>
/// Covers the v0.10 kind-tagged generalization of <see cref="DatasetHandleCache"/>
/// (SPEC-v0.10 §5.2): any payload type can be stored behind an opaque handle and
/// is resolved back by runtime type, with a kind/type mismatch surfacing as a
/// clean miss. Pinned to the cache collection — the cache is a process-wide
/// static, so these run serialized with the chart / index-history suites.
/// </summary>
[Collection(ChartDatasetCacheCollection.Name)]
public sealed class DatasetHandleCacheTests
{
    sealed record AlphaPayload(string Value);
    sealed record BetaPayload(int Number);
    sealed record BigPayload(string Blob);

    [Fact]
    public void AddThenTryGet_RoundTripsPayload()
    {
        string id = DatasetHandleCache.Add("alpha", new AlphaPayload("hello"));

        DatasetHandleCache.TryGet(id, out AlphaPayload? got).Should().BeTrue();
        got!.Value.Should().Be("hello");
    }

    [Fact]
    public void TryGet_WrongType_ReturnsFalse()
    {
        string id = DatasetHandleCache.Add("alpha", new AlphaPayload("hello"));

        DatasetHandleCache.TryGet(id, out BetaPayload? got).Should().BeFalse(
            "a handle resolves only as the type it was stored as");
        got.Should().BeNull();
    }

    [Fact]
    public void TryGet_DistinctKinds_AreIsolatedByType()
    {
        string alphaId = DatasetHandleCache.Add("alpha", new AlphaPayload("a"));
        string betaId = DatasetHandleCache.Add("beta", new BetaPayload(42));

        DatasetHandleCache.TryGet(alphaId, out AlphaPayload? a).Should().BeTrue();
        a!.Value.Should().Be("a");
        DatasetHandleCache.TryGet(betaId, out BetaPayload? b).Should().BeTrue();
        b!.Number.Should().Be(42);

        // Cross-resolving the other kind misses.
        DatasetHandleCache.TryGet(alphaId, out BetaPayload? _).Should().BeFalse();
        DatasetHandleCache.TryGet(betaId, out AlphaPayload? _).Should().BeFalse();
    }

    [Fact]
    public void TryGet_UnknownHandle_ReturnsFalse()
    {
        DatasetHandleCache.TryGet("ds_does_not_exist", out AlphaPayload? got).Should().BeFalse();
        got.Should().BeNull();
    }

    [Fact]
    public void TryUpdate_ReplacesPayloadUnderSameHandle()
    {
        string id = DatasetHandleCache.Add("alpha", new AlphaPayload("before"));

        DatasetHandleCache.TryUpdate(id, new AlphaPayload("after")).Should().BeTrue();

        DatasetHandleCache.TryGet(id, out AlphaPayload? got).Should().BeTrue();
        got!.Value.Should().Be("after");
    }

    [Fact]
    public void TryUpdate_UnknownHandle_ReturnsFalse()
    {
        DatasetHandleCache.TryUpdate("ds_does_not_exist", new AlphaPayload("x")).Should().BeFalse();
    }

    [Fact]
    public void Add_OversizedPayload_Throws()
    {
        // Per-dataset cap is 5 MB; a ~6 MB string blob serializes past it.
        var oversized = new BigPayload(new string('x', 6 * 1024 * 1024));

        Action act = () => DatasetHandleCache.Add("big", oversized);

        act.Should().Throw<InvalidOperationException>().WithMessage("*too large to cache*");
    }
}
