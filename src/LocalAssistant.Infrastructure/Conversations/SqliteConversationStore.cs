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

    public int RetentionDays { get; set; } = 30;
}

public sealed class SqliteConversationStore : IConversationStore, IConversationContextRetriever
{
    public const int MaximumListPageSize = 50;
    public const int MaximumHistoryPageSize = 100;
    private const string UntitledConversation = "Untitled conversation";
    private const int MaximumTitleLength = 80;
    private readonly string _connectionString;
    private readonly object _initializationSync = new();
    private readonly TimeProvider _clock;
    private readonly TimeSpan _retention;
    private readonly ConversationRetrievalOptions _retrievalOptions;
    private Task? _initialization;

    public SqliteConversationStore(
        IOptions<SqliteConversationStoreOptions> options,
        IOptions<ConversationRetrievalOptions> retrievalOptions,
        TimeProvider clock)
    {
        _clock = clock;
        if (options.Value.RetentionDays <= 0) throw new ArgumentOutOfRangeException(nameof(options));
        _retention = TimeSpan.FromDays(options.Value.RetentionDays);
        _retrievalOptions = retrievalOptions.Value;
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
        await DeleteExpiredAsync(connection, transaction, cancellationToken);
        var now = _clock.GetUtcNow();
        await ExecuteAsync(connection, transaction, "INSERT OR IGNORE INTO Conversations (ConversationId, OwnerPrincipalId, ExpiresAtUnixMilliseconds, LastActivityUnixMilliseconds) VALUES ($id, $owner, $expires, $lastActivity);", cancellationToken, ("$id", (object)identifier), ("$owner", ownerPrincipalId), ("$expires", now.Add(_retention).ToUnixTimeMilliseconds()), ("$lastActivity", now.ToUnixTimeMilliseconds()));
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
        await ExecuteAsync(
            connection,
            transaction,
            "UPDATE Conversations SET LastActivityUnixMilliseconds = $lastActivity, IndexingRequestedAtUnixMilliseconds = NULL WHERE ConversationId = $id;",
            cancellationToken,
            ("$id", (object)identifier),
            ("$lastActivity", _clock.GetUtcNow().ToUnixTimeMilliseconds()));
        await RefreshSearchDocumentAsync(connection, transaction, identifier, owner, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask<bool> DeleteOwnedAsync(Guid conversationId, string ownerPrincipalId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var identifier = conversationId.ToString("N");
        await ExecuteAsync(connection, transaction, "DELETE FROM ConversationSearchDocuments WHERE ConversationId = $id AND OwnerPrincipalId = $owner; DELETE FROM ConversationSearch WHERE ConversationId = $id AND OwnerPrincipalId = $owner;", cancellationToken, ("$id", (object)identifier), ("$owner", ownerPrincipalId));
        await ExecuteAsync(connection, transaction, "DELETE FROM ConversationMessages WHERE ConversationId = $id AND EXISTS (SELECT 1 FROM Conversations WHERE ConversationId = $id AND OwnerPrincipalId = $owner);", cancellationToken, ("$id", (object)identifier), ("$owner", ownerPrincipalId));
        var deleted = await ExecuteAsync(connection, transaction, "DELETE FROM Conversations WHERE ConversationId = $id AND OwnerPrincipalId = $owner;", cancellationToken, ("$id", (object)identifier), ("$owner", ownerPrincipalId));
        await transaction.CommitAsync(cancellationToken);
        return deleted > 0;
    }

    public async ValueTask<ConversationPage<ConversationSummary>> ListOwnedAsync(
        string ownerPrincipalId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        var pageSize = NormalizePageSize(limit, MaximumListPageSize);
        var cursorValue = ConversationListCursor.Parse(cursor);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ConversationId, LastActivityUnixMilliseconds, IndexingRequestedAtUnixMilliseconds FROM Conversations WHERE OwnerPrincipalId = $owner AND ($cursorActivity IS NULL OR LastActivityUnixMilliseconds < $cursorActivity OR (LastActivityUnixMilliseconds = $cursorActivity AND ConversationId > $cursorId)) ORDER BY LastActivityUnixMilliseconds DESC, ConversationId ASC LIMIT $limit;";
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$cursorActivity", cursorValue?.LastActivityUnixMilliseconds ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$cursorId", cursorValue?.ConversationId.ToString("N") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$limit", pageSize + 1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<ConversationListRow>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ConversationListRow(
                Guid.ParseExact(reader.GetString(0), "N"),
                reader.GetInt64(1),
                reader.IsDBNull(2) ? null : reader.GetInt64(2)));
        }
        await reader.DisposeAsync();

