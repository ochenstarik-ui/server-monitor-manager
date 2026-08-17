namespace ServerMonitorManager.Core;

public sealed record NodeEnrollmentCodeResponse(
    string NodeId,
    string Code,
    string CaFingerprint,
    DateTimeOffset ExpiresAt);
