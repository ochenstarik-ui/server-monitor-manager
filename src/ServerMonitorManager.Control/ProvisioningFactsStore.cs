using System.Text.Json;
using Microsoft.Data.Sqlite;
using ServerMonitorManager.Core;

namespace ServerMonitorManager.Control;

public sealed partial class ControlStore
{
    public async Task<NodePreflightFacts?> RecordPreflightFactsAsync(
        string nodeId,
        string jobId,
        ProvisioningPreflightReportRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var operationKey = $"provisioning-preflight:{nodeId}:{jobId}:{request.IdempotencyKey}";
        var requestHash = Fingerprint(request, SmmJsonContext.Default.ProvisioningPreflightReportRequest);
        var cached = await ReadIdempotentAsync(
            connection, transaction, operationKey, requestHash,
            SmmJsonContext.Default.NodePreflightFacts, cancellationToken);
        if (cached is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return cached;
        }

        var job = await ReadProvisioningJobAsync(connection, transaction, jobId, cancellationToken);
        if (job is null || !string.Equals(job.NodeId, nodeId, StringComparison.Ordinal))
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        if (job.ActionType != "preflight" || job.SchemaVersion != 1
            || job.State != ProvisioningJobStates.Preflight)
        {
            throw new ProvisioningTransitionException(job.State, "PreflightFacts");
        }

        var facts = new NodePreflightFacts(
            nodeId, 1, request.Facts, request.ObservedAt, jobId, DateTimeOffset.UtcNow);
        var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO node_preflight_facts(
                node_id, schema_version, facts_json, observed_at, source_job_id, updated_at)
            VALUES ($node, $schema, $facts, $observed, $job, $updated)
            ON CONFLICT(node_id) DO UPDATE SET
                schema_version = excluded.schema_version,
                facts_json = excluded.facts_json,
                observed_at = excluded.observed_at,
                source_job_id = excluded.source_job_id,
                updated_at = excluded.updated_at;
            """;
        upsert.Parameters.AddWithValue("$node", facts.NodeId);
        upsert.Parameters.AddWithValue("$schema", facts.SchemaVersion);
        upsert.Parameters.AddWithValue(
            "$facts", JsonSerializer.Serialize(facts.Facts, SmmJsonContext.Default.ProvisioningPreflightResult));
        upsert.Parameters.AddWithValue("$observed", facts.ObservedAt.ToString("O"));
        upsert.Parameters.AddWithValue("$job", facts.SourceJobId);
        upsert.Parameters.AddWithValue("$updated", facts.UpdatedAt.ToString("O"));
        await upsert.ExecuteNonQueryAsync(cancellationToken);
        await WriteProvisioningEventAsync(
            connection, transaction, jobId, "preflight.facts-recorded", job.State,
            "Validated preflight facts were recorded.", facts.UpdatedAt, cancellationToken);
        await WriteIdempotentAsync(
            connection, transaction, operationKey, requestHash, facts,
            SmmJsonContext.Default.NodePreflightFacts, cancellationToken);
        await WriteAuditAsync(
            connection, transaction, nodeId, "provisioning.preflight.facts", jobId,
            JsonSerializer.Serialize(new { facts.SchemaVersion, facts.ObservedAt }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return facts;
    }

    public async Task<NodePreflightFacts?> GetPreflightFactsAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT schema_version, facts_json, observed_at, source_job_id, updated_at
            FROM node_preflight_facts WHERE node_id = $node;
            """;
        command.Parameters.AddWithValue("$node", nodeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        var facts = JsonSerializer.Deserialize(
            reader.GetString(1), SmmJsonContext.Default.ProvisioningPreflightResult)
            ?? throw new InvalidDataException("Stored preflight facts are invalid.");
        return new NodePreflightFacts(
            nodeId, reader.GetInt32(0), facts, DateTimeOffset.Parse(reader.GetString(2)),
            reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4)));
    }

    public async Task<NodePreflightDesiredState> SetPreflightDesiredStateAsync(
        string nodeId,
        PreflightDesiredStateUpdateRequest request,
        string actor,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var operationKey = $"preflight-desired:{actor}:{nodeId}:{request.IdempotencyKey}";
        var requestHash = Fingerprint(request, SmmJsonContext.Default.PreflightDesiredStateUpdateRequest);
        var cached = await ReadIdempotentAsync(
            connection, transaction, operationKey, requestHash,
            SmmJsonContext.Default.NodePreflightDesiredState, cancellationToken);
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

        var readVersion = connection.CreateCommand();
        readVersion.Transaction = transaction;
        readVersion.CommandText =
            "SELECT version FROM node_preflight_desired_state WHERE node_id = $node;";
        readVersion.Parameters.AddWithValue("$node", nodeId);
        var existingVersion = await readVersion.ExecuteScalarAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var desired = new NodePreflightDesiredState(
            nodeId, request.SchemaVersion, request.Desired,
            existingVersion is null ? 1 : Convert.ToInt64(existingVersion) + 1,
            actor, request.AuditReason, now);
        var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO node_preflight_desired_state(
                node_id, schema_version, desired_json, version,
                updated_by, audit_reason, updated_at)
            VALUES ($node, $schema, $desired, $version, $actor, $reason, $updated)
            ON CONFLICT(node_id) DO UPDATE SET
                schema_version = excluded.schema_version,
                desired_json = excluded.desired_json,
                version = excluded.version,
                updated_by = excluded.updated_by,
                audit_reason = excluded.audit_reason,
                updated_at = excluded.updated_at;
            """;
        upsert.Parameters.AddWithValue("$node", desired.NodeId);
        upsert.Parameters.AddWithValue("$schema", desired.SchemaVersion);
        upsert.Parameters.AddWithValue(
            "$desired",
            JsonSerializer.Serialize(desired.Desired, SmmJsonContext.Default.PreflightDesiredRequirements));
        upsert.Parameters.AddWithValue("$version", desired.Version);
        upsert.Parameters.AddWithValue("$actor", desired.UpdatedBy);
        upsert.Parameters.AddWithValue("$reason", desired.AuditReason);
        upsert.Parameters.AddWithValue("$updated", desired.UpdatedAt.ToString("O"));
        await upsert.ExecuteNonQueryAsync(cancellationToken);
        await WriteIdempotentAsync(
            connection, transaction, operationKey, requestHash, desired,
            SmmJsonContext.Default.NodePreflightDesiredState, cancellationToken);
        await WriteAuditAsync(
            connection, transaction, actor, "provisioning.preflight.desired", nodeId,
            JsonSerializer.Serialize(new { desired.SchemaVersion, desired.Version }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return desired;
    }

    public async Task<NodePreflightDesiredState?> GetPreflightDesiredStateAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT schema_version, desired_json, version, updated_by, audit_reason, updated_at
            FROM node_preflight_desired_state WHERE node_id = $node;
            """;
        command.Parameters.AddWithValue("$node", nodeId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        var desired = JsonSerializer.Deserialize(
            reader.GetString(1), SmmJsonContext.Default.PreflightDesiredRequirements)
            ?? throw new InvalidDataException("Stored preflight desired state is invalid.");
        return new NodePreflightDesiredState(
            nodeId, reader.GetInt32(0), desired, reader.GetInt64(2), reader.GetString(3),
            reader.GetString(4), DateTimeOffset.Parse(reader.GetString(5)));
    }

    public async Task<PreflightDriftAssessment?> AssessPreflightDriftAsync(
        string nodeId,
        CancellationToken cancellationToken = default)
    {
        await using (var connection = await OpenAsync(cancellationToken))
        {
            var exists = connection.CreateCommand();
            exists.CommandText = "SELECT EXISTS(SELECT 1 FROM agents WHERE node_id = $node);";
            exists.Parameters.AddWithValue("$node", nodeId);
            if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) != 1)
            {
                return null;
            }
        }
        var desired = await GetPreflightDesiredStateAsync(nodeId, cancellationToken);
        var facts = await GetPreflightFactsAsync(nodeId, cancellationToken);
        return PreflightDriftEvaluator.Assess(nodeId, desired, facts);
    }
}

