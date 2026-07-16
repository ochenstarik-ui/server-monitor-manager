using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using ServerMonitorManager.Core;

namespace ServerMonitorManager.Control;

public sealed class ControlStore(IOptions<ControlOptions> options)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = options.Value.DatabasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var path = new SqliteConnectionStringBuilder(_connectionString).DataSource;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;
            CREATE TABLE IF NOT EXISTS enrollment_tokens (
                token_hash TEXT PRIMARY KEY,
                node_id TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                consumed_at TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS agents (
                node_id TEXT PRIMARY KEY,
                name TEXT NOT NULL,
                certificate_thumbprint TEXT NOT NULL UNIQUE,
                certificate_expires_at TEXT NOT NULL,
                status TEXT NOT NULL,
                agent_version TEXT NOT NULL DEFAULT '',
                last_seen_at TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS metric_samples (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                node_id TEXT NOT NULL REFERENCES agents(node_id) ON DELETE CASCADE,
                recorded_at TEXT NOT NULL,
                payload_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_metric_samples_node_time
                ON metric_samples(node_id, recorded_at DESC);
            CREATE TABLE IF NOT EXISTS idempotency (
                operation_key TEXT PRIMARY KEY,
                request_hash TEXT NOT NULL,
                response_json TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS audit (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                recorded_at TEXT NOT NULL,
                actor TEXT NOT NULL,
                action TEXT NOT NULL,
                subject TEXT NOT NULL,
                details_json TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<string> CreateEnrollmentTokenAsync(
        string nodeId,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO enrollment_tokens(token_hash, node_id, expires_at)
            VALUES ($hash, $node, $expires);
            """;
        command.Parameters.AddWithValue("$hash", Hash(token));
        command.Parameters.AddWithValue("$node", nodeId);
        command.Parameters.AddWithValue("$expires", DateTimeOffset.UtcNow.Add(lifetime).ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return token;
    }

    public async Task<EnrollmentResponse?> EnrollAsync(
        EnrollmentRequest request,
        Func<IssuedCertificate> issueCertificate,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var cached = await ReadIdempotentAsync<EnrollmentResponse>(
            connection,
            transaction,
            $"enroll:{request.IdempotencyKey}",
            Fingerprint(request, SmmJsonContext.Default.EnrollmentRequest),
            SmmJsonContext.Default.EnrollmentResponse,
            cancellationToken);
        if (cached is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return cached;
        }

        var consume = connection.CreateCommand();
        consume.Transaction = transaction;
        consume.CommandText = """
            UPDATE enrollment_tokens
            SET consumed_at = $now
            WHERE token_hash = $hash
              AND node_id = $node
              AND consumed_at IS NULL
              AND expires_at >= $now;
            """;
        var now = DateTimeOffset.UtcNow.ToString("O");
        consume.Parameters.AddWithValue("$now", now);
        consume.Parameters.AddWithValue("$hash", Hash(request.Token));
        consume.Parameters.AddWithValue("$node", request.NodeId);
        if (await consume.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var issued = issueCertificate();
        var response = new EnrollmentResponse(
            request.NodeId,
            issued.CertificatePem,
            issued.CertificateAuthorityPem,
            issued.ExpiresAt);
        var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO agents(node_id, name, certificate_thumbprint, certificate_expires_at, status)
            VALUES ($node, $node, $thumbprint, $expires, 'Enrolled')
            ON CONFLICT(node_id) DO UPDATE SET
                certificate_thumbprint = excluded.certificate_thumbprint,
                certificate_expires_at = excluded.certificate_expires_at,
                status = 'Enrolled';
            """;
        upsert.Parameters.AddWithValue("$node", request.NodeId);
        upsert.Parameters.AddWithValue("$thumbprint", issued.Thumbprint);
        upsert.Parameters.AddWithValue("$expires", issued.ExpiresAt.ToString("O"));
        await upsert.ExecuteNonQueryAsync(cancellationToken);
        await WriteIdempotentAsync(
            connection,
            transaction,
            $"enroll:{request.IdempotencyKey}",
            Fingerprint(request, SmmJsonContext.Default.EnrollmentRequest),
            response,
            SmmJsonContext.Default.EnrollmentResponse,
            cancellationToken);
        await WriteAuditAsync(connection, transaction, request.NodeId, "agent.enroll", request.NodeId, "{}", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return response;
    }

    public async Task<bool> IsCertificateActiveAsync(
        string thumbprint,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM agents
                WHERE certificate_thumbprint = $thumbprint
                  AND certificate_expires_at > $now
                  AND status != 'Revoked');
            """;
        command.Parameters.AddWithValue("$thumbprint", thumbprint);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    public async Task<bool> IsCertificateForNodeAsync(
        string thumbprint,
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM agents
                WHERE certificate_thumbprint = $thumbprint
                  AND node_id = $node
                  AND certificate_expires_at > $now
                  AND status != 'Revoked');
            """;
        command.Parameters.AddWithValue("$thumbprint", thumbprint);
        command.Parameters.AddWithValue("$node", nodeId);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    public async Task<AgentHeartbeatResponse> RecordHeartbeatAsync(
        AgentHeartbeat heartbeat,
        int nextHeartbeatSeconds,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var cached = await ReadIdempotentAsync<AgentHeartbeatResponse>(
            connection,
            transaction,
            $"heartbeat:{heartbeat.NodeId}:{heartbeat.IdempotencyKey}",
            Fingerprint(heartbeat, SmmJsonContext.Default.AgentHeartbeat),
            SmmJsonContext.Default.AgentHeartbeatResponse,
            cancellationToken);
        if (cached is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return cached;
        }

        var now = DateTimeOffset.UtcNow;
        var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE agents
            SET status = 'Online', agent_version = $version, last_seen_at = $now
            WHERE node_id = $node;
            """;
        update.Parameters.AddWithValue("$version", heartbeat.AgentVersion);
        update.Parameters.AddWithValue("$now", now.ToString("O"));
        update.Parameters.AddWithValue("$node", heartbeat.NodeId);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new InvalidOperationException("Unknown agent node id.");
        }

        var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO metric_samples(node_id, recorded_at, payload_json)
            VALUES ($node, $now, $payload);
            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("$node", heartbeat.NodeId);
        insert.Parameters.AddWithValue("$now", now.ToString("O"));
        insert.Parameters.AddWithValue(
            "$payload",
            JsonSerializer.Serialize(heartbeat, SmmJsonContext.Default.AgentHeartbeat));
        var sequence = Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken));
        var response = new AgentHeartbeatResponse(now, sequence, nextHeartbeatSeconds);
        await WriteIdempotentAsync(
            connection,
            transaction,
            $"heartbeat:{heartbeat.NodeId}:{heartbeat.IdempotencyKey}",
            Fingerprint(heartbeat, SmmJsonContext.Default.AgentHeartbeat),
            response,
            SmmJsonContext.Default.AgentHeartbeatResponse,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return response;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Fingerprint<T>(T value, JsonTypeInfo<T> typeInfo)
        => Hash(JsonSerializer.Serialize(value, typeInfo));

    private static async Task<T?> ReadIdempotentAsync<T>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string requestHash,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT request_hash, response_json FROM idempotency WHERE operation_key = $key;";
        command.Parameters.AddWithValue("$key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return default;
        }
        if (!string.Equals(reader.GetString(0), requestHash, StringComparison.Ordinal))
        {
            throw new IdempotencyConflictException();
        }
        return JsonSerializer.Deserialize(reader.GetString(1), typeInfo);
    }

    private static async Task WriteIdempotentAsync<T>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string requestHash,
        T response,
        JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO idempotency(operation_key, request_hash, response_json, created_at)
            VALUES ($key, $requestHash, $response, $now);
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$requestHash", requestHash);
        command.Parameters.AddWithValue("$response", JsonSerializer.Serialize(response, typeInfo));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task WriteAuditAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string actor,
        string action,
        string subject,
        string details,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO audit(recorded_at, actor, action, subject, details_json)
            VALUES ($now, $actor, $action, $subject, $details);
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$subject", subject);
        command.Parameters.AddWithValue("$details", details);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed class IdempotencyConflictException : Exception
{
    public IdempotencyConflictException()
        : base("Idempotency key was reused with a different request.")
    {
    }
}
