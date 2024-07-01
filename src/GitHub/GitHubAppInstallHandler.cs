using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HyperSharp.Protocol;
using HyperSharp.Responders;
using HyperSharp.Results;
using Microsoft.Extensions.Logging;
using Octokit.Webhooks.Events;
using Octokit.Webhooks.Models;
using OoLunar.GitHubForumWebhookWorker.Configuration;
using OoLunar.GitHubForumWebhookWorker.Discord;
using Remora.Discord.API.Objects;
using RepositoryInstallation = Octokit.Webhooks.Models.InstallationEvent.Repository;

namespace OoLunar.GitHubForumWebhookWorker.GitHub
{
    public sealed class GitHubAppInstallHandler : IValueTaskResponder<HyperContext, HyperStatus>
    {
        public static Type[] Needs => [typeof(GitHubVerifier)];

        private readonly ILogger<GitHubAppInstallHandler> _logger;
        private readonly GitHubConfiguration _gitHubConfiguration;
        private readonly DatabaseManager _discordWebhookManager;
        private readonly DiscordApiRoutes _discordApiRoutes;
        private readonly HttpClient _httpClient;

        public GitHubAppInstallHandler(ILogger<GitHubAppInstallHandler> logger, GitHubForumWebhookWorkerConfiguration configuration, DatabaseManager discordWebhookManager, DiscordApiRoutes discordApiRoutes, HttpClient httpClient)
        {
            _logger = logger;
            _gitHubConfiguration = configuration.GitHub;
            _discordWebhookManager = discordWebhookManager;
            _discordApiRoutes = discordApiRoutes;
            _httpClient = httpClient;
        }

        [SuppressMessage("Roslyn", "IDE0046", Justification = "Ternary rabbit hole.")]
        public async ValueTask<Result<HyperStatus>> RespondAsync(HyperContext context, CancellationToken cancellationToken = default)
        {
            if (!context.Headers.TryGetValue("X-GitHub-Event", out string? eventType))
            {
                return HyperStatus.BadRequest(new Error("Missing X-GitHub-Event header."));
            }
            else if (eventType == "ping")
            {
                return HyperStatus.OK();
            }
            else if (eventType != "installation")
            {
                return HyperStatus.NotImplemented();
            }
            else if (JsonSerializer.Deserialize<InstallationEvent>(context.Metadata["body"]) is not InstallationEvent installation)
            {
                return HyperStatus.BadRequest(new Error("Failed to deserialize installation payload."));
            }
            else
            {
                return installation.Action switch
                {
                    "created" => await InstallAsync(installation, cancellationToken),
                    //"deleted" => await UninstallAsync(context, installation, cancellationToken),
                    _ => HyperStatus.NotImplemented(),
                };
            }
        }

