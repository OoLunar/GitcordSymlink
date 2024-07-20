using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HyperSharp.Protocol;
using HyperSharp.Responders;
using HyperSharp.Results;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OoLunar.GitcordSymlink.Discord.RemoraInteractions;
using Remora.Commands.Trees;
using Remora.Discord.API;
using Remora.Discord.API.Abstractions.Gateway.Events;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Discord.API.Objects;
using Remora.Discord.Commands.Extensions;
using Remora.Discord.Gateway.Services;
using Remora.Discord.Interactivity.Services;

namespace OoLunar.GitcordSymlink.Discord
{
    public sealed class DiscordCommandHandler : IValueTaskResponder<HyperContext, HyperStatus>
    {
        public static Type[] Needs => [typeof(DiscordVerifier)];

        private readonly DiscordApiRoutes _apiRoutes;
        private readonly ILogger<DiscordCommandHandler> _logger;
        private readonly JsonSerializerOptions _jsonSerializerOptions;
        private readonly IResponderDispatchService _responderDispatchService;
        private readonly CommandTree _commandTree;
        private readonly InMemoryDataService<string, InteractionWebhookResponse> _dataService;

        public DiscordCommandHandler(DiscordApiRoutes apiRoutes, ILogger<DiscordCommandHandler> logger, IOptionsSnapshot<JsonSerializerOptions> jsonSerializerOptions, CommandTree commandTree, IResponderDispatchService responderDispatchService)
        {
            _apiRoutes = apiRoutes;
            _logger = logger;
            _jsonSerializerOptions = jsonSerializerOptions.Get("HyperSharp");
            _responderDispatchService = responderDispatchService;
            _commandTree = commandTree;
            _dataService = InMemoryDataService<string, InteractionWebhookResponse>.Instance;
        }

        public async ValueTask RegisterCommandsAsync()
        {
            // Register all commands
            ApiResult<IReadOnlyList<IApplicationCommand>> discordCommands = await _apiRoutes.RegisterApplicationCommandsAsync(_commandTree.CreateApplicationCommands());
            if (discordCommands.Value is null)
            {
                _logger.LogCritical("Failed to register commands: {StatusCode} {ReasonPhrase}, {Body}", (int)discordCommands.StatusCode, discordCommands.StatusCode, discordCommands.Error);
                return;
            }
            else if (discordCommands.Value is null)
            {
                _logger.LogCritical("Failed to read commands from response.");
                return;
            }

            _logger.LogInformation("Registered {Count} commands.", discordCommands.Value.Count);
            return;
        }

        public async ValueTask<Result<HyperStatus>> RespondAsync(HyperContext context, CancellationToken cancellationToken = default)
        {
            IInteractionCreate? interaction = JsonSerializer.Deserialize<IInteractionCreate>(context.Metadata["body"], _jsonSerializerOptions);
            if (interaction is null)
            {
                return HyperStatus.BadRequest(new Error("Invalid interaction data."));
            }

            Result<InteractionResponse> response = interaction.Type switch
            {
                InteractionType.Ping => Result.Success(new InteractionResponse(InteractionCallbackType.Pong)),
                InteractionType.ApplicationCommand => await ExecuteCommandAsync(interaction, cancellationToken),
                _ => Result.Failure<InteractionResponse>($"Unknown interaction type: {interaction.Type}"),
            };

            return response.IsSuccess ? HyperStatus.OK(response.Value) : HyperStatus.BadRequest(response.Errors);
        }

        private async ValueTask<InteractionResponse> ExecuteCommandAsync(IInteractionCreate interaction, CancellationToken cancellationToken = default)
        {
            InteractionWebhookResponse data = new(new TaskCompletionSource<IInteractionResponse>());
            if (!_dataService.TryAddData(interaction.Token, data))
            {
                throw new InvalidOperationException("The interaction was already responded to, or doesn't exist.");
            }

            await _responderDispatchService.DispatchAsync(new Payload<IInteractionCreate>(interaction), cancellationToken);
            if (!_dataService.TryRemoveData(interaction.Token))
            {
                throw new InvalidOperationException("The interaction was already responded to, or doesn't exist.");
            }

            IInteractionResponse response = await data.ResponseTCS.Task;
            return Unsafe.As<IInteractionResponse, InteractionResponse>(ref response);
        }
    }
}
