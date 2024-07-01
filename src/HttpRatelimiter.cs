using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace OoLunar.GitHubForumWebhookWorker
{
    public sealed class HttpRatelimiter : DelegatingHandler
    {
        private readonly ILogger<HttpRatelimiter> _logger;

        public HttpRatelimiter(ILogger<HttpRatelimiter> logger) : base(new HttpClientHandler()) => _logger = logger;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage responseMessage;
            do
            {
                responseMessage = await base.SendAsync(request, cancellationToken);
                if (responseMessage.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (!responseMessage.Headers.NonValidated.TryGetValues("X-RateLimit-Reset", out HeaderStringValues values))
                    {
                        _logger.LogWarning("Rate limited by {Host} API, but no X-RateLimit-Reset header was found. Waiting 15 seconds.", request.RequestUri?.Host);
                        await Task.Delay(15000, cancellationToken);
                        continue;
                    }

                    string? resetString = values.FirstOrDefault();
                    if (string.IsNullOrWhiteSpace(resetString))
                    {
                        _logger.LogWarning("Rate limited by {Host} API, but the X-RateLimit-Reset header was empty. Waiting 15 seconds.", request.RequestUri?.Host);
                        await Task.Delay(15000, cancellationToken);
                        continue;
                    }

                    if (!long.TryParse(resetString.Split('.')[0], out long unixTimestampSeconds))
                    {
                        _logger.LogWarning("Rate limited by {Host} API, but the X-RateLimit-Reset header was not a valid Unix timestamp. Waiting 15 seconds.", request.RequestUri?.Host);
                        await Task.Delay(15000, cancellationToken);
                        continue;
                    }

                    TimeSpan delay = DateTimeOffset.FromUnixTimeSeconds(unixTimestampSeconds) - DateTimeOffset.UtcNow;
                    _logger.LogDebug("Rate limited by {Host} API, waiting {TimeSpan} before retrying...", request.RequestUri?.Host, delay);
                    await Task.Delay(delay, cancellationToken);
                }
            } while (responseMessage.StatusCode == HttpStatusCode.TooManyRequests);

            return responseMessage;
        }
    }
}
