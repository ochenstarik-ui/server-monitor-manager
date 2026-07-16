using System.Text.Json;
using Windows.Storage;

namespace ServerMonitorManager_Desktop;

public sealed class ServerStorage
{
    private const string FileName = "servers.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<IReadOnlyList<ServerProfileData>> LoadAsync()
    {
        try
        {
            var file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileName);
            var json = await FileIO.ReadTextAsync(file);
            return JsonSerializer.Deserialize<List<ServerProfileData>>(json, JsonOptions) ?? [];
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

    public async Task SaveAsync(IEnumerable<ServerProfileData> servers)
    {
        var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
            FileName,
            CreationCollisionOption.ReplaceExisting);
        await FileIO.WriteTextAsync(file, JsonSerializer.Serialize(servers, JsonOptions));
    }
}
