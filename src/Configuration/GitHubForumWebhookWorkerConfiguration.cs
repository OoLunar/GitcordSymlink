namespace OoLunar.GitHubForumWebhookWorker.Configuration
{
    public sealed record GitHubForumWebhookWorkerConfiguration
    {
        public required DiscordConfiguration Discord { get; init; }
        public required DatabaseConfiguration Database { get; init; } = new();
        public LoggerConfiguration Logger { get; init; } = new();
    }
}
