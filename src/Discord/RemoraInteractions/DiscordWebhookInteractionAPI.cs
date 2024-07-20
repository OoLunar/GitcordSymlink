// This file was originally pulled from https://github.com/VelvetToroyashi/RemoraHTTPInteractions/blob/4a94b104535f150053f06b1aa99c86d9b84d81ef/RemoraHTTPInteractions/Services/InMemoryDataStore.cs
// All credit goes to the original author, VelvetToroyashi.
// Changes made:
// - Formatting
// - Replacing `InMemoryDataStore` with `InMemoryDataService` and updating relevant references
// - Change of namespace
// - Removal of `ExcludeFromCodeCoverage` attribute

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OneOf;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Discord.API.Abstractions.Rest;
using Remora.Discord.API.Objects;
using Remora.Discord.Caching.API;
using Remora.Discord.Commands.Contexts;
using Remora.Discord.Commands.Services;
using Remora.Discord.Interactivity.Services;
using Remora.Rest.Core;
using Remora.Results;

namespace OoLunar.GitcordSymlink.Discord.RemoraInteractions
{
    public class DiscordWebhookInteractionAPI : IDiscordRestInteractionAPI
    {
        private readonly ContextInjectionService? _contextInjector;
        private readonly IDiscordRestInteractionAPI _underlying;
        private readonly InMemoryDataService<string, InteractionWebhookResponse> _dataService;

        public DiscordWebhookInteractionAPI(IDiscordRestInteractionAPI underlying, ContextInjectionService? contextInjector = default)
        {
            if (underlying is CachingDiscordRestInteractionAPI)
            {
                throw new InvalidOperationException($"The call to `.AddHttpInteractions` should be called before `.AddDiscordCaching()`");
            }

            _underlying = underlying;
            _contextInjector = contextInjector;
            _dataService = InMemoryDataService<string, InteractionWebhookResponse>.Instance;
        }

        /// <summary>
        /// This method does NOT create an interaction response, but instead sets the requisite data where it can then be returned to an ASP.NET Core controller/endpoint.
        /// </summary>
        /// <inheritdoc/>
        public async Task<Result> CreateInteractionResponseAsync(Snowflake interactionID, string interactionToken, IInteractionResponse response, Optional<IReadOnlyList<OneOf<FileData, IPartialAttachment>>> attachments = default, CancellationToken ct = default)
        {
            Result<DataLease<string, InteractionWebhookResponse>> interactionResult = await _dataService.LeaseDataAsync(interactionToken, ct);
            if (!interactionResult.IsSuccess)
            {
                // The interaction was either already responded to, or doesn't exist.
                // If it hasn't, however, this is always infallible
                return Result.FromError(interactionResult);
            }

            await using DataLease<string, InteractionWebhookResponse> interaction = interactionResult.Entity;

            // Ensure that the interaction is deleted after the request is complete.
            // It's important that this is done here because asynchronous disposal doesn't
            // guarantee that the object will be disposed of before the caller that waits on the TCS
            // is resumed. This caused a concurrency issue in 1.0.1
            interaction.Delete();
            interaction.Data.ResponseTCS.SetResult(response);

            // TODO: if (interactionID.Timestamp < (DateTime.UtcNow.AddSeconds(-3))) return Result.FromError();
            return Result.FromSuccess();
        }

        /// <inheritdoc />
        public Task<Result<IMessage>> GetOriginalInteractionResponseAsync(Snowflake applicationID, string interactionToken, CancellationToken ct = default) =>
            _underlying.GetOriginalInteractionResponseAsync(applicationID, interactionToken, ct);

        /// <inheritdoc />
        public Task<Result<IMessage>> EditOriginalInteractionResponseAsync(Snowflake applicationID, string token, Optional<string?> content = default, Optional<IReadOnlyList<IEmbed>?> embeds = default, Optional<IAllowedMentions?> allowedMentions = default, Optional<IReadOnlyList<IMessageComponent>?> components = default, Optional<IReadOnlyList<OneOf<FileData, IPartialAttachment>>?> attachments = default, CancellationToken ct = default)
            => _underlying.EditOriginalInteractionResponseAsync(applicationID, token, content, embeds, allowedMentions, components, attachments, ct);

