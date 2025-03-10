using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OoLunar.GitcordSymlink.Configuration;
using OoLunar.GitcordSymlink.Entities;

namespace OoLunar.GitcordSymlink
{
    public sealed class DatabaseManager : IDisposable
    {
        private readonly ILogger<DatabaseManager> _logger;
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
        private readonly SqliteCommand _getAccountCommand;
        private readonly SqliteCommand _getWebhookCommand;
        private readonly SqliteCommand _getPostIdCommand;
        private readonly SqliteCommand _getAllRepositoriesCommand;
        private readonly SqliteCommand _updateAccountCommand;

        /// <summary>
        /// An SQL command lock to prevent multiple SQL commands from being executed at the same time.
        /// </summary>
        private readonly SemaphoreSlim _commandLock = new(0, 1);

        public DatabaseManager(GitcordSymlinkConfiguration configuration, ILogger<DatabaseManager> logger)
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
            _createAccountTableCommand.CommandText = @"CREATE TABLE IF NOT EXISTS Account (Name TEXT, ChannelId INTEGER NOT NULL, SyncAccountOptions INTEGER NOT NULL, WebhookUrl TEXT)";
            _createAccountTableCommand.ExecuteNonQuery();

            _createRepositoryTableCommand = _connection.CreateCommand();
            _createRepositoryTableCommand.CommandText = @"CREATE TABLE IF NOT EXISTS Repository (Account TEXT, Name TEXT, ThreadId INTEGER NOT NULL)";
            _createRepositoryTableCommand.ExecuteNonQuery();

            _createNewAccountCommand = _connection.CreateCommand();
            _createNewAccountCommand.CommandText = "INSERT INTO Account (Name, ChannelId, SyncAccountOptions, WebhookUrl) VALUES (@Name, @ChannelId, @SyncAccountOptions, @WebhookUrl)";
            _createNewAccountCommand.Parameters.Add(new SqliteParameter("@Name", DbType.String));
            _createNewAccountCommand.Parameters.Add(new SqliteParameter("@ChannelId", DbType.Int64));
            _createNewAccountCommand.Parameters.Add(new SqliteParameter("@SyncAccountOptions", DbType.Int32));
            _createNewAccountCommand.Parameters.Add(new SqliteParameter("@WebhookUrl", DbType.String));
            _createNewAccountCommand.Prepare();

            _createNewRepositoryCommand = _connection.CreateCommand();
            _createNewRepositoryCommand.CommandText = "INSERT INTO Repository (Account, Name, ThreadId) VALUES (@Account, @Name, @ThreadId)";
            _createNewRepositoryCommand.Parameters.Add(new SqliteParameter("@Account", DbType.String));
            _createNewRepositoryCommand.Parameters.Add(new SqliteParameter("@Name", DbType.String));
            _createNewRepositoryCommand.Parameters.Add(new SqliteParameter("@ThreadId", DbType.Int64));
            _createNewRepositoryCommand.Prepare();

            _getAccountCommand = _connection.CreateCommand();
            _getAccountCommand.CommandText = "SELECT ChannelId, SyncAccountOptions, WebhookUrl FROM Account WHERE Name = @Name";
            _getAccountCommand.Parameters.Add(new SqliteParameter("@Name", DbType.String));
            _getAccountCommand.Prepare();

            _getWebhookCommand = _connection.CreateCommand();
            _getWebhookCommand.CommandText = "SELECT WebhookUrl FROM Account WHERE Name = @Name";
            _getWebhookCommand.Parameters.Add(new SqliteParameter("@Name", DbType.String));
            _getWebhookCommand.Prepare();

            _getPostIdCommand = _connection.CreateCommand();
            _getPostIdCommand.CommandText = "SELECT ThreadId FROM Repository WHERE Account = @Account AND Name = @Name";
            _getPostIdCommand.Parameters.Add(new SqliteParameter("@Account", DbType.String));
            _getPostIdCommand.Parameters.Add(new SqliteParameter("@Name", DbType.String));
            _getPostIdCommand.Prepare();

