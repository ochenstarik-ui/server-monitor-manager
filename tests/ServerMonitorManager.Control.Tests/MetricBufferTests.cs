using ServerMonitorManager.Agent;
using ServerMonitorManager.Core;
using Xunit;

namespace ServerMonitorManager.Control.Tests;

public sealed class MetricBufferTests : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"smm-buffer-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task BufferSurvivesRestartAndAcknowledgesInOrder()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var options = CreateOptions(maximum: 20, recent: 5, factor: 3);
        var firstBuffer = new MetricBuffer(options);
        await firstBuffer.EnqueueAsync(CreateHeartbeat(1), cancellationToken);
        await firstBuffer.EnqueueAsync(CreateHeartbeat(2), cancellationToken);
        await firstBuffer.EnqueueAsync(CreateHeartbeat(3), cancellationToken);

        var restartedBuffer = new MetricBuffer(options);
        var pending = await restartedBuffer.PeekAsync(10, cancellationToken);
        Assert.Equal(["sample-1", "sample-2", "sample-3"], pending.Select(x => x.IdempotencyKey));

        await restartedBuffer.AcknowledgeAsync("sample-1", cancellationToken);
        var afterAcknowledgement = await new MetricBuffer(options).PeekAsync(10, cancellationToken);
        Assert.Equal(["sample-2", "sample-3"], afterAcknowledgement.Select(x => x.IdempotencyKey));
    }

    [Fact]
    public async Task BufferIsBoundedAndKeepsRecentSamplesAtFullResolution()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var options = CreateOptions(maximum: 10, recent: 4, factor: 3);
        var buffer = new MetricBuffer(options);
        for (var index = 1; index <= 30; index++)
        {
            await buffer.EnqueueAsync(CreateHeartbeat(index), cancellationToken);
        }

        var pending = await buffer.PeekAsync(100, cancellationToken);
        Assert.InRange(pending.Count, 4, 10);
        Assert.Equal(
            ["sample-27", "sample-28", "sample-29", "sample-30"],
            pending.TakeLast(4).Select(x => x.IdempotencyKey));
        Assert.True(pending.SequenceEqual(pending.OrderBy(x => x.SentAt)));
    }

    [Fact]
    public async Task CorruptBufferIsQuarantinedInsteadOfStoppingAgent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var options = CreateOptions(maximum: 20, recent: 5, factor: 3);
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "metric-buffer.json"),
            "not-json",
            cancellationToken);

        var buffer = new MetricBuffer(options);
        var pending = await buffer.PeekAsync(10, cancellationToken);

        Assert.Empty(pending);
        Assert.Single(Directory.GetFiles(_directory, "metric-buffer.json.corrupt-*"));
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    private AgentOptions CreateOptions(int maximum, int recent, int factor)
        => new()
        {
            NodeId = "home",
            StateDirectory = _directory,
            BufferMaxSamples = maximum,
            BufferRecentSamples = recent,
            BufferDownsampleFactor = factor
        };

    private static AgentHeartbeat CreateHeartbeat(int sequence)
        => new(
            "home",
            "test",
            DateTimeOffset.UnixEpoch.AddMinutes(sequence),
            sequence,
            sequence,
            100,
            sequence,
            100,
            sequence,
            sequence,
            sequence,
            $"sample-{sequence}");
}
