using Xunit;

namespace RedoxNet.Mcp.LsOpenApi.Tests.Tools;

/// <summary>
/// xUnit collection that serializes every test class touching <c>DatasetHandleCache</c>.
/// </summary>
/// <remarks>
/// The cache is a process-wide static singleton with a 16-entry LRU. With xUnit's
/// default per-class parallelism, multiple chart-tool test classes can each register
/// their dataset_id within the same process and evict each other's entries before the
/// originating test's follow-up call (ls_reframe_chart / ls_add_indicator) runs.
/// On fast CI runners this surfaced as flaky "Unknown or expired dataset_id" or
/// "period_type is required for a multi-timeframe dataset_id" errors.
///
/// Pinning all cache-touching classes to a single non-parallel collection keeps the
/// production cache size unchanged while making the suite deterministic.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ChartDatasetCacheCollection
{
    public const string Name = "ChartDatasetHandleCache";
}
