using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using ServerMonitorManager.Control;
using ServerMonitorManager.Core;
using Xunit;

namespace ServerMonitorManager.Control.Tests;

public sealed class ControlMaintenanceTests : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"smm-maintenance-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task ExpiredLinkIsDisabledAndFirewallStateIsReconciled()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (store, _) = CreateServices();
        await store.InitializeAsync(cancellationToken);
        await EnrollAgentAsync(store, "ai-agent", "AA11", cancellationToken);
        await EnrollAgentAsync(store, "home", "BB22", cancellationToken);
        var applier = new RecordingPolicyApplier();
        var service = new LinkService(store, applier, new ControlEventBroker());
        var link = await service.CreateAsync(
            new LinkPolicyCreateRequest(
                "ai-agent", "home", "tcp", 22, 1, "ttl-test", Guid.NewGuid().ToString()),
            "windows-pc",
            cancellationToken);

        var result = await service.ExpireDueLinksAsync(
            link.CreatedAt.AddMinutes(2), cancellationToken);
        var persisted = Assert.Single(await store.ListLinksAsync(cancellationToken));

        Assert.Equal(new LinkExpirationResult(1, 0), result);
        Assert.Equal("Disabled", persisted.DesiredState);
        Assert.Equal("Disabled", persisted.ActualState);
        Assert.Equal(1, applier.DisconnectCalls);
    }

    [Fact]
    public async Task ExpirationRetriesPartialFirewallRemoval()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (store, _) = CreateServices();
        await store.InitializeAsync(cancellationToken);
        await EnrollAgentAsync(store, "ai-agent", "CC33", cancellationToken);
        await EnrollAgentAsync(store, "home", "DD44", cancellationToken);
        var applier = new RecordingPolicyApplier { FailDisconnect = true };
        var service = new LinkService(store, applier, new ControlEventBroker());
        var link = await service.CreateAsync(
            new LinkPolicyCreateRequest(
                "ai-agent", "home", "tcp", 22, 1, "retry-test", Guid.NewGuid().ToString()),
            "windows-pc",
            cancellationToken);
        var expiredAt = link.CreatedAt.AddMinutes(2);

        Assert.Equal(new LinkExpirationResult(0, 1),
            await service.ExpireDueLinksAsync(expiredAt, cancellationToken));
        applier.FailDisconnect = false;
        Assert.Equal(new LinkExpirationResult(1, 0),
            await service.ExpireDueLinksAsync(expiredAt.AddSeconds(1), cancellationToken));

        var persisted = Assert.Single(await store.ListLinksAsync(cancellationToken));
        Assert.Equal("Disabled", persisted.ActualState);
        Assert.Equal(2, applier.DisconnectCalls);
    }

    [Fact]
    public async Task MaintenancePrunesExpiredOperationalDataAndVersionsSchema()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (store, options) = CreateServices();
        await store.InitializeAsync(cancellationToken);
        await EnrollAgentAsync(store, "home", "EE55", cancellationToken);
        var now = DateTimeOffset.UtcNow;
        await store.RecordHeartbeatAsync(
            new AgentHeartbeat(
                "home", "test", now, 0.2, 1, 2, 1, 2, 3, 4, 5, Guid.NewGuid().ToString()),
            30,
            cancellationToken);

        await using (var connection = new SqliteConnection($"Data Source={options.DatabasePath}"))
        {
            await connection.OpenAsync(cancellationToken);
            var age = connection.CreateCommand();
            age.CommandText = """
                UPDATE metric_samples SET recorded_at = $old;
                UPDATE idempotency SET created_at = $old;
                UPDATE audit SET recorded_at = $old;
                INSERT INTO device_tokens(token_hash, device_id, expires_at, consumed_at)
                VALUES ('expired', 'old-device', $old, NULL);
                """;
            age.Parameters.AddWithValue("$old", now.AddDays(-400).ToString("O"));
            await age.ExecuteNonQueryAsync(cancellationToken);
        }

        var result = await store.MaintainAsync(now, cancellationToken);
        Assert.True(result.MetricsDeleted >= 1);
        Assert.True(result.IdempotencyDeleted >= 1);
        Assert.True(result.AuditDeleted >= 1);
        Assert.True(result.TokensDeleted >= 1);

        await using var verify = new SqliteConnection($"Data Source={options.DatabasePath}");
        await verify.OpenAsync(cancellationToken);
        var version = verify.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(6L, (long)(await version.ExecuteScalarAsync(cancellationToken))!);
    }

    [Fact]
    public async Task ExpiredProvisioningJobsAreCancelledOrRequireReconciliationAndCanRetry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (store, _) = CreateServices();
        await store.InitializeAsync(cancellationToken);
        await EnrollAgentAsync(store, "queued-node", "AABB", cancellationToken);
        await EnrollAgentAsync(store, "running-node", "CCDD", cancellationToken);
        using var parameters = JsonDocument.Parse("{}");
        var queued = await store.CreateProvisioningJobAsync(
            "queued-node",
            new ProvisioningJobCreateRequest(
                "system.base-install", 1, parameters.RootElement.Clone(), 5,
                "Await approval", Guid.NewGuid().ToString()),
            "operator",
            cancellationToken);
        var running = await store.CreateProvisioningJobAsync(
            "running-node",
            new ProvisioningJobCreateRequest(
                "preflight", 1, parameters.RootElement.Clone(), 5,
                "Inspect node", Guid.NewGuid().ToString()),
            "operator",
            cancellationToken);
        Assert.NotNull(await store.ClaimNextProvisioningJobAsync("running-node", cancellationToken));

        var result = await store.MaintainAsync(
            running.ExpiresAt.AddSeconds(1), cancellationToken);

        Assert.Equal(1, result.ProvisioningJobsCancelled);
        Assert.Equal(1, result.ProvisioningJobsNeedingReconciliation);
        Assert.Equal(ProvisioningJobStates.Cancelled,
            (await store.GetProvisioningJobAsync(queued.Id, cancellationToken))!.State);
        var reconciliation = await store.GetProvisioningJobAsync(running.Id, cancellationToken);
        Assert.Equal(ProvisioningJobStates.NeedsReconciliation, reconciliation!.State);
        Assert.Equal("job.ttl_expired", reconciliation.LastError);

        var retried = await store.RetryProvisioningJobAsync(
            running.Id,
            new ProvisioningJobCommandRequest("Factual state checked", Guid.NewGuid().ToString()),
            "operator",
            cancellationToken);
        Assert.Equal(ProvisioningJobStates.Queued, retried!.State);
        Assert.Equal(0, retried.ProgressPercent);
        Assert.Null(retried.LastError);
        Assert.True(retried.ExpiresAt > running.ExpiresAt);
        Assert.NotNull(await store.ClaimNextProvisioningJobAsync("running-node", cancellationToken));
    }

    [Fact]
    public async Task BackupCanRestoreDatabaseAndCertificateAuthority()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var (store, options) = CreateServices();
        await File.WriteAllBytesAsync(options.CertificateAuthorityPath, [1, 2, 3, 4], cancellationToken);
        await store.InitializeAsync(cancellationToken);
        await EnrollAgentAsync(store, "home", "FF66", cancellationToken);
        var backups = new ControlBackupService(
            store, Options.Create(options), NullLogger<ControlBackupService>.Instance);
        var backupPath = await backups.CreateAsync(DateTimeOffset.UtcNow, cancellationToken);

        SqliteConnection.ClearAllPools();
        File.Delete(options.DatabasePath);
        await File.WriteAllBytesAsync(options.CertificateAuthorityPath, [9, 9], cancellationToken);
        await backups.RestoreAsync(backupPath, cancellationToken);

        var restoredStore = new ControlStore(Options.Create(options));
        await restoredStore.InitializeAsync(cancellationToken);
        Assert.Single(await restoredStore.ListAgentsAsync(cancellationToken));
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(
            options.CertificateAuthorityPath, cancellationToken));
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
        return ValueTask.CompletedTask;
    }

    private (ControlStore Store, ControlOptions Options) CreateServices()
    {
        Directory.CreateDirectory(_directory);
        var options = new ControlOptions
        {
            DatabasePath = Path.Combine(_directory, "control.db"),
            CertificateAuthorityPath = Path.Combine(_directory, "control-ca.pfx"),
            BackupDirectory = Path.Combine(_directory, "backups"),
            MetricRetentionHours = 24,
            IdempotencyRetentionHours = 1,
            AuditRetentionDays = 1
        };
        return (new ControlStore(Options.Create(options)), options);
    }

    private static async Task EnrollAgentAsync(
        ControlStore store,
        string nodeId,
        string thumbprint,
        CancellationToken cancellationToken)
    {
        var token = await store.CreateEnrollmentTokenAsync(
            nodeId, TimeSpan.FromMinutes(10), cancellationToken);
        var result = await store.EnrollAsync(
            new EnrollmentRequest(nodeId, token, "csr", Guid.NewGuid().ToString()),
            () => new IssuedCertificate(
                "certificate", "ca", thumbprint, DateTimeOffset.UtcNow.AddYears(1)),
            cancellationToken);
        Assert.NotNull(result);
    }

    private sealed class RecordingPolicyApplier : ILinkPolicyApplier
    {
        public bool FailDisconnect { get; set; }
        public int DisconnectCalls { get; private set; }

        public Task ApplyConnectAsync(LinkPolicy link, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ApplyDisconnectAsync(LinkPolicy link, CancellationToken cancellationToken)
        {
            DisconnectCalls++;
            return FailDisconnect
                ? Task.FromException(new InvalidOperationException("simulated firewall failure"))
                : Task.CompletedTask;
        }
    }
}
