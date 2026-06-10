using System.Net;
using System.Net.Http.Headers;

namespace Holomove;

public class RetryHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// Marks a request as non-idempotent (e.g. media upload, post create): a timeout or
    /// 5xx after the server already did the work would duplicate it on retry. Such
    /// requests are sent once; only the 401 re-auth retry remains (the server rejects
    /// those before doing any work). Callers recover by checking whether the write
    /// actually landed.
    /// </summary>
    public static readonly HttpRequestOptionsKey<bool> NoRetry = new("Holomove.NoRetry");

    /// <summary>
    /// Invoked on 401 Unauthorized. Returns a fresh bearer token, or null to skip.
    /// Called at most once per request (across all retries).
    /// </summary>
    public Func<Task<string?>>? ReauthAsync { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;
        var currentToken = request.Headers.Authorization?.Parameter;
        var didReauth = false;
        request.Options.TryGetValue(NoRetry, out var noRetry);

        for (var i = 0; i <= MaxRetries; i++)
        {
            try
            {
                var clonedRequest = await CloneRequest(request);
                if (!string.IsNullOrEmpty(currentToken))
                    clonedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", currentToken);

                response = await base.SendAsync(clonedRequest, cancellationToken);

                // Expired JWT? Re-auth once and retry.
                if (response.StatusCode == HttpStatusCode.Unauthorized &&
                    !didReauth && ReauthAsync != null && !string.IsNullOrEmpty(currentToken) &&
                    i < MaxRetries)
                {
                    didReauth = true;
                    var newToken = await ReauthAsync();
                    if (!string.IsNullOrEmpty(newToken))
                    {
                        currentToken = newToken;
                        response.Dispose();
                        continue;
                    }
                }

                if (response.IsSuccessStatusCode
                    || (int)response.StatusCode < 500 && response.StatusCode != HttpStatusCode.TooManyRequests
                    || i >= MaxRetries
                    || noRetry) return response;

                var delay = response.StatusCode is HttpStatusCode.GatewayTimeout or HttpStatusCode.ServiceUnavailable
                    ? (i + 1) * 10000   // 10/20/30s for server overload
                    : (i + 1) * 2000;
                await Task.Delay(delay, cancellationToken);
            }
            catch (HttpRequestException) when (i < MaxRetries && !noRetry)
            {
                var delay = (i + 1) * 2000;
                await Task.Delay(delay, cancellationToken);
            }
            // TaskCanceledException without caller cancellation = HttpClient.Timeout fired.
            // Treat as transient (server hung) and retry. Caller cancellation re-throws.
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && i < MaxRetries && !noRetry)
            {
                var delay = (i + 1) * 5000;
                await Task.Delay(delay, cancellationToken);
            }
        }

        return response ?? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
    }

    private static async Task<HttpRequestMessage> CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);

        if (request.Content != null)
        {
            var content = await request.Content.ReadAsByteArrayAsync();
            clone.Content = new ByteArrayContent(content);

            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }
}
