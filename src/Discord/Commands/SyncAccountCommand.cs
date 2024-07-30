using System;
using System.ComponentModel;
using System.Threading.Tasks;
using DSharpPlus.Commands;
using DSharpPlus.Entities;
using Microsoft.Extensions.Logging;

namespace OoLunar.GitcordSymlink.Discord.Commands
{
    public sealed class SyncAccountCommand
    {
        private readonly ILogger<SyncAccountCommand> _logger;
        private readonly DatabaseManager _discordWebhookManager;

        public SyncAccountCommand(ILogger<SyncAccountCommand> logger, DatabaseManager discordWebhookManager)
        {
            _logger = logger;
            _discordWebhookManager = discordWebhookManager;
        }

        [Command("sync_account"), Description("Syncs a GitHub account or organization with a Discord channel.")]
        public async ValueTask ExecuteAsync(CommandContext context, string accountName, bool includeArchivedRepositories = false, bool includeForkedRepositories = false, bool includePrivateRepositories = false, DiscordChannel? channel = null)
        {
            // Normalize the account name to prevent case sensitivity issues and possible duplicates.
            accountName = accountName.ToLowerInvariant();
            if (channel is null)
            {
                // TODO: Create channel automatically if it doesn't exist.
                await context.RespondAsync("Please specify a channel to sync the account with.");
                return;
            }

            // Create a new webhook for the channel.
            await context.DeferResponseAsync();
            DiscordWebhook webhook;
            try
            {
                webhook = await channel.CreateWebhookAsync("Gitcord Symlink", reason: $"Creating GitHub webhook for {PluralizeCorrectly(accountName)} GitHub account.");
            }
            catch (Exception error)
            {
                _logger.LogError("Failed to create a webhook for the channel: {Error}", error);
                throw;
            }

            // Store the webhook URL in the database.
            await _discordWebhookManager.CreateNewAccountAsync(accountName, channel.Id, $"{webhook.Url}/github");

            // Let the user sync the account with the webhook.
            await context.RespondAsync(new DiscordInteractionResponseBuilder()
            {
                Content = $"Please sync your GitHub account with the webhook: {webhook.Url}",
                IsEphemeral = true
            });
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
