namespace ServerMonitorManager.Control;

public sealed class ControlOptions
{
    public const string SectionName = "Control";

    public string DatabasePath { get; init; } = "/var/lib/ochenstarik-server-monitor-manager/control.db";

    public string CertificateAuthorityPath { get; init; } = "/etc/ochenstarik-server-monitor-manager/control-ca.pfx";

    public string? CertificateAuthorityPassword { get; init; }

    public int HeartbeatSeconds { get; init; } = 30;
}
