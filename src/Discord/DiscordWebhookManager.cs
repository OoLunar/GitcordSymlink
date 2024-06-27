using System;
using System.Data;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OoLunar.GitHubForumWebhookWorker.Configuration;

namespace OoLunar.GitHubForumWebhookWorker.Discord
{
    public sealed class DiscordWebhookManager : IDisposable
    {
        private readonly ILogger<DiscordWebhookManager> _logger;
        private readonly SqliteConnection _connection;

        /// <summary>
        /// This table stores GitHub users and organizations, mapping them to channel ids and webhook urls.
        /// </summary>
        private readonly SqliteCommand _createAccountTableCommand;

        /// <summary>
        /// This table stores repositories, mapping them to thread ids.
        /// </summary>
        private readonly SqliteCommand _createRepositoryTableCommand;

        private readonly SqliteCommand _createNewAccountCommand;
        private readonly SqliteCommand _createNewRepositoryCommand;
        private readonly SqliteCommand _getWebhookCommand;
        private readonly SqliteCommand _getPostIdCommand;

        /// <summary>
        /// An SQL command lock to prevent multiple SQL commands from being executed at the same time.
        /// </summary>
        private readonly SemaphoreSlim _commandLock = new(0, 1);

        public DiscordWebhookManager(GitHubForumWebhookWorkerConfiguration configuration, ILogger<DiscordWebhookManager> logger)
        {
            _logger = logger;

            SqliteConnectionStringBuilder connectionStringBuilder = new()
            {
                DataSource = configuration.Database.Path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Password = configuration.Database.Password
            };

            _connection = new SqliteConnection(connectionStringBuilder.ConnectionString);
            _connection.Open();

            _createAccountTableCommand = _connection.CreateCommand();
            _createAccountTableCommand.CommandText = @"CREATE TABLE IF NOT EXISTS Account (Name TEXT, ChannelId INTEGER NOT NULL, WebhookUrl TEXT)";
            _createAccountTableCommand.ExecuteNonQuery();

            _createRepositoryTableCommand = _connection.CreateCommand();
            _createRepositoryTableCommand.CommandText = @"CREATE TABLE IF NOT EXISTS Repository (Account TEXT, Name TEXT, ThreadId INTEGER NOT NULL)";
            _createRepositoryTableCommand.ExecuteNonQuery();

            _createNewAccountCommand = _connection.CreateCommand();
            _createNewAccountCommand.CommandText = "INSERT INTO Account (Name, ChannelId, WebhookUrl) VALUES (@Name, @ChannelId, @WebhookUrl)";
            _createNewAccountCommand.Parameters.Add(new SqliteParameter("@Name", DbType.String));
            _createNewAccountCommand.Parameters.Add(new SqliteParameter("@ChannelId", DbType.Int64));
            _createNewAccountCommand.Parameters.Add(new SqliteParameter("@WebhookUrl", DbType.String));
            _createNewAccountCommand.Prepare();

            _createNewRepositoryCommand = _connection.CreateCommand();
            _createNewRepositoryCommand.CommandText = "INSERT INTO Repository (Account, Name, ThreadId) VALUES (@Account, @Name, @ThreadId)";
            _createNewRepositoryCommand.Parameters.Add(new SqliteParameter("@Account", DbType.String));
            _createNewRepositoryCommand.Parameters.Add(new SqliteParameter("@Name", DbType.String));
            _createNewRepositoryCommand.Parameters.Add(new SqliteParameter("@ThreadId", DbType.Int64));
            _createNewRepositoryCommand.Prepare();

            _getWebhookCommand = _connection.CreateCommand();
            _getWebhookCommand.CommandText = "SELECT WebhookUrl FROM Account WHERE Name = @Name";
            _getWebhookCommand.Parameters.Add(new SqliteParameter("@Name", DbType.String));
            _getWebhookCommand.Prepare();

            _getPostIdCommand = _connection.CreateCommand();
            _getPostIdCommand.CommandText = "SELECT ThreadId FROM Repository WHERE Account = @Account AND Name = @Name";
            _getPostIdCommand.Parameters.Add(new SqliteParameter("@Account", DbType.String));
            _getPostIdCommand.Parameters.Add(new SqliteParameter("@Name", DbType.String));
            _getPostIdCommand.Prepare();

            // Release the lock
            _commandLock.Release();
        }

