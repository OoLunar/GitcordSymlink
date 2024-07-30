using System;
using System.Text;
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
            byte[] response = await _discordClient.HandleHttpInteractionAsync(Encoding.UTF8.GetBytes(context.Metadata["body"]), cancellationToken);
            return HyperStatus.OK(_headers, response, HyperSerializers.RawAsync);
        }
    }
}
