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
    ProvisioningPreflightResult? Preflight,
    SystemBaseInstallPlan? BaseInstallPlan);

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

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SystemBaseInstallParameters(
    string Timezone,
    string Locale,
    bool AptUpdate,
    bool AptUpgrade,
    int PackageCatalogVersion,
    string[] PackageGroupIds,
    string SwapMode,
    int? SwapSizeMiB,
    int VmSwappiness,
    bool EnableUnattendedUpgrades,
    string RebootPolicy);

public sealed record SystemPackageGroup(
    string Id,
    string[] Packages);

public sealed record SystemBaseInstallCatalog(
    int Version,
    SystemPackageGroup[] Groups);

public sealed record SystemBaseInstallPlan(
    string Timezone,
    string Locale,
    bool AptUpdate,
    bool AptUpgrade,
    string[] Packages,
    string SwapMode,
    int? SwapSizeMiB,
    int VmSwappiness,
    bool EnableUnattendedUpgrades,
    string RebootPolicy,
    string[] Warnings);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SystemBaseInstallPlanReportRequest(
    SystemBaseInstallPlan Plan,
    string IdempotencyKey);

public sealed record ProvisioningBaseInstallPlanRecord(
    string JobId,
    string NodeId,
    int SchemaVersion,
    SystemBaseInstallPlan Plan,
    DateTimeOffset CreatedAt);

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
    public const string SystemBaseInstallModuleHash =
        "355d55e214b941160a32957ced1a681e3c7324f94ecb340f26042f0c3b59b99e";
}

public static class SystemBaseInstallCatalogDefinition
{
    public const int Version = 1;

    public static SystemBaseInstallCatalog Create()
        => new(Version,
        [
            new("core", ["ca-certificates", "curl", "jq"]),
            new("development", ["build-essential", "git"]),
            new("diagnostics", ["htop", "iotop"]),
            new("container-host", ["dbus-user-session", "uidmap"])
        ]);

    public static bool ContainsGroup(string id)
        => id is "core" or "development" or "diagnostics" or "container-host";

    public static string[] ExpandGroups(IEnumerable<string> ids)
    {
        var selected = ids.ToHashSet(StringComparer.Ordinal);
        return Create().Groups
            .Where(group => selected.Contains(group.Id))
            .SelectMany(group => group.Packages)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}

public static class SystemBaseInstallSchema
{
    private static readonly HashSet<string> AllowedPlanWarnings =
        new(["apt.missing", "timezone.missing"], StringComparer.Ordinal);

    public static bool TryParse(JsonElement json, out SystemBaseInstallParameters? parameters)
    {
        try
        {
            parameters = JsonSerializer.Deserialize(
                json, SmmJsonContext.Default.SystemBaseInstallParameters);
            return parameters is not null && IsValid(parameters);
        }
        catch (JsonException)
        {
            parameters = null;
            return false;
        }
    }

    public static bool IsValid(SystemBaseInstallParameters parameters)
        => IsSafeTimezone(parameters.Timezone)
           && IsSafeLocale(parameters.Locale)
           && (!parameters.AptUpgrade || parameters.AptUpdate)
           && parameters.PackageCatalogVersion == SystemBaseInstallCatalogDefinition.Version
           && parameters.PackageGroupIds is { Length: <= 4 }
           && parameters.PackageGroupIds.Distinct(StringComparer.Ordinal).Count()
               == parameters.PackageGroupIds.Length
           && parameters.PackageGroupIds.All(SystemBaseInstallCatalogDefinition.ContainsGroup)
           && parameters.SwapMode is "disabled" or "automatic" or "explicit"
           && (parameters.SwapMode == "explicit"
               ? parameters.SwapSizeMiB is >= 128 and <= 1_048_576
               : parameters.SwapSizeMiB is null)
           && parameters.VmSwappiness is >= 0 and <= 200
           && parameters.RebootPolicy == "never";

    public static bool IsValidPlan(
        SystemBaseInstallParameters parameters,
        SystemBaseInstallPlan plan)
        => plan is not null
           && string.Equals(plan.Timezone, parameters.Timezone, StringComparison.Ordinal)
           && string.Equals(plan.Locale, parameters.Locale, StringComparison.Ordinal)
           && plan.AptUpdate == parameters.AptUpdate
           && plan.AptUpgrade == parameters.AptUpgrade
           && plan.Packages is not null
           && plan.Packages.SequenceEqual(
               SystemBaseInstallCatalogDefinition.ExpandGroups(parameters.PackageGroupIds),
               StringComparer.Ordinal)
           && string.Equals(plan.SwapMode, parameters.SwapMode, StringComparison.Ordinal)
           && plan.SwapSizeMiB == parameters.SwapSizeMiB
           && plan.VmSwappiness == parameters.VmSwappiness
           && plan.EnableUnattendedUpgrades == parameters.EnableUnattendedUpgrades
           && string.Equals(plan.RebootPolicy, parameters.RebootPolicy, StringComparison.Ordinal)
           && plan.Warnings is { Length: <= 2 }
           && plan.Warnings.Distinct(StringComparer.Ordinal).Count() == plan.Warnings.Length
           && plan.Warnings.All(AllowedPlanWarnings.Contains);

    private static bool IsSafeTimezone(string? value)
        => value is { Length: >= 1 and <= 64 }
           && value[0] is not '/' and not '.'
           && !value.Contains("..", StringComparison.Ordinal)
           && value.All(character => char.IsAsciiLetterOrDigit(character)
               || character is '/' or '_' or '-' or '+');

    private static bool IsSafeLocale(string? value)
        => value is { Length: >= 1 and <= 32 }
           && value.All(character => char.IsAsciiLetterOrDigit(character)
               || character is '_' or '-' or '.' or '@');
}