        var hasMore = rows.Count > pageSize;
        var pageRows = rows.Take(pageSize).ToArray();
        var items = new List<ConversationSummary>(pageRows.Length);
        foreach (var row in pageRows)
        {
            items.Add(new ConversationSummary(
                row.ConversationId,
                await GetTitleAsync(connection, row.ConversationId, cancellationToken),
                DateTimeOffset.FromUnixTimeMilliseconds(row.LastActivityUnixMilliseconds),
                row.IndexingRequestedAtUnixMilliseconds is long indexingRequested
                    ? DateTimeOffset.FromUnixTimeMilliseconds(indexingRequested)
                    : null));
        }

        var nextCursor = hasMore
            ? ConversationListCursor.Create(pageRows[^1].LastActivityUnixMilliseconds, pageRows[^1].ConversationId)
            : null;
        return new ConversationPage<ConversationSummary>(items, nextCursor);
    }

    public async ValueTask<ConversationDetails?> GetOwnedDetailsAsync(
        Guid conversationId,
        string ownerPrincipalId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT LastActivityUnixMilliseconds, IndexingRequestedAtUnixMilliseconds FROM Conversations WHERE ConversationId = $id AND OwnerPrincipalId = $owner;";
        command.Parameters.AddWithValue("$id", conversationId.ToString("N"));
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var lastActivity = reader.GetInt64(0);
        long? indexingRequested = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        await reader.DisposeAsync();
        return new ConversationDetails(
            conversationId,
            await GetTitleAsync(connection, conversationId, cancellationToken),
            DateTimeOffset.FromUnixTimeMilliseconds(lastActivity),
            indexingRequested is long timestamp
                ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
                : null);
    }

    public async ValueTask<ConversationPage<PublicConversationMessage>?> GetOwnedHistoryAsync(
        Guid conversationId,
        string ownerPrincipalId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerPrincipalId);
        var pageSize = NormalizePageSize(limit, MaximumHistoryPageSize);
        var cursorValue = ConversationHistoryCursor.Parse(cursor);
        await using var connection = await OpenAsync(cancellationToken);
        if (!await ExistsOwnedAsync(connection, conversationId, ownerPrincipalId, cancellationToken))
        {
            return null;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SequenceNumber, PayloadJson FROM ConversationMessages WHERE ConversationId = $id AND SequenceNumber > $cursor ORDER BY SequenceNumber ASC;";
        command.Parameters.AddWithValue("$id", conversationId.ToString("N"));
        command.Parameters.AddWithValue("$cursor", cursorValue?.SequenceNumber ?? -1);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var messages = new List<PublicConversationMessage>();
        long? lastSequenceNumber = null;
        var hasMore = false;
        while (await reader.ReadAsync(cancellationToken))
        {
            var message = JsonSerializer.Deserialize<ConversationMessage>(reader.GetString(1), SerializerOptions);
            if (message is null ||
                message.Role is not (ConversationRole.User or ConversationRole.Assistant) ||
                string.IsNullOrWhiteSpace(message.Content))
            {
                continue;
            }

            if (messages.Count == pageSize)
            {
                hasMore = true;
                break;
            }

            messages.Add(new PublicConversationMessage(message.Role, message.Content));
            lastSequenceNumber = reader.GetInt64(0);
        }

        return new ConversationPage<PublicConversationMessage>(
            messages,
            hasMore && lastSequenceNumber is long sequenceNumber
                ? ConversationHistoryCursor.Create(sequenceNumber)
                : null);
    }

    public async ValueTask<bool> RequestImmediateIndexingAsync(
        Guid conversationId,
        string ownerPrincipalId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var updated = await ExecuteAsync(
            connection,
            null,
            "UPDATE Conversations SET IndexingRequestedAtUnixMilliseconds = COALESCE(IndexingRequestedAtUnixMilliseconds, $requestedAt) WHERE ConversationId = $id AND OwnerPrincipalId = $owner AND EXISTS (SELECT 1 FROM ConversationSearchDocuments WHERE ConversationId = $id AND OwnerPrincipalId = $owner);",
            cancellationToken,
            ("$requestedAt", _clock.GetUtcNow().ToUnixTimeMilliseconds()),
            ("$id", conversationId.ToString("N")),
            ("$owner", ownerPrincipalId));
        return updated == 1;
    }

    public async ValueTask<int> DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var deleted = await DeleteExpiredAsync(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    public async ValueTask<ConversationRetrievalResult> RetrieveAsync(
        string ownerPrincipalId,
        Guid currentConversationId,
        string message,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(ownerPrincipalId) ||
            string.IsNullOrWhiteSpace(message))
        {
            return ConversationRetrievalResult.Empty;
        }

        if (!_retrievalOptions.Enabled)
        {
            return ConversationRetrievalResult.Empty;
        }

        var query = CreateSearchQuery(message);
        if (query is null)
        {
            return ConversationRetrievalResult.Empty;
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT document.ConversationId, document.LastActivityUnixMilliseconds, document.SearchText, document.Topic, document.Summary FROM ConversationSearch search INNER JOIN ConversationSearchDocuments document ON document.ConversationId = search.ConversationId AND document.OwnerPrincipalId = search.OwnerPrincipalId WHERE search.OwnerPrincipalId = $owner AND search.ConversationId <> $currentConversationId AND search.SearchText MATCH $query ORDER BY rank LIMIT $limit;";
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$currentConversationId", currentConversationId.ToString("N"));
        command.Parameters.AddWithValue("$query", query);
        command.Parameters.AddWithValue("$limit", _retrievalOptions.MaximumMatches);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var matches = new List<ConversationRetrievedContext>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var text = reader.GetString(2);
            var fragment = LimitText(text, _retrievalOptions.MaximumContextCharacters);
            matches.Add(new ConversationRetrievedContext(
                Guid.ParseExact(reader.GetString(0), "N"),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                reader.IsDBNull(3) ? LimitText(text, 80) : reader.GetString(3),
                reader.IsDBNull(4) ? LimitText(text, 300) : reader.GetString(4),
                fragment,
                1));
        }

        return matches.Count == 0
            ? ConversationRetrievalResult.Empty
            : new ConversationRetrievalResult(matches);
    }

    public async ValueTask<IReadOnlyList<ConversationRetrievedContext>>
        RetrieveByEmbeddingAsync(
            string ownerPrincipalId,
            Guid currentConversationId,
            TextEmbedding queryEmbedding,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queryEmbedding);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT ConversationId, LastActivityUnixMilliseconds, COALESCE(IndexedSearchText, SearchText), EmbeddingJson, Topic, Summary FROM ConversationSearchDocuments WHERE OwnerPrincipalId = $owner AND ConversationId <> $currentConversationId AND EmbeddingModel = $model AND EmbeddingJson IS NOT NULL ORDER BY LastActivityUnixMilliseconds DESC LIMIT 50;";
        command.Parameters.AddWithValue("$owner", ownerPrincipalId);
        command.Parameters.AddWithValue("$currentConversationId", currentConversationId.ToString("N"));
        command.Parameters.AddWithValue("$model", queryEmbedding.Model);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var matches = new List<ConversationRetrievedContext>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var values = JsonSerializer.Deserialize<IReadOnlyList<float>>(
                reader.GetString(3),
                SerializerOptions);
            if (values is null || values.Count != queryEmbedding.Values.Count ||
                values.Any(value => !float.IsFinite(value)))
            {
                continue;
            }

            var score = CalculateCosineSimilarity(queryEmbedding.Values, values);
            if (score < 0.65)
            {
                continue;
            }

            var text = reader.GetString(2);
            matches.Add(new ConversationRetrievedContext(
                Guid.ParseExact(reader.GetString(0), "N"),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                reader.IsDBNull(4) ? LimitText(text, 80) : reader.GetString(4),
                reader.IsDBNull(5) ? LimitText(text, 300) : reader.GetString(5),
                LimitText(text, _retrievalOptions.MaximumContextCharacters),
                score));
        }

        return matches;
    }

    public async ValueTask<IReadOnlyList<ConversationEmbeddingIndexCandidate>>
        ListPendingEmbeddingIndexesAsync(CancellationToken cancellationToken)
    {
        var cutoff = _clock.GetUtcNow().Subtract(_retrievalOptions.IndexingDelay)
            .ToUnixTimeMilliseconds();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT document.ConversationId, document.OwnerPrincipalId, document.LastActivityUnixMilliseconds, document.SearchText, document.SearchVersion, document.IndexedSearchVersion, document.SummaryIndexedVersion FROM ConversationSearchDocuments document INNER JOIN Conversations conversation ON conversation.ConversationId = document.ConversationId AND conversation.OwnerPrincipalId = document.OwnerPrincipalId WHERE (conversation.LastActivityUnixMilliseconds <= $cutoff OR conversation.IndexingRequestedAtUnixMilliseconds IS NOT NULL) AND (document.IndexedSearchVersion IS NULL OR document.IndexedSearchVersion <> document.SearchVersion OR document.SummaryIndexedVersion IS NULL OR document.SummaryIndexedVersion <> document.SearchVersion) ORDER BY conversation.IndexingRequestedAtUnixMilliseconds DESC, document.LastActivityUnixMilliseconds LIMIT 10;";
        command.Parameters.AddWithValue("$cutoff", cutoff);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var candidates = new List<ConversationEmbeddingIndexCandidate>();
        while (await reader.ReadAsync(cancellationToken))
        {
            candidates.Add(new ConversationEmbeddingIndexCandidate(
                Guid.ParseExact(reader.GetString(0), "N"),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.IsDBNull(5) || reader.GetInt64(5) != reader.GetInt64(4),
                reader.IsDBNull(6) || reader.GetInt64(6) != reader.GetInt64(4)));
        }

        return candidates;
    }

    public async ValueTask<bool> StoreIndexAsync(
        ConversationEmbeddingIndexCandidate candidate,
        TextEmbedding? embedding,
        ConversationIndexSummary? summary,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (embedding is null && summary is null)
        {
            return false;
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE ConversationSearchDocuments SET EmbeddingModel = COALESCE($model, EmbeddingModel), EmbeddingJson = COALESCE($embedding, EmbeddingJson), IndexedSearchText = CASE WHEN $embedding IS NULL THEN IndexedSearchText ELSE SearchText END, Topic = COALESCE($topic, Topic), Summary = COALESCE($summary, Summary), KeywordsJson = COALESCE($keywords, KeywordsJson), IndexedActivityUnixMilliseconds = CASE WHEN $embedding IS NULL THEN IndexedActivityUnixMilliseconds ELSE $activity END, SummaryIndexedActivityUnixMilliseconds = CASE WHEN $summary IS NULL THEN SummaryIndexedActivityUnixMilliseconds ELSE $activity END, IndexedSearchVersion = CASE WHEN $embedding IS NULL THEN IndexedSearchVersion ELSE $version END, SummaryIndexedVersion = CASE WHEN $summary IS NULL THEN SummaryIndexedVersion ELSE $version END WHERE ConversationId = $id AND OwnerPrincipalId = $owner AND SearchVersion = $version;";
        command.Parameters.AddWithValue("$model", (object?)embedding?.Model ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$embedding",
            embedding is null
                ? DBNull.Value
                : JsonSerializer.Serialize(embedding.Values, SerializerOptions));
        command.Parameters.AddWithValue("$topic", (object?)summary?.Topic ?? DBNull.Value);
        command.Parameters.AddWithValue("$summary", (object?)summary?.Summary ?? DBNull.Value);
        command.Parameters.AddWithValue("$keywords", summary is null ? DBNull.Value : JsonSerializer.Serialize(summary.Keywords, SerializerOptions));
        command.Parameters.AddWithValue("$activity", candidate.LastActivityUnixMilliseconds);
        command.Parameters.AddWithValue("$version", candidate.SearchVersion);
        command.Parameters.AddWithValue("$id", candidate.ConversationId.ToString("N"));
        command.Parameters.AddWithValue("$owner", candidate.OwnerPrincipalId);
        var updated = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        if (updated)
        {
            await ExecuteAsync(
                connection,
                transaction,
                "UPDATE Conversations SET IndexingRequestedAtUnixMilliseconds = NULL WHERE ConversationId = $id AND OwnerPrincipalId = $owner AND NOT EXISTS (SELECT 1 FROM ConversationSearchDocuments WHERE ConversationId = $id AND OwnerPrincipalId = $owner AND (IndexedSearchVersion IS NULL OR IndexedSearchVersion <> SearchVersion OR SummaryIndexedVersion IS NULL OR SummaryIndexedVersion <> SearchVersion));",
                cancellationToken,
                ("$id", candidate.ConversationId.ToString("N")),
                ("$owner", candidate.OwnerPrincipalId));
        }

        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    private static int NormalizePageSize(int requestedLimit, int maximumLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedLimit);

        return Math.Min(requestedLimit, maximumLimit);
    }

    private static async Task<bool> ExistsOwnedAsync(
        SqliteConnection connection,
        Guid conversationId,
        string ownerPrincipalId,
        CancellationToken cancellationToken)
    {
        var owner = await ScalarAsync(
            connection,
            null,
            "SELECT OwnerPrincipalId FROM Conversations WHERE ConversationId = $id AND OwnerPrincipalId = $owner;",
            cancellationToken,
            ("$id", conversationId.ToString("N")),
            ("$owner", ownerPrincipalId));
        return owner is not null;
    }

    private static async Task<string> GetTitleAsync(
        SqliteConnection connection,
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT PayloadJson FROM ConversationMessages WHERE ConversationId = $id ORDER BY SequenceNumber ASC;";
        command.Parameters.AddWithValue("$id", conversationId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var message = JsonSerializer.Deserialize<ConversationMessage>(reader.GetString(0), SerializerOptions);
            if (message?.Role != ConversationRole.User || string.IsNullOrWhiteSpace(message.Content))
            {
                continue;
            }

            return LimitText(message.Content.Trim(), MaximumTitleLength);
        }

        return UntitledConversation;
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
        await ExecuteAsync(connection, null, "CREATE TABLE IF NOT EXISTS Conversations (ConversationId TEXT PRIMARY KEY NOT NULL, OwnerPrincipalId TEXT NOT NULL, ExpiresAtUnixMilliseconds INTEGER NOT NULL, LastActivityUnixMilliseconds INTEGER NOT NULL DEFAULT 0, IndexingRequestedAtUnixMilliseconds INTEGER NULL); CREATE TABLE IF NOT EXISTS ConversationMessages (ConversationId TEXT NOT NULL, SequenceNumber INTEGER NOT NULL, PayloadJson TEXT NOT NULL, PRIMARY KEY (ConversationId, SequenceNumber), FOREIGN KEY (ConversationId) REFERENCES Conversations(ConversationId)); CREATE TABLE IF NOT EXISTS ConversationSearchDocuments (ConversationId TEXT PRIMARY KEY NOT NULL, OwnerPrincipalId TEXT NOT NULL, LastActivityUnixMilliseconds INTEGER NOT NULL, SearchText TEXT NOT NULL, IndexedSearchText TEXT NULL, EmbeddingModel TEXT NULL, EmbeddingJson TEXT NULL, Topic TEXT NULL, Summary TEXT NULL, KeywordsJson TEXT NULL, IndexedActivityUnixMilliseconds INTEGER NULL, SummaryIndexedActivityUnixMilliseconds INTEGER NULL); CREATE VIRTUAL TABLE IF NOT EXISTS ConversationSearch USING fts5(ConversationId UNINDEXED, OwnerPrincipalId UNINDEXED, SearchText);", CancellationToken.None);
        try
        {
            await ExecuteAsync(connection, null, "ALTER TABLE Conversations ADD COLUMN ExpiresAtUnixMilliseconds INTEGER NOT NULL DEFAULT 0;", CancellationToken.None);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 1 && exception.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
        }
        await AddConversationColumnIfMissingAsync(
            connection,
            "IndexingRequestedAtUnixMilliseconds INTEGER NULL");
        await AddSearchDocumentColumnIfMissingAsync(
            connection,
            "EmbeddingModel TEXT NULL");
        await AddSearchDocumentColumnIfMissingAsync(
            connection,
            "EmbeddingJson TEXT NULL");
        await AddSearchDocumentColumnIfMissingAsync(
            connection,
            "IndexedSearchText TEXT NULL");
        await AddSearchDocumentColumnIfMissingAsync(connection, "SearchVersion INTEGER NOT NULL DEFAULT 0");
        await AddSearchDocumentColumnIfMissingAsync(connection, "IndexedSearchVersion INTEGER NULL");
        await AddSearchDocumentColumnIfMissingAsync(connection, "SummaryIndexedVersion INTEGER NULL");
        await AddSearchDocumentColumnIfMissingAsync(connection, "IndexedActivityUnixMilliseconds INTEGER NULL");
        await AddSearchDocumentColumnIfMissingAsync(connection, "SummaryIndexedActivityUnixMilliseconds INTEGER NULL");
        await AddSearchDocumentColumnIfMissingAsync(connection, "Topic TEXT NULL");
        await AddSearchDocumentColumnIfMissingAsync(connection, "Summary TEXT NULL");
        await AddSearchDocumentColumnIfMissingAsync(connection, "KeywordsJson TEXT NULL");
        try
        {
            await ExecuteAsync(connection, null, "ALTER TABLE Conversations ADD COLUMN LastActivityUnixMilliseconds INTEGER NOT NULL DEFAULT 0;", CancellationToken.None);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 1 && exception.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    private async Task<int> DeleteExpiredAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken) =>
        await ExecuteAsync(connection, transaction, "DELETE FROM ConversationSearchDocuments WHERE ConversationId IN (SELECT ConversationId FROM Conversations WHERE ExpiresAtUnixMilliseconds <= $now); DELETE FROM ConversationSearch WHERE ConversationId IN (SELECT ConversationId FROM Conversations WHERE ExpiresAtUnixMilliseconds <= $now); DELETE FROM ConversationMessages WHERE ConversationId IN (SELECT ConversationId FROM Conversations WHERE ExpiresAtUnixMilliseconds <= $now); DELETE FROM Conversations WHERE ExpiresAtUnixMilliseconds <= $now;", cancellationToken, ("$now", _clock.GetUtcNow().ToUnixTimeMilliseconds()));

    private static async Task RefreshSearchDocumentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string conversationId,
        string ownerPrincipalId,
        CancellationToken cancellationToken)
    {
        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT PayloadJson FROM ConversationMessages WHERE ConversationId = $id ORDER BY SequenceNumber;";
        select.Parameters.AddWithValue("$id", conversationId);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken);
        var contents = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var message = JsonSerializer.Deserialize<ConversationMessage>(reader.GetString(0), SerializerOptions);
            if (message is not null &&
                message.Role is ConversationRole.User or ConversationRole.Assistant &&
                !string.IsNullOrWhiteSpace(message.Content))
            {
                contents.Add(message.Content);
            }
        }

        var searchText = string.Join(Environment.NewLine, contents);
        await ExecuteAsync(connection, transaction, "DELETE FROM ConversationSearch WHERE ConversationId = $id;", cancellationToken, ("$id", conversationId));
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return;
        }

        var updated = await ExecuteAsync(connection, transaction, "UPDATE ConversationSearchDocuments SET LastActivityUnixMilliseconds = (SELECT LastActivityUnixMilliseconds FROM Conversations WHERE ConversationId = $id AND OwnerPrincipalId = $owner), SearchText = $text, SearchVersion = SearchVersion + 1 WHERE ConversationId = $id AND OwnerPrincipalId = $owner;", cancellationToken, ("$id", conversationId), ("$owner", ownerPrincipalId), ("$text", searchText));
        if (updated == 0)
        {
            await ExecuteAsync(connection, transaction, "INSERT INTO ConversationSearchDocuments (ConversationId, OwnerPrincipalId, LastActivityUnixMilliseconds, SearchText, SearchVersion) SELECT ConversationId, OwnerPrincipalId, LastActivityUnixMilliseconds, $text, 1 FROM Conversations WHERE ConversationId = $id AND OwnerPrincipalId = $owner;", cancellationToken, ("$id", conversationId), ("$owner", ownerPrincipalId), ("$text", searchText));
        }

        await ExecuteAsync(connection, transaction, "INSERT INTO ConversationSearch (ConversationId, OwnerPrincipalId, SearchText) VALUES ($id, $owner, $text);", cancellationToken, ("$id", conversationId), ("$owner", ownerPrincipalId), ("$text", searchText));
    }

    private static string? CreateSearchQuery(string message)
    {
        var tokens = message
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => new string(token.Where(char.IsLetterOrDigit).ToArray()))
            .Where(token => token.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();

        return tokens.Length == 0
            ? null
            : string.Join(" OR ", tokens.Select(token => $"\"{token}\""));
    }

    private static string LimitText(string text, int maximumLength) =>
        text.Length <= maximumLength
            ? text
            : text[..maximumLength] + "…";

    private static double CalculateCosineSimilarity(
        IReadOnlyList<float> left,
        IReadOnlyList<float> right)
    {
        double dotProduct = 0;
        double leftMagnitude = 0;
        double rightMagnitude = 0;
        for (var index = 0; index < left.Count; index++)
        {
            dotProduct += left[index] * right[index];
            leftMagnitude += left[index] * left[index];
            rightMagnitude += right[index] * right[index];
        }

        if (leftMagnitude == 0 || rightMagnitude == 0)
        {
            return 0;
        }

        return dotProduct / Math.Sqrt(leftMagnitude * rightMagnitude);
    }

    private static async Task AddSearchDocumentColumnIfMissingAsync(
        SqliteConnection connection,
        string columnDefinition)
    {
        try
        {
            await ExecuteAsync(
                connection,
                null,
                $"ALTER TABLE ConversationSearchDocuments ADD COLUMN {columnDefinition};",
                CancellationToken.None);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 1 &&
                                               exception.Message.Contains(
                                                   "duplicate column name",
                                                   StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    private static async Task AddConversationColumnIfMissingAsync(
        SqliteConnection connection,
        string columnDefinition)
    {
        try
        {
            await ExecuteAsync(
                connection,
                null,
                $"ALTER TABLE Conversations ADD COLUMN {columnDefinition};",
                CancellationToken.None);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 1 &&
                                               exception.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    private static async Task<int> ExecuteAsync(SqliteConnection connection, SqliteTransaction? transaction, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return await command.ExecuteNonQueryAsync(cancellationToken);
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

    private sealed record ConversationListCursor(long LastActivityUnixMilliseconds, Guid ConversationId)
    {
        public static string Create(long lastActivityUnixMilliseconds, Guid conversationId) =>
            Encode(new ConversationListCursor(lastActivityUnixMilliseconds, conversationId));

        public static ConversationListCursor? Parse(string? value) =>
            string.IsNullOrWhiteSpace(value)
                ? null
                : Decode<ConversationListCursor>(value);
    }

    private sealed record ConversationHistoryCursor(long SequenceNumber)
    {
        public static string Create(long sequenceNumber) =>
            Encode(new ConversationHistoryCursor(sequenceNumber));

        public static ConversationHistoryCursor? Parse(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var cursor = Decode<ConversationHistoryCursor>(value);
            if (cursor.SequenceNumber < 0)
            {
                throw new ArgumentException("The conversation history cursor is invalid.", nameof(value));
            }

            return cursor;
        }
    }

    private sealed record ConversationListRow(
        Guid ConversationId,
        long LastActivityUnixMilliseconds,
        long? IndexingRequestedAtUnixMilliseconds);

    private static string Encode<T>(T value) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value, SerializerOptions))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static T Decode<T>(string value)
    {
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            return JsonSerializer.Deserialize<T>(Convert.FromBase64String(padded), SerializerOptions)
                ?? throw new ArgumentException("The conversation cursor is invalid.", nameof(value));
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The conversation cursor is invalid.", nameof(value), exception);
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
}

public sealed record ConversationEmbeddingIndexCandidate(
    Guid ConversationId,
    string OwnerPrincipalId,
    long LastActivityUnixMilliseconds,
    string Text,
    long SearchVersion,
    bool RequiresEmbedding,
    bool RequiresSummary);

public sealed class AuthenticatedConversationStore(IConversationStore persistentStore, IConversationStore ephemeralStore) : IConversationStore
{
    public async ValueTask<ConversationMetadata> GetOrCreateMetadataAsync(
        Guid conversationId,
        string? ownerPrincipalId,
        CancellationToken cancellationToken)
    {
        if (ownerPrincipalId is not null)
        {
            return await persistentStore.GetOrCreateMetadataAsync(
                conversationId,
                ownerPrincipalId,
                cancellationToken);
        }

        return await ephemeralStore.GetOrCreateMetadataAsync(
            conversationId,
            null,
            cancellationToken);
    }

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

    public ValueTask<bool> DeleteOwnedAsync(
        Guid conversationId,
        string ownerPrincipalId,
        CancellationToken cancellationToken) =>
        persistentStore.DeleteOwnedAsync(
            conversationId,
            ownerPrincipalId,
            cancellationToken);

    public ValueTask<ConversationPage<ConversationSummary>> ListOwnedAsync(
        string ownerPrincipalId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken) =>
        persistentStore.ListOwnedAsync(
            ownerPrincipalId,
            cursor,
            limit,
            cancellationToken);

    public ValueTask<ConversationDetails?> GetOwnedDetailsAsync(
        Guid conversationId,
        string ownerPrincipalId,
        CancellationToken cancellationToken) =>
        persistentStore.GetOwnedDetailsAsync(
            conversationId,
            ownerPrincipalId,
            cancellationToken);

    public ValueTask<ConversationPage<PublicConversationMessage>?> GetOwnedHistoryAsync(
        Guid conversationId,
        string ownerPrincipalId,
        string? cursor,
        int limit,
        CancellationToken cancellationToken) =>
        persistentStore.GetOwnedHistoryAsync(
            conversationId,
            ownerPrincipalId,
            cursor,
            limit,
            cancellationToken);
}
