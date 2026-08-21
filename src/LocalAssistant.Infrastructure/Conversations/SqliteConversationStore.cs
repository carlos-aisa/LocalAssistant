using System.Text.Json;
using LocalAssistant.Core.Conversations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Infrastructure.Conversations;

public sealed class SqliteConversationStoreOptions
{
    public const string SectionName = "LocalAssistant:ConversationPersistence";

    public bool Enabled { get; set; }

    public string? DatabasePath { get; set; }
}

public sealed class SqliteConversationStore : IConversationStore
{
    private readonly string _connectionString;
    private readonly object _initializationSync = new();
    private Task? _initialization;

    public SqliteConversationStore(IOptions<SqliteConversationStoreOptions> options)
    {
        var databasePath = ResolveDatabasePath(options.Value.DatabasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();
    }

    public async ValueTask<ConversationMetadata> GetOrCreateMetadataAsync(Guid conversationId, string? ownerPrincipalId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ownerPrincipalId))
        {
            throw new ArgumentException("Persistent conversations require an owner.", nameof(ownerPrincipalId));
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var identifier = conversationId.ToString("N");
        await ExecuteAsync(connection, transaction, "INSERT OR IGNORE INTO Conversations (ConversationId, OwnerPrincipalId) VALUES ($id, $owner);", cancellationToken, ("$id", (object)identifier), ("$owner", ownerPrincipalId));
        var owner = await ScalarAsync(connection, transaction, "SELECT OwnerPrincipalId FROM Conversations WHERE ConversationId = $id;", cancellationToken, ("$id", (object)identifier));
        await transaction.CommitAsync(cancellationToken);
        return new(conversationId, owner ?? throw new InvalidOperationException("The persisted conversation metadata is invalid."));
    }

    public async ValueTask<ConversationMetadata?> GetMetadataAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var owner = await ScalarAsync(connection, null, "SELECT OwnerPrincipalId FROM Conversations WHERE ConversationId = $id;", cancellationToken, ("$id", conversationId.ToString("N")));
        return owner is null ? null : new(conversationId, owner);
    }

    public async ValueTask<IReadOnlyList<ConversationMessage>> GetMessagesAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT PayloadJson FROM ConversationMessages WHERE ConversationId = $id ORDER BY SequenceNumber;";
        command.Parameters.AddWithValue("$id", conversationId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var messages = new List<ConversationMessage>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var message = JsonSerializer.Deserialize<ConversationMessage>(reader.GetString(0), SerializerOptions)
                ?? throw new InvalidOperationException("A persisted conversation message is invalid.");
            messages.Add(message);
        }

        return messages;
    }

    public async ValueTask AppendAsync(Guid conversationId, ConversationMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var identifier = conversationId.ToString("N");
        var owner = await ScalarAsync(connection, transaction, "SELECT OwnerPrincipalId FROM Conversations WHERE ConversationId = $id;", cancellationToken, ("$id", (object)identifier));
        if (owner is null)
        {
            throw new InvalidOperationException("Conversation metadata must exist before appending a persisted message.");
        }

        await ExecuteAsync(connection, transaction, "INSERT INTO ConversationMessages (ConversationId, SequenceNumber, PayloadJson) SELECT $id, COALESCE(MAX(SequenceNumber) + 1, 0), $payload FROM ConversationMessages WHERE ConversationId = $id;", cancellationToken, ("$id", (object)identifier), ("$payload", JsonSerializer.Serialize(message, SerializerOptions)));
        await transaction.CommitAsync(cancellationToken);
    }

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        Task initialization;
        lock (_initializationSync)
        {
            _initialization ??= InitializeAsync();
            initialization = _initialization;
        }

        return initialization.WaitAsync(cancellationToken);
    }

    private async Task InitializeAsync()
    {
        var databasePath = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? throw new InvalidOperationException("The conversation database directory is invalid."));
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(connection, null, "CREATE TABLE IF NOT EXISTS Conversations (ConversationId TEXT PRIMARY KEY NOT NULL, OwnerPrincipalId TEXT NOT NULL); CREATE TABLE IF NOT EXISTS ConversationMessages (ConversationId TEXT NOT NULL, SequenceNumber INTEGER NOT NULL, PayloadJson TEXT NOT NULL, PRIMARY KEY (ConversationId, SequenceNumber), FOREIGN KEY (ConversationId) REFERENCES Conversations(ConversationId));", CancellationToken.None);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<string?> ScalarAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ResolveDatabasePath(string? configuredPath)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalAssistant", "conversations.db")
            : configuredPath;
        if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException("The conversation database path must be absolute.");
        return Path.GetFullPath(path);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
}

public sealed class AuthenticatedConversationStore(IConversationStore persistentStore, IConversationStore ephemeralStore) : IConversationStore
{
    public ValueTask<ConversationMetadata> GetOrCreateMetadataAsync(Guid conversationId, string? ownerPrincipalId, CancellationToken cancellationToken) =>
        ownerPrincipalId is null
            ? ephemeralStore.GetOrCreateMetadataAsync(conversationId, null, cancellationToken)
            : persistentStore.GetOrCreateMetadataAsync(conversationId, ownerPrincipalId, cancellationToken);

    public async ValueTask<ConversationMetadata?> GetMetadataAsync(Guid conversationId, CancellationToken cancellationToken) =>
        await persistentStore.GetMetadataAsync(conversationId, cancellationToken) ?? await ephemeralStore.GetMetadataAsync(conversationId, cancellationToken);

    public async ValueTask<IReadOnlyList<ConversationMessage>> GetMessagesAsync(Guid conversationId, CancellationToken cancellationToken) =>
        await persistentStore.GetMetadataAsync(conversationId, cancellationToken) is not null
            ? await persistentStore.GetMessagesAsync(conversationId, cancellationToken)
            : await ephemeralStore.GetMessagesAsync(conversationId, cancellationToken);

    public async ValueTask AppendAsync(Guid conversationId, ConversationMessage message, CancellationToken cancellationToken)
    {
        if (await persistentStore.GetMetadataAsync(conversationId, cancellationToken) is not null)
        {
            await persistentStore.AppendAsync(conversationId, message, cancellationToken);
            return;
        }

        await ephemeralStore.AppendAsync(conversationId, message, cancellationToken);
    }
}