        private async ValueTask<HyperStatus> InstallAsync(InstallationEvent installation, CancellationToken cancellationToken)
        {
            (ulong channelId, string? webhookUrl) = await _discordWebhookManager.GetAccountAsync(installation.Installation.Account.Login, cancellationToken);
            if (webhookUrl is null)
            {
                return HyperStatus.BadRequest(new Error("Please sync your account with the Discord bot before installing the GitHub app."));
            }

            // Get the installation access token
            (string accessToken, DateTimeOffset expiresAt) = await GetAccessTokenAsync(installation);

            _logger.LogInformation("Installing GitHub app for {Account}, 0/{RepositoryCount} repositories done...", installation.Installation.Account.Login, installation.Repositories.Count());

            // Install the webhooks onto all the repositories
            foreach (RepositoryInstallation repository in installation.Repositories.OrderBy(repository => repository.Id))
            {
                // Grab the repository and skip it if it's archived, private or a fork
                if (repository.Private)
                {
                    _logger.LogInformation("Skipping private repository {Repository}", repository.FullName);
                    continue;
                }

                Repository? repositoryDetails = await GetRepositoryAsync(accessToken, repository.FullName);
                if (repositoryDetails is null || repositoryDetails.Archived || repositoryDetails.Fork)
                {
                    _logger.LogInformation("Skipping archived/forked repository {Repository}", repository.FullName);
                    continue;
                }

                _logger.LogInformation("Installing webhook for {Repository}", repository.FullName);

                // See if the repository already has a thread
                ulong? postId = await _discordWebhookManager.GetPostIdAsync(installation.Installation.Account.Login, repository.Name, cancellationToken);

                // Verify the post still exists
                if (postId is not null)
                {
                    DiscordApiResult<Channel> existingThread = await _discordApiRoutes.GetChannelAsync(postId.Value);
                    if (existingThread.Value is null)
                    {
                        _logger.LogError("Failed to get thread channel: {StatusCode} {Error}", existingThread.StatusCode, existingThread.Error);
                        return HyperStatus.InternalServerError(new Error("Failed to get channel."));
                    }
                    else if (existingThread.Value is not null)
                    {
                        // The post exists
                        _logger.LogInformation("Thread channel already exists for {Repository}, skipping...", repository.FullName);
                        continue;
                    }
                }

                // Create a new post
                DiscordApiResult<Channel> newThread = await _discordApiRoutes.CreateThreadChannelAsync(channelId, repository.FullName, $"https://github.com/{repository.FullName}");
                if (newThread.Value is null)
                {
                    _logger.LogError("Failed to create thread channel: {StatusCode} {Error}", newThread.StatusCode, newThread.Error);
                    return HyperStatus.InternalServerError(new Error("Failed to create channel."));
                }

                // Store the repository and thread ID in the database
                await _discordWebhookManager.CreateNewRepositoryAsync(installation.Installation.Account.Login, repository.Name, newThread.Value.ID.Value, cancellationToken);

                // Check to see if the access token expired
                if (expiresAt < DateTimeOffset.UtcNow)
                {
                    // Refresh the access token
                    (accessToken, expiresAt) = await GetAccessTokenAsync(installation);
                }

                // Add the webhook to the repository
                await InstallWebhookAsync(accessToken, repository.FullName, $"{webhookUrl}?thread_id={newThread.Value.ID}");
                _logger.LogInformation("Installed webhook for {Repository}", repository.FullName);
            }

            _logger.LogInformation("Installed GitHub app for {Account}, {RepositoryCount}/{RepositoryCount} repositories done.", installation.Installation.Account.Login, installation.Repositories.Count(), installation.Repositories.Count());

            return HyperStatus.OK();
        }

