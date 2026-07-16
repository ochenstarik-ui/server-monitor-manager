using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ServerMonitorManager_Desktop;

public sealed record ServerProfileData(
    string Id,
    string Name,
    string Host,
    int Port,
    string User,
    bool IsHub = false);

public sealed class ServerViewModel : INotifyPropertyChanged
{
    private string _status = "Ожидает проверки";
    private string _cpuText = "—";
    private double _cpuPercent;
    private string _memoryText = "—";
    private string _diskText = "—";
    private string _latencyText = "—";
    private string _healthText = "—";
    private bool _isOnline;
    private bool _hasWarning;

    public ServerViewModel(ServerProfileData profile) => Profile = profile;

    public ServerProfileData Profile { get; }
    public string Name => Profile.Name;
    public string Endpoint => $"{Profile.User}@{Profile.Host}:{Profile.Port}";
    public bool IsHub => Profile.IsHub;
    public string Status { get => _status; set => Set(ref _status, value); }
    public string CpuText { get => _cpuText; set => Set(ref _cpuText, value); }
    public double CpuPercent { get => _cpuPercent; set => Set(ref _cpuPercent, value); }
    public string MemoryText { get => _memoryText; set => Set(ref _memoryText, value); }
    public string DiskText { get => _diskText; set => Set(ref _diskText, value); }
    public string LatencyText { get => _latencyText; set => Set(ref _latencyText, value); }
    public string HealthText { get => _healthText; set => Set(ref _healthText, value); }
    public bool IsOnline { get => _isOnline; set => Set(ref _isOnline, value); }
    public bool HasWarning { get => _hasWarning; set => Set(ref _hasWarning, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
