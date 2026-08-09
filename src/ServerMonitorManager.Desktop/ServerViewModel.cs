using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ServerMonitorManager_Desktop;

public sealed record ServerProfileData(
    string Id,
    string Name,
    string Host,
    int Port,
    string User,
    bool IsHub = false,
    string? HostKeyFingerprint = null);

public sealed class ServerViewModel : INotifyPropertyChanged
{
    private string _status;
    private string _cpuText = "—";
    private double _cpuPercent;
    private string _memoryText = "—";
    private string _diskText = "—";
    private string _latencyText = "—";
    private string _healthText = "—";
    private bool _isOnline;
    private bool _hasWarning;
    private int? _certificateRemainingDays;

    public ServerViewModel(ServerProfileData profile)
    {
        Profile = profile;
        _status = HostKeyPendingConfirmation
            ? "Требуется подтверждение host key"
            : "Ожидает проверки";
    }

    public ServerProfileData Profile { get; }
    public string Name => Profile.Name;
    public string Endpoint => $"{Profile.User}@{Profile.Host}:{Profile.Port}";
    public bool IsHub => Profile.IsHub;
    public bool HostKeyPendingConfirmation => string.IsNullOrWhiteSpace(Profile.HostKeyFingerprint);
    public string HostKeyConfirmationAction => HostKeyPendingConfirmation
        ? "Подтвердить host key"
        : "Подтвердить заново";
    public string Status { get => _status; set => Set(ref _status, value); }
    public string CpuText { get => _cpuText; set => Set(ref _cpuText, value); }
    public double CpuPercent { get => _cpuPercent; set => Set(ref _cpuPercent, value); }
    public string MemoryText { get => _memoryText; set => Set(ref _memoryText, value); }
    public string DiskText { get => _diskText; set => Set(ref _diskText, value); }
    public string LatencyText { get => _latencyText; set => Set(ref _latencyText, value); }
    public string HealthText { get => _healthText; set => Set(ref _healthText, value); }
    public bool IsOnline { get => _isOnline; set => Set(ref _isOnline, value); }
    public bool HasWarning { get => _hasWarning; set => Set(ref _hasWarning, value); }
    public int? CertificateRemainingDays
    {
        get => _certificateRemainingDays;
        set
        {
            if (Set(ref _certificateRemainingDays, value))
            {
                OnPropertyChanged(nameof(CertificateWarningText));
                OnPropertyChanged(nameof(IsCertificateExpiring));
            }
        }
    }
    public bool IsCertificateExpiring => CertificateRemainingDays.HasValue && CertificateRemainingDays.Value < 10;
    public string CertificateWarningText => CertificateRemainingDays.HasValue
        ? (IsCertificateExpiring ? $"Cert: {CertificateRemainingDays.Value}d (expiring)" : $"Cert: {CertificateRemainingDays.Value}d")
        : string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
