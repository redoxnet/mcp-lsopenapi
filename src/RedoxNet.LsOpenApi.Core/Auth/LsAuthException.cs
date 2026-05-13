namespace RedoxNet.LsOpenApi.Core.Auth;

/// <summary>
/// Thrown when authentication with LS증권 OpenAPI fails.
/// </summary>
/// <remarks>
/// Common causes: missing credentials, invalid app key/secret, network error
/// against <c>/oauth2/token</c>, or a response payload that cannot be parsed.
/// </remarks>
public sealed class LsAuthException : Exception
{
    /// <summary>
    /// Optional HTTP status code returned by the token endpoint.
    /// </summary>
    public int? StatusCode { get; }

    /// <summary>
    /// Optional response body from the token endpoint.
    /// </summary>
    public string? ResponseBody { get; }

    /// <summary>
    /// Creates a new <see cref="LsAuthException"/>.
    /// </summary>
    /// <param name="message">Human-readable description of the failure.</param>
    /// <param name="statusCode">HTTP status code from the token endpoint, if available.</param>
    /// <param name="responseBody">Raw response body, if available. Should not contain secrets.</param>
    /// <param name="innerException">Optional inner exception (e.g. <see cref="HttpRequestException"/>).</param>
    public LsAuthException(
        string message,
        int? statusCode = null,
        string? responseBody = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }
}