        private void DatabaseStateChanged(object sender, StateChangeEventArgs eventArgs)
        {
            if (eventArgs.CurrentState is not ConnectionState.Closed and not ConnectionState.Broken)
            {
                return;
            }

            _commandLock.Wait();
            while (_connection.State is ConnectionState.Closed or ConnectionState.Broken)
            {
                try
                {
                    _connection.Open();

                    _createAccountTableCommand.Connection = _connection;
                    _createAccountTableCommand.ExecuteNonQuery();

                    _createRepositoryTableCommand.Connection = _connection;
                    _createRepositoryTableCommand.ExecuteNonQuery();

                    _createNewAccountCommand.Connection = _connection;
                    _createNewAccountCommand.Prepare();

                    _createNewRepositoryCommand.Connection = _connection;
                    _createNewRepositoryCommand.Prepare();

                    _getWebhookCommand.Connection = _connection;
                    _getWebhookCommand.Prepare();

                    _getPostIdCommand.Connection = _connection;
                    _getPostIdCommand.Prepare();
                }
                catch (Exception error)
                {
                    _logger.LogError(error, "Failed to open the database connection");
                    Thread.Sleep(1000);
                }
            }

            _commandLock.Release();
        }

        public async ValueTask CreateNewAccountAsync(string accountName, ulong channelId, string? webhookUrl = null, CancellationToken cancellationToken = default)
        {
            try
            {
                await _commandLock.WaitAsync(cancellationToken);
                _createNewAccountCommand.Parameters["@Name"].Value = accountName;
                _createNewAccountCommand.Parameters["@ChannelId"].Value = channelId;
                _createNewAccountCommand.Parameters["@WebhookUrl"].Value = webhookUrl;
                await _createNewAccountCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async ValueTask CreateNewRepositoryAsync(string accountName, string repositoryName, ulong threadId, CancellationToken cancellationToken = default)
        {
            try
            {
                await _commandLock.WaitAsync(cancellationToken);
                _createNewRepositoryCommand.Parameters["@Account"].Value = accountName;
                _createNewRepositoryCommand.Parameters["@Name"].Value = repositoryName;
                _createNewRepositoryCommand.Parameters["@ThreadId"].Value = threadId;
                await _createNewRepositoryCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async ValueTask<string?> GetWebhookUrlAsync(string accountName, CancellationToken cancellationToken = default)
        {
            try
            {
                await _commandLock.WaitAsync(cancellationToken);
                _getWebhookCommand.Parameters["@Name"].Value = accountName;
                return await _getWebhookCommand.ExecuteScalarAsync(cancellationToken) as string;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async ValueTask<ulong?> GetPostIdAsync(string accountName, string repositoryName, CancellationToken cancellationToken = default)
        {
            try
            {
                await _commandLock.WaitAsync(cancellationToken);
                _getPostIdCommand.Parameters["@Account"].Value = accountName;
                _getPostIdCommand.Parameters["@Name"].Value = repositoryName;
                long value = (long)(await _getPostIdCommand.ExecuteScalarAsync(cancellationToken) ?? 0);
                return value is 0 ? null : Unsafe.As<long, ulong>(ref value);
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public void Dispose()
        {
            _connection.StateChange -= DatabaseStateChanged;
            _connection.Close();
            _connection.Dispose();
            _createAccountTableCommand.Dispose();
            _createRepositoryTableCommand.Dispose();
            _createNewAccountCommand.Dispose();
            _createNewRepositoryCommand.Dispose();
            _getWebhookCommand.Dispose();
            _getPostIdCommand.Dispose();
            _commandLock.Dispose();
        }
    }
}
