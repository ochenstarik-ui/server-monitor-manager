using System.Text.Json.Serialization;

namespace ServerMonitorManager.Control;

internal sealed record LinkOrphanAuditDetails(
    string SourceNodeId,
    string TargetNodeId,
    string Protocol,
    int Port);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LinkOrphanAuditDetails))]
internal sealed partial class ControlJsonContext : JsonSerializerContext;
