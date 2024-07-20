namespace OoLunar.GitcordSymlink.Configuration
{
    public sealed record DiscordConfiguration
    {
        public required string Token { get; init; }
        public required string PublicKey { get; init; }
        public required ulong ApplicationId { get; init; }
        public required ulong GuildId { get; init; }
    }
}
