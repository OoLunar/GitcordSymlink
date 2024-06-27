using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using HyperSharp.Protocol;
using HyperSharp.Responders;
using HyperSharp.Results;
using OoLunar.GitHubForumWebhookWorker.Configuration;

namespace OoLunar.GitHubForumWebhookWorker
{
    public sealed class DiscordForumPoster : IValueTaskResponder<HyperContext, HyperStatus>
    {
        public static Type[] Needs => [typeof(GitHubVerifier)];

        private readonly DiscordConfiguration _discordConfiguration;
        private readonly DiscordWebhookManager _webhookManager;
        private readonly HttpClient _httpClient;

        public DiscordForumPoster(GitHubForumWebhookWorkerConfiguration configuration, DiscordWebhookManager webhookManager, HttpClient httpClient)
        {
            _discordConfiguration = configuration.Discord;
            _webhookManager = webhookManager;
            _httpClient = httpClient;
        }

        public async ValueTask<Result<HyperStatus>> RespondAsync(HyperContext context, CancellationToken cancellationToken = default)
        {
            string[] segments = context.Route.AbsolutePath.Split('/');
            string accountName = segments[2].ToLowerInvariant();
            string repositoryName = segments[3].ToLowerInvariant();

            string? webhookUrl = await _webhookManager.GetWebhookUrlAsync(accountName, cancellationToken);
            if (webhookUrl is null)
            {
                return HyperStatus.NotFound("Webhook not found.");
            }

            ulong? postId = await _webhookManager.GetPostIdAsync(accountName, repositoryName, cancellationToken);
            if (postId is null)
            {
                return HyperStatus.NotFound("Post not found.");
            }

            // Forward the payload to Discord
            using HttpRequestMessage request = new(HttpMethod.Post, $"{webhookUrl}?thread_id={postId}")
            {
                Content = new StringContent(context.Metadata["body"], MediaTypeHeaderValue.Parse("application/json")),
            };

            foreach ((string key, byte[] value) in context.Headers)
            {
                if (key is "Content-Length" or "Content-Type" or "Host")
                {
                    continue;
                }

                request.Headers.Add(key, Encoding.UTF8.GetString(value));
            }

            HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            HyperHeaderCollection responseHeaders = [];
            foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers.OrderBy(x => x.Key))
            {
                foreach (string value in header.Value)
                {
                    responseHeaders.Add(header.Key, value);
                }
            }

            return new HyperStatus(response.StatusCode, responseHeaders, await response.Content.ReadAsStringAsync(cancellationToken));
        }
    }
}
