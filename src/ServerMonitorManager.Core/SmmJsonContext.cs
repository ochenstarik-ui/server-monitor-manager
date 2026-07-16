using System.Text.Json.Serialization;

namespace ServerMonitorManager.Core;

[JsonSerializable(typeof(EnrollmentRequest))]
[JsonSerializable(typeof(EnrollmentResponse))]
[JsonSerializable(typeof(AgentHeartbeat))]
[JsonSerializable(typeof(AgentHeartbeatResponse))]
[JsonSerializable(typeof(AgentSummary[]))]
public sealed partial class SmmJsonContext : JsonSerializerContext;
