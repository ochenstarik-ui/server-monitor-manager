using System.Diagnostics.CodeAnalysis;

namespace ServerMonitorManager.Control;

public sealed class ControlOptions
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(ControlOptions))]
    public ControlOptions()
    {
    }

    public const string SectionName = "Control";

    public string DatabasePath { get; set; } = "/var/lib/ochenstarik-server-monitor-manager/control.db";

    public string CertificateAuthorityPath { get; set; } = "/etc/ochenstarik-server-monitor-manager/control-ca.pfx";

    public string? CertificateAuthorityPassword { get; set; }

    public int HeartbeatSeconds { get; set; } = 30;

    public int ClientCertificateDays { get; set; } = 30;

    public int MaxBufferedMetricAgeHours { get; set; } = 24;

    public int MetricRetentionHours { get; set; } = 168;

    public int IdempotencyRetentionHours { get; set; } = 24;

    public int AuditRetentionDays { get; set; } = 90;

    public int LinkRetentionDays { get; set; } = 90;

    public int MaintenanceIntervalMinutes { get; set; } = 15;

    public int LinkExpirationPollSeconds { get; set; } = 15;

    public int LinkReconciliationSeconds { get; set; } = 300;

    public string BackupDirectory { get; set; } = "/var/lib/ochenstarik-server-monitor-manager/backups";

    public int BackupIntervalHours { get; set; } = 24;

    public int BackupRetentionCount { get; set; } = 7;

    public string HubHelperPath { get; set; } = "/usr/local/libexec/ochenstarik-smm-policy-apply";

    public string PrivilegeEscalationPath { get; set; } = "/usr/bin/sudo";

    public string? PublicUrl { get; set; }

    public string PublicUrlPath { get; set; } = "/etc/ochenstarik-server-monitor-manager/control-public-url";

    public string MeshEnvironmentPath { get; set; } = "/etc/ochenstarik-server-monitor-manager/mesh.env";

    public string MeshNodesPath { get; set; } = "/var/lib/ochenstarik-server-monitor-manager/mesh/nodes.tsv";

    public string HubPublicKeyPath { get; set; } = "/etc/ochenstarik-server-monitor-manager/wireguard/hub.pub";

    public string? HubEndpoint { get; set; }

    public string? HubPublicKey { get; set; }

    public string MeshNetwork { get; set; } = "10.77.0.0/24";
}
