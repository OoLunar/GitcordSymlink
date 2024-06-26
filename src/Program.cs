using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OoLunar.GitHubForumWebhookWorker.Configuration;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;
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

            // Almost start the program
            IServiceProvider serviceProvider = services.BuildServiceProvider();
            GitHubForumWebhookWorkerConfiguration gitHubForumWebhookWorker = serviceProvider.GetRequiredService<GitHubForumWebhookWorkerConfiguration>();

            // Wait for commands
            await Task.Delay(-1);
        }
    }
}
