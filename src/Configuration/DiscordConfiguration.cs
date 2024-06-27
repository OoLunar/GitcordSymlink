namespace OoLunar.GitHubForumWebhookWorker.Configuration
{
    public sealed record DiscordConfiguration
    {
        public required string? Token { get; init; }
        public ulong GuildId { get; init; }
    }
}
