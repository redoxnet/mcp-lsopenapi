namespace RedoxNet.LsOpenApi.Core.Http;

/// <summary>
/// Thrown when a TR call against LS증권 OpenAPI fails at the transport or
/// protocol layer (HTTP error, malformed JSON, missing block, etc.).
/// </summary>
/// <remarks>
/// Business-level errors (LS returns 200 with a non-success <c>rsp_cd</c>) are
/// surfaced via <see cref="LsTrResponse.IsSuccess"/> instead of throwing.
/// </remarks>
public sealed class LsTrException : Exception
{
    /// <summary>TR code that failed.</summary>
    public string TrCode { get; }

    /// <summary>HTTP status code returned by LS, if any.</summary>
    public int? StatusCode { get; }

    /// <summary>Raw response body, if any. Should not contain secrets.</summary>
    public string? ResponseBody { get; }

    /// <summary>
    /// Creates a new <see cref="LsTrException"/>.
    /// </summary>
    /// <param name="trCode">TR code that failed.</param>
    /// <param name="message">Human-readable description.</param>
    /// <param name="statusCode">HTTP status, when available.</param>
    /// <param name="responseBody">Response body, when available.</param>
    /// <param name="innerException">Optional inner exception.</param>
    public LsTrException(
        string trCode,
        string message,
        int? statusCode = null,
        string? responseBody = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        TrCode = trCode;
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}
