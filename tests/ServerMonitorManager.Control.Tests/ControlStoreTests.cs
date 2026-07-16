using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using ServerMonitorManager.Control;
using ServerMonitorManager.Core;
using Xunit;

namespace ServerMonitorManager.Control.Tests;

public sealed class ControlStoreTests : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"smm-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task EnrollmentTokenIsAtomicAndIdempotent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        var token = await store.CreateEnrollmentTokenAsync("home", TimeSpan.FromMinutes(10), cancellationToken);
        var idempotencyKey = Guid.NewGuid().ToString();
        var request = new EnrollmentRequest("home", token, "unused-in-store-test", idempotencyKey);
        var issued = new IssuedCertificate("certificate", "ca", "AA11", DateTimeOffset.UtcNow.AddYears(1));

        var first = await store.EnrollAsync(request, () => issued, cancellationToken);
        var retry = await store.EnrollAsync(request, () => throw new InvalidOperationException("must use cache"), cancellationToken);
        var replay = await store.EnrollAsync(
            request with { IdempotencyKey = Guid.NewGuid().ToString() },
            () => issued,
            cancellationToken);

        Assert.NotNull(first);
        Assert.Equal(first, retry);
        Assert.Null(replay);
        Assert.True(await store.IsCertificateForNodeAsync("AA11", "home", cancellationToken));
        Assert.False(await store.IsCertificateForNodeAsync("AA11", "other", cancellationToken));
    }

    [Fact]
    public async Task HeartbeatRetryDoesNotDuplicateMetricSample()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        var token = await store.CreateEnrollmentTokenAsync("home", TimeSpan.FromMinutes(10), cancellationToken);
        var issued = new IssuedCertificate("certificate", "ca", "BB22", DateTimeOffset.UtcNow.AddYears(1));
        await store.EnrollAsync(
            new EnrollmentRequest("home", token, "csr", Guid.NewGuid().ToString()),
            () => issued,
            cancellationToken);
        var heartbeat = new AgentHeartbeat(
            "home", "test", DateTimeOffset.UtcNow, 0.5, 1, 2, 3, 4, 5, 6, 7,
            Guid.NewGuid().ToString());

        var first = await store.RecordHeartbeatAsync(heartbeat, 30, cancellationToken);
        var retry = await store.RecordHeartbeatAsync(heartbeat, 30, cancellationToken);

        Assert.Equal(first, retry);
        Assert.Equal(1, first.Sequence);
        await Assert.ThrowsAsync<IdempotencyConflictException>(() => store.RecordHeartbeatAsync(
            heartbeat with { LoadOne = 0.9 },
            30,
            cancellationToken));
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

    private ControlStore CreateStore()
    {
        Directory.CreateDirectory(_directory);
        return new ControlStore(Options.Create(new ControlOptions
        {
            DatabasePath = Path.Combine(_directory, "control.db"),
            CertificateAuthorityPath = Path.Combine(_directory, "unused.pfx")
        }));
    }
}
