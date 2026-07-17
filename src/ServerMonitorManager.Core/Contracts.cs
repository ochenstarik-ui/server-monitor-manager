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

public sealed record CertificateReenrollmentRequest(
    string Reason,
    string IdempotencyKey);

public sealed record CertificateReenrollmentTicket(
    string EntityType,
    string EntityId,
    string Token,
    DateTimeOffset ExpiresAt,
    int DisabledLinks);

public sealed record CertificateStatusEvent(
    string EntityType,
    string EntityId,
    string Status,
    int DisabledLinks);

public sealed record DeviceEnrollmentRequest(
    string DeviceId,
    string Token,
    string CertificateSigningRequestPem,
    string IdempotencyKey);

public sealed record DeviceEnrollmentResponse(
    string DeviceId,
    string CertificatePem,
    string CertificateAuthorityPem,
    DateTimeOffset ExpiresAt);

public sealed record AutomationTokenCreateRequest(
    string AutomationId,
    string SourceNodeId,
    string IdempotencyKey);

public sealed record AutomationTokenResponse(
    string AutomationId,
    string SourceNodeId,
    string Token,
    DateTimeOffset ExpiresAt);

public sealed record AutomationEnrollmentRequest(
    string AutomationId,
    string Token,
    string CertificateSigningRequestPem,
    string IdempotencyKey);

public sealed record AutomationEnrollmentResponse(
    string AutomationId,
    string SourceNodeId,
    string CertificatePem,
    string CertificateAuthorityPem,
    DateTimeOffset ExpiresAt);

public sealed record AutomationScope(
    string AutomationId,
    string SourceNodeId,
    DateTimeOffset ExpiresAt);

public sealed record AutomationLinkGrant(
    string TargetNodeId,
    string Protocol,
    int Port,
    string DesiredState,
    string ActualState,
    long Version,
    DateTimeOffset? ExpiresAt);

public sealed record LinkPolicyCreateRequest(
    string SourceNodeId,
    string TargetNodeId,
    string Protocol,
    int Port,
    int TtlMinutes,
    string Reason,
    string IdempotencyKey);

public sealed record LinkPolicyDisableRequest(string IdempotencyKey);

public sealed record LinkPolicy(
    string Id,
    string SourceNodeId,
    string TargetNodeId,
    string Protocol,
    int Port,
    int TtlMinutes,
    string Reason,
    string DesiredState,
    string ActualState,
    long Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset UpdatedAt,
    string? LastError);

public sealed record ControlEvent(
    long Sequence,
    string Type,
    string Subject,
    DateTimeOffset RecordedAt,
    string PayloadJson);

public sealed record ControlError(string Error);