internal static class PreflightDriftEvaluator
{
    public static PreflightDriftAssessment Assess(
        string nodeId,
        NodePreflightDesiredState? desired,
        NodePreflightFacts? facts)
    {
        if (desired is null)
        {
            return new PreflightDriftAssessment(
                nodeId, PreflightDriftStatuses.NotConfigured, [], null, facts);
        }
        if (facts is null)
        {
            return new PreflightDriftAssessment(
                nodeId, PreflightDriftStatuses.Unknown, [PreflightDriftCodes.FactsMissing], desired, null);
        }

        var drift = new List<string>();
        AddMissing(
            drift, desired.Desired.RequireSystemd, facts.Facts.HasSystemd,
            PreflightDriftCodes.SystemdMissing);
        AddMissing(
            drift, desired.Desired.RequireSshd, facts.Facts.HasSshd,
            PreflightDriftCodes.SshdMissing);
        AddMissing(
            drift, desired.Desired.RequireNftables, facts.Facts.HasNftables,
            PreflightDriftCodes.NftablesMissing);
        AddMissing(
            drift, desired.Desired.RequireWireGuard, facts.Facts.HasWireGuard,
            PreflightDriftCodes.WireGuardMissing);
        AddMissing(
            drift, desired.Desired.RequireApt, facts.Facts.HasApt,
            PreflightDriftCodes.AptMissing);
        if (!desired.Desired.AllowedArchitectures.Contains(
                facts.Facts.Architecture, StringComparer.Ordinal))
        {
            drift.Add(PreflightDriftCodes.ArchitectureUnsupported);
        }
        return new PreflightDriftAssessment(
            nodeId,
            drift.Count == 0 ? PreflightDriftStatuses.InSync : PreflightDriftStatuses.Drifted,
            [.. drift], desired, facts);
    }

    private static void AddMissing(List<string> drift, bool required, bool present, string code)
    {
        if (required && !present)
        {
            drift.Add(code);
        }
    }
}
