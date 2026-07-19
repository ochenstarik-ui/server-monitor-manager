using System.Text.Json;
using System.Text.Json.Serialization;

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

public sealed record ProvisioningJobCreateRequest(
    string ActionType,
    int SchemaVersion,
    JsonElement Parameters,
    int TtlMinutes,
    string AuditReason,
    string IdempotencyKey);

public sealed record ProvisioningJobCommandRequest(
    string Reason,
    string IdempotencyKey);

public sealed record ProvisioningJobProgressRequest(
    string State,
    int ProgressPercent,
    string Step,
    string EventCode,
    string Message,
    string IdempotencyKey);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProvisioningHelperRequest(
    string ProtocolVersion,
    string JobId,
    string ActionType,
    int SchemaVersion,
    string ModuleHash,
    JsonElement Parameters);

public sealed record ProvisioningHelperResponse(
    bool Success,
    string Code,
    string Message,
    ProvisioningPreflightResult? Preflight);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProvisioningPreflightResult(
    string OperatingSystem,
    string OperatingSystemVersion,
    string Architecture,
    bool HasSystemd,
    bool HasSshd,
    bool HasNftables,
    bool HasWireGuard,
    bool HasApt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProvisioningPreflightReportRequest(
    ProvisioningPreflightResult Facts,
    DateTimeOffset ObservedAt,
    string IdempotencyKey);

public sealed record NodePreflightFacts(
    string NodeId,
    int SchemaVersion,
    ProvisioningPreflightResult Facts,
    DateTimeOffset ObservedAt,
    string SourceJobId,
    DateTimeOffset UpdatedAt);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PreflightDesiredRequirements(
    bool RequireSystemd,
    bool RequireSshd,
    bool RequireNftables,
    bool RequireWireGuard,
    bool RequireApt,
    string[] AllowedArchitectures);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PreflightDesiredStateUpdateRequest(
    int SchemaVersion,
    PreflightDesiredRequirements Desired,
    string AuditReason,
    string IdempotencyKey);

public sealed record NodePreflightDesiredState(
    string NodeId,
    int SchemaVersion,
    PreflightDesiredRequirements Desired,
    long Version,
    string UpdatedBy,
    string AuditReason,
    DateTimeOffset UpdatedAt);

public sealed record PreflightDriftAssessment(
    string NodeId,
    string Status,
    string[] DriftCodes,
    NodePreflightDesiredState? Desired,
    NodePreflightFacts? Facts);

public static class PreflightDriftStatuses
{
    public const string NotConfigured = "NotConfigured";
    public const string Unknown = "Unknown";
    public const string InSync = "InSync";
    public const string Drifted = "Drifted";
}

public static class PreflightDriftCodes
{
    public const string FactsMissing = "facts.missing";
    public const string SystemdMissing = "systemd.missing";
    public const string SshdMissing = "sshd.missing";
    public const string NftablesMissing = "nftables.missing";
    public const string WireGuardMissing = "wireguard.missing";
    public const string AptMissing = "apt.missing";
    public const string ArchitectureUnsupported = "architecture.unsupported";
}

public sealed record ProvisioningJob(
    string Id,
    string NodeId,
    string ActionType,
    int SchemaVersion,
    JsonElement Parameters,
    string State,
    bool ConfirmationRequired,
    string AuditReason,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? CancelledAt,
    long Version,
    int ProgressPercent,
    string CurrentStep,
    string? LastError);

public sealed record ProvisioningEvent(
    long Sequence,
    string JobId,
    DateTimeOffset RecordedAt,
    string EventType,
    string State,
    string Step,
    int ProgressPercent,
    string Message);

public static class ProvisioningJobStates
{
    public const string Queued = "Queued";
    public const string Preflight = "Preflight";
    public const string AwaitingConfirmation = "AwaitingConfirmation";
    public const string Running = "Running";
    public const string Verifying = "Verifying";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string NeedsReconciliation = "NeedsReconciliation";
    public const string RollingBack = "RollingBack";
    public const string RolledBack = "RolledBack";
    public const string RollbackFailed = "RollbackFailed";
    public const string Cancelled = "Cancelled";
}

public static class ProvisioningActionCatalog
{
    public const string PreflightModuleHash =
        "2dc48fb4528a291221954fc2dd3478d431b66fe34228f29684ce1648dbe2f32b";
}
