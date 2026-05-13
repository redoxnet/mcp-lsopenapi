using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using RedoxNet.LsOpenApi.Core.Auth;
using RedoxNet.LsOpenApi.Core.Catalog;

namespace RedoxNet.LsOpenApi.Core.Http;

/// <summary>
/// HTTP client for LS증권 OpenAPI TR endpoints.
/// </summary>
/// <remarks>
/// Handles bearer-token attachment, per-TR rate limiting, transient-error
/// retries (HTTP 408/429/5xx), continuation headers, and response parsing.
/// Business-level failures (LS returns 200 with a non-success <c>rsp_cd</c>)
/// are surfaced via <see cref="LsTrResponse.IsSuccess"/>, not thrown.
/// </remarks>
public sealed class LsApiClient
{
    static readonly MediaTypeHeaderValue JsonContentType = MediaTypeHeaderValue.Parse("application/json; charset=utf-8");

    readonly HttpClient _httpClient;
    readonly LsApiOptions _options;
    readonly ILsTokenSource _tokenSource;
    readonly TrCatalog _catalog;
    readonly TrRateLimiter _rateLimiter;
    readonly ILogger<LsApiClient> _logger;
    readonly AsyncRetryPolicy<HttpResponseMessage> _retryPolicy;

    /// <summary>
    /// Creates a new LS API client.
    /// </summary>
    /// <param name="httpClient">HTTP client; should have <see cref="HttpClient.BaseAddress"/> unset (the client uses <see cref="LsApiOptions.ResolveBaseUrl"/>).</param>
    /// <param name="options">Endpoint and timeout options.</param>
    /// <param name="tokenSource">Source of bearer tokens (typically <see cref="LsTokenIssuer"/>).</param>
    /// <param name="catalog">Catalog used to look up TR paths. Defaults to <see cref="TrCatalog.Default"/>.</param>
    /// <param name="rateLimiter">Optional per-TR rate limiter. A fresh one is allocated when omitted.</param>
    /// <param name="logger">Optional logger.</param>
    public LsApiClient(
        HttpClient httpClient,
        IOptions<LsApiOptions> options,
        ILsTokenSource tokenSource,
        TrCatalog? catalog = null,
        TrRateLimiter? rateLimiter = null,
        ILogger<LsApiClient>? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _tokenSource = tokenSource ?? throw new ArgumentNullException(nameof(tokenSource));
        _catalog = catalog ?? TrCatalog.Default;
        _rateLimiter = rateLimiter ?? new TrRateLimiter();
        _logger = logger ?? NullLogger<LsApiClient>.Instance;
        _retryPolicy = BuildRetryPolicy(_logger);
    }

    /// <summary>
    /// Invokes an LS TR with the supplied input block.
    /// </summary>
    /// <param name="trCode">TR code, e.g. <c>"t1101"</c>.</param>
    /// <param name="inBlock">Input block fields (without the wrapping key).</param>
    /// <param name="continuationKey">Continuation key from a prior response, when paging.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed response. Check <see cref="LsTrResponse.IsSuccess"/> for business success.</returns>
    /// <exception cref="LsTrException">Thrown for HTTP-level or parsing failures.</exception>
    /// <exception cref="KeyNotFoundException">Thrown when <paramref name="trCode"/> is not in the catalog.</exception>
    public Task<LsTrResponse> CallTrAsync(
        string trCode,
        JsonObject inBlock,
        string? continuationKey = null,
        CancellationToken cancellationToken = default)
    {
        LsTrRequest request = LsTrRequest.FromInBlock(trCode, inBlock, continuationKey);
        return SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Invokes an LS TR with a pre-built request.
    /// </summary>
    /// <param name="request">The TR request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The parsed response.</returns>
    /// <exception cref="LsTrException">Thrown for HTTP-level or parsing failures.</exception>
    public async Task<LsTrResponse> SendAsync(LsTrRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        TrMeta meta = _catalog.Get(request.TrCode);
        await _rateLimiter.WaitAsync(meta.TrCode, meta.RateLimitPerSec, cancellationToken).ConfigureAwait(false);

        LsAccessToken token = await _tokenSource.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        Uri endpoint = new(_options.ResolveBaseUrl(), meta.Path);

        string jsonBody = request.Body.ToJsonString(LsCoreJson.Wire);

        HttpResponseMessage response = await _retryPolicy
            .ExecuteAsync(async ctx =>
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, endpoint);
                message.Headers.Authorization = new AuthenticationHeaderValue(token.TokenType, token.AccessToken);
                message.Headers.TryAddWithoutValidation("tr_cd", meta.TrCode);
                message.Headers.TryAddWithoutValidation("tr_cont", request.ContinuationKey is null ? "N" : "Y");
                if (request.ContinuationKey is not null)
                    message.Headers.TryAddWithoutValidation("tr_cont_key", request.ContinuationKey);

                message.Content = new StringContent(jsonBody, Encoding.UTF8);
                message.Content.Headers.ContentType = JsonContentType;

                return await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
            }, new Context(meta.TrCode))
            .ConfigureAwait(false);

