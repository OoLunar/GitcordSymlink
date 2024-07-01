using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOf;
using Remora.Commands.Attributes;
using Remora.Commands.Groups;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Discord.API.Objects;
using Remora.Rest.Core;
using Remora.Results;

namespace OoLunar.GitHubForumWebhookWorker.Discord.Commands
{
    public sealed class SyncAccountCommand : CommandGroup
    {
        private readonly ILogger<SyncAccountCommand> _logger;
        private readonly DatabaseManager _discordWebhookManager;
        private readonly DiscordApiRoutes _apiRoutes;

        public SyncAccountCommand(ILogger<SyncAccountCommand> logger, DatabaseManager discordWebhookManager, DiscordApiRoutes apiRoutes)
        {
            _logger = logger;
            _discordWebhookManager = discordWebhookManager;
            _apiRoutes = apiRoutes;
        }

        [Command("sync_account"), Description("Syncs a GitHub account or organization with a Discord channel.")]
        public async ValueTask<Result<InteractionResponse>> ExecuteAsync(string accountName, IPartialChannel? channel = null)
        {
            if (channel is null)
            {
                // TODO: Create channel automatically if it doesn't exist.
                return Result<InteractionResponse>.FromSuccess(new InteractionResponse(InteractionCallbackType.ChannelMessageWithSource, new Optional<OneOf<IInteractionMessageCallbackData, IInteractionAutocompleteCallbackData, IInteractionModalCallbackData>>(new InteractionMessageCallbackData(
                    Content: "Please specify a channel to sync the account with.",
                    Flags: MessageFlags.Ephemeral
                ))));
            }

            // Create a new webhook for the channel.
            DiscordApiResult<Webhook> webhook = await _apiRoutes.CreateWebhookAsync(channel, $"Creating GitHub webhook for {PluralizeCorrectly(accountName)} GitHub account.");
            if (webhook.Value is null)
            {
                _logger.LogError("Failed to create a webhook for the channel: {HttpStatusCode} {HttpStatusReason} {Error}", (int)webhook.StatusCode, webhook.StatusCode, webhook.Error);
                return Result<InteractionResponse>.FromSuccess(new InteractionResponse(InteractionCallbackType.ChannelMessageWithSource, new Optional<OneOf<IInteractionMessageCallbackData, IInteractionAutocompleteCallbackData, IInteractionModalCallbackData>>(new InteractionMessageCallbackData(
                    Content: "Failed to create a webhook for the channel.",
                    Flags: MessageFlags.Ephemeral
                ))));
            }

            // Store the webhook URL in the database.
            await _discordWebhookManager.CreateNewAccountAsync(accountName, channel.ID.Value.Value, $"{webhook.Value.URL.Value}/github");

            // Let the user sync the account with the webhook.
            return Result<InteractionResponse>.FromSuccess(new InteractionResponse(InteractionCallbackType.ChannelMessageWithSource, new Optional<OneOf<IInteractionMessageCallbackData, IInteractionAutocompleteCallbackData, IInteractionModalCallbackData>>(new InteractionMessageCallbackData(
                Content: "Please give me permission to add webhooks to your repositories: https://github.com/apps/gitcord-symlink/installations/new",
                Flags: MessageFlags.Ephemeral
            ))));
        }

        public static string PluralizeCorrectly(string str) => str.Length == 0 ? str : str[^1] switch
        {
            // Ensure it doesn't already end with `'s`
            's' when str.Length > 1 && str[^2] == '\'' => str,
            's' => str + '\'',
            '\'' => str + 's',
            _ => str + "'s"
        };
    }
}
