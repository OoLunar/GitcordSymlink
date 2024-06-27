namespace OoLunar.GitHubForumWebhookWorker.Configuration
{
    public sealed record GitHubForumWebhookWorkerConfiguration
    {
        public required DiscordConfiguration Discord { get; init; }
        public required DatabaseConfiguration Database { get; init; }
        public LoggerConfiguration Logger { get; init; } = new();
    }
}
