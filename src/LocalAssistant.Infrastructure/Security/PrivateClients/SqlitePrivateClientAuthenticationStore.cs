using System.Globalization;
using LocalAssistant.Core.Security.PrivateClients;
using Microsoft.Data.Sqlite;

namespace LocalAssistant.Infrastructure.Security.PrivateClients;

public sealed class SqlitePrivateClientAuthenticationStore : IPrivateClientAuthenticationStore
{
    private readonly string _connectionString;

    public SqlitePrivateClientAuthenticationStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("The private client database directory is invalid.", nameof(databasePath));
        }

        Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();
    }

    public async ValueTask<bool> HasClientsAsync(CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM PrivateClients);";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    public async ValueTask<RegisteredPrivateClient?> FindActiveClientAsync(
        string clientId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        return await FindActiveClientAsync(connection, null, clientId, cancellationToken);
    }

    public async ValueTask<AdministrativeChallenge> CreateAdministrativeChallengeAsync(
        AdministrativeChallengeOperation operation,
        string? clientId,
        string secretHash,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretHash);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiresAtUtc, createdAtUtc);

        if (operation == AdministrativeChallengeOperation.CreateClient != string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("The challenge target does not match its operation.", nameof(clientId));
        }

        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var challenge = new AdministrativeChallenge(
            Guid.NewGuid().ToString("N"), operation, clientId, expiresAtUtc);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO PrivateClientAdministrativeChallenges
                (ChallengeId, Operation, ClientId, SecretHash, CreatedAtUtc, ExpiresAtUtc, ConsumedAtUtc)
            VALUES ($id, $operation, $clientId, $secretHash, $createdAtUtc, $expiresAtUtc, NULL);
            """;
        command.Parameters.AddWithValue("$id", challenge.ChallengeId);
        command.Parameters.AddWithValue("$operation", operation.ToString());
        command.Parameters.AddWithValue("$clientId", (object?)clientId ?? DBNull.Value);
        command.Parameters.AddWithValue("$secretHash", secretHash);
        command.Parameters.AddWithValue("$createdAtUtc", ToDatabaseValue(createdAtUtc));
        command.Parameters.AddWithValue("$expiresAtUtc", ToDatabaseValue(expiresAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return challenge;
    }

    public ValueTask<RegisteredPrivateClient?> ConsumeCreateClientChallengeAsync(
        string secretHash,
        string clientId,
        string ownerPrincipalId,
        string displayName,
        string credentialHash,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ConsumeChallengeAsync(
            secretHash,
            AdministrativeChallengeOperation.CreateClient,
            null,
            now,
            async (connection, transaction, _) =>
            {
                var client = new RegisteredPrivateClient(
                    clientId, ownerPrincipalId, displayName, PrivateClientStatus.Active, now, null, 1);
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO PrivateClients
                        (ClientId, OwnerPrincipalId, DisplayName, CredentialHash, CredentialVersion, CreatedAtUtc, RevokedAtUtc)
                    VALUES ($id, $owner, $name, $credentialHash, 1, $createdAtUtc, NULL);
                    """;
                command.Parameters.AddWithValue("$id", client.ClientId);
                command.Parameters.AddWithValue("$owner", client.OwnerPrincipalId);
                command.Parameters.AddWithValue("$name", client.DisplayName);
                command.Parameters.AddWithValue("$credentialHash", credentialHash);
                command.Parameters.AddWithValue("$createdAtUtc", ToDatabaseValue(now));
                await command.ExecuteNonQueryAsync(cancellationToken);
                return client;
            },
            cancellationToken);

    public ValueTask<RegisteredPrivateClient?> ConsumeRotateCredentialChallengeAsync(
        string secretHash,
        string expectedClientId,
        string credentialHash,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ConsumeChallengeAsync(
            secretHash,
            AdministrativeChallengeOperation.RotateCredential,
            expectedClientId,
            now,
            async (connection, transaction, challenge) =>
            {
                var client = await FindActiveClientAsync(connection, transaction, challenge.ClientId!, cancellationToken);
                if (client is null)
                {
                    return null;
                }

                var nextVersion = checked(client.CredentialVersion + 1);
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE PrivateClients
                    SET CredentialHash = $credentialHash, CredentialVersion = $credentialVersion
                    WHERE ClientId = $id AND RevokedAtUtc IS NULL;
                    UPDATE PrivateClientSessions SET RevokedAtUtc = $now
                    WHERE ClientId = $id AND RevokedAtUtc IS NULL;
                    """;
                command.Parameters.AddWithValue("$credentialHash", credentialHash);
                command.Parameters.AddWithValue("$credentialVersion", nextVersion);
                command.Parameters.AddWithValue("$id", client.ClientId);
                command.Parameters.AddWithValue("$now", ToDatabaseValue(now));
                await command.ExecuteNonQueryAsync(cancellationToken);
                return client with { CredentialVersion = nextVersion };
            },
            cancellationToken);

    public ValueTask<RegisteredPrivateClient?> ConsumeRevokeClientChallengeAsync(
        string secretHash,
        string expectedClientId,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        ConsumeChallengeAsync(
            secretHash,
            AdministrativeChallengeOperation.RevokeClient,
            expectedClientId,
            now,
            async (connection, transaction, challenge) =>
            {
                var client = await FindActiveClientAsync(connection, transaction, challenge.ClientId!, cancellationToken);
                if (client is null)
                {
                    return null;
                }

                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE PrivateClients SET RevokedAtUtc = $now
                    WHERE ClientId = $id AND RevokedAtUtc IS NULL;
                    UPDATE PrivateClientSessions SET RevokedAtUtc = $now
                    WHERE ClientId = $id AND RevokedAtUtc IS NULL;
                    """;
                command.Parameters.AddWithValue("$id", client.ClientId);
                command.Parameters.AddWithValue("$now", ToDatabaseValue(now));
                await command.ExecuteNonQueryAsync(cancellationToken);
                return client;
            },
            cancellationToken);

    public async ValueTask<PrivateClientSession?> CreateSessionAsync(
        string clientId,
        string credentialHash,
        string accessTokenHash,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiresAtUtc, issuedAtUtc);

        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var client = await FindActiveClientAsync(connection, transaction, clientId, cancellationToken);
        if (client is null || !await MatchesCredentialAsync(connection, transaction, clientId, credentialHash, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var session = new PrivateClientSession(
            Guid.NewGuid().ToString("N"), client.ClientId, client.OwnerPrincipalId,
            issuedAtUtc, expiresAtUtc, null, client.CredentialVersion);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO PrivateClientSessions
                (SessionId, ClientId, AccessTokenHash, IssuedAtUtc, ExpiresAtUtc, RevokedAtUtc, CredentialVersion)
            VALUES ($id, $clientId, $tokenHash, $issuedAtUtc, $expiresAtUtc, NULL, $credentialVersion);
            """;
        command.Parameters.AddWithValue("$id", session.SessionId);
        command.Parameters.AddWithValue("$clientId", session.ClientId);
        command.Parameters.AddWithValue("$tokenHash", accessTokenHash);
        command.Parameters.AddWithValue("$issuedAtUtc", ToDatabaseValue(issuedAtUtc));
        command.Parameters.AddWithValue("$expiresAtUtc", ToDatabaseValue(expiresAtUtc));
        command.Parameters.AddWithValue("$credentialVersion", session.CredentialVersion);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return session;
    }

    public async ValueTask<PrivateClientSession?> FindActiveSessionAsync(
        string accessTokenHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.SessionId, s.ClientId, c.OwnerPrincipalId, s.IssuedAtUtc, s.ExpiresAtUtc, s.CredentialVersion
            FROM PrivateClientSessions s JOIN PrivateClients c ON c.ClientId = s.ClientId
            WHERE s.AccessTokenHash = $tokenHash AND s.RevokedAtUtc IS NULL AND c.RevokedAtUtc IS NULL
              AND s.ExpiresAtUtc > $now AND s.CredentialVersion = c.CredentialVersion;
            """;
        command.Parameters.AddWithValue("$tokenHash", accessTokenHash);
        command.Parameters.AddWithValue("$now", ToDatabaseValue(now));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new PrivateClientSession(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            ParseDatabaseValue(reader.GetString(3)), ParseDatabaseValue(reader.GetString(4)), null, reader.GetInt64(5));
    }

    private async ValueTask<RegisteredPrivateClient?> ConsumeChallengeAsync(
        string secretHash,
        AdministrativeChallengeOperation expectedOperation,
        string? expectedClientId,
        DateTimeOffset now,
        Func<SqliteConnection, SqliteTransaction, AdministrativeChallenge, Task<RegisteredPrivateClient?>> operation,
        CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var challenge = await FindChallengeAsync(connection, transaction, secretHash, expectedOperation, now, cancellationToken);
        if (challenge is null || !string.Equals(challenge.ClientId, expectedClientId, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var result = await operation(connection, transaction, challenge);
        if (result is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await using var consume = connection.CreateCommand();
        consume.Transaction = transaction;
        consume.CommandText = "UPDATE PrivateClientAdministrativeChallenges SET ConsumedAtUtc = $now WHERE ChallengeId = $id AND ConsumedAtUtc IS NULL;";
        consume.Parameters.AddWithValue("$now", ToDatabaseValue(now));
        consume.Parameters.AddWithValue("$id", challenge.ChallengeId);
        if (await consume.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("The administrative challenge could not be consumed.");
        }

        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task<AdministrativeChallenge?> FindChallengeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string secretHash,
        AdministrativeChallengeOperation operation,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ChallengeId, ClientId, ExpiresAtUtc FROM PrivateClientAdministrativeChallenges
            WHERE SecretHash = $secretHash AND Operation = $operation AND ConsumedAtUtc IS NULL AND ExpiresAtUtc > $now;
            """;
        command.Parameters.AddWithValue("$secretHash", secretHash);
        command.Parameters.AddWithValue("$operation", operation.ToString());
        command.Parameters.AddWithValue("$now", ToDatabaseValue(now));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new AdministrativeChallenge(reader.GetString(0), operation, reader.IsDBNull(1) ? null : reader.GetString(1), ParseDatabaseValue(reader.GetString(2)))
            : null;
    }

    private static async Task<RegisteredPrivateClient?> FindActiveClientAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string clientId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ClientId, OwnerPrincipalId, DisplayName, CreatedAtUtc, CredentialVersion FROM PrivateClients
            WHERE ClientId = $id AND RevokedAtUtc IS NULL;
            """;
        command.Parameters.AddWithValue("$id", clientId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new RegisteredPrivateClient(reader.GetString(0), reader.GetString(1), reader.GetString(2), PrivateClientStatus.Active,
                ParseDatabaseValue(reader.GetString(3)), null, reader.GetInt64(4))
            : null;
    }

    private static async Task<bool> MatchesCredentialAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string clientId,
        string credentialHash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM PrivateClients WHERE ClientId = $id AND CredentialHash = $credentialHash AND RevokedAtUtc IS NULL);";
        command.Parameters.AddWithValue("$id", clientId);
        command.Parameters.AddWithValue("$credentialHash", credentialHash);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) == 1;
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS PrivateClients (
                ClientId TEXT PRIMARY KEY, OwnerPrincipalId TEXT NOT NULL, DisplayName TEXT NOT NULL,
                CredentialHash TEXT NOT NULL, CredentialVersion INTEGER NOT NULL, CreatedAtUtc TEXT NOT NULL, RevokedAtUtc TEXT NULL);
            CREATE TABLE IF NOT EXISTS PrivateClientAdministrativeChallenges (
                ChallengeId TEXT PRIMARY KEY, Operation TEXT NOT NULL, ClientId TEXT NULL, SecretHash TEXT NOT NULL UNIQUE,
                CreatedAtUtc TEXT NOT NULL, ExpiresAtUtc TEXT NOT NULL, ConsumedAtUtc TEXT NULL,
                FOREIGN KEY(ClientId) REFERENCES PrivateClients(ClientId));
            CREATE TABLE IF NOT EXISTS PrivateClientSessions (
                SessionId TEXT PRIMARY KEY, ClientId TEXT NOT NULL, AccessTokenHash TEXT NOT NULL UNIQUE,
                IssuedAtUtc TEXT NOT NULL, ExpiresAtUtc TEXT NOT NULL, RevokedAtUtc TEXT NULL, CredentialVersion INTEGER NOT NULL,
                FOREIGN KEY(ClientId) REFERENCES PrivateClients(ClientId));
            CREATE INDEX IF NOT EXISTS IX_PrivateClientSessions_AccessTokenHash ON PrivateClientSessions(AccessTokenHash);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static string ToDatabaseValue(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseDatabaseValue(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
