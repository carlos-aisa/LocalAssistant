using LocalAssistant.Core.Documents;
using Microsoft.Data.Sqlite;

namespace LocalAssistant.Infrastructure.Documents;

public sealed class SqliteDocumentSemanticIndex : IDocumentSemanticIndex
{
    private readonly string _connectionString;
    private readonly object _sync = new();
    private Task? _initialization;

    public SqliteDocumentSemanticIndex(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();
    }

    public async ValueTask ReplaceAsync(
        string relativePath,
        long sizeBytes,
        DateTimeOffset lastModifiedUtc,
        IReadOnlyList<string> chunks,
        CancellationToken cancellationToken)
    {
        Validate(relativePath, sizeBytes, lastModifiedUtc, chunks);

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        await ExecuteAsync(
            connection,
            transaction,
            "DELETE FROM DocumentSemanticChunks WHERE RelativePath = $path;",
            cancellationToken,
            ("$path", relativePath));

        for (var position = 0; position < chunks.Count; position++)
        {
            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO DocumentSemanticChunks
                    (RelativePath, SizeBytes, LastModifiedUnixMilliseconds, Position, Text)
                VALUES
                    ($path, $size, $modified, $position, $text);
                """,
                cancellationToken,
                ("$path", relativePath),
                ("$size", sizeBytes),
                ("$modified", lastModifiedUtc.ToUnixTimeMilliseconds()),
                ("$position", position),
                ("$text", chunks[position]));
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask RemoveAsync(string relativePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(
            connection,
            null,
            "DELETE FROM DocumentSemanticChunks WHERE RelativePath = $path;",
            cancellationToken,
            ("$path", relativePath));
    }

    public async ValueTask<IReadOnlyList<DocumentSemanticChunk>> GetChunksAsync(string relativePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT RelativePath, SizeBytes, LastModifiedUnixMilliseconds, Position, Text
            FROM DocumentSemanticChunks
            WHERE RelativePath = $path
            ORDER BY Position;
            """;
        command.Parameters.AddWithValue("$path", relativePath);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var chunks = new List<DocumentSemanticChunk>();

        while (await reader.ReadAsync(cancellationToken))
        {
            chunks.Add(
                new DocumentSemanticChunk(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                    reader.GetInt32(3),
                    reader.GetString(4)));
        }

        return chunks;
    }

    public async ValueTask<IReadOnlyList<IndexedDocument>> GetDocumentsAsync(
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT RelativePath, SizeBytes, LastModifiedUnixMilliseconds
            FROM DocumentSemanticChunks
            GROUP BY RelativePath, SizeBytes, LastModifiedUnixMilliseconds
            ORDER BY RelativePath;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var documents = new List<IndexedDocument>();

        while (await reader.ReadAsync(cancellationToken))
        {
            documents.Add(
                new IndexedDocument(
                    reader.GetString(0),
                    reader.GetInt64(1),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2))));
        }

        return documents;
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
        lock (_sync)
        {
            _initialization ??= InitializeAsync();
        }

        return _initialization.WaitAsync(cancellationToken);
    }

    private async Task InitializeAsync()
    {
        var path = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The index directory is invalid.");

        Directory.CreateDirectory(directory);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await ExecuteAsync(
            connection,
            null,
            """
            CREATE TABLE IF NOT EXISTS DocumentSemanticChunks (
                RelativePath TEXT NOT NULL,
                SizeBytes INTEGER NOT NULL,
                LastModifiedUnixMilliseconds INTEGER NOT NULL,
                Position INTEGER NOT NULL,
                Text TEXT NOT NULL,
                PRIMARY KEY (RelativePath, Position)
            );
            """,
            CancellationToken.None);
    }

    private static void Validate(
        string relativePath,
        long sizeBytes,
        DateTimeOffset lastModifiedUtc,
        IReadOnlyList<string> chunks)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentOutOfRangeException.ThrowIfNegative(sizeBytes);
        if (lastModifiedUtc == default || chunks.Count == 0 || chunks.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("The document chunks are invalid.");
        }
    }

    private static async Task ExecuteAsync(
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

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
