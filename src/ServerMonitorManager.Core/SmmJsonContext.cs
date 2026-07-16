using System.Text.Json.Serialization;

namespace ServerMonitorManager.Core;

[JsonSerializable(typeof(EnrollmentRequest))]
[JsonSerializable(typeof(EnrollmentResponse))]
[JsonSerializable(typeof(AgentHeartbeat))]
[JsonSerializable(typeof(AgentHeartbeat[]))]
[JsonSerializable(typeof(AgentHeartbeatResponse))]
[JsonSerializable(typeof(AgentSummary[]))]
[JsonSerializable(typeof(CertificateReenrollmentRequest))]
[JsonSerializable(typeof(CertificateReenrollmentTicket))]
[JsonSerializable(typeof(CertificateStatusEvent))]
[JsonSerializable(typeof(DeviceEnrollmentRequest))]
[JsonSerializable(typeof(DeviceEnrollmentResponse))]
[JsonSerializable(typeof(LinkPolicyCreateRequest))]
[JsonSerializable(typeof(LinkPolicyDisableRequest))]
[JsonSerializable(typeof(LinkPolicy))]
[JsonSerializable(typeof(LinkPolicy[]))]
[JsonSerializable(typeof(ControlEvent))]
[JsonSerializable(typeof(ControlError))]
public sealed partial class SmmJsonContext : JsonSerializerContext;
