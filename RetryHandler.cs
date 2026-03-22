using System.Net;

namespace Holomove;

public class RetryHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
{
    public int MaxRetries { get; init; } = 3;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        HttpResponseMessage? response = null;

        for (var i = 0; i <= MaxRetries; i++)
        {
            try
            {
                var clonedRequest = await CloneRequest(request);
                response = await base.SendAsync(clonedRequest, cancellationToken);

                if (response.IsSuccessStatusCode
                    || (int)response.StatusCode < 500 && response.StatusCode != HttpStatusCode.TooManyRequests
                    || i >= MaxRetries) return response;

                var delay = response.StatusCode is HttpStatusCode.GatewayTimeout or HttpStatusCode.ServiceUnavailable
                    ? (i + 1) * 10000   // 10/20/30s for server overload
                    : (i + 1) * 2000;
                await Task.Delay(delay, cancellationToken);
            }
            catch (HttpRequestException) when (i < MaxRetries)
            {
                var delay = (i + 1) * 2000;
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
