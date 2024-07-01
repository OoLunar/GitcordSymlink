using System.Net;

namespace OoLunar.GitHubForumWebhookWorker.Discord
{
    public readonly struct DiscordApiResult<T>
    {
        public required HttpStatusCode StatusCode { get; init; }
        public required string? Error { get; init; }
        public required T? Value { get; init; }
    }
}
