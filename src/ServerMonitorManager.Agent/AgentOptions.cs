namespace ServerMonitorManager.Agent;

public sealed class AgentOptions
{
    public string NodeId { get; set; } = Environment.MachineName.ToLowerInvariant();
    public Uri ControlUrl { get; set; } = new("https://127.0.0.1:7443");
    public string StateDirectory { get; set; } = "/var/lib/ochenstarik-server-monitor-manager/agent";
    public string CertificateAuthorityPath { get; set; } = "/etc/ochenstarik-server-monitor-manager/control-ca.crt";
    public string ProvisioningSocketPath { get; set; } = "/run/ochenstarik-server-monitor-manager/provisioning.sock";
    public string EnrollmentTokenDirectory { get; set; } = "/var/lib/ochenstarik-server-monitor-manager-enrollment";
    public string? EnrollTokenFile { get; set; }
    public int HeartbeatSeconds { get; set; } = 30;
    public int BufferMaxSamples { get; set; } = 720;
    public int BufferRecentSamples { get; set; } = 120;
    public int BufferDownsampleFactor { get; set; } = 4;
    public int UploadBatchSize { get; set; } = 20;
    public int MaxRetrySeconds { get; set; } = 300;
}