        /// <inheritdoc />
        public Task<Result> DeleteOriginalInteractionResponseAsync(Snowflake applicationID, string token, CancellationToken ct = default) =>
            _underlying.DeleteOriginalInteractionResponseAsync(applicationID, token, ct);

        /// <inheritdoc />
        public async Task<Result<IMessage>> CreateFollowupMessageAsync(Snowflake applicationID, string token, Optional<string> content = default, Optional<bool> isTTS = default, Optional<IReadOnlyList<IEmbed>> embeds = default, Optional<IAllowedMentions> allowedMentions = default, Optional<IReadOnlyList<IMessageComponent>> components = default, Optional<IReadOnlyList<OneOf<FileData, IPartialAttachment>>> attachments = default, Optional<MessageFlags> flags = default, CancellationToken ct = default)
        {
            // Hack! Use the presence of a context to determine that this is the initial response,
            // and thus, can be returned as a response, instead of having to make an API call!
            if (_contextInjector is not { Context: InteractionContext { HasRespondedToInteraction: false } context })
            {
                return await _underlying.CreateFollowupMessageAsync(applicationID, token, content, isTTS, embeds, allowedMentions, components, attachments, flags, ct);
            }

            Result<DataLease<string, InteractionWebhookResponse>> interactionResult = await _dataService.LeaseDataAsync(token, ct);
            if (!interactionResult.IsSuccess)
            {
                return Result<IMessage>.FromError(interactionResult);
            }

            await using DataLease<string, InteractionWebhookResponse> interaction = interactionResult.Entity;
            IInteractionResponse adhocResponse = new InteractionResponse(InteractionCallbackType.ChannelMessageWithSource, new(new InteractionMessageCallbackData(
                Content: content,
                IsTTS: isTTS,
                Embeds: embeds,
                AllowedMentions: allowedMentions,
                Components: components,
                Flags: flags
            )));

            interaction.Delete();
            interaction.Data.ResponseTCS.SetResult(adhocResponse);

            // Why?, you may be asking. To this I answer that this is a cursed optimization to make responses faster.
            // By releasing the TCS, we respond to the interaction via HTTP, which is faster than opening a new connection
            // to Discord when using the /callback endpoint; however this would technically break the return of this method
            // for any callers that rely on the return value. So, at the expense of holding the caller slightly longer
            // (sending the response is asynchronous) to grab the response, we can return the same message that would be
            // returned by the API call.
            //
            // Though, all of this only even matters if this method is called instead of CreateInteractionResponse, which
            // is technically undocumented-but-supported behavior. 💀
            Result<IMessage> messageResult = await _underlying.GetOriginalInteractionResponseAsync(applicationID, token, ct);
            context.HasRespondedToInteraction = messageResult.IsSuccess;
            return messageResult;
        }

        /// <inheritdoc />
        public Task<Result<IMessage>> GetFollowupMessageAsync(Snowflake applicationID, string token, Snowflake messageID, CancellationToken ct = default)
            => _underlying.GetFollowupMessageAsync(applicationID, token, messageID, ct);

        /// <inheritdoc />
        public Task<Result<IMessage>> EditFollowupMessageAsync(Snowflake applicationID, string token, Snowflake messageID, Optional<string?> content = default, Optional<IReadOnlyList<IEmbed>?> embeds = default, Optional<IAllowedMentions?> allowedMentions = default, Optional<IReadOnlyList<IMessageComponent>?> components = default, Optional<IReadOnlyList<OneOf<FileData, IPartialAttachment>>?> attachments = default, CancellationToken ct = default)
            => _underlying.EditFollowupMessageAsync(applicationID, token, messageID, content, embeds, allowedMentions, components, attachments, ct);

        /// <inheritdoc />
        public Task<Result> DeleteFollowupMessageAsync(Snowflake applicationID, string token, Snowflake messageID, CancellationToken ct = default)
            => _underlying.DeleteFollowupMessageAsync(applicationID, token, messageID, ct);
    }
}
