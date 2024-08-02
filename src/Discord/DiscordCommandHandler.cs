using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DSharpPlus;
using HyperSharp.Protocol;
using HyperSharp.Responders;
using HyperSharp.Results;

namespace OoLunar.GitcordSymlink.Discord
{
    public sealed class DiscordCommandHandler : IValueTaskResponder<HyperContext, HyperStatus>
    {
        public static Type[] Needs => [typeof(DiscordVerifier)];
        private static readonly HyperHeaderCollection _headers = new()
        {
            { "Content-Type", "application/json; charset=utf-8" }
        };

        private readonly DiscordClient _discordClient;
        public DiscordCommandHandler(DiscordClient discordClient) => _discordClient = discordClient;

        public async ValueTask<Result<HyperStatus>> RespondAsync(HyperContext context, CancellationToken cancellationToken = default)
        {
            byte[] request = Encoding.UTF8.GetBytes(context.Metadata["body"]);
            ulong? guildId = GetGuildId(request);
            if (guildId is not null && !_discordClient.Guilds.ContainsKey(guildId.Value))
            {
                // Populate the cache if the guild doesn't exist
                await _discordClient.GetGuildAsync(guildId.Value);
            }

            byte[] response = await _discordClient.HandleHttpInteractionAsync(request, cancellationToken);
            return HyperStatus.OK(_headers, response, HyperSerializers.RawAsync);
        }

        private static ulong? GetGuildId(byte[] utf8Json)
        {
            Utf8JsonReader reader = new(utf8Json);
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.GetString() == "guild_id")
                {
                    reader.Read();
                    string? value = reader.GetString();
                    if (ulong.TryParse(value, out ulong guildId))
                    {
                        return guildId;
                    }

                    break;
                }
            }

            return null;
        }
    }
}
