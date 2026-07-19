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
}
