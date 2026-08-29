using LocalAssistant.Core.Memory;
using LocalAssistant.Infrastructure.Conversations;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace LocalAssistant.Infrastructure.Memory;

public sealed class SqlitePersonalMemoryStore : IPersonalMemoryStore
{
    private readonly string _connectionString;
    private readonly object _initializationSync = new();
    private readonly TimeProvider _clock;
    private readonly TimeSpan _retention;
    private Task? _initialization;

    public SqlitePersonalMemoryStore(
        IOptions<SqliteConversationStoreOptions> options,
        TimeProvider clock)
    {
        if (options.Value.RetentionDays <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        _clock = clock;
        _retention = TimeSpan.FromDays(options.Value.RetentionDays);
        var databasePath = ResolveDatabasePath(options.Value.DatabasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();
    }

    public async ValueTask<PersonalMemory> CreateAsync(
        string ownerPrincipalId,
        PersonalMemoryDraft draft,
        CancellationToken cancellationToken)
    {
        ValidateOwnerPrincipalId(ownerPrincipalId);
        ArgumentNullException.ThrowIfNull(draft);

        var now = _clock.GetUtcNow();
        var memory = new PersonalMemory(
            Guid.NewGuid(),
            ownerPrincipalId,
            draft.Text,
            now,
            now,
            now.Add(_retention));

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await DeleteExpiredAsync(connection, transaction, cancellationToken);
        await ExecuteAsync(
            connection,
            transaction,
            "INSERT INTO PersonalMemories (MemoryId, OwnerPrincipalId, Text, CreatedAtUnixMilliseconds, ModifiedAtUnixMilliseconds, ExpiresAtUnixMilliseconds) VALUES ($id, $owner, $text, $createdAt, $modifiedAt, $expiresAt);",
            cancellationToken,
            ("$id", memory.Id.ToString("N")),
            ("$owner", memory.OwnerPrincipalId),
            ("$text", memory.Text),
            ("$createdAt", memory.CreatedAtUtc.ToUnixTimeMilliseconds()),
            ("$modifiedAt", memory.ModifiedAtUtc.ToUnixTimeMilliseconds()),
            ("$expiresAt", memory.ExpiresAtUtc.ToUnixTimeMilliseconds()));
        await transaction.CommitAsync(cancellationToken);

        return memory;
    }

    public async ValueTask<IReadOnlyList<PersonalMemory>> ListOwnedAsync(
        string ownerPrincipalId,
        PersonalMemoryListQuery query,
        CancellationToken cancellationToken)
    {
        ValidateOwnerPrincipalId(ownerPrincipalId);
        ArgumentNullException.ThrowIfNull(query);

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await DeleteExpiredAsync(connection, transaction, cancellationToken);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT MemoryId, Text, CreatedAtUnixMilliseconds, ModifiedAtUnixMilliseconds, ExpiresAtUnixMilliseconds FROM PersonalMemories WHERE OwnerPrincipalId = $owner AND ExpiresAtUnixMilliseconds > $now ORDER BY ModifiedAtUnixMilliseconds DESC, MemoryId DESC LIMIT $limit;";
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$now", _clock.GetUtcNow().ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$limit", query.Limit);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var memories = new List<PersonalMemory>();
        while (await reader.ReadAsync(cancellationToken))
        {
            memories.Add(new PersonalMemory(
                Guid.ParseExact(reader.GetString(0), "N"),
                ownerPrincipalId,
                reader.GetString(1),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4))));
        }

        await transaction.CommitAsync(cancellationToken);
        return memories;
    }

    public async ValueTask<bool> DeleteOwnedAsync(
        Guid memoryId,
        string ownerPrincipalId,
        CancellationToken cancellationToken)
    {
        ValidateOwnerPrincipalId(ownerPrincipalId);

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await DeleteExpiredAsync(connection, transaction, cancellationToken);
        var deleted = await ExecuteAsync(
            connection,
            transaction,
            "DELETE FROM PersonalMemories WHERE MemoryId = $id AND OwnerPrincipalId = $owner;",
            cancellationToken,
            ("$id", memoryId.ToString("N")),
            ("$owner", ownerPrincipalId));
        await transaction.CommitAsync(cancellationToken);

        return deleted > 0;
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
        Directory.CreateDirectory(
            Path.GetDirectoryName(databasePath)
            ?? throw new InvalidOperationException("The personal memory database directory is invalid."));

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            null,
            "CREATE TABLE IF NOT EXISTS PersonalMemories (MemoryId TEXT PRIMARY KEY NOT NULL, OwnerPrincipalId TEXT NOT NULL, Text TEXT NOT NULL, CreatedAtUnixMilliseconds INTEGER NOT NULL, ModifiedAtUnixMilliseconds INTEGER NOT NULL, ExpiresAtUnixMilliseconds INTEGER NOT NULL); CREATE INDEX IF NOT EXISTS IX_PersonalMemories_Owner_Expires_Modified ON PersonalMemories (OwnerPrincipalId, ExpiresAtUnixMilliseconds, ModifiedAtUnixMilliseconds DESC);",
            CancellationToken.None);
    }

    private async ValueTask<int> DeleteExpiredAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken) =>
        await ExecuteAsync(
            connection,
            transaction,
            "DELETE FROM PersonalMemories WHERE ExpiresAtUnixMilliseconds <= $now;",
            cancellationToken,
            ("$now", _clock.GetUtcNow().ToUnixTimeMilliseconds()));

    private static async Task<int> ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ResolveDatabasePath(string? configuredPath)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LocalAssistant",
                "conversations.db")
            : configuredPath;
        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException("The personal memory database path must be absolute.");
        }

        return Path.GetFullPath(path);
    }

    private static void ValidateOwnerPrincipalId(string ownerPrincipalId)
    {
        if (string.IsNullOrWhiteSpace(ownerPrincipalId))
        {
            throw new ArgumentException(
                "Personal memories require an owner.",
                nameof(ownerPrincipalId));
        }
    }
}
