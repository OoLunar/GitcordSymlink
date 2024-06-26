using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using DSharpPlus;
using DSharpPlus.Commands;
using DSharpPlus.Commands.Processors.SlashCommands;
using DSharpPlus.Commands.Processors.TextCommands;
using DSharpPlus.Commands.Processors.TextCommands.Parsing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OoLunar.GitHubForumWebhookWorker.Configuration;
using OoLunar.GitHubForumWebhookWorker.Events;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;
using DSharpPlusDiscordConfiguration = DSharpPlus.DiscordConfiguration;
using SerilogLoggerConfiguration = Serilog.LoggerConfiguration;

namespace OoLunar.GitHubForumWebhookWorker
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
                configurationBuilder.AddEnvironmentVariables("GitHubForumWebhookWorker__");
                configurationBuilder.AddCommandLine(args);

                IConfiguration configuration = configurationBuilder.Build();
                GitHubForumWebhookWorkerConfiguration? gitHubForumWebhookWorker = configuration.Get<GitHubForumWebhookWorkerConfiguration>();
                if (gitHubForumWebhookWorker is null)
                {
                    Console.WriteLine("No configuration found! Please modify the config file, set environment variables or pass command line arguments. Exiting...");
                    Environment.Exit(1);
                }

                return gitHubForumWebhookWorker;
            });

            services.AddLogging(logging =>
            {
                IServiceProvider serviceProvider = logging.Services.BuildServiceProvider();
                GitHubForumWebhookWorkerConfiguration gitHubForumWebhookWorker = serviceProvider.GetRequiredService<GitHubForumWebhookWorkerConfiguration>();
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

                logging.AddSerilog(serilogLoggerConfiguration.CreateLogger());
            });

            services.AddSingleton((serviceProvider) =>
            {
                DiscordEventManager eventManager = new(serviceProvider);
                eventManager.GatherEventHandlers(typeof(Program).Assembly);
                return eventManager;
            });

            services.AddSingleton(serviceProvider =>
            {
                GitHubForumWebhookWorkerConfiguration gitHubForumWebhookWorker = serviceProvider.GetRequiredService<GitHubForumWebhookWorkerConfiguration>();
                if (gitHubForumWebhookWorker.Discord is null || string.IsNullOrWhiteSpace(gitHubForumWebhookWorker.Discord.Token))
                {
                    serviceProvider.GetRequiredService<ILogger<Program>>().LogCritical("Discord token is not set! Exiting...");
                    Environment.Exit(1);
                }

                DiscordShardedClient discordClient = new(new DSharpPlusDiscordConfiguration
                {
                    Token = gitHubForumWebhookWorker.Discord.Token,
                    Intents = TextCommandProcessor.RequiredIntents | SlashCommandProcessor.RequiredIntents | DiscordIntents.GuildVoiceStates | DiscordIntents.MessageContents,
                    LoggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>(),
                });

                return discordClient;
            });

            // Almost start the program
            IServiceProvider serviceProvider = services.BuildServiceProvider();
            GitHubForumWebhookWorkerConfiguration gitHubForumWebhookWorker = serviceProvider.GetRequiredService<GitHubForumWebhookWorkerConfiguration>();
            DiscordShardedClient discordClient = serviceProvider.GetRequiredService<DiscordShardedClient>();
            DiscordEventManager eventManager = serviceProvider.GetRequiredService<DiscordEventManager>();

            // Register extensions here since these involve asynchronous operations
            IReadOnlyDictionary<int, CommandsExtension> commandsExtensions = await discordClient.UseCommandsAsync(new CommandsConfiguration()
            {
                ServiceProvider = serviceProvider,
                DebugGuildId = gitHubForumWebhookWorker.Discord.GuildId
            });

            // Iterate through each Discord shard
            foreach (CommandsExtension commandsExtension in commandsExtensions.Values)
            {
                // Add all commands by scanning the current assembly
                commandsExtension.AddCommands(typeof(Program).Assembly);

                // Add text commands (h!ping) with a custom prefix, keeping all the other processors in their default state
                await commandsExtension.AddProcessorsAsync(new TextCommandProcessor(new()
                {
                    PrefixResolver = new DefaultPrefixResolver(gitHubForumWebhookWorker.Discord.Prefix).ResolvePrefixAsync
                }));

                // Register event handlers for the commands extension
                eventManager.RegisterEventHandlers(commandsExtension);
            }

            // Register event handlers for the main Discord client
            eventManager.RegisterEventHandlers(discordClient);

            // Connect to Discord
            await discordClient.StartAsync();

            // Wait for commands
            await Task.Delay(-1);
        }
    }
}
