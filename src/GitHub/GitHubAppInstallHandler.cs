using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.Exceptions;
using HyperSharp.Protocol;
using HyperSharp.Responders;
using HyperSharp.Results;
using Microsoft.Extensions.Logging;
using Octokit.Webhooks.Events;
using Octokit.Webhooks.Models;
using Octokit.Webhooks.Models.PingEvent;
using RepositoryInstallation = Octokit.Webhooks.Models.InstallationEvent.Repository;

namespace OoLunar.GitcordSymlink.GitHub
{
    public sealed class GitHubAppInstallHandler : IValueTaskResponder<HyperContext, HyperStatus>
    {
        public static Type[] Needs => [typeof(GitHubVerifier)];

        private readonly ILogger<GitHubAppInstallHandler> _logger;
        private readonly DatabaseManager _discordWebhookManager;
        private readonly GitHubApiRoutes _gitHubApiRoutes;
        private readonly DiscordClient _discordClient;

        public GitHubAppInstallHandler(ILogger<GitHubAppInstallHandler> logger, DatabaseManager discordWebhookManager, GitHubApiRoutes gitHubApiRoutes, DiscordClient discordClient)
        {
            _logger = logger;
            _discordWebhookManager = discordWebhookManager;
            _gitHubApiRoutes = gitHubApiRoutes;
            _discordClient = discordClient;
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
            (ulong channelId, string? webhookUrl) = await _discordWebhookManager.GetAccountAsync(installation.Installation.Account.Login.ToLowerInvariant(), cancellationToken);
            if (webhookUrl is null)
            {
                return HyperStatus.BadRequest(new Error("Please sync your account with the Discord bot before installing the GitHub app."));
            }

            // Get the installation access token
            (string accessToken, DateTimeOffset expiresAt) = await _gitHubApiRoutes.GetAccessTokenAsync(installation.Installation.Id, installation.Repositories.Select(repository => repository.Id));
            _logger.LogInformation("Installing GitHub app for {Account}, 0/{RepositoryCount} repositories done...", installation.Installation.Account.Login, installation.Repositories.Count());

            // Install the webhooks onto all the repositories
            foreach (RepositoryInstallation repository in installation.Repositories)
            {
                // Grab the repository and skip it if it's archived, private or a fork
                if (repository.Private)
                {
                    _logger.LogInformation("Skipping private repository {Repository}", repository.FullName);
                    continue;
                }

                // Get the repo and skip it if it's a fork or archived
                ApiResult<Repository> repositoryDetails = await _gitHubApiRoutes.GetRepositoryAsync(accessToken, repository.FullName);
                if (!repositoryDetails.IsSuccessful)
                {
                    _logger.LogError("Failed to get repository details for {Repository}: {StatusCode} {Error}", repository.FullName, repositoryDetails.StatusCode, repositoryDetails.Error);
                    return HyperStatus.InternalServerError(new Error("Failed to get repository details."));
                }
                else if (repositoryDetails.Value.Archived || repositoryDetails.Value.Fork)
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
                    DiscordChannel existingThread;
                    try
                    {
                        existingThread = await _discordClient.GetChannelAsync(postId.Value);
                    }
                    catch (DiscordException error)
                    {
                        if (error.Response is null)
                        {
                            _logger.LogError("Failed to get thread channel: {Error}", error.Message);
                            return HyperStatus.InternalServerError(new Error("Failed to get channel."));
                        }
                        else if (error.Response.StatusCode == HttpStatusCode.NotFound)
                        {
                            // The post doesn't exist
                            _logger.LogInformation("Thread channel doesn't exist for {Repository}, creating a new one...", repository.FullName);
                        }
                        else
                        {
                            _logger.LogError("Failed to get thread channel: {StatusCode} {Error}", error.Response.StatusCode, error.JsonMessage);
                            return HyperStatus.InternalServerError(new Error("Failed to get channel."));
                        }
                    }
                }

                // Create a new post
                DiscordThreadChannel threadChannel;
                try
                {
                    DiscordChannel parentChannel = await _discordClient.GetChannelAsync(channelId);
                    threadChannel = await parentChannel.CreateThreadAsync(repository.FullName, DiscordAutoArchiveDuration.Week, DiscordChannelType.PublicThread, $"Creating thread for the {repository.FullName} GitHub repository.");
                }
                catch (DiscordException error)
                {
                    if (error.Response is null)
                    {
                        _logger.LogError("Failed to create thread channel: {Error}", error.Message);
                        return HyperStatus.InternalServerError(new Error("Failed to create channel."));
                    }

                    _logger.LogError("Failed to create thread channel: {StatusCode} {Error}", error.Response.StatusCode, error.JsonMessage);
                    return HyperStatus.InternalServerError(new Error("Failed to create channel."));
                }

                // Store the repository and thread ID in the database
                await _discordWebhookManager.CreateNewRepositoryAsync(installation.Installation.Account.Login, repository.Name, threadChannel.Id, cancellationToken);

                // Check to see if the access token expired
                if (expiresAt < DateTimeOffset.UtcNow)
                {
                    // Refresh the access token
                    (accessToken, expiresAt) = await _gitHubApiRoutes.GetAccessTokenAsync(installation.Installation.Id, installation.Repositories.Select(repository => repository.Id));
                }

                // Add the webhook to the repository
                ApiResult<Hook> webhook = await _gitHubApiRoutes.InstallWebhookAsync(accessToken, repository.FullName, $"{webhookUrl}?thread_id={threadChannel.Id}", AppEvent.All);
                if (webhook.Value is null)
                {
                    _logger.LogError("Failed to install webhook for {Repository}: {StatusCode} {Error}", repository.FullName, webhook.StatusCode, webhook.Error);
                    return HyperStatus.InternalServerError(new Error("Failed to install webhook."));
                }

                // Test the webhook
                ApiResult<object> testWebhook = await _gitHubApiRoutes.TestWebhookAsync(accessToken, repository.FullName, webhook.Value.Id);
                if (!testWebhook.IsSuccessful)
                {
                    _logger.LogError("Failed to test webhook for {Repository}: {StatusCode} {Error}", repository.FullName, testWebhook.StatusCode, testWebhook.Error);
                    return HyperStatus.InternalServerError(new Error("Failed to test webhook."));
                }

                _logger.LogInformation("Installed webhook for {Repository}", repository.FullName);
            }

            _logger.LogInformation("Installed GitHub app for {Account}, {RepositoryCount}/{RepositoryCount} repositories done.", installation.Installation.Account.Login, installation.Repositories.Count(), installation.Repositories.Count());
            return HyperStatus.OK();
        }
    }
}
