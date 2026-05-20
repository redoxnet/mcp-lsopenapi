using FluentAssertions;
using Microsoft.ML.Tokenizers;

namespace RedoxNet.Mcp.LsOpenApi.Tests.TestSupport;

/// <summary>
/// Token estimator for LS OpenAPI response budget assertions (SPEC-v0.9 §4.1).
/// Uses the cl100k_base tokenizer (Microsoft.ML.Tokenizers) for ~2% accuracy
/// on mixed Korean/English JSON. A char-count fallback is retained for offline
/// runs where the tokenizer model file is unavailable.
/// </summary>
internal static class TokenEstimator
{
    private static readonly Tokenizer _tokenizer = TiktokenTokenizer.CreateForModel("gpt-4");

    /// <summary>Exact token count via cl100k_base. Preferred for budget assertions.</summary>
    public static int Count(string json) => _tokenizer.CountTokens(json);

    /// <summary>
    /// Char-count fallback. ±20% error on Korean-heavy payloads.
    /// Use only when <see cref="Count"/> is unavailable.
    /// </summary>
    public static int Estimate(string json) => (int)Math.Ceiling(json.Length / 3.5);
}

/// <summary>
/// Token-budget assertion over a serialized tool response (SPEC-v0.9 §2.4 / §4.1).
/// </summary>
internal static class TokenBudgetAssertions
{
    /// <summary>
    /// Asserts the response fits within <paramref name="maxTokens"/> measured by
    /// cl100k_base. The failure message reports the measured count.
    /// </summary>
    public static void ShouldFitTokenBudget(this string json, int maxTokens)
    {
        int actual = TokenEstimator.Count(json);
        actual.Should().BeLessThan(
            maxTokens,
            "the response should fit the {0}-token budget (cl100k_base measured {1})",
            maxTokens,
            actual);
    }
}

