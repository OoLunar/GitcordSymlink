namespace OoLunar.GitcordSymlink.Entities
{
    public sealed class GitcordAccount
    {
        public required string Name { get; init; }
        public required ulong ChannelId { get; init; }
        public required GitcordSyncOptions SyncOptions { get; init; }
        public string? WebhookUrl { get; internal set; }
    }
}
