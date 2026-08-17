namespace ServerMonitorManager.Core;

public sealed record NodeEnrollmentCodeResponse(
    string NodeId,
    string Code,
    string CaFingerprint,
    DateTimeOffset ExpiresAt);

public sealed record NodeEnrollmentCodeIssuedDetails(
    string NodeId,
    string Actor,
    string NodeAddress,
    DateTimeOffset ExpiresAt);