            _getAllRepositoriesCommand = _connection.CreateCommand();
            _getAllRepositoriesCommand.CommandText = "SELECT Name, ThreadId FROM Repository WHERE Account = @Account";
            _getAllRepositoriesCommand.Parameters.Add(new SqliteParameter("@Account", DbType.String));
            _getAllRepositoriesCommand.Prepare();

            _updateAccountCommand = _connection.CreateCommand();
            _updateAccountCommand.CommandText = "UPDATE Account SET ChannelId = @ChannelId, SyncAccountOptions = @SyncAccountOptions, WebhookUrl = @WebhookUrl WHERE Name = @Name";
            _updateAccountCommand.Parameters.Add(new SqliteParameter("@ChannelId", DbType.Int64));
            _updateAccountCommand.Parameters.Add(new SqliteParameter("@SyncAccountOptions", DbType.Int32));
            _updateAccountCommand.Parameters.Add(new SqliteParameter("@WebhookUrl", DbType.String));
            _updateAccountCommand.Parameters.Add(new SqliteParameter("@Name", DbType.String));
            _updateAccountCommand.Prepare();

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

        public async ValueTask CreateNewAccountAsync(string accountName, ulong channelId, GitcordSyncOptions syncOptions, string? webhookUrl = null, CancellationToken cancellationToken = default)
        {
            try
            {
                await _commandLock.WaitAsync(cancellationToken);
                _createNewAccountCommand.Parameters["@Name"].Value = accountName;
                _createNewAccountCommand.Parameters["@ChannelId"].Value = channelId;
                _createNewAccountCommand.Parameters["@SyncAccountOptions"].Value = (int)syncOptions;
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

        public async ValueTask<GitcordAccount?> GetAccountAsync(string accountName, CancellationToken cancellationToken = default)
        {
            try
            {
                await _commandLock.WaitAsync(cancellationToken);
                _getAccountCommand.Parameters["@Name"].Value = accountName;
                using SqliteDataReader reader = await _getAccountCommand.ExecuteReaderAsync(cancellationToken);
                return reader.Read() ? new GitcordAccount
                {
                    Name = accountName,
                    ChannelId = unchecked((ulong)reader.GetInt64(0)),
                    SyncOptions = (GitcordSyncOptions)reader.GetInt32(1),
                    WebhookUrl = reader.GetString(2)
                } : null;
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
                object? value = await _getPostIdCommand.ExecuteScalarAsync(cancellationToken);
                return value is null ? null : Convert.ToUInt64(value, CultureInfo.InvariantCulture);
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async ValueTask<IReadOnlyDictionary<string, ulong>> GetAllRepositoriesAsync(string accountName, CancellationToken cancellationToken = default)
        {
            try
            {
                await _commandLock.WaitAsync(cancellationToken);
                _getAllRepositoriesCommand.Parameters["@Account"].Value = accountName;
                using SqliteDataReader reader = await _getAllRepositoriesCommand.ExecuteReaderAsync(cancellationToken);
                Dictionary<string, ulong> repositories = [];
                while (reader.Read())
                {
                    repositories.Add(reader.GetString(0), unchecked((ulong)reader.GetInt64(1)));
                }

                return repositories;
            }
            finally
            {
                _commandLock.Release();
            }
        }

        public async ValueTask<bool> UpdateAccountAsync(GitcordAccount account, CancellationToken cancellationToken = default)
        {
            try
            {
                await _commandLock.WaitAsync(cancellationToken);
                _updateAccountCommand.Parameters["@ChannelId"].Value = account.ChannelId;
                _updateAccountCommand.Parameters["@SyncAccountOptions"].Value = (int)account.SyncOptions;
                _updateAccountCommand.Parameters["@WebhookUrl"].Value = account.WebhookUrl;
                _updateAccountCommand.Parameters["@Name"].Value = account.Name;
                return await _updateAccountCommand.ExecuteNonQueryAsync(cancellationToken) > 0;
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
            _updateAccountCommand.Dispose();
            _commandLock.Dispose();
        }
    }
}
