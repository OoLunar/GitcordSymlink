namespace OoLunar.GitcordSymlink.Configuration
{
    public sealed class GitHubConfiguration
    {
        public required string ClientId { get; init; }
        public required string ClientSecret { get; init; }
        public required string WebhookSecret { get; init; }
        public required string PrivateKeyPath { get; init; }
    }
}
