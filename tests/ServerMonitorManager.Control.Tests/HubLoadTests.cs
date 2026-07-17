using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using ServerMonitorManager.Control;
using ServerMonitorManager.Core;
using Xunit;

namespace ServerMonitorManager.Control.Tests;

public sealed class HubLoadTests : IAsyncDisposable
{
    private const int NodeCount = 100;
    private const int HeartbeatWaves = 3;
    private static readonly TimeSpan WaveDeadline = TimeSpan.FromSeconds(30);
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"smm-load-tests-{Guid.NewGuid():N}");

    [Fact]
    [Trait("Category", "Load")]
    public async Task OneHubAcceptsConcurrentHeartbeatsFromOneHundredNodes()
    {
        var testCancellationToken = TestContext.Current.CancellationToken;
        var store = CreateStore();
        await store.InitializeAsync(testCancellationToken);
        await EnrollNodesAsync(store, testCancellationToken);

        var sentAt = DateTimeOffset.UtcNow.AddMinutes(-HeartbeatWaves);
        var latestResponses = new AgentHeartbeatResponse[NodeCount];
        var stopwatch = Stopwatch.StartNew();

        for (var wave = 0; wave < HeartbeatWaves; wave++)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(testCancellationToken);
            deadline.CancelAfter(WaveDeadline);
            var currentWave = wave;
            var responses = await Task.WhenAll(Enumerable.Range(0, NodeCount).Select(async nodeIndex =>
            {
                var mutation = await store.RecordHeartbeatAsync(
                    CreateHeartbeat(nodeIndex, currentWave, sentAt),
                    (int)WaveDeadline.TotalSeconds,
                    deadline.Token);
                Assert.True(mutation.RequiresReconciliation);
                return mutation.Response;
            }));

            Assert.Equal(NodeCount, responses.Select(response => response.Sequence).Distinct().Count());
            latestResponses = responses;
        }

        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < WaveDeadline * HeartbeatWaves,
            $"The heartbeat workload took {stopwatch.Elapsed}; expected less than {WaveDeadline * HeartbeatWaves}.");

        var replayResponses = await Task.WhenAll(Enumerable.Range(0, NodeCount).Select(async nodeIndex =>
            (await store.RecordHeartbeatAsync(
                CreateHeartbeat(nodeIndex, HeartbeatWaves - 1, sentAt),
                (int)WaveDeadline.TotalSeconds,
                testCancellationToken)).Response));
        Assert.Equal(
            latestResponses.Select(response => response.Sequence),
            replayResponses.Select(response => response.Sequence));

        var agents = await store.ListAgentsAsync(testCancellationToken);
        Assert.Equal(NodeCount, agents.Count);
        Assert.All(agents, agent =>
        {
            Assert.Equal("Online", agent.Status);
            Assert.Equal("load-test", agent.AgentVersion);
            Assert.NotNull(agent.LastSeenAt);
        });

        await using var connection = new SqliteConnection(
            $"Data Source={Path.Combine(_directory, "control.db")}");
        await connection.OpenAsync(testCancellationToken);
        var metricCount = connection.CreateCommand();
        metricCount.CommandText = "SELECT COUNT(*) FROM metric_samples;";
        Assert.Equal(
            NodeCount * HeartbeatWaves,
            Convert.ToInt32(await metricCount.ExecuteScalarAsync(testCancellationToken)));
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

    private static async Task EnrollNodesAsync(ControlStore store, CancellationToken cancellationToken)
    {
        for (var index = 0; index < NodeCount; index++)
        {
            var nodeId = NodeId(index);
            var token = await store.CreateEnrollmentTokenAsync(
                nodeId,
                TimeSpan.FromMinutes(10),
                cancellationToken);
            var result = await store.EnrollAsync(
                new EnrollmentRequest(nodeId, token, "csr", $"enroll-{nodeId}"),
                () => new IssuedCertificate(
                    "certificate",
                    "ca",
                    $"LOAD{index:D3}",
                    DateTimeOffset.UtcNow.AddYears(1)),
                cancellationToken);
            Assert.NotNull(result);
        }
    }

    private static AgentHeartbeat CreateHeartbeat(int nodeIndex, int wave, DateTimeOffset sentAt)
        => new(
            NodeId(nodeIndex),
            "load-test",
            sentAt.AddSeconds(wave * WaveDeadline.TotalSeconds),
            0.25 + (nodeIndex % 10 / 20d),
            512L * 1024 * 1024,
            2L * 1024 * 1024 * 1024,
            8L * 1024 * 1024 * 1024,
            32L * 1024 * 1024 * 1024,
            wave * 4096L,
            wave * 2048L,
            3600 + wave * 30L,
            $"heartbeat-{wave:D2}-{nodeIndex:D3}");

    private static string NodeId(int index) => $"load-node-{index:D3}";
}
