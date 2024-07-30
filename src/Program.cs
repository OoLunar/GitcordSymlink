using System;
using System.Globalization;
using System.Net.Http;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Commands;
using DSharpPlus.Extensions;
using HyperSharp;
using HyperSharp.Setup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OoLunar.GitcordSymlink.Configuration;
using OoLunar.GitcordSymlink.Discord;
using OoLunar.GitcordSymlink.GitHub;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;
using SerilogLoggerConfiguration = Serilog.LoggerConfiguration;

namespace OoLunar.GitcordSymlink
{
    public sealed class Program
    {
        public static async Task Main(string[] args)
        {
            IServiceCollection services = new ServiceCollection();
            services.AddSingleton(serviceProvider =>
            {
                ConfigurationBuilder configurationBuilder = new();
                configurationBuilder.Sources.Clear();
                configurationBuilder.AddJsonFile("config.json", true, true);
#if DEBUG
                configurationBuilder.AddJsonFile("config.debug.json", true, true);
#endif
                configurationBuilder.AddEnvironmentVariables("GitcordSymlink__");
                configurationBuilder.AddCommandLine(args);

                IConfiguration configuration = configurationBuilder.Build();
                GitcordSymlinkConfiguration? gitcordSymlinkConfiguration = configuration.Get<GitcordSymlinkConfiguration>();
                if (gitcordSymlinkConfiguration is null)
                {
                    Console.WriteLine("No configuration found! Please modify the config file, set environment variables or pass command line arguments. Exiting...");
                    Environment.Exit(1);
                }

                return gitcordSymlinkConfiguration;
            });

            services.AddLogging(logging =>
            {
                IServiceProvider serviceProvider = logging.Services.BuildServiceProvider();
                GitcordSymlinkConfiguration gitHubForumWebhookWorker = serviceProvider.GetRequiredService<GitcordSymlinkConfiguration>();
                SerilogLoggerConfiguration serilogLoggerConfiguration = new();
                serilogLoggerConfiguration.MinimumLevel.Is(gitHubForumWebhookWorker.Logger.LogLevel);
                serilogLoggerConfiguration.WriteTo.Console(
                    formatProvider: CultureInfo.InvariantCulture,
                    outputTemplate: gitHubForumWebhookWorker.Logger.Format,
                    theme: AnsiConsoleTheme.Code
                );

                serilogLoggerConfiguration.WriteTo.File(
                    formatProvider: CultureInfo.InvariantCulture,
                    path: $"{gitHubForumWebhookWorker.Logger.Path}/{gitHubForumWebhookWorker.Logger.FileName}.log",
                    rollingInterval: gitHubForumWebhookWorker.Logger.RollingInterval,
                    outputTemplate: gitHubForumWebhookWorker.Logger.Format
                );

                // Sometimes the user/dev needs more or less information about a speific part of the bot
                // so we allow them to override the log level for a specific namespace.
                if (gitHubForumWebhookWorker.Logger.Overrides.Count > 0)
                {
                    foreach ((string key, LogEventLevel value) in gitHubForumWebhookWorker.Logger.Overrides)
                    {
                        serilogLoggerConfiguration.MinimumLevel.Override(key, value);
                    }
                }

                Log.Logger = serilogLoggerConfiguration.CreateLogger();
                logging.AddSerilog();
            });

            // Add our ratelimiter
            services.AddTransient<HttpRatelimiter>();

            // Add our http client
            services.AddTransient((serviceProvider) =>
            {
                HttpClient httpClient = new(serviceProvider.GetRequiredService<HttpRatelimiter>())
                {
                    DefaultRequestVersion = new Version(2, 0),
                    Timeout = TimeSpan.FromHours(1)
                };

                httpClient.DefaultRequestHeaders.Add("User-Agent", $"OoLunar.GitcordSymlink/{ThisAssembly.Project.Version} ({ThisAssembly.Project.RepositoryUrl})");
                return httpClient;
            });

            services.AddSingleton<DatabaseManager>();
            services.AddSingleton<DiscordCommandHandler>();
            services.AddSingleton<GitHubApiRoutes>();

            // Add DSharpPlus
            services.AddDiscordClient((serviceProvider) => serviceProvider.GetRequiredService<GitcordSymlinkConfiguration>().Discord.Token, DiscordIntents.All);

            // Add our http server
            services.AddHyperSharp((config) =>
            {
                config.Timeout = TimeSpan.FromHours(1);
                config.AddResponders(typeof(Program).Assembly);
            });

            // Almost start the program
            IServiceProvider serviceProvider = services.BuildServiceProvider();
            GitcordSymlinkConfiguration gitcordSymlinkConfiguration = serviceProvider.GetRequiredService<GitcordSymlinkConfiguration>();

            // Register commands
            DiscordClient discordClient = serviceProvider.GetRequiredService<DiscordClient>();
            CommandsExtension commandsExtension = discordClient.UseCommands();
            commandsExtension.AddCommands(typeof(Program).Assembly);

            // Start the server
            HyperServer server = serviceProvider.GetRequiredService<HyperServer>();
            server.Start();

            // Start Discord
            await discordClient.ConnectAsync();

            // Wait for commands
            await Task.Delay(-1);
        }
    }
}
