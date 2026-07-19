using System.Text.Json;
using Microsoft.Data.Sqlite;
using ServerMonitorManager.Core;

namespace ServerMonitorManager.Control;

public sealed partial class ControlStore
{
    public async Task<ProvisioningBaseInstallPlanRecord?> RecordBaseInstallPlanAsync(
        string nodeId,
        string jobId,
        SystemBaseInstallPlanReportRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var operationKey = $"provisioning-base-install-plan:{nodeId}:{jobId}:{request.IdempotencyKey}";
        var requestHash = Fingerprint(
            request, SmmJsonContext.Default.SystemBaseInstallPlanReportRequest);
        var cached = await ReadIdempotentAsync(
            connection, transaction, operationKey, requestHash,
            SmmJsonContext.Default.ProvisioningBaseInstallPlanRecord, cancellationToken);
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
        if (job.ActionType != "system.base-install" || job.SchemaVersion != 1
            || job.State != ProvisioningJobStates.Preflight || !job.ConfirmationRequired
            || job.ConfirmedAt is not null)
        {
            throw new ProvisioningTransitionException(job.State, ProvisioningJobStates.AwaitingConfirmation);
        }
        if (!SystemBaseInstallSchema.TryParse(job.Parameters, out var parameters)
            || parameters is null
            || !SystemBaseInstallSchema.IsValidPlan(parameters, request.Plan))
        {
            throw new ProvisioningPlanValidationException();
        }

        var now = DateTimeOffset.UtcNow;
        var record = new ProvisioningBaseInstallPlanRecord(
            jobId, nodeId, job.SchemaVersion, request.Plan, now);
        var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO provisioning_base_install_plans(
                job_id, schema_version, plan_json, created_at)
            VALUES ($job, $schema, $plan, $created);
            """;
        insert.Parameters.AddWithValue("$job", record.JobId);
        insert.Parameters.AddWithValue("$schema", record.SchemaVersion);
        insert.Parameters.AddWithValue(
            "$plan", JsonSerializer.Serialize(record.Plan, SmmJsonContext.Default.SystemBaseInstallPlan));
        insert.Parameters.AddWithValue("$created", record.CreatedAt.ToString("O"));
        await insert.ExecuteNonQueryAsync(cancellationToken);

        var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE provisioning_jobs SET
                state = $state,
                progress_percent = 25,
                current_step = 'awaiting-confirmation',
                updated_at = $updated,
                version = version + 1
            WHERE id = $id AND node_id = $node AND version = $version
              AND state = $preflight AND confirmed_at IS NULL;
            """;
        update.Parameters.AddWithValue("$state", ProvisioningJobStates.AwaitingConfirmation);
        update.Parameters.AddWithValue("$updated", now.ToString("O"));
        update.Parameters.AddWithValue("$id", job.Id);
        update.Parameters.AddWithValue("$node", nodeId);
        update.Parameters.AddWithValue("$version", job.Version);
        update.Parameters.AddWithValue("$preflight", ProvisioningJobStates.Preflight);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new ProvisioningTransitionException(job.State, ProvisioningJobStates.AwaitingConfirmation);
        }

        await WriteProvisioningEventAsync(
            connection, transaction, jobId, "base-install.plan-recorded",
            ProvisioningJobStates.AwaitingConfirmation,
            "Validated base installation plan is awaiting operator confirmation.", now,
            cancellationToken);
        await WriteIdempotentAsync(
            connection, transaction, operationKey, requestHash, record,
            SmmJsonContext.Default.ProvisioningBaseInstallPlanRecord, cancellationToken);
        await WriteAuditAsync(
            connection, transaction, nodeId, "provisioning.base-install.plan", jobId,
            JsonSerializer.Serialize(new
            {
                record.SchemaVersion,
                PackageCount = record.Plan.Packages.Length,
                WarningCodes = record.Plan.Warnings
            }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return record;
    }

    public async Task<ProvisioningBaseInstallPlanRecord?> GetBaseInstallPlanAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.schema_version, p.plan_json, p.created_at, j.node_id
            FROM provisioning_base_install_plans p
            INNER JOIN provisioning_jobs j ON j.id = p.job_id
            WHERE p.job_id = $job;
            """;
        command.Parameters.AddWithValue("$job", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        var plan = JsonSerializer.Deserialize(
            reader.GetString(1), SmmJsonContext.Default.SystemBaseInstallPlan)
            ?? throw new InvalidDataException("Stored base installation plan is invalid.");
        return new ProvisioningBaseInstallPlanRecord(
            jobId, reader.GetString(3), reader.GetInt32(0), plan,
            DateTimeOffset.Parse(reader.GetString(2)));
    }
}

public sealed class ProvisioningPlanValidationException()
    : Exception("Base installation plan does not match the validated job parameters.");
