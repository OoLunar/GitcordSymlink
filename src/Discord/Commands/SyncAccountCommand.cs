using System.ComponentModel;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OneOf;
using OoLunar.GitcordSymlink.Discord.RemoraInteractions;
using Remora.Commands.Attributes;
using Remora.Commands.Groups;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Discord.API.Objects;
using Remora.Discord.Commands.Contexts;
using Remora.Rest.Core;
using Remora.Results;

namespace OoLunar.GitcordSymlink.Discord.Commands
{
    public sealed class SyncAccountCommand : CommandGroup
    {
        private readonly ILogger<SyncAccountCommand> _logger;
        private readonly DatabaseManager _discordWebhookManager;
        private readonly DiscordApiRoutes _apiRoutes;
        private readonly IInteractionCommandContext _commandContext;
        private readonly DiscordWebhookInteractionAPI _interactionAPI;

        public SyncAccountCommand(ILogger<SyncAccountCommand> logger, DatabaseManager discordWebhookManager, DiscordApiRoutes apiRoutes, IInteractionCommandContext context, DiscordWebhookInteractionAPI interactionAPI)
        {
            _logger = logger;
            _discordWebhookManager = discordWebhookManager;
            _apiRoutes = apiRoutes;
            _commandContext = context;
            _interactionAPI = interactionAPI;
        }

        [Command("sync_account"), Description("Syncs a GitHub account or organization with a Discord channel.")]
        public async ValueTask<Result> ExecuteAsync(string accountName, bool includeArchivedRepositories = false, bool includeForkedRepositories = false, bool includePrivateRepositories = false, IPartialChannel? channel = null)
        {
            // Normalize the account name to prevent case sensitivity issues and possible duplicates.
            accountName = accountName.ToLowerInvariant();
            if (channel is null)
            {
                // TODO: Create channel automatically if it doesn't exist.
                await _interactionAPI.CreateInteractionResponseAsync(_commandContext.Interaction.ID, _commandContext.Interaction.Token, new InteractionResponse(InteractionCallbackType.ChannelMessageWithSource, new Optional<OneOf<IInteractionMessageCallbackData, IInteractionAutocompleteCallbackData, IInteractionModalCallbackData>>(new InteractionMessageCallbackData(
                    Content: "Please specify a channel to sync the account with.",
                    Flags: MessageFlags.Ephemeral
                ))));
                return Result.FromSuccess();
            }

            // Create a new webhook for the channel.
            ApiResult<Webhook> webhook = await _apiRoutes.CreateWebhookAsync(channel, $"Creating GitHub webhook for {PluralizeCorrectly(accountName)} GitHub account.");
            if (webhook.Value is null)
            {
                _logger.LogError("Failed to create a webhook for the channel: {HttpStatusCode} {HttpStatusReason} {Error}", (int)webhook.StatusCode, webhook.StatusCode, webhook.Error);
                await _interactionAPI.CreateInteractionResponseAsync(_commandContext.Interaction.ID, _commandContext.Interaction.Token, new InteractionResponse(InteractionCallbackType.ChannelMessageWithSource, new Optional<OneOf<IInteractionMessageCallbackData, IInteractionAutocompleteCallbackData, IInteractionModalCallbackData>>(new InteractionMessageCallbackData(
                    Content: "Failed to create a webhook for the channel.",
                    Flags: MessageFlags.Ephemeral
                ))));

                return Result.FromSuccess();
            }

            // Store the webhook URL in the database.
            await _discordWebhookManager.CreateNewAccountAsync(accountName, channel.ID.Value.Value, $"{webhook.Value.URL.Value}/github");

            // Let the user sync the account with the webhook.
            await _interactionAPI.CreateInteractionResponseAsync(_commandContext.Interaction.ID, _commandContext.Interaction.Token, new InteractionResponse(InteractionCallbackType.ChannelMessageWithSource, new Optional<OneOf<IInteractionMessageCallbackData, IInteractionAutocompleteCallbackData, IInteractionModalCallbackData>>(new InteractionMessageCallbackData(
                Content: $"Please sync your GitHub account with the webhook: {webhook.Value.URL}",
                Flags: MessageFlags.Ephemeral
            ))));

            return Result.FromSuccess();
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
