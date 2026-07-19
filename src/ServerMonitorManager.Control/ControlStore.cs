using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using ServerMonitorManager.Core;

namespace ServerMonitorManager.Control;

public sealed partial class ControlStore(IOptions<ControlOptions> options)
{
    private const int CurrentSchemaVersion = 5;
    private readonly ControlOptions _options = options.Value;
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
        var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var schemaVersion = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(cancellationToken));
        if (schemaVersion > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Control database schema {schemaVersion} is newer than supported schema {CurrentSchemaVersion}.");
        }
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
            CREATE TABLE IF NOT EXISTS automation_tokens (
                token_hash TEXT PRIMARY KEY,
                automation_id TEXT NOT NULL,
                source_node_id TEXT NOT NULL REFERENCES agents(node_id),
                expires_at TEXT NOT NULL,
                consumed_at TEXT NULL
            );
            CREATE TABLE IF NOT EXISTS automations (
                automation_id TEXT PRIMARY KEY,
                source_node_id TEXT NOT NULL REFERENCES agents(node_id),
                certificate_thumbprint TEXT NOT NULL UNIQUE,
                certificate_expires_at TEXT NOT NULL,
                status TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS metric_samples (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                node_id TEXT NOT NULL REFERENCES agents(node_id) ON DELETE CASCADE,
                recorded_at TEXT NOT NULL,
                payload_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_metric_samples_node_time
                ON metric_samples(node_id, recorded_at DESC);
            CREATE TABLE IF NOT EXISTS agent_reconciliation (
                node_id TEXT PRIMARY KEY REFERENCES agents(node_id) ON DELETE CASCADE,
                required INTEGER NOT NULL,
                requested_at TEXT NOT NULL
            );
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

        if (schemaVersion < 1)
        {
            var markVersionOne = connection.CreateCommand();
            markVersionOne.CommandText = "PRAGMA user_version = 1;";
            await markVersionOne.ExecuteNonQueryAsync(cancellationToken);
        }

        if (schemaVersion < 2)
        {
            await using var migration =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var migrateProvisioning = connection.CreateCommand();
            migrateProvisioning.Transaction = migration;
            migrateProvisioning.CommandText = """
                CREATE TABLE IF NOT EXISTS provisioning_jobs (
                    id TEXT PRIMARY KEY,
                    node_id TEXT NOT NULL REFERENCES agents(node_id) ON DELETE CASCADE,
                    action_type TEXT NOT NULL,
                    schema_version INTEGER NOT NULL,
                    parameters_json TEXT NOT NULL,
                    state TEXT NOT NULL,
                    confirmation_required INTEGER NOT NULL,
                    audit_reason TEXT NOT NULL,
                    created_by TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    expires_at TEXT NOT NULL,
                    confirmed_at TEXT NULL,
                    cancelled_at TEXT NULL,
                    version INTEGER NOT NULL,
                    last_error TEXT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_provisioning_jobs_node_created
                    ON provisioning_jobs(node_id, created_at DESC);
                CREATE UNIQUE INDEX IF NOT EXISTS ux_provisioning_jobs_active_node
                    ON provisioning_jobs(node_id)
                    WHERE state NOT IN ('Completed', 'Cancelled', 'Failed', 'RolledBack', 'RollbackFailed');
                CREATE TABLE IF NOT EXISTS provisioning_events (
                    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                    job_id TEXT NOT NULL REFERENCES provisioning_jobs(id) ON DELETE CASCADE,
                    recorded_at TEXT NOT NULL,
                    event_type TEXT NOT NULL,
                    state TEXT NOT NULL,
                    message TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_provisioning_events_job_sequence
                    ON provisioning_events(job_id, sequence);
                PRAGMA user_version = 2;
                """;
            await migrateProvisioning.ExecuteNonQueryAsync(cancellationToken);
            await migration.CommitAsync(cancellationToken);
        }

        if (schemaVersion < 3)
        {
            await using var migration =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var migrateProgress = connection.CreateCommand();
            migrateProgress.Transaction = migration;
            migrateProgress.CommandText = """
                ALTER TABLE provisioning_jobs
                    ADD COLUMN progress_percent INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE provisioning_jobs
                    ADD COLUMN current_step TEXT NOT NULL DEFAULT '';
                PRAGMA user_version = 3;
                """;
            await migrateProgress.ExecuteNonQueryAsync(cancellationToken);
            await migration.CommitAsync(cancellationToken);
        }

        if (schemaVersion < 4)
        {
            await using var migration =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var strengthenProvisioningLock = connection.CreateCommand();
            strengthenProvisioningLock.Transaction = migration;
            strengthenProvisioningLock.CommandText = """
                DROP INDEX IF EXISTS ux_provisioning_jobs_active_node;
                CREATE UNIQUE INDEX ux_provisioning_jobs_active_node
                    ON provisioning_jobs(node_id)
                    WHERE state NOT IN ('Completed', 'Cancelled', 'RolledBack');
                PRAGMA user_version = 4;
                """;
            await strengthenProvisioningLock.ExecuteNonQueryAsync(cancellationToken);
            await migration.CommitAsync(cancellationToken);
        }

        if (schemaVersion < 5)
        {
            await using var migration =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var addStructuredEventFields = connection.CreateCommand();
            addStructuredEventFields.Transaction = migration;
            addStructuredEventFields.CommandText = """
                ALTER TABLE provisioning_events
                    ADD COLUMN step TEXT NOT NULL DEFAULT '';
                ALTER TABLE provisioning_events
                    ADD COLUMN progress_percent INTEGER NOT NULL DEFAULT 0;
                PRAGMA user_version = 5;
                """;
            await addStructuredEventFields.ExecuteNonQueryAsync(cancellationToken);
            await migration.CommitAsync(cancellationToken);
        }
    }

    public async Task<ControlMaintenanceResult> MaintainAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO provisioning_events(
                job_id, recorded_at, event_type, state, message, step, progress_percent)
            SELECT id, $now, 'job.expired', 'Cancelled',
                   'Provisioning job expired before execution.', 'expired', progress_percent
            FROM provisioning_jobs
            WHERE expires_at <= $now
              AND state IN ('Queued', 'AwaitingConfirmation');
            UPDATE provisioning_jobs SET
                state = 'Cancelled', cancelled_at = $now, updated_at = $now,
                current_step = 'expired', version = version + 1,
                last_error = 'job.ttl_expired'
            WHERE expires_at <= $now
              AND state IN ('Queued', 'AwaitingConfirmation');
            SELECT changes();
            INSERT INTO provisioning_events(
                job_id, recorded_at, event_type, state, message, step, progress_percent)
            SELECT id, $now, 'job.reconciliation.required', 'NeedsReconciliation',
                   'Execution lease expired; factual state must be inspected.',
                   'reconcile', progress_percent
            FROM provisioning_jobs
            WHERE expires_at <= $now
              AND state IN ('Preflight', 'Running', 'Verifying');
            UPDATE provisioning_jobs SET
                state = 'NeedsReconciliation', updated_at = $now,
                current_step = 'reconcile', version = version + 1,
                last_error = 'job.ttl_expired'
            WHERE expires_at <= $now
              AND state IN ('Preflight', 'Running', 'Verifying');
            SELECT changes();
            INSERT INTO provisioning_events(
                job_id, recorded_at, event_type, state, message, step, progress_percent)
            SELECT id, $now, 'job.rollback.reconciliation.required', 'NeedsReconciliation',
                   'Rollback lease expired; factual state must be inspected.',
                   'rollback-reconcile', progress_percent
            FROM provisioning_jobs
            WHERE expires_at <= $now AND state = 'RollingBack';
            UPDATE provisioning_jobs SET
                state = 'NeedsReconciliation', updated_at = $now,
                current_step = 'rollback-reconcile', version = version + 1,
                last_error = 'job.rollback.ttl_expired'
            WHERE expires_at <= $now AND state = 'RollingBack';
            SELECT changes();
            DELETE FROM metric_samples WHERE recorded_at < $metric_cutoff;
            SELECT changes();
            DELETE FROM idempotency WHERE created_at < $idempotency_cutoff;
            SELECT changes();
            DELETE FROM audit WHERE recorded_at < $audit_cutoff;
            SELECT changes();
            DELETE FROM enrollment_tokens WHERE consumed_at IS NOT NULL OR expires_at < $now;
            SELECT changes();
            DELETE FROM device_tokens WHERE consumed_at IS NOT NULL OR expires_at < $now;
            SELECT changes();
            DELETE FROM automation_tokens WHERE consumed_at IS NOT NULL OR expires_at < $now;
            SELECT changes();
            """;
        command.Parameters.AddWithValue(
            "$metric_cutoff", now.AddHours(-_options.MetricRetentionHours).ToString("O"));
        command.Parameters.AddWithValue(
            "$idempotency_cutoff", now.AddHours(-_options.IdempotencyRetentionHours).ToString("O"));
        command.Parameters.AddWithValue(
            "$audit_cutoff", now.AddDays(-_options.AuditRetentionDays).ToString("O"));
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        var changes = new int[9];
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            for (var index = 0; index < changes.Length; index++)
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    changes[index] = reader.GetInt32(0);
                }
                await reader.NextResultAsync(cancellationToken);
            }
        }
        await transaction.CommitAsync(cancellationToken);

        var optimize = connection.CreateCommand();
        optimize.CommandText = "PRAGMA optimize; PRAGMA wal_checkpoint(PASSIVE);";
        await optimize.ExecuteNonQueryAsync(cancellationToken);
        return new ControlMaintenanceResult(
            changes[3], changes[4], changes[5], changes[6] + changes[7] + changes[8],
            changes[0], changes[1] + changes[2]);
    }

    public async Task BackupDatabaseAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        await using var source = await OpenAsync(cancellationToken);
        await using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
        cancellationToken.ThrowIfCancellationRequested();
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

    public async Task<AutomationTokenResponse> CreateAutomationTokenAsync(
        AutomationTokenCreateRequest request,
        string actor,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var operationKey = $"automation-token:{actor}:{request.IdempotencyKey}";
        var requestHash = Fingerprint(request, SmmJsonContext.Default.AutomationTokenCreateRequest);
        var cached = await ReadIdempotentAsync<AutomationTokenResponse>(
            connection,
            transaction,
            operationKey,
            requestHash,
            SmmJsonContext.Default.AutomationTokenResponse,
            cancellationToken);
        if (cached is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return cached;
        }

        var source = connection.CreateCommand();
        source.Transaction = transaction;
        source.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM agents
                WHERE node_id = $source AND status != 'Revoked');
            """;
        source.Parameters.AddWithValue("$source", request.SourceNodeId);
        if (Convert.ToInt32(await source.ExecuteScalarAsync(cancellationToken)) != 1)
        {
            throw new InvalidOperationException("Automation source Node is not registered.");
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expiresAt = DateTimeOffset.UtcNow.Add(lifetime);
        var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO automation_tokens(
                token_hash, automation_id, source_node_id, expires_at)
            VALUES ($hash, $automation, $source, $expires);
            """;
        insert.Parameters.AddWithValue("$hash", Hash(token));
        insert.Parameters.AddWithValue("$automation", request.AutomationId);
        insert.Parameters.AddWithValue("$source", request.SourceNodeId);
        insert.Parameters.AddWithValue("$expires", expiresAt.ToString("O"));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        var response = new AutomationTokenResponse(
            request.AutomationId, request.SourceNodeId, token, expiresAt);
        await WriteIdempotentAsync(
            connection,
            transaction,
            operationKey,
            requestHash,
            response,
            SmmJsonContext.Default.AutomationTokenResponse,
            cancellationToken);
        await WriteAuditAsync(
            connection,
            transaction,
            actor,
            "automation.enrollment.requested",
            request.AutomationId,
            JsonSerializer.Serialize(
                new AutomationScope(request.AutomationId, request.SourceNodeId, expiresAt),
                SmmJsonContext.Default.AutomationScope),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return response;
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

    public async Task<AutomationEnrollmentResponse?> EnrollAutomationAsync(
        AutomationEnrollmentRequest request,
        Func<IssuedCertificate> issueCertificate,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var operationKey = $"automation-enroll:{request.IdempotencyKey}";
        var requestHash = Fingerprint(request, SmmJsonContext.Default.AutomationEnrollmentRequest);
        var cached = await ReadIdempotentAsync<AutomationEnrollmentResponse>(
            connection,
            transaction,
            operationKey,
            requestHash,
            SmmJsonContext.Default.AutomationEnrollmentResponse,
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
            UPDATE automation_tokens
            SET consumed_at = $now
            WHERE token_hash = $hash
              AND automation_id = $automation
              AND consumed_at IS NULL
              AND expires_at >= $now
            RETURNING source_node_id;
            """;
        consume.Parameters.AddWithValue("$now", now);
        consume.Parameters.AddWithValue("$hash", Hash(request.Token));
        consume.Parameters.AddWithValue("$automation", request.AutomationId);
        var sourceNodeId = await consume.ExecuteScalarAsync(cancellationToken) as string;
        if (sourceNodeId is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        var issued = issueCertificate();
        var response = new AutomationEnrollmentResponse(
            request.AutomationId,
            sourceNodeId,
            issued.CertificatePem,
            issued.CertificateAuthorityPem,
            issued.ExpiresAt);
        var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO automations(
                automation_id, source_node_id, certificate_thumbprint, certificate_expires_at, status)
            VALUES ($automation, $source, $thumbprint, $expires, 'Active')
            ON CONFLICT(automation_id) DO UPDATE SET
                source_node_id = excluded.source_node_id,
                certificate_thumbprint = excluded.certificate_thumbprint,
                certificate_expires_at = excluded.certificate_expires_at,
                status = 'Active';
            """;
        upsert.Parameters.AddWithValue("$automation", request.AutomationId);
        upsert.Parameters.AddWithValue("$source", sourceNodeId);
        upsert.Parameters.AddWithValue("$thumbprint", issued.Thumbprint);
        upsert.Parameters.AddWithValue("$expires", issued.ExpiresAt.ToString("O"));
        await upsert.ExecuteNonQueryAsync(cancellationToken);
        await WriteIdempotentAsync(
            connection,
            transaction,
            operationKey,
            requestHash,
            response,
            SmmJsonContext.Default.AutomationEnrollmentResponse,
            cancellationToken);
        await WriteAuditAsync(
            connection,
            transaction,
            request.AutomationId,
            "automation.enroll",
            request.AutomationId,
            JsonSerializer.Serialize(
                new AutomationScope(request.AutomationId, sourceNodeId, issued.ExpiresAt),
                SmmJsonContext.Default.AutomationScope),
            cancellationToken);
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
            SELECT node_id, 'Agent', NULL FROM agents
            WHERE certificate_thumbprint = $thumbprint
              AND certificate_expires_at > $now
              AND status != 'Revoked'
            UNION ALL
            SELECT device_id, 'Operator', NULL FROM devices
            WHERE certificate_thumbprint = $thumbprint
              AND certificate_expires_at > $now
              AND status != 'Revoked'
            UNION ALL
            SELECT automation_id, 'Automation', source_node_id FROM automations
            WHERE certificate_thumbprint = $thumbprint
              AND certificate_expires_at > $now
              AND status != 'Revoked'
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$thumbprint", thumbprint);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ControlIdentity(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2))
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

    public async Task<AgentHeartbeatMutation> RecordHeartbeatAsync(
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
            var cachedReconciliationRequired = await IsAgentReconciliationRequiredAsync(
                connection, transaction, heartbeat.NodeId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new AgentHeartbeatMutation(cached, cachedReconciliationRequired);
        }

        var now = DateTimeOffset.UtcNow;
        var previous = connection.CreateCommand();
        previous.Transaction = transaction;
        previous.CommandText = "SELECT status, last_seen_at FROM agents WHERE node_id = $node;";
        previous.Parameters.AddWithValue("$node", heartbeat.NodeId);
        string previousStatus;
        DateTimeOffset? previousLastSeenAt;
        await using (var reader = await previous.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Unknown agent node id.");
            }
            previousStatus = reader.GetString(0);
            previousLastSeenAt = reader.IsDBNull(1) ? null : DateTimeOffset.Parse(reader.GetString(1));
        }
        var reconnectThreshold = TimeSpan.FromSeconds(Math.Max(60, nextHeartbeatSeconds * 3));
        var requiresReconciliation = previousStatus != "Online"
                                     || previousLastSeenAt is null
                                     || now - previousLastSeenAt >= reconnectThreshold;
        if (requiresReconciliation)
        {
            var requireReconciliation = connection.CreateCommand();
            requireReconciliation.Transaction = transaction;
            requireReconciliation.CommandText = """
                INSERT INTO agent_reconciliation(node_id, required, requested_at)
                VALUES ($node, 1, $now)
                ON CONFLICT(node_id) DO UPDATE SET
                    required = 1,
                    requested_at = excluded.requested_at;
                """;
            requireReconciliation.Parameters.AddWithValue("$node", heartbeat.NodeId);
            requireReconciliation.Parameters.AddWithValue("$now", now.ToString("O"));
            await requireReconciliation.ExecuteNonQueryAsync(cancellationToken);
        }
        var reconciliationRequired = await IsAgentReconciliationRequiredAsync(
            connection, transaction, heartbeat.NodeId, cancellationToken);
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
        return new AgentHeartbeatMutation(response, reconciliationRequired);
    }

    public async Task CompleteAgentReconciliationAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE agent_reconciliation
            SET required = 0
            WHERE node_id = $node;
            """;
        command.Parameters.AddWithValue("$node", nodeId);
        await command.ExecuteNonQueryAsync(cancellationToken);
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
        if (existing.DesiredState == "Disabled"
            || await HasNewerLinkAsync(connection, transaction, existing, cancellationToken))
        {
            await WriteIdempotentAsync(
                connection,
                transaction,
                $"link-disable:{actor}:{id}:{request.IdempotencyKey}",
                Fingerprint(request, SmmJsonContext.Default.LinkPolicyDisableRequest),
                existing,
                SmmJsonContext.Default.LinkPolicy,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new LinkMutation(existing, true);
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

    public async Task<IReadOnlyList<LinkPolicy>> ListExpiredLinksAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var result = new List<LinkPolicy>();
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT * FROM links
            WHERE expires_at IS NOT NULL
              AND expires_at <= $now
              AND (desired_state = 'Active'
                   OR (desired_state = 'Disabled' AND actual_state IN ('Disconnecting', 'Partial')))
            ORDER BY expires_at, version;
            """;
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadLink(reader));
        }
        return result;
    }

    public async Task<IReadOnlyList<LinkPolicy>> ListEffectiveLinksForNodeAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        var result = new List<LinkPolicy>();
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT current.*
            FROM links AS current
            WHERE (current.source_node_id = $node OR current.target_node_id = $node)
              AND NOT EXISTS (
                  SELECT 1
                  FROM links AS newer
                  WHERE newer.source_node_id = current.source_node_id
                    AND newer.target_node_id = current.target_node_id
                    AND newer.protocol = current.protocol
                    AND newer.port = current.port
                    AND newer.version > current.version)
            ORDER BY current.version;
            """;
        command.Parameters.AddWithValue("$node", nodeId);
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

    private static async Task<bool> IsAgentReconciliationRequiredAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string nodeId,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE((
                SELECT required FROM agent_reconciliation WHERE node_id = $node
            ), 0);
            """;
        command.Parameters.AddWithValue("$node", nodeId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
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

    private static async Task<bool> HasNewerLinkAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LinkPolicy link,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM links
                WHERE source_node_id = $source
                  AND target_node_id = $target
                  AND protocol = $protocol
                  AND port = $port
                  AND version > $version);
            """;
        command.Parameters.AddWithValue("$source", link.SourceNodeId);
        command.Parameters.AddWithValue("$target", link.TargetNodeId);
        command.Parameters.AddWithValue("$protocol", link.Protocol);
        command.Parameters.AddWithValue("$port", link.Port);
        command.Parameters.AddWithValue("$version", link.Version);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
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

public sealed record ControlIdentity(string Id, string Role, string? SourceNodeId = null);

public sealed record LinkMutation(LinkPolicy Link, bool IsReplay);

public sealed record AgentHeartbeatMutation(
    AgentHeartbeatResponse Response,
    bool RequiresReconciliation);

public sealed record LinkReconciliationResult(int Applied, int Failed);

public sealed record AgentReenrollmentMutation(
    CertificateReenrollmentTicket Ticket,
    IReadOnlyList<LinkPolicy> Links,
    bool IsReplay);
