using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace ServerMonitorManager.Control.Tests;

public sealed class SchemaCompatibilityTests : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"smm-schema-compatibility-{Guid.NewGuid():N}");

    [Fact]
    public async Task PublishedAlpha7DatabaseRemainsReadableAndRestorable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_directory);
        var fixturePath = Path.Combine(
            FindRepositoryRoot(), "tests", "fixtures", "control-v0.1.0-alpha.7.db");
        Assert.True(File.Exists(fixturePath), $"Published database fixture is missing: {fixturePath}");

        var options = new ControlOptions
        {
            DatabasePath = Path.Combine(_directory, "control.db"),
            CertificateAuthorityPath = Path.Combine(_directory, "control-ca.pfx"),
            BackupDirectory = Path.Combine(_directory, "backups")
        };
        File.Copy(fixturePath, options.DatabasePath);
        await File.WriteAllBytesAsync(
            options.CertificateAuthorityPath, [1, 2, 3, 4], cancellationToken);

        var schemaVersionBefore = await ReadUserVersionAsync(options.DatabasePath, cancellationToken);
        Assert.Equal(8L, schemaVersionBefore);

        var store = new ControlStore(Options.Create(options));
        await store.InitializeAsync(cancellationToken);

        var schemaVersionAfter = await ReadUserVersionAsync(options.DatabasePath, cancellationToken);
        Assert.Equal(schemaVersionBefore, schemaVersionAfter);

        var agents = await store.ListAgentsAsync(cancellationToken);
        Assert.Equal(["source-node", "target-node"], agents.Select(agent => agent.NodeId));
        Assert.Equal("0.1.0-alpha.7", agents[0].AgentVersion);

        Assert.Equal(
            new ControlIdentity("source-node", "Agent"),
            await store.ResolveIdentityAsync("AGENT-THUMBPRINT", cancellationToken));
        Assert.Equal(
            new ControlIdentity("operator-device", "Operator"),
            await store.ResolveIdentityAsync("DEVICE-THUMBPRINT", cancellationToken));
        Assert.Equal(
            new ControlIdentity("automation-one", "Automation", "source-node"),
            await store.ResolveIdentityAsync("AUTOMATION-THUMBPRINT", cancellationToken));

        var link = Assert.Single(await store.ListLinksAsync(cancellationToken));
        Assert.Equal("source-node", link.SourceNodeId);
        Assert.Equal("target-node", link.TargetNodeId);
        Assert.Equal("Active", link.ActualState);

        var job = await store.GetProvisioningJobAsync(
            "22222222222222222222222222222222", cancellationToken);
        Assert.NotNull(job);
        Assert.Equal("source-node", job.NodeId);
        Assert.Equal("Completed", job.State);
        var provisioningEvent = Assert.Single((await store.ListProvisioningEventsAsync(
            job.Id, 100, cancellationToken))!);
        Assert.Equal("job.completed", provisioningEvent.EventType);

        await using (var connection = new SqliteConnection($"Data Source={options.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync(cancellationToken);
            var audit = connection.CreateCommand();
            audit.CommandText = "SELECT actor, action, subject, details_json FROM audit;";
            await using var reader = await audit.ExecuteReaderAsync(cancellationToken);
            Assert.True(await reader.ReadAsync(cancellationToken));
            Assert.Equal("operator-device", reader.GetString(0));
            Assert.Equal("fixture.created", reader.GetString(1));
            Assert.Equal("source-node", reader.GetString(2));
            Assert.Contains("v0.1.0-alpha.7", reader.GetString(3), StringComparison.Ordinal);
            Assert.False(await reader.ReadAsync(cancellationToken));
        }

        var backups = new ControlBackupService(
            store, Options.Create(options), NullLogger<ControlBackupService>.Instance);
        var backupPath = await backups.CreateAsync(DateTimeOffset.UtcNow, cancellationToken);
        await using (var connection = new SqliteConnection($"Data Source={options.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync(cancellationToken);
            var mutate = connection.CreateCommand();
            mutate.CommandText = "UPDATE agents SET name = 'mutated';";
            await mutate.ExecuteNonQueryAsync(cancellationToken);
        }
        await File.WriteAllBytesAsync(options.CertificateAuthorityPath, [9, 9], cancellationToken);

        SqliteConnection.ClearAllPools();
        await backups.RestoreAsync(backupPath, cancellationToken);

        var restoredStore = new ControlStore(Options.Create(options));
        await restoredStore.InitializeAsync(cancellationToken);
        Assert.Equal(schemaVersionBefore, await ReadUserVersionAsync(options.DatabasePath, cancellationToken));
        Assert.Equal("Source Node", (await restoredStore.ListAgentsAsync(cancellationToken))[0].Name);
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

    private static async Task<long> ReadUserVersionAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ServerMonitorManager.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
               ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
