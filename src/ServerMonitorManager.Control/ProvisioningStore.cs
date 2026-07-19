using System.Text.Json;
using Microsoft.Data.Sqlite;
using ServerMonitorManager.Core;

namespace ServerMonitorManager.Control;

public sealed partial class ControlStore
{
    public async Task<ProvisioningJob> CreateProvisioningJobAsync(
        string nodeId,
        ProvisioningJobCreateRequest request,
        string actor,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var operationKey = $"provisioning-create:{actor}:{nodeId}:{request.IdempotencyKey}";
        var requestHash = Fingerprint(request, SmmJsonContext.Default.ProvisioningJobCreateRequest);
        var cached = await ReadIdempotentAsync(
            connection, transaction, operationKey, requestHash,
            SmmJsonContext.Default.ProvisioningJob, cancellationToken);
        if (cached is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return cached;
        }

        var exists = connection.CreateCommand();
        exists.Transaction = transaction;
        exists.CommandText = "SELECT EXISTS(SELECT 1 FROM agents WHERE node_id = $node);";
        exists.Parameters.AddWithValue("$node", nodeId);
        if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) != 1)
        {
            throw new ProvisioningNodeNotFoundException(nodeId);
        }

        var now = DateTimeOffset.UtcNow;
        var confirmationRequired = ProvisioningJobValidator.RequiresConfirmation(request.ActionType);
        var job = new ProvisioningJob(
            Guid.NewGuid().ToString("N"),
            nodeId,
            request.ActionType,
            request.SchemaVersion,
            request.Parameters.Clone(),
            confirmationRequired
                ? ProvisioningJobStates.AwaitingConfirmation
                : ProvisioningJobStates.Queued,
            confirmationRequired,
            request.AuditReason,
            actor,
            now,
            now,
            now.AddMinutes(request.TtlMinutes),
            null,
            null,
            1,
            0,
            "queued",
            null);

        var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO provisioning_jobs(
                id, node_id, action_type, schema_version, parameters_json, state,
                confirmation_required, audit_reason, created_by, created_at, updated_at,
                expires_at, confirmed_at, cancelled_at, version, last_error)
            VALUES(
                $id, $node, $action, $schema, $parameters, $state,
                $confirmation, $reason, $actor, $created, $updated,
                $expires, NULL, NULL, $version, NULL);
            """;
        AddProvisioningJobParameters(insert, job);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await WriteProvisioningEventAsync(
            connection, transaction, job.Id, "job.created", job.State,
            "Provisioning job accepted by the control plane.", now, cancellationToken);
        await WriteIdempotentAsync(
            connection, transaction, operationKey, requestHash, job,
            SmmJsonContext.Default.ProvisioningJob, cancellationToken);
        await WriteAuditAsync(
            connection, transaction, actor, "provisioning.job.created", job.Id,
            JsonSerializer.Serialize(new { job.NodeId, job.ActionType, job.SchemaVersion, job.AuditReason }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return job;
    }

    public async Task<ProvisioningJob?> GetProvisioningJobAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM provisioning_jobs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProvisioningJob(reader) : null;
    }

    public async Task<ProvisioningJob?> ClaimNextProvisioningJobAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE provisioning_jobs SET
                state = $preflight,
                updated_at = $now,
                version = version + 1
            WHERE id = (
                SELECT id FROM provisioning_jobs
                WHERE node_id = $node
                  AND state = $queued
                  AND expires_at > $now
                ORDER BY created_at, id
                LIMIT 1)
              AND state = $queued
            RETURNING *;
            """;
        command.Parameters.AddWithValue("$node", nodeId);
        command.Parameters.AddWithValue("$queued", ProvisioningJobStates.Queued);
        command.Parameters.AddWithValue("$preflight", ProvisioningJobStates.Preflight);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        ProvisioningJob? job;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            job = await reader.ReadAsync(cancellationToken) ? ReadProvisioningJob(reader) : null;
        }
        if (job is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        await WriteProvisioningEventAsync(
            connection, transaction, job.Id, "job.claimed", job.State,
            "Provisioning job claimed by its assigned Node.", now, cancellationToken);
        await WriteAuditAsync(
            connection, transaction, nodeId, "provisioning.job.claimed", job.Id,
            JsonSerializer.Serialize(new { job.NodeId, job.ActionType, job.SchemaVersion }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return job;
    }

    public Task<ProvisioningJob?> ConfirmProvisioningJobAsync(
        string id,
        ProvisioningJobCommandRequest request,
        string actor,
        CancellationToken cancellationToken = default)
        => TransitionProvisioningJobAsync(
            id, request, actor, ProvisioningJobStates.AwaitingConfirmation,
            ProvisioningJobStates.Queued, "job.confirmed", "provisioning.job.confirmed",
            setConfirmedAt: true, cancellationToken);

    public Task<ProvisioningJob?> CancelProvisioningJobAsync(
        string id,
        ProvisioningJobCommandRequest request,
        string actor,
        CancellationToken cancellationToken = default)
        => TransitionProvisioningJobAsync(
            id, request, actor, null, ProvisioningJobStates.Cancelled,
            "job.cancelled", "provisioning.job.cancelled",
            setConfirmedAt: false, cancellationToken);

    public async Task<ProvisioningJob?> ReportProvisioningProgressAsync(
        string nodeId,
        string id,
        ProvisioningJobProgressRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var operationKey = $"provisioning-progress:{nodeId}:{id}:{request.IdempotencyKey}";
        var requestHash = Fingerprint(request, SmmJsonContext.Default.ProvisioningJobProgressRequest);
        var cached = await ReadIdempotentAsync(
            connection, transaction, operationKey, requestHash,
            SmmJsonContext.Default.ProvisioningJob, cancellationToken);
        if (cached is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return cached;
        }

        var current = await ReadProvisioningJobAsync(connection, transaction, id, cancellationToken);
        if (current is null || !string.Equals(current.NodeId, nodeId, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        if (!ProvisioningStateMachine.CanReport(
                current.State, request.State, current.ProgressPercent, request.ProgressPercent))
        {
            throw new ProvisioningTransitionException(current.State, request.State);
        }

        var now = DateTimeOffset.UtcNow;
        var updated = current with
        {
            State = request.State,
            ProgressPercent = request.ProgressPercent,
            CurrentStep = request.Step,
            UpdatedAt = now,
            Version = current.Version + 1,
            LastError = request.State is ProvisioningJobStates.Failed
                or ProvisioningJobStates.NeedsReconciliation
                ? request.EventCode
                : null
        };
        var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE provisioning_jobs SET
                state = $state,
                progress_percent = $progress,
                current_step = $step,
                updated_at = $updated,
                version = $version,
                last_error = $error
            WHERE id = $id AND node_id = $node AND version = $previous_version;
            """;
        update.Parameters.AddWithValue("$state", updated.State);
        update.Parameters.AddWithValue("$progress", updated.ProgressPercent);
        update.Parameters.AddWithValue("$step", updated.CurrentStep);
        update.Parameters.AddWithValue("$updated", updated.UpdatedAt.ToString("O"));
        update.Parameters.AddWithValue("$version", updated.Version);
        update.Parameters.AddWithValue("$error", (object?)updated.LastError ?? DBNull.Value);
        update.Parameters.AddWithValue("$id", updated.Id);
        update.Parameters.AddWithValue("$node", nodeId);
        update.Parameters.AddWithValue("$previous_version", current.Version);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new ProvisioningTransitionException(current.State, request.State);
        }

        await WriteProvisioningEventAsync(
            connection, transaction, id, request.EventCode, request.State,
            request.Message, now, cancellationToken);
        await WriteIdempotentAsync(
            connection, transaction, operationKey, requestHash, updated,
            SmmJsonContext.Default.ProvisioningJob, cancellationToken);
        await WriteAuditAsync(
            connection, transaction, nodeId, "provisioning.job.progress", id,
            JsonSerializer.Serialize(new
            {
                request.State,
                request.ProgressPercent,
                request.Step,
                request.EventCode
            }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    private async Task<ProvisioningJob?> TransitionProvisioningJobAsync(
        string id,
        ProvisioningJobCommandRequest request,
        string actor,
        string? requiredState,
        string targetState,
        string eventType,
        string auditAction,
        bool setConfirmedAt,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var operationKey = $"{eventType}:{actor}:{id}:{request.IdempotencyKey}";
        var requestHash = Fingerprint(request, SmmJsonContext.Default.ProvisioningJobCommandRequest);
        var cached = await ReadIdempotentAsync(
            connection, transaction, operationKey, requestHash,
            SmmJsonContext.Default.ProvisioningJob, cancellationToken);
        if (cached is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return cached;
        }

        var current = await ReadProvisioningJobAsync(connection, transaction, id, cancellationToken);
        if (current is null)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        var allowed = requiredState is not null
            ? current.State == requiredState
            : current.State is ProvisioningJobStates.Queued
                or ProvisioningJobStates.Preflight
                or ProvisioningJobStates.AwaitingConfirmation;
        if (!allowed || current.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new ProvisioningTransitionException(current.State, targetState);
        }

        var now = DateTimeOffset.UtcNow;
        var updated = current with
        {
            State = targetState,
            UpdatedAt = now,
            ConfirmedAt = setConfirmedAt ? now : current.ConfirmedAt,
            CancelledAt = targetState == ProvisioningJobStates.Cancelled ? now : current.CancelledAt,
            Version = current.Version + 1
        };
        var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE provisioning_jobs SET
                state = $state, updated_at = $updated, confirmed_at = $confirmed,
                cancelled_at = $cancelled, version = $version
            WHERE id = $id AND version = $previous_version;
            """;
        AddProvisioningJobParameters(update, updated);
        update.Parameters.AddWithValue("$previous_version", current.Version);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new ProvisioningTransitionException(current.State, targetState);
        }

        await WriteProvisioningEventAsync(
            connection, transaction, id, eventType, targetState, request.Reason, now, cancellationToken);
        await WriteIdempotentAsync(
            connection, transaction, operationKey, requestHash, updated,
            SmmJsonContext.Default.ProvisioningJob, cancellationToken);
        await WriteAuditAsync(
            connection, transaction, actor, auditAction, id,
            JsonSerializer.Serialize(new { request.Reason, State = targetState }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    private static async Task<ProvisioningJob?> ReadProvisioningJobAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT * FROM provisioning_jobs WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProvisioningJob(reader) : null;
    }

    private static ProvisioningJob ReadProvisioningJob(SqliteDataReader reader)
        => new(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
            ParseParameters(reader.GetString(4)), reader.GetString(5),
            reader.GetInt32(6) == 1, reader.GetString(7), reader.GetString(8),
            DateTimeOffset.Parse(reader.GetString(9)), DateTimeOffset.Parse(reader.GetString(10)),
            DateTimeOffset.Parse(reader.GetString(11)),
            reader.IsDBNull(12) ? null : DateTimeOffset.Parse(reader.GetString(12)),
            reader.IsDBNull(13) ? null : DateTimeOffset.Parse(reader.GetString(13)),
            reader.GetInt64(14), reader.GetInt32(16), reader.GetString(17),
            reader.IsDBNull(15) ? null : reader.GetString(15));

    private static JsonElement ParseParameters(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void AddProvisioningJobParameters(SqliteCommand command, ProvisioningJob job)
    {
        command.Parameters.AddWithValue("$id", job.Id);
        command.Parameters.AddWithValue("$node", job.NodeId);
        command.Parameters.AddWithValue("$action", job.ActionType);
        command.Parameters.AddWithValue("$schema", job.SchemaVersion);
        command.Parameters.AddWithValue("$parameters", job.Parameters.GetRawText());
        command.Parameters.AddWithValue("$state", job.State);
        command.Parameters.AddWithValue("$confirmation", job.ConfirmationRequired ? 1 : 0);
        command.Parameters.AddWithValue("$reason", job.AuditReason);
        command.Parameters.AddWithValue("$actor", job.CreatedBy);
        command.Parameters.AddWithValue("$created", job.CreatedAt.ToString("O"));
        command.Parameters.AddWithValue("$updated", job.UpdatedAt.ToString("O"));
        command.Parameters.AddWithValue("$expires", job.ExpiresAt.ToString("O"));
        command.Parameters.AddWithValue("$confirmed", job.ConfirmedAt is null ? DBNull.Value : job.ConfirmedAt.Value.ToString("O"));
        command.Parameters.AddWithValue("$cancelled", job.CancelledAt is null ? DBNull.Value : job.CancelledAt.Value.ToString("O"));
        command.Parameters.AddWithValue("$version", job.Version);
    }

    private static async Task WriteProvisioningEventAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string jobId,
        string eventType,
        string state,
        string message,
        DateTimeOffset recordedAt,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO provisioning_events(job_id, recorded_at, event_type, state, message)
            VALUES ($job, $recorded, $event, $state, $message);
            """;
        command.Parameters.AddWithValue("$job", jobId);
        command.Parameters.AddWithValue("$recorded", recordedAt.ToString("O"));
        command.Parameters.AddWithValue("$event", eventType);
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$message", message);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

public sealed class ProvisioningNodeNotFoundException(string nodeId) : Exception($"Node '{nodeId}' was not found.");

public sealed class ProvisioningTransitionException(string state, string targetState)
    : Exception($"Provisioning job cannot transition from '{state}' to '{targetState}'.");

internal static class ProvisioningStateMachine
{
    public static bool CanReport(string current, string target, int currentProgress, int targetProgress)
    {
        if (targetProgress < currentProgress || targetProgress is < 0 or > 100)
        {
            return false;
        }
        if (target is ProvisioningJobStates.Failed or ProvisioningJobStates.NeedsReconciliation)
        {
            return current is ProvisioningJobStates.Preflight
                or ProvisioningJobStates.Running
                or ProvisioningJobStates.Verifying;
        }
        return (current, target) switch
        {
            (ProvisioningJobStates.Preflight, ProvisioningJobStates.Preflight) => true,
            (ProvisioningJobStates.Preflight, ProvisioningJobStates.Running) => true,
            (ProvisioningJobStates.Running, ProvisioningJobStates.Running) => true,
            (ProvisioningJobStates.Running, ProvisioningJobStates.Verifying) => true,
            (ProvisioningJobStates.Verifying, ProvisioningJobStates.Verifying) => true,
            (ProvisioningJobStates.Verifying, ProvisioningJobStates.Completed) => targetProgress == 100,
            _ => false
        };
    }
}
