using System.Text.Json;
using Windows.Storage;

namespace ServerMonitorManager_Desktop;

public sealed record MetricSampleData(
    string ServerId,
    DateTimeOffset Timestamp,
    double CpuPercent,
    double MemoryPercent,
    double DiskPercent);

public sealed class MetricsHistoryStorage
{
    private const string FileName = "metrics-history.json";
    private const int MaxSamplesPerServer = 240;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public async Task<List<MetricSampleData>> LoadAsync()
    {
        try
        {
            var file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileName);
            var json = await FileIO.ReadTextAsync(file);
            return JsonSerializer.Deserialize<List<MetricSampleData>>(json, JsonOptions) ?? [];
        }
        catch (FileNotFoundException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task SaveAsync(IEnumerable<MetricSampleData> samples)
    {
        var trimmed = samples
            .GroupBy(sample => sample.ServerId, StringComparer.Ordinal)
            .SelectMany(group => group.OrderByDescending(sample => sample.Timestamp).Take(MaxSamplesPerServer))
            .OrderBy(sample => sample.Timestamp)
            .ToList();
        var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
            FileName,
            CreationCollisionOption.ReplaceExisting);
        await FileIO.WriteTextAsync(file, JsonSerializer.Serialize(trimmed, JsonOptions));
    }
}
