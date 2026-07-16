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
            CREATE TABLE IF NOT EXISTS device_tokens (
                token_hash TEXT PRIMARY KEY,
                device_id TEXT NOT NULL,
                expires_at TEXT NOT NULL,
                consumed_at TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS devices (
                device_id TEXT PRIMARY KEY,
                certificate_thumbprint TEXT NOT NULL UNIQUE,
                certificate_expires_at TEXT NOT NULL,
                status TEXT NOT NULL,
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
            CREATE TABLE IF NOT EXISTS links (
                id TEXT PRIMARY KEY,
                source_node_id TEXT NOT NULL REFERENCES agents(node_id),
                target_node_id TEXT NOT NULL REFERENCES agents(node_id),
                protocol TEXT NOT NULL,
                port INTEGER NOT NULL,
                ttl_minutes INTEGER NOT NULL,
                reason TEXT NOT NULL,
                desired_state TEXT NOT NULL,
                actual_state TEXT NOT NULL,
                version INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                expires_at TEXT NULL,
                updated_at TEXT NOT NULL,
                last_error TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_links_active_policy
                ON links(source_node_id, target_node_id, protocol, port)
                WHERE desired_state = 'Active';
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

    public async Task<string> CreateDeviceEnrollmentTokenAsync(
        string deviceId,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO device_tokens(token_hash, device_id, expires_at)
            VALUES ($hash, $device, $expires);
            """;
        command.Parameters.AddWithValue("$hash", Hash(token));
        command.Parameters.AddWithValue("$device", deviceId);
        command.Parameters.AddWithValue("$expires", DateTimeOffset.UtcNow.Add(lifetime).ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return token;
    }

    public async Task<DeviceEnrollmentResponse?> EnrollDeviceAsync(
        DeviceEnrollmentRequest request,
        Func<IssuedCertificate> issueCertificate,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var cached = await ReadIdempotentAsync<DeviceEnrollmentResponse>(
            connection,
            transaction,
            $"device-enroll:{request.IdempotencyKey}",
            Fingerprint(request, SmmJsonContext.Default.DeviceEnrollmentRequest),
            SmmJsonContext.Default.DeviceEnrollmentResponse,
            cancellationToken);
        if (cached is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return cached;
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        var consume = connection.CreateCommand();
        consume.Transaction = transaction;
        consume.CommandText = """
            UPDATE device_tokens
            SET consumed_at = $now
            WHERE token_hash = $hash
              AND device_id = $device
              AND consumed_at IS NULL
              AND expires_at >= $now;
            """;
        consume.Parameters.AddWithValue("$now", now);
        consume.Parameters.AddWithValue("$hash", Hash(request.Token));
        consume.Parameters.AddWithValue("$device", request.DeviceId);
        if (await consume.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var issued = issueCertificate();
        var response = new DeviceEnrollmentResponse(
            request.DeviceId,
            issued.CertificatePem,
            issued.CertificateAuthorityPem,
            issued.ExpiresAt);
        var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO devices(device_id, certificate_thumbprint, certificate_expires_at, status)
            VALUES ($device, $thumbprint, $expires, 'Active')
            ON CONFLICT(device_id) DO UPDATE SET
                certificate_thumbprint = excluded.certificate_thumbprint,
                certificate_expires_at = excluded.certificate_expires_at,
                status = 'Active';
            """;
        upsert.Parameters.AddWithValue("$device", request.DeviceId);
        upsert.Parameters.AddWithValue("$thumbprint", issued.Thumbprint);
        upsert.Parameters.AddWithValue("$expires", issued.ExpiresAt.ToString("O"));
        await upsert.ExecuteNonQueryAsync(cancellationToken);
        await WriteIdempotentAsync(
            connection,
            transaction,
            $"device-enroll:{request.IdempotencyKey}",
            Fingerprint(request, SmmJsonContext.Default.DeviceEnrollmentRequest),
            response,
            SmmJsonContext.Default.DeviceEnrollmentResponse,
            cancellationToken);
        await WriteAuditAsync(
            connection, transaction, request.DeviceId, "device.enroll", request.DeviceId, "{}", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return response;
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
        return await ResolveIdentityAsync(thumbprint, cancellationToken) is not null;
    }

    public async Task<ControlIdentity?> ResolveIdentityAsync(
        string thumbprint,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT node_id, 'Agent' FROM agents
            WHERE certificate_thumbprint = $thumbprint
              AND certificate_expires_at > $now
              AND status != 'Revoked'
            UNION ALL
            SELECT device_id, 'Operator' FROM devices
            WHERE certificate_thumbprint = $thumbprint
              AND certificate_expires_at > $now
              AND status != 'Revoked'
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$thumbprint", thumbprint);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ControlIdentity(reader.GetString(0), reader.GetString(1))
            : null;
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
        insert.Parameters.AddWithValue("$now", heartbeat.SentAt.ToString("O"));
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

    public async Task<IReadOnlyList<AgentSummary>> ListAgentsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<AgentSummary>();
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT node_id, name, status, agent_version, last_seen_at
            FROM agents ORDER BY name;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new AgentSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4))));
        }
        return result;
    }

    public async Task<AgentReenrollmentMutation?> BeginAgentReenrollmentAsync(
        string nodeId,
        CertificateReenrollmentRequest request,
        string actor,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var operationKey = $"agent-reenroll:{actor}:{nodeId}:{request.IdempotencyKey}";
        var requestHash = Fingerprint(request, SmmJsonContext.Default.CertificateReenrollmentRequest);
        var cached = await ReadIdempotentAsync<CertificateReenrollmentTicket>(
            connection,
            transaction,
            operationKey,
            requestHash,
            SmmJsonContext.Default.CertificateReenrollmentTicket,
            cancellationToken);
        if (cached is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new AgentReenrollmentMutation(cached, [], true);
        }

        var exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT EXISTS(SELECT 1 FROM agents WHERE node_id = $id);";
        exists.Parameters.AddWithValue("$id", nodeId);
        if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var revoke = connection.CreateCommand();
        revoke.Transaction = transaction;
        revoke.CommandText = "UPDATE agents SET status = 'Revoked' WHERE node_id = $id;";
        revoke.Parameters.AddWithValue("$id", nodeId);
        await revoke.ExecuteNonQueryAsync(cancellationToken);

        var pendingLinks = new List<LinkPolicy>();
        var links = connection.CreateCommand();
        links.Transaction = transaction;
        links.CommandText = """
            SELECT * FROM links
            WHERE (source_node_id = $id OR target_node_id = $id)
              AND desired_state != 'Disabled'
            ORDER BY created_at;
            """;
        links.Parameters.AddWithValue("$id", nodeId);
        await using (var reader = await links.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                pendingLinks.Add(ReadLink(reader) with
                {
                    DesiredState = "Disabled",
                    ActualState = "Disconnecting",
                    Version = reader.GetInt64(9) + 1,
                    UpdatedAt = now,
                    LastError = null
                });
            }
        }

        foreach (var link in pendingLinks)
        {
            var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE links SET
                    desired_state = 'Disabled',
                    actual_state = 'Disconnecting',
                    version = $version,
                    updated_at = $updated,
                    last_error = NULL
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$version", link.Version);
            update.Parameters.AddWithValue("$updated", link.UpdatedAt.ToString("O"));
            update.Parameters.AddWithValue("$id", link.Id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        var token = CreateSecureToken();
        var expiresAt = now.Add(lifetime);
        await ReplaceEnrollmentTokenAsync(
            connection,
            transaction,
            "enrollment_tokens",
            "node_id",
            nodeId,
            token,
            now,
            expiresAt,
            cancellationToken);
        var ticket = new CertificateReenrollmentTicket(
            "Agent", nodeId, token, expiresAt, pendingLinks.Count);
        await WriteIdempotentAsync(
            connection,
            transaction,
            operationKey,
            requestHash,
            ticket,
            SmmJsonContext.Default.CertificateReenrollmentTicket,
            cancellationToken);
        await WriteAuditAsync(
            connection,
            transaction,
            actor,
            "agent.reenrollment.requested",
            nodeId,
            JsonSerializer.Serialize(
                new CertificateStatusEvent("Agent", nodeId, "Revoked", pendingLinks.Count),
                SmmJsonContext.Default.CertificateStatusEvent),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AgentReenrollmentMutation(ticket, pendingLinks, false);
    }

    public async Task<CertificateReenrollmentTicket?> BeginDeviceReenrollmentAsync(
        string deviceId,
        CertificateReenrollmentRequest request,
        string actor,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var operationKey = $"device-reenroll:{actor}:{deviceId}:{request.IdempotencyKey}";
        var requestHash = Fingerprint(request, SmmJsonContext.Default.CertificateReenrollmentRequest);
        var cached = await ReadIdempotentAsync<CertificateReenrollmentTicket>(
            connection,
            transaction,
            operationKey,
            requestHash,
            SmmJsonContext.Default.CertificateReenrollmentTicket,
            cancellationToken);
        if (cached is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return cached;
        }

        var revoke = connection.CreateCommand();
        revoke.Transaction = transaction;
        revoke.CommandText = "UPDATE devices SET status = 'Revoked' WHERE device_id = $id;";
        revoke.Parameters.AddWithValue("$id", deviceId);
        if (await revoke.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var token = CreateSecureToken();
        var expiresAt = now.Add(lifetime);
        await ReplaceEnrollmentTokenAsync(
            connection,
            transaction,
            "device_tokens",
            "device_id",
            deviceId,
            token,
            now,
            expiresAt,
            cancellationToken);
        var ticket = new CertificateReenrollmentTicket("Operator", deviceId, token, expiresAt, 0);
        await WriteIdempotentAsync(
            connection,
            transaction,
            operationKey,
            requestHash,
            ticket,
            SmmJsonContext.Default.CertificateReenrollmentTicket,
            cancellationToken);
        await WriteAuditAsync(
            connection,
            transaction,
            actor,
            "device.reenrollment.requested",
            deviceId,
            JsonSerializer.Serialize(
                new CertificateStatusEvent("Operator", deviceId, "Revoked", 0),
                SmmJsonContext.Default.CertificateStatusEvent),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ticket;
    }

    public async Task<LinkMutation> CreateLinkMutationAsync(
        LinkPolicyCreateRequest request,
        string actor,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var cached = await ReadIdempotentAsync<LinkPolicy>(
            connection,
            transaction,
            $"link-create:{actor}:{request.IdempotencyKey}",
            Fingerprint(request, SmmJsonContext.Default.LinkPolicyCreateRequest),
            SmmJsonContext.Default.LinkPolicy,
            cancellationToken);
        if (cached is not null)
        {
            var current = await ReadLinkAsync(connection, transaction, cached.Id, cancellationToken) ?? cached;
            await transaction.CommitAsync(cancellationToken);
            return new LinkMutation(current, true);
        }

        var nodes = connection.CreateCommand();
        nodes.Transaction = transaction;
        nodes.CommandText = """
            SELECT COUNT(*) FROM agents
            WHERE node_id IN ($source, $target) AND status != 'Revoked';
            """;
        nodes.Parameters.AddWithValue("$source", request.SourceNodeId);
        nodes.Parameters.AddWithValue("$target", request.TargetNodeId);
        if (Convert.ToInt32(await nodes.ExecuteScalarAsync(cancellationToken)) != 2)
        {
            throw new InvalidOperationException("Source or target agent is not registered.");
        }

        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? expiresAt = request.TtlMinutes == 0
            ? null
            : now.AddMinutes(request.TtlMinutes);
        var versionCommand = connection.CreateCommand();
        versionCommand.Transaction = transaction;
        versionCommand.CommandText = "SELECT COALESCE(MAX(version), 0) + 1 FROM links;";
        var version = Convert.ToInt64(await versionCommand.ExecuteScalarAsync(cancellationToken));
        var link = new LinkPolicy(
            Guid.NewGuid().ToString("N"),
            request.SourceNodeId,
            request.TargetNodeId,
            request.Protocol,
            request.Port,
            request.TtlMinutes,
            request.Reason,
            "Active",
            "Connecting",
            version,
            now,
            expiresAt,
            now,
            null);
        var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO links(
                id, source_node_id, target_node_id, protocol, port, ttl_minutes, reason,
                desired_state, actual_state, version, created_at, expires_at, updated_at, last_error)
            VALUES (
                $id, $source, $target, $protocol, $port, $ttl, $reason,
                $desired, $actual, $version, $created, $expires, $updated, NULL);
            """;
        AddLinkParameters(insert, link);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await WriteIdempotentAsync(
            connection,
            transaction,
            $"link-create:{actor}:{request.IdempotencyKey}",
            Fingerprint(request, SmmJsonContext.Default.LinkPolicyCreateRequest),
            link,
            SmmJsonContext.Default.LinkPolicy,
            cancellationToken);
        await WriteAuditAsync(
            connection,
            transaction,
            actor,
            "link.connect.requested",
            link.Id,
            JsonSerializer.Serialize(link, SmmJsonContext.Default.LinkPolicy),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LinkMutation(link, false);
    }

    public async Task<LinkMutation?> BeginDisableLinkMutationAsync(
        string id,
        LinkPolicyDisableRequest request,
        string actor,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var cached = await ReadIdempotentAsync<LinkPolicy>(
            connection,
            transaction,
            $"link-disable:{actor}:{id}:{request.IdempotencyKey}",
            Fingerprint(request, SmmJsonContext.Default.LinkPolicyDisableRequest),
            SmmJsonContext.Default.LinkPolicy,
            cancellationToken);
        if (cached is not null)
        {
            var current = await ReadLinkAsync(connection, transaction, cached.Id, cancellationToken) ?? cached;
            await transaction.CommitAsync(cancellationToken);
            return new LinkMutation(current, true);
        }

        var existing = await ReadLinkAsync(connection, transaction, id, cancellationToken);
        if (existing is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        var now = DateTimeOffset.UtcNow;
        var link = existing with
        {
            DesiredState = "Disabled",
            ActualState = "Disconnecting",
            Version = existing.Version + 1,
            UpdatedAt = now,
            LastError = null
        };
        var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE links SET
                desired_state = $desired,
                actual_state = $actual,
                version = $version,
                updated_at = $updated,
                last_error = NULL
            WHERE id = $id;
            """;
        AddLinkParameters(update, link);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await WriteIdempotentAsync(
            connection,
            transaction,
            $"link-disable:{actor}:{id}:{request.IdempotencyKey}",
            Fingerprint(request, SmmJsonContext.Default.LinkPolicyDisableRequest),
            link,
            SmmJsonContext.Default.LinkPolicy,
            cancellationToken);
        await WriteAuditAsync(
            connection, transaction, actor, "link.disconnect.requested", id, "{}", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new LinkMutation(link, false);
    }

    public async Task<LinkPolicy?> SetLinkActualStateAsync(
        string id,
        string state,
        string? error,
        string actor,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE links SET actual_state = $state, last_error = $error, updated_at = $now
            WHERE id = $id;
            """;
        update.Parameters.AddWithValue("$state", state);
        update.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        update.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$id", id);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        var link = await ReadLinkAsync(connection, transaction, id, cancellationToken);
        await WriteAuditAsync(
            connection,
            transaction,
            actor,
            $"link.state.{state.ToLowerInvariant()}",
            id,
            error is null
                ? "{}"
                : JsonSerializer.Serialize(new ControlError(error), SmmJsonContext.Default.ControlError),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return link;
    }

    public async Task<IReadOnlyList<LinkPolicy>> ListLinksAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<LinkPolicy>();
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM links ORDER BY created_at DESC;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadLink(reader));
        }
        return result;
    }

    private static void AddLinkParameters(SqliteCommand command, LinkPolicy link)
    {
        command.Parameters.AddWithValue("$id", link.Id);
        command.Parameters.AddWithValue("$source", link.SourceNodeId);
        command.Parameters.AddWithValue("$target", link.TargetNodeId);
        command.Parameters.AddWithValue("$protocol", link.Protocol);
        command.Parameters.AddWithValue("$port", link.Port);
        command.Parameters.AddWithValue("$ttl", link.TtlMinutes);
        command.Parameters.AddWithValue("$reason", link.Reason);
        command.Parameters.AddWithValue("$desired", link.DesiredState);
        command.Parameters.AddWithValue("$actual", link.ActualState);
        command.Parameters.AddWithValue("$version", link.Version);
        command.Parameters.AddWithValue("$created", link.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue(
            "$expires",
            link.ExpiresAt is null ? DBNull.Value : link.ExpiresAt.Value.ToString("O"));
        command.Parameters.AddWithValue("$updated", link.UpdatedAt.ToString("O"));
    }

    private static async Task<LinkPolicy?> ReadLinkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM links WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadLink(reader) : null;
    }

    private static LinkPolicy ReadLink(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetInt64(9),
            DateTimeOffset.Parse(reader.GetString(10)),
            reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11)),
            DateTimeOffset.Parse(reader.GetString(12)),
            reader.IsDBNull(13) ? null : reader.GetString(13));

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task ReplaceEnrollmentTokenAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string idColumn,
        string id,
        string token,
        DateTimeOffset now,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        var consume = connection.CreateCommand();
        consume.Transaction = transaction;
        consume.CommandText = $"UPDATE {table} SET consumed_at = $now WHERE {idColumn} = $id AND consumed_at IS NULL;";
        consume.Parameters.AddWithValue("$now", now.ToString("O"));
        consume.Parameters.AddWithValue("$id", id);
        await consume.ExecuteNonQueryAsync(cancellationToken);

        var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = $"INSERT INTO {table}(token_hash, {idColumn}, expires_at) VALUES ($hash, $id, $expires);";
        insert.Parameters.AddWithValue("$hash", Hash(token));
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$expires", expiresAt.ToString("O"));
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string CreateSecureToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

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

public sealed record ControlIdentity(string Id, string Role);

public sealed record LinkMutation(LinkPolicy Link, bool IsReplay);

public sealed record AgentReenrollmentMutation(
    CertificateReenrollmentTicket Ticket,
    IReadOnlyList<LinkPolicy> Links,
    bool IsReplay);
