using System.Net;
using System.Net.Http;

namespace RedoxNet.LsOpenApi.Core.Tests.TestSupport;

/// <summary>
/// Test-only <see cref="HttpMessageHandler"/> that returns canned responses
/// and records every request the SUT sends.
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

    public List<HttpRequestMessage> Requests { get; } = new();

    public StubHttpMessageHandler(HttpStatusCode status, string responseBody, string mediaType = "application/json")
        : this((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(responseBody, System.Text.Encoding.UTF8, mediaType),
        }))
    {
    }

    public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        _responder = responder ?? throw new ArgumentNullException(nameof(responder));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(await CloneAsync(request, cancellationToken).ConfigureAwait(false));
        return await _responder(request, cancellationToken).ConfigureAwait(false);
    }

    static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage source, CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri);
        foreach (var header in source.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (source.Content is not null)
        {
            string body = await source.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var content = new StringContent(body);
            content.Headers.Clear();
            foreach (var header in source.Content.Headers)
                content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            clone.Content = content;
        }
        return clone;
    }
}
