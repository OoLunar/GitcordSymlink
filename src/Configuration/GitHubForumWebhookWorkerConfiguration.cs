namespace OoLunar.GitHubForumWebhookWorker.Configuration
{
    public sealed record GitHubForumWebhookWorkerConfiguration
    {
        public required DatabaseConfiguration Database { get; init; } = new();
        public required DiscordConfiguration Discord { get; init; }
        public required GitHubConfiguration GitHub { get; init; }
        public LoggerConfiguration Logger { get; init; } = new();
    }
}
