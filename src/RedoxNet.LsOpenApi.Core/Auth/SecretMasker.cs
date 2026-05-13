namespace RedoxNet.LsOpenApi.Core.Auth;

/// <summary>
/// Renders secrets as <c>****XXXX</c> (last four characters visible) so they
/// can appear in logs without leaking the full value.
/// </summary>
/// <remarks>
/// Always pass through this helper before writing an app key, app secret,
/// access token, or any other bearer credential to a log sink.
/// </remarks>
public static class SecretMasker
{
    /// <summary>
    /// Default placeholder rendered when the input is null, empty, or shorter
    /// than the minimum length needed to safely reveal a suffix.
    /// </summary>
    public const string EmptyPlaceholder = "(empty)";

    /// <summary>
    /// Masks a secret by replacing the leading characters with <c>"****"</c>
    /// and revealing only the trailing four characters.
    /// </summary>
    /// <param name="secret">Secret value. May be <see langword="null"/>.</param>
    /// <returns>The masked rendering, e.g. <c>"****abcd"</c>.</returns>
    public static string Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
            return EmptyPlaceholder;

        if (secret.Length <= 4)
            return "****";

        return "****" + secret[^4..];
    }

    /// <summary>
    /// Masks a secret while preserving a short prefix for diagnostics
    /// (e.g. distinguishing two appkeys at a glance).
    /// </summary>
    /// <param name="secret">Secret value. May be <see langword="null"/>.</param>
    /// <param name="prefixLength">Number of leading characters to retain.</param>
    /// <returns>The masked rendering, e.g. <c>"abc****wxyz"</c>.</returns>
    public static string MaskWithPrefix(string? secret, int prefixLength = 4)
    {
        if (string.IsNullOrEmpty(secret))
            return EmptyPlaceholder;

        if (secret.Length <= prefixLength + 4)
            return "****";

        return secret[..prefixLength] + "****" + secret[^4..];
    }
}
