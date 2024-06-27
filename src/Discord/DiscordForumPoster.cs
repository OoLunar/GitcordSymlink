using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HyperSharp.Protocol;
using HyperSharp.Responders;
using HyperSharp.Results;
using OoLunar.GitHubForumWebhookWorker.GitHub;

namespace OoLunar.GitHubForumWebhookWorker.Discord
{
    public sealed class DiscordForumPoster : IValueTaskResponder<HyperContext, HyperStatus>
    {
        public static Type[] Needs => [typeof(GitHubVerifier)];
        private static readonly IReadOnlyList<string> _forwardedHeaders = [
            "User-Agent",
            "X-GitHub-Event",
            "X-GitHub-Delivery",
            "X-GitHub-Hook-Id",
            "X-GitHub-Hook-Installation-Target-Id",
            "X-GitHub-Hook-Installation-Target-Type"
        ];

        private readonly DiscordWebhookManager _webhookManager;
        private readonly HttpClient _httpClient;

        public DiscordForumPoster(DiscordWebhookManager webhookManager, HttpClient httpClient)
        {
            _webhookManager = webhookManager;
            _httpClient = httpClient;
        }

        public async ValueTask<Result<HyperStatus>> RespondAsync(HyperContext context, CancellationToken cancellationToken = default)
        {
            string account = context.Metadata["account"].ToLowerInvariant();
            string? webhookUrl = await _webhookManager.GetWebhookUrlAsync(account, cancellationToken);
            if (webhookUrl is null)
            {
                return HyperStatus.NotFound("Webhook not found.");
            }

            // Add the thread id, if present
            ulong? postId = await _webhookManager.GetPostIdAsync(account, context.Metadata["repository"].ToLowerInvariant(), cancellationToken);
            if (postId is not null)
            {
                webhookUrl = $"{webhookUrl}?thread_id={postId}";
            }

            // Try formatting the JSON
            if (!context.Metadata.TryGetValue("body", out string? body))
            {
                return HyperStatus.BadRequest("Missing body.");
            }

            // Forward the payload to Discord
            using HttpRequestMessage request = new(HttpMethod.Post, webhookUrl)
            {
                Content = JsonContent.Create(
                    inputValue: JsonSerializer.Deserialize<JsonElement>(body),
                    options: new JsonSerializerOptions(JsonSerializerDefaults.Web)
                )
            };

            // Forward the required headers to Discord
            foreach (string key in _forwardedHeaders)
            {
                if (context.Headers.TryGetValues(key, out List<string>? values))
                {
                    request.Headers.TryAddWithoutValidation(key, values);
                }
            }

            // Send the request to Discord
            HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

            // Forward the response to GitHub
            return new HyperStatus(response.StatusCode, new(response.Headers), await response.Content.ReadAsStringAsync(cancellationToken));
        }
    }
}