        try
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "TR {TrCode} returned HTTP {Status}: {BodyPreview}",
                    meta.TrCode, (int)response.StatusCode, Truncate(body, 256));
                throw new LsTrException(
                    meta.TrCode,
                    $"TR '{meta.TrCode}' returned HTTP {(int)response.StatusCode}.",
                    statusCode: (int)response.StatusCode,
                    responseBody: body);
            }

            JsonElement root;
            try
            {
                root = JsonDocument.Parse(body).RootElement;
            }
            catch (JsonException ex)
            {
                throw new LsTrException(
                    meta.TrCode,
                    $"TR '{meta.TrCode}' response was not valid JSON.",
                    statusCode: (int)response.StatusCode,
                    responseBody: body,
                    innerException: ex);
            }

            // Continuation: header-based (CSPAQ-style) takes precedence. If no
            // tr_cont header is sent at all, fall back to body-based legacy
            // style — read each named field from <TrCode>OutBlock.
            bool hasCont = false;
            string? contKey = null;
            Dictionary<string, string>? contKeys = null;
            bool headerSeen = response.Headers.TryGetValues("tr_cont", out IEnumerable<string>? contValues);

            if (headerSeen)
            {
                hasCont = string.Equals(contValues!.FirstOrDefault(), "Y", StringComparison.OrdinalIgnoreCase);
                if (response.Headers.TryGetValues("tr_cont_key", out IEnumerable<string>? contKeyValues))
                    contKey = contKeyValues.FirstOrDefault();
            }
            else if (meta.Continuation.Supported
                     && meta.Continuation.KeyFields is { Count: > 0 } keyFields
                     && root.ValueKind == JsonValueKind.Object)
            {
                string headerBlockName = $"{meta.TrCode}OutBlock";
                if (root.TryGetProperty(headerBlockName, out JsonElement headerBlock))
                {
                    foreach (string field in keyFields)
                    {
                        if (!headerBlock.TryGetProperty(field, out JsonElement keyValue))
                            continue;
                        string? bodyKey = keyValue.ValueKind switch
                        {
                            JsonValueKind.String => keyValue.GetString(),
                            JsonValueKind.Number => keyValue.GetRawText(),
                            _ => null,
                        };
                        if (string.IsNullOrWhiteSpace(bodyKey))
                            continue;
                        contKeys ??= new Dictionary<string, string>(StringComparer.Ordinal);
                        contKeys[field] = bodyKey;
                    }
                    if (contKeys is { Count: > 0 })
                        hasCont = true;
                }
            }

            var result = new LsTrResponse(
                meta.TrCode, (int)response.StatusCode, body, root, hasCont, contKey, contKeys);

            if (result.IsSuccess)
            {
                _logger.LogDebug(
                    "TR {TrCode} OK (blocks={Blocks}, continuation={Cont}).",
                    meta.TrCode, string.Join(",", result.OutBlockNames), hasCont);
            }
            else
            {
                _logger.LogInformation(
                    "TR {TrCode} returned business code {RspCode} ({RspMsg}).",
                    meta.TrCode, result.RspCode, result.RspMessage);
            }

            return result;
        }
        finally
        {
            response.Dispose();
        }
    }

    static AsyncRetryPolicy<HttpResponseMessage> BuildRetryPolicy(ILogger logger) =>
        Policy<HttpResponseMessage>
            .Handle<HttpRequestException>()
            .Or<TaskCanceledException>(ex => ex.InnerException is TimeoutException)
            .OrResult(response =>
                response.StatusCode is HttpStatusCode.RequestTimeout
                                     or HttpStatusCode.TooManyRequests
                                     or HttpStatusCode.BadGateway
                                     or HttpStatusCode.ServiceUnavailable
                                     or HttpStatusCode.GatewayTimeout)
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt => TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1)),
                onRetry: (outcome, delay, attempt, context) =>
                {
                    string trCode = context.OperationKey ?? "?";
                    if (outcome.Exception is not null)
                    {
                        logger.LogWarning(
                            outcome.Exception,
                            "TR {TrCode} retry {Attempt} after exception; waiting {Delay}ms.",
                            trCode, attempt, delay.TotalMilliseconds);
                    }
                    else
                    {
                        logger.LogWarning(
                            "TR {TrCode} retry {Attempt} after HTTP {Status}; waiting {Delay}ms.",
                            trCode, attempt, (int)outcome.Result.StatusCode, delay.TotalMilliseconds);
                    }
                });

    static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