        private async ValueTask<Repository?> GetRepositoryAsync(string accessToken, string repositoryFullName)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, $"https://api.github.com/repos/{repositoryFullName}");
            request.Headers.Add("Accept", "application/vnd.github+json");
            request.Headers.Add("Authorization", $"Bearer {accessToken}");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using HttpResponseMessage response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to get repository {Repository}: {StatusCode} {Error}", repositoryFullName, response.StatusCode, await response.Content.ReadAsStringAsync());
            }

            Repository? repository = await response.Content.ReadFromJsonAsync<Repository>();
            if (repository is null)
            {
                _logger.LogError("Failed to get repository {Repository}: {StatusCode} {Error}", repositoryFullName, response.StatusCode, await response.Content.ReadAsStringAsync());
            }

            return repository;
        }

        private async ValueTask InstallWebhookAsync(string accessToken, string repositoryFullName, string webhook)
        {
            using HttpRequestMessage createWebhookRequest = new(HttpMethod.Post, $"https://api.github.com/repos/{repositoryFullName}/hooks");
            createWebhookRequest.Headers.Add("Accept", "application/vnd.github+json");
            createWebhookRequest.Headers.Add("Authorization", $"Bearer {accessToken}");
            createWebhookRequest.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            createWebhookRequest.Content = JsonContent.Create(new
            {
                name = "web",
                active = true,
                events = new string[] { "*" },
                config = new
                {
                    url = webhook,
                    content_type = "json"
                }
            });

            using HttpResponseMessage createWebhookResponse = await _httpClient.SendAsync(createWebhookRequest);
            if (!createWebhookResponse.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to install webhook to {Repository}: {StatusCode} {Error}", repositoryFullName, createWebhookResponse.StatusCode, await createWebhookResponse.Content.ReadAsStringAsync());
                return;
            }

            JsonElement json = await createWebhookResponse.Content.ReadFromJsonAsync<JsonElement>();
            if (!json.TryGetProperty("id", out JsonElement hookIdJson) || !hookIdJson.TryGetUInt64(out ulong hookId))
            {
                _logger.LogError("Failed to get hook ID for {Repository}: {StatusCode} {Error}", repositoryFullName, createWebhookResponse.StatusCode, json);
                return;
            }

            using HttpRequestMessage testWebhookRequest = new(HttpMethod.Post, $"https://api.github.com/repos/{repositoryFullName}/hooks/{hookId}/tests");
            testWebhookRequest.Headers.Add("Accept", "application/vnd.github+json");
            testWebhookRequest.Headers.Add("Authorization", $"Bearer {accessToken}");
            testWebhookRequest.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using HttpResponseMessage testWebhookResponse = await _httpClient.SendAsync(testWebhookRequest);
            if (!testWebhookResponse.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to test webhook for {Repository}: {StatusCode} {Error}", repositoryFullName, testWebhookResponse.StatusCode, await testWebhookResponse.Content.ReadAsStringAsync());
            }
        }

        [SuppressMessage("Roslyn", "IDE0046", Justification = "Terinary rabbit hole.")]
        private async ValueTask<(string, DateTimeOffset)> GetAccessTokenAsync(InstallationEvent installationEvent)
        {
            // Generate a JWT
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string jwt = GenerateJWT(now);

            using HttpRequestMessage request = new(HttpMethod.Post, $"https://api.github.com/app/installations/{installationEvent.Installation.Id}/access_tokens");
            request.Headers.Add("Accept", "application/vnd.github+json");
            request.Headers.Add("Authorization", $"Bearer {jwt}");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            request.Content = JsonContent.Create(new
            {
                repository_ids = installationEvent.Repositories.Select(repository => repository.Id),
                permissions = new
                {
                    repository_hooks = "write",
                    organization_hooks = "write"
                }
            });

            using HttpResponseMessage response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Failed to get access token: {response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            }

            JsonElement json = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (!json.TryGetProperty("token", out JsonElement tokenJson))
            {
                throw new InvalidOperationException($"Failed to get access token, missing 'token' element: {json}");
            }
            else if (tokenJson.GetString() is not string token || string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException($"Failed to get access token, 'token' element is null or whitespace: {json}");
            }
            else if (!json.TryGetProperty("expires_at", out JsonElement expiresAtJson))
            {
                throw new InvalidOperationException($"Failed to get access token, missing 'expires_at' element: {json}");
            }
            else if (!expiresAtJson.TryGetDateTimeOffset(out DateTimeOffset expiresAt))
            {
                throw new InvalidOperationException($"Failed to get access token, 'expires_at' element is not a valid date: {json}");
            }
            else
            {
                return (token, expiresAt);
            }
        }

        private string GenerateJWT(DateTimeOffset now)
        {
            string header = ConvertToBase64Url(JsonSerializer.SerializeToUtf8Bytes(new
            {
                alg = "RS256",
                typ = "JWT"
            }));

            long nowUnixTimestamp = now.ToUnixTimeSeconds();
            string payload = ConvertToBase64Url(JsonSerializer.SerializeToUtf8Bytes(new
            {
                iat = nowUnixTimestamp - 10,
                exp = nowUnixTimestamp + 600,
                iss = _gitHubConfiguration.ClientId
            }));

            using RSA rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(_gitHubConfiguration.PrivateKeyPath));

            string unsignedJwt = $"{header}.{payload}";
            byte[] signatureBytes = rsa.SignData(Encoding.UTF8.GetBytes(unsignedJwt), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            string signature = ConvertToBase64Url(signatureBytes);

            return $"{unsignedJwt}.{signature}";
        }

        private static string ConvertToBase64Url(byte[] input) => Convert.ToBase64String(input).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
