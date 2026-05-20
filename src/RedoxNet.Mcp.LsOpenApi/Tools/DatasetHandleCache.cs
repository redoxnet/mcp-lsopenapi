using System.Security.Cryptography;
using System.Text.Json;
using RedoxNet.LsOpenApi.Core.Indicators;
using RedoxNet.LsOpenApi.Core.Models;

namespace RedoxNet.Mcp.LsOpenApi.Tools;

/// <summary>
/// Bounded process-local cache for datasets referenced by opaque handles.
/// </summary>
/// <remarks>
/// v0.10 (SPEC-v0.10 §5.2): the cache is <em>kind-tagged</em> — any payload
/// type can be stored. Each entry carries a human-readable <c>kind</c> tag and
/// is resolved back by runtime type via <see cref="TryGet{T}"/>. Originally
/// chart-only; it now also backs <c>ls_get_index_history</c> export datasets.
/// The shared infrastructure (handle generation, count-LRU, 5MB cap, lock) is
/// unchanged — only the payload became polymorphic.
/// <para>
/// This is intentionally small; handles are short-lived affordances for
/// follow-up tool calls, not durable storage. In the current stdio server this
/// cache is process-wide, not a true per-chat-session store.
/// </para>
/// </remarks>
internal static class DatasetHandleCache
{
    const int MaxDatasets = 16;
    const int MaxBytesPerDataset = 5 * 1024 * 1024;

    static readonly object Gate = new();
    static readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);

    /// <summary>
    /// Adds a <paramref name="kind"/>-tagged payload and returns its opaque handle.
    /// </summary>
    /// <param name="kind">Human-readable payload tag, e.g. "chart" or "index_history".</param>
    /// <param name="payload">The dataset payload; resolved back by type via <see cref="TryGet{T}"/>.</param>
    public static string Add(string kind, object payload)
    {
        string id = "ds_" + RandomNumberGenerator.GetHexString(8).ToLowerInvariant();
        int bytes = Measure(payload);

        lock (Gate)
        {
            Entries[id] = new Entry(kind, payload, DateTimeOffset.UtcNow, bytes);
            EvictIfNeeded();
        }
        return id;
    }

    /// <summary>
    /// Tries to resolve a payload by handle as type <typeparamref name="T"/>,
    /// refreshing its LRU timestamp. Returns false when the handle is unknown
    /// or the stored payload is not a <typeparamref name="T"/> (kind mismatch).
    /// </summary>
    public static bool TryGet<T>(string id, out T? payload) where T : class
    {
        lock (Gate)
        {
            if (Entries.TryGetValue(id, out Entry? entry) && entry.Payload is T typed)
            {
                Entries[id] = entry with { LastAccessUtc = DateTimeOffset.UtcNow };
                payload = typed;
                return true;
            }

            payload = null;
            return false;
        }
    }

    /// <summary>
    /// Replaces the payload under an existing handle, keeping its original
    /// <c>kind</c> tag, and refreshes the LRU timestamp. Returns false when the
    /// handle is unknown.
    /// </summary>
    public static bool TryUpdate(string id, object payload)
    {
        int bytes = Measure(payload);

        lock (Gate)
        {
            if (!Entries.TryGetValue(id, out Entry? entry))
                return false;

            Entries[id] = new Entry(entry.Kind, payload, DateTimeOffset.UtcNow, bytes);
            EvictIfNeeded();
            return true;
        }
    }

    /// <summary>Serializes the payload to size it, rejecting datasets over the per-entry cap.</summary>
    static int Measure(object payload)
    {
        int bytes = JsonSerializer.SerializeToUtf8Bytes(payload, McpJson.Tool).Length;
        if (bytes > MaxBytesPerDataset)
            throw new InvalidOperationException($"Dataset is too large to cache ({bytes} bytes).");
        return bytes;
    }

    static void EvictIfNeeded()
    {
        while (Entries.Count > MaxDatasets)
        {
            string oldest = Entries
                .OrderBy(kv => kv.Value.LastAccessUtc)
                .First()
                .Key;
            Entries.Remove(oldest);
        }
    }

    sealed record Entry(string Kind, object Payload, DateTimeOffset LastAccessUtc, int ByteSize);
}

/// <summary>
/// Internal dataset payload held behind a <c>dataset_id</c>.
/// </summary>
internal sealed record ChartDataset(
    string Shcode,
    string? Name,
    IReadOnlyList<string> PeriodTypes,
    IReadOnlyList<ChartDatasetFrame> Frames,
    DateTimeOffset CreatedAtUtc);

/// <summary>
/// One cached chart frame.
/// </summary>
internal sealed record ChartDatasetFrame(
    string PeriodType,
    string TrCode,
    int MinuteUnit,
    string? SourceFrom,
    string? SourceTo,
    IReadOnlyList<Candle> Candles,
    IReadOnlyDictionary<string, IReadOnlyList<double?>> Indicators,
    IReadOnlyList<IndicatorSpec> IndicatorSpecs,
    ChartContext Context,
    AnalyticalSummary Summary);
