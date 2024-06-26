using System.ComponentModel;
using System.Threading.Tasks;
using DSharpPlus.Commands;

namespace OoLunar.GitHubForumWebhookWorker.Commands
{
    public sealed class PingCommand
    {
        [Command("ping"), Description("Checks the current latency of the bot.")]
        public static async ValueTask ExecuteAsync(CommandContext context) => await context.RespondAsync($"Pong! {context.Client.Ping}ms");
    }
}
