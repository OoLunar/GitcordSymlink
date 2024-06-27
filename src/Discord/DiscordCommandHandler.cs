using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HyperSharp.Protocol;
using HyperSharp.Responders;
using HyperSharp.Results;
using Microsoft.Extensions.Options;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Discord.API.Objects;

namespace OoLunar.GitHubForumWebhookWorker.Discord
{
    public sealed class DiscordCommandHandler : IValueTaskResponder<HyperContext, HyperStatus>
    {
        public static Type[] Needs => [typeof(DiscordVerifier)];
        private readonly JsonSerializerOptions _jsonSerializerOptions;

        public DiscordCommandHandler(IOptionsSnapshot<JsonSerializerOptions> jsonSerializerOptions) => _jsonSerializerOptions = jsonSerializerOptions.Get("HyperSharp");

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
                _ => Result.Failure<InteractionResponse>($"Unknown interaction type: {interaction.Type}"),
            };

            return response.IsSuccess ? HyperStatus.OK(response.Value) : HyperStatus.BadRequest(response.Errors);
        }
    }
}
