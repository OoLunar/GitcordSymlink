using System.ComponentModel;
using System.Threading.Tasks;
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
        [Command("sync_account"), Description("Syncs a GitHub account or organization with a Discord channel.")]
        public ValueTask<Result<InteractionResponse>> ExecuteAsync(string accountName) => ValueTask.FromResult(Result<InteractionResponse>.FromSuccess(new InteractionResponse(InteractionCallbackType.ChannelMessageWithSource, new Optional<OneOf<IInteractionMessageCallbackData, IInteractionAutocompleteCallbackData, IInteractionModalCallbackData>>(new InteractionMessageCallbackData(
            Content: "This command is not yet implemented.",
            Flags: MessageFlags.Ephemeral
        )))));
    }
}
