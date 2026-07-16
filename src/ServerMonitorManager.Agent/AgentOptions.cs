namespace ServerMonitorManager.Agent;

public sealed class AgentOptions
{
    public string NodeId { get; init; } = Environment.MachineName.ToLowerInvariant();
    public Uri ControlUrl { get; init; } = new("https://127.0.0.1:7443");
    public string StateDirectory { get; init; } = "/var/lib/ochenstarik-server-monitor-manager/agent";
    public string CertificateAuthorityPath { get; init; } = "/etc/ochenstarik-server-monitor-manager/control-ca.crt";
    public int HeartbeatSeconds { get; init; } = 30;
    public int BufferMaxSamples { get; init; } = 720;
    public int BufferRecentSamples { get; init; } = 120;
    public int BufferDownsampleFactor { get; init; } = 4;
    public int UploadBatchSize { get; init; } = 20;
    public int MaxRetrySeconds { get; init; } = 300;
}
