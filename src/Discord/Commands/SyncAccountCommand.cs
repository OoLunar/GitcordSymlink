using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using DSharpPlus.Commands;
using DSharpPlus.Commands.ContextChecks;
using DSharpPlus.Commands.Processors.SlashCommands;
using DSharpPlus.Entities;
using Humanizer;
using Microsoft.Extensions.Logging;
using OoLunar.GitcordSymlink.Entities;

namespace OoLunar.GitcordSymlink.Discord.Commands
{
    public sealed class SyncAccountCommand
    {
        private static readonly DefaultReaction _starReaction = DefaultReaction.FromEmoji(DiscordEmoji.FromUnicode("⭐"));

        private readonly ILogger<SyncAccountCommand> _logger;
        private readonly DatabaseManager _databaseManager;

        public SyncAccountCommand(ILogger<SyncAccountCommand> logger, DatabaseManager discordWebhookManager)
        {
            _logger = logger;
            _databaseManager = discordWebhookManager;
        }

        [Command("sync_account"), Description("Syncs a GitHub account or organization with a Discord channel."), RequireGuild]
        public async ValueTask ExecuteAsync(SlashCommandContext context, string accountName, bool includePublicRepositories = true, bool includeArchivedRepositories = false, bool includeForkedRepositories = false, bool includePrivateRepositories = false, DiscordChannel? channel = null)
        {
            // Create a new webhook for the channel.
            await context.DeferResponseAsync(true);

            // Set the flags
            GitcordSyncOptions syncOptions = GitcordSyncOptions.None;
            syncOptions |= includePublicRepositories ? GitcordSyncOptions.Public : 0;
            syncOptions |= includeArchivedRepositories ? GitcordSyncOptions.Archived : 0;
            syncOptions |= includeForkedRepositories ? GitcordSyncOptions.Forked : 0;
            syncOptions |= includePrivateRepositories ? GitcordSyncOptions.Private : 0;

            // TODO: Check to see if we already have a channel for this account.
            // If we do, we should ensure the webhook exists on the channel and
            // Iterate over the repositories again to ensure they all use the webhook.
            GitcordAccount? account = await _databaseManager.GetAccountAsync(accountName);
            if (account is not null)
            {
                await ResyncRepositories(account, syncOptions, channel);
                return;
            }

            // Create channel automatically if it doesn't exist.
            channel ??= await context.Guild!.CreateChannelAsync(
                accountName.Kebaberize().ToLowerInvariant(),
                DiscordChannelType.GuildForum,
                topic: $"Synced with {PluralizeCorrectly(accountName)} GitHub account.",
                reason: $"Creating channel for {PluralizeCorrectly(accountName)} GitHub account.",
                defaultAutoArchiveDuration: DiscordAutoArchiveDuration.Week,
                defaultReactionEmoji: _starReaction,
                defaultSortOrder: DiscordDefaultSortOrder.CreationDate
            );

            // Normalize the account name to prevent case sensitivity issues and possible duplicates.
            accountName = accountName.ToLowerInvariant();

            // Create the webhook for GitHub to use.
            DiscordWebhook webhook = await CreateWebhookAsync(channel, accountName);

            // Store the webhook URL in the database.
            await _databaseManager.CreateNewAccountAsync(accountName, channel.Id, syncOptions, $"{webhook.Url}/github");

            // Let the user sync the account with the webhook.
            await context.RespondAsync("Please give me permission to add webhooks to your repositories: https://github.com/apps/gitcord-symlink/installations/new");
        }

        private async ValueTask ResyncRepositories(GitcordAccount account, GitcordSyncOptions syncOptions, DiscordChannel? channel)
        {
            if (account.SyncOptions != syncOptions || (channel is not null && account.ChannelId != channel.Id))
            {
                account = new()
                {
                    Name = account.Name,
                    SyncOptions = syncOptions,
                    ChannelId = channel?.Id ?? account.ChannelId,
                    WebhookUrl = account.WebhookUrl
                };

                // Update the sync options in the database.
                if (!await _databaseManager.UpdateAccountAsync(account))
                {
                    _logger.LogError("Failed to update the sync options for the account {Account}.", account.Name);
                    throw new InvalidOperationException("Failed to update the sync options for the account.");
                }
            }

            // Get all repositories available for the account.
            IReadOnlyDictionary<string, ulong> repositories = await _databaseManager.GetAllRepositoriesAsync(account.Name);

        }

        private async ValueTask<DiscordWebhook> CreateWebhookAsync(DiscordChannel channel, string accountName)
        {
            try
            {
                return await channel.CreateWebhookAsync("Gitcord Symlink", reason: $"Creating GitHub webhook for {PluralizeCorrectly(accountName)} GitHub account.");
            }
            catch (Exception error)
            {
                _logger.LogError("Failed to create a webhook for the channel: {Error}", error);
                throw;
            }
        }

        private static string PluralizeCorrectly(string str) => str.Length == 0 ? str : str[^1] switch
        {
            // Ensure it doesn't already end with `'s`
            's' when str.Length > 1 && str[^2] == '\'' => str,
            's' => str + '\'',
            '\'' => str + 's',
            _ => str + "'s"
        };
    }
}
