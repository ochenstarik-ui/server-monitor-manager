using System.Text.Json;
using ServerMonitorManager.Core;

namespace ServerMonitorManager.Agent;

internal sealed class MetricBuffer
{
    private readonly AgentOptions _options;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<AgentHeartbeat>? _samples;

    public MetricBuffer(AgentOptions options)
    {
        _options = options;
        _path = Path.Combine(options.StateDirectory, "metric-buffer.json");
    }

    public async Task EnqueueAsync(AgentHeartbeat heartbeat, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var samples = await LoadAsync(cancellationToken);
            if (samples.Any(sample => sample.IdempotencyKey == heartbeat.IdempotencyKey))
            {
                return;
            }

            samples.Add(heartbeat);
            Compact(samples);
            await PersistAsync(samples, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<AgentHeartbeat>> PeekAsync(
        int maximumCount,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var samples = await LoadAsync(cancellationToken);
            return samples.Take(maximumCount).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AcknowledgeAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var samples = await LoadAsync(cancellationToken);
            if (samples.Count == 0)
            {
                return;
            }

            if (!string.Equals(samples[0].IdempotencyKey, idempotencyKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Metric acknowledgements must preserve queue order.");
            }

            samples.RemoveAt(0);
            await PersistAsync(samples, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<AgentHeartbeat>> LoadAsync(CancellationToken cancellationToken)
    {
        if (_samples is not null)
        {
            return _samples;
        }

        Directory.CreateDirectory(_options.StateDirectory);
        if (!File.Exists(_path))
        {
            _samples = [];
            return _samples;
        }

        try
        {
            await using var stream = File.OpenRead(_path);
            var loaded = await JsonSerializer.DeserializeAsync(
                stream,
                SmmJsonContext.Default.AgentHeartbeatArray,
                cancellationToken) ?? [];
            _samples = loaded
                .Where(sample => string.Equals(sample.NodeId, _options.NodeId, StringComparison.Ordinal))
                .OrderBy(sample => sample.SentAt)
                .ToList();
            Compact(_samples);
            return _samples;
        }
        catch (JsonException)
        {
            var corruptPath = $"{_path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            File.Move(_path, corruptPath, overwrite: true);
            _samples = [];
            return _samples;
        }
    }

    private void Compact(List<AgentHeartbeat> samples)
    {
        while (samples.Count > _options.BufferMaxSamples)
        {
            var recentCount = Math.Min(_options.BufferRecentSamples, _options.BufferMaxSamples);
            if (recentCount >= samples.Count)
            {
                samples.RemoveRange(0, samples.Count - _options.BufferMaxSamples);
                return;
            }

            var olderCount = samples.Count - recentCount;
            var compacted = new List<AgentHeartbeat>(_options.BufferMaxSamples);
            for (var start = 0; start < olderCount; start += _options.BufferDownsampleFactor)
            {
                var count = Math.Min(_options.BufferDownsampleFactor, olderCount - start);
                compacted.Add(samples
                    .GetRange(start, count)
                    .MaxBy(ImportanceScore)!);
            }

            compacted.AddRange(samples.GetRange(olderCount, recentCount));
            samples.Clear();
            samples.AddRange(compacted.OrderBy(sample => sample.SentAt));
        }
    }

    private async Task PersistAsync(List<AgentHeartbeat> samples, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.StateDirectory);
        var temporaryPath = $"{_path}.tmp";
        await using (var stream = new FileStream(
            temporaryPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                samples.ToArray(),
                SmmJsonContext.Default.AgentHeartbeatArray,
                cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        SetOwnerOnlyPermissions(temporaryPath);
        File.Move(temporaryPath, _path, overwrite: true);
    }

    private static double ImportanceScore(AgentHeartbeat sample)
    {
        var memory = Ratio(sample.MemoryUsedBytes, sample.MemoryTotalBytes);
        var disk = Ratio(sample.DiskUsedBytes, sample.DiskTotalBytes);
        return Math.Max(sample.LoadOne, Math.Max(memory, disk));
    }

    private static double Ratio(long value, long total)
        => total <= 0 ? 0 : (double)value / total;

    private static void SetOwnerOnlyPermissions(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
