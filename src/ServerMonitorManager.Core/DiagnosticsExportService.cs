using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ServerMonitorManager.Core;

public sealed record DiagnosticServerInput(
    string EndpointIdentity,
    bool IsHub,
    bool IsOnline,
    bool HasWarning,
    double CpuPercent);

public sealed record DiagnosticNodeInput(
    string NodeIdentity,
    string State,
    int HandshakeAgeSeconds);

public sealed record DiagnosticLinkInput(
    string SourceIdentity,
    string TargetIdentity,
    string Protocol,
    int Port,
    string State,
    long Version,
    long ExpiresUnix);

public sealed record DiagnosticMetricInput(
    string ServerIdentity,
    DateTimeOffset Timestamp,
    double CpuPercent,
    double MemoryPercent,
    double DiskPercent);

public static class DiagnosticsExportService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static string CreateJson(
        IEnumerable<DiagnosticServerInput> servers,
        IEnumerable<DiagnosticNodeInput> nodes,
        IEnumerable<DiagnosticLinkInput> links,
        IEnumerable<DiagnosticMetricInput> history,
        bool controlConfigured)
    {
        var serverSnapshots = servers.Select(server => new DiagnosticServer(
            Fingerprint($"endpoint:{server.EndpointIdentity}"),
            server.IsHub,
            server.IsOnline,
            server.HasWarning,
            Math.Round(server.CpuPercent, 2))).ToArray();
        var nodeSnapshots = nodes.Select(node => new DiagnosticNode(
            Fingerprint($"node:{node.NodeIdentity}"),
            NormalizeState(node.State),
            node.HandshakeAgeSeconds)).ToArray();
        var linkSnapshots = links.Select(link => new DiagnosticLink(
            Fingerprint($"node-name:{link.SourceIdentity}"),
            Fingerprint($"node-name:{link.TargetIdentity}"),
            link.Protocol,
            link.Port,
            NormalizeState(link.State),
            link.Version,
            link.ExpiresUnix)).ToArray();
        var metricSnapshots = history
            .OrderBy(sample => sample.Timestamp)
            .Select(sample => new DiagnosticMetric(
                Fingerprint($"server-id:{sample.ServerIdentity}"),
                sample.Timestamp,
                Math.Round(sample.CpuPercent, 2),
                Math.Round(sample.MemoryPercent, 2),
                Math.Round(sample.DiskPercent, 2)))
            .ToArray();
        var snapshot = new DiagnosticSnapshot(
            1,
            DateTimeOffset.UtcNow,
            Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
            Environment.OSVersion.VersionString,
            RuntimeInformation.ProcessArchitecture.ToString(),
            controlConfigured,
            serverSnapshots,
            nodeSnapshots,
            linkSnapshots,
            metricSnapshots);
        return JsonSerializer.Serialize(snapshot, JsonOptions);
    }

    private static string Fingerprint(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();

    private static string NormalizeState(string value)
        => value.Trim().ToLowerInvariant() switch
        {
            "online" => "online",
            "offline" => "offline",
            "connecting" => "connecting",
            "active" => "active",
            "disconnecting" => "disconnecting",
            "partial" => "partial",
            "disabled" => "disabled",
            "failed" => "failed",
            _ => "unknown"
        };

    private sealed record DiagnosticSnapshot(
        int FormatVersion,
        DateTimeOffset GeneratedAt,
        string AppVersion,
        string OsVersion,
        string Architecture,
        bool ControlConfigured,
        IReadOnlyList<DiagnosticServer> Servers,
        IReadOnlyList<DiagnosticNode> Nodes,
        IReadOnlyList<DiagnosticLink> Links,
        IReadOnlyList<DiagnosticMetric> Metrics);

    private sealed record DiagnosticServer(
        string EndpointFingerprint,
        bool IsHub,
        bool IsOnline,
        bool HasWarning,
        double CpuPercent);

    private sealed record DiagnosticNode(
        string NodeFingerprint,
        string State,
        int HandshakeAgeSeconds);

    private sealed record DiagnosticLink(
        string SourceFingerprint,
        string TargetFingerprint,
        string Protocol,
        int Port,
        string State,
        long Version,
        long ExpiresUnix);

    private sealed record DiagnosticMetric(
        string ServerFingerprint,
        DateTimeOffset Timestamp,
        double CpuPercent,
        double MemoryPercent,
        double DiskPercent);
}
