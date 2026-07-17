namespace ServerMonitorManager.Control;

public sealed class ControlOptions
{
    public const string SectionName = "Control";

    public string DatabasePath { get; init; } = "/var/lib/ochenstarik-server-monitor-manager/control.db";

    public string CertificateAuthorityPath { get; init; } = "/etc/ochenstarik-server-monitor-manager/control-ca.pfx";

    public string? CertificateAuthorityPassword { get; init; }

    public int HeartbeatSeconds { get; init; } = 30;

    public int MaxBufferedMetricAgeHours { get; init; } = 24;

    public int MetricRetentionHours { get; init; } = 168;

    public int IdempotencyRetentionHours { get; init; } = 24;

    public int AuditRetentionDays { get; init; } = 90;

    public int MaintenanceIntervalMinutes { get; init; } = 15;

    public int LinkExpirationPollSeconds { get; init; } = 15;

    public string BackupDirectory { get; init; } = "/var/lib/ochenstarik-server-monitor-manager/backups";

    public int BackupIntervalHours { get; init; } = 24;

    public int BackupRetentionCount { get; init; } = 7;

    public string HubHelperPath { get; init; } = "/usr/local/libexec/ochenstarik-smm-policy-apply";

    public string PrivilegeEscalationPath { get; init; } = "/usr/bin/sudo";
}
