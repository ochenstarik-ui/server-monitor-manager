namespace ServerMonitorManager.Core;

public sealed record EnrollmentRequest(
    string NodeId,
    string Token,
    string CertificateSigningRequestPem,
    string IdempotencyKey);

public sealed record EnrollmentResponse(
    string NodeId,
    string CertificatePem,
    string CertificateAuthorityPem,
    DateTimeOffset ExpiresAt);

public sealed record AgentHeartbeat(
    string NodeId,
    string AgentVersion,
    DateTimeOffset SentAt,
    double LoadOne,
    long MemoryUsedBytes,
    long MemoryTotalBytes,
    long DiskUsedBytes,
    long DiskTotalBytes,
    long NetworkReceiveBytes,
    long NetworkTransmitBytes,
    long UptimeSeconds,
    string IdempotencyKey);

public sealed record AgentHeartbeatResponse(
    DateTimeOffset AcceptedAt,
    long Sequence,
    int NextHeartbeatSeconds);

public sealed record AgentSummary(
    string NodeId,
    string Name,
    string Status,
    string AgentVersion,
    DateTimeOffset? LastSeenAt);
