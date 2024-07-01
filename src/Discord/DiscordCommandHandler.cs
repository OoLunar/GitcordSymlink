using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HyperSharp.Protocol;
using HyperSharp.Responders;
using HyperSharp.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OneOf;
using OoLunar.GitHubForumWebhookWorker.Discord.Commands;
using Remora.Commands.Trees;
using Remora.Commands.Trees.Nodes;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Discord.API.Objects;
using Remora.Discord.Commands.Extensions;
using Remora.Rest.Core;

namespace OoLunar.GitHubForumWebhookWorker.Discord
{
    public sealed class DiscordCommandHandler : IValueTaskResponder<HyperContext, HyperStatus>
    {
        public static Type[] Needs => [typeof(DiscordVerifier)];
        private static Dictionary<Snowflake, string> _commandMappings = [];

        private readonly DiscordApiRoutes _apiRoutes;
        private readonly ILogger<DiscordCommandHandler> _logger;
        private readonly JsonSerializerOptions _jsonSerializerOptions;

        // Command objects
        private readonly SyncAccountCommand _syncAccountCommand;

        public DiscordCommandHandler(IServiceProvider serviceProvider, DiscordApiRoutes apiRoutes, ILogger<DiscordCommandHandler> logger, IOptionsSnapshot<JsonSerializerOptions> jsonSerializerOptions)
        {
            _apiRoutes = apiRoutes;
            _logger = logger;
            _jsonSerializerOptions = jsonSerializerOptions.Get("HyperSharp");

            _syncAccountCommand = ActivatorUtilities.GetServiceOrCreateInstance<SyncAccountCommand>(serviceProvider);
        }

        public async ValueTask RegisterCommandsAsync()
        {
            // Find all commands
            CommandTreeBuilder commandTreeBuilder = new();
            foreach (Type type in typeof(Program).Assembly.ExportedTypes)
            {
                if (type.FullName is null || !type.FullName.StartsWith("OoLunar.GitHubForumWebhookWorker.Discord.Commands", StringComparison.Ordinal))
                {
                    continue;
                }

                commandTreeBuilder.RegisterModule(type);
            }

            // Build the commands
            CommandTree commandTree = commandTreeBuilder.Build();

            // Register all commands
            DiscordApiResult<IReadOnlyList<IApplicationCommand>> discordCommands = await _apiRoutes.RegisterApplicationCommandsAsync(commandTree.CreateApplicationCommands());
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

            // Map command id's back to command names
            Dictionary<Snowflake, string> commandMappings = [];
            foreach (KeyValuePair<(Optional<Snowflake> GuildID, Snowflake CommandID), OneOf<IReadOnlyDictionary<string, CommandNode>, CommandNode>> commandList in commandTree.MapDiscordCommands(discordCommands.Value))
            {
                // For now we're only going to support global commands, ignoring guild commands
                // Additionally we're only going to support commands with a single node, ignoring group commands
                if (commandList.Key.GuildID.HasValue || commandList.Value.IsT0)
                {
                    continue;
                }

                CommandNode commandNode = commandList.Value.AsT1;
                commandMappings[commandList.Key.CommandID] = commandNode.Key;
            }

            _commandMappings = commandMappings;
            _logger.LogInformation("Registered {Count} commands.", commandMappings.Count);
            return;
        }

        public async ValueTask<Result<HyperStatus>> RespondAsync(HyperContext context, CancellationToken cancellationToken = default)
        {
            Interaction? interaction = JsonSerializer.Deserialize<Interaction>(context.Metadata["body"], _jsonSerializerOptions);
            if (interaction is null)
            {
                return HyperStatus.BadRequest(new Error("Invalid interaction data."));
            }

            Result<InteractionResponse> response = interaction.Type switch
            {
                InteractionType.Ping => Result.Success(new InteractionResponse(InteractionCallbackType.Pong)),
                InteractionType.ApplicationCommand => await ExecuteCommandAsync(interaction),
                _ => Result.Failure<InteractionResponse>($"Unknown interaction type: {interaction.Type}"),
            };

            return response.IsSuccess ? HyperStatus.OK(response.Value) : HyperStatus.BadRequest(response.Errors);
        }

        private async ValueTask<Result<InteractionResponse>> ExecuteCommandAsync(Interaction interaction) => !_commandMappings.TryGetValue(interaction.Data.Value.AsT0.ID, out string? commandName)
            ? Result.Failure<InteractionResponse>($"No handler found for command ID: {interaction.Data.Value.AsT0.ID} ({interaction.Data.Value.AsT0.Name})")
            : commandName switch
            {
                "sync_account" => ConvertRemoraResultToHyperSharp(await _syncAccountCommand.ExecuteAsync(interaction.Data.Value.AsT0.Options.Value[0].Value.Value.AsT0, interaction.Data.Value.AsT0.Resolved.Value.Channels.Value.First().Value)),
                _ => Result.Failure<InteractionResponse>($"No handler found for command: {commandName}"),
            };

        private static Result<InteractionResponse> ConvertRemoraResultToHyperSharp(Remora.Results.Result<InteractionResponse> result) => result.IsSuccess
            ? result.Entity
            : Result.Failure<InteractionResponse>(new Error(result.Error.Message));
    }
}
