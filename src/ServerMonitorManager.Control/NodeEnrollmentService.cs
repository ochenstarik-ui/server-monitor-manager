using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ServerMonitorManager.Core;

namespace ServerMonitorManager.Control;

public sealed class NodeEnrollmentService : IDisposable
{
    private readonly IOptions<ControlOptions> _options;
    private readonly ControlStore _store;
    private readonly CertificateAuthority _authority;
    private readonly ControlEventBroker _broker;
    private readonly SemaphoreSlim _meshLock = new(1, 1);

    public NodeEnrollmentService(
        IOptions<ControlOptions> options,
        ControlStore store,
        CertificateAuthority authority,
        ControlEventBroker broker)
    {
        _options = options;
        _store = store;
        _authority = authority;
        _broker = broker;
    }

    public async Task<NodeEnrollmentCodeResponse> CreateEnrollmentCodeAsync(
        string nodeId,
        string actor,
        CancellationToken cancellationToken = default)
    {
        if (!NodeIdValidator.IsValid(nodeId))
        {
            throw new ArgumentException("Node id must contain 1-63 lowercase letters, digits, or hyphens.", nameof(nodeId));
        }

        var controlUrl = ResolveControlPublicUrl();
        var (caPem, caFingerprint) = ResolveCertificateAuthorityInfo();
        var (hubEndpoint, hubPublicKey, meshNetwork) = ResolveMeshConfiguration();

        string nodeAddress;
        await _meshLock.WaitAsync(cancellationToken);
        try
        {
            nodeAddress = await ReserveNodeAddressAsync(nodeId, cancellationToken);
        }
        finally
        {
            _meshLock.Release();
        }

        var lifetime = TimeSpan.FromMinutes(10);
        var token = await _store.CreateEnrollmentTokenAsync(nodeId, lifetime, cancellationToken);
        var expiresAt = DateTimeOffset.UtcNow.Add(lifetime);

        await _store.RecordEnrollmentCodeIssuedAsync(nodeId, actor, nodeAddress, expiresAt, cancellationToken);
        _broker.Publish(
            "agent.enrollment_code.issued",
            nodeId,
            JsonSerializer.Serialize(
                new NodeEnrollmentCodeIssuedDetails(nodeId, actor, nodeAddress, expiresAt),
                SmmJsonContext.Default.NodeEnrollmentCodeIssuedDetails));

        var code = string.Join(".",
            "SMMNODE2",
            Base64UrlEncode(controlUrl),
            Base64UrlEncode(caPem),
            Base64UrlEncode(nodeId),
            Base64UrlEncode(token),
            Base64UrlEncode(hubEndpoint),
            Base64UrlEncode(hubPublicKey),
            Base64UrlEncode(nodeAddress),
            Base64UrlEncode(meshNetwork));

        return new NodeEnrollmentCodeResponse(nodeId, code, caFingerprint, expiresAt);
    }

    private string ResolveControlPublicUrl()
    {
        var options = _options.Value;
        if (!string.IsNullOrWhiteSpace(options.PublicUrl))
        {
            return options.PublicUrl.Trim();
        }

        if (File.Exists(options.PublicUrlPath))
        {
            var content = File.ReadAllText(options.PublicUrlPath).Trim();
            if (!string.IsNullOrWhiteSpace(content))
            {
                return content;
            }
        }

        throw new InvalidOperationException("Control public URL is missing.");
    }

    private (string Pem, string Fingerprint) ResolveCertificateAuthorityInfo()
    {
        var cert = _authority.PublicCertificate;
        var pem = cert.ExportCertificatePem();
        var hash = cert.GetCertHash(HashAlgorithmName.SHA256);
        var fingerprint = string.Join(":", hash.Select(b => b.ToString("X2")));
        return (pem, fingerprint);
    }

    private (string HubEndpoint, string HubPublicKey, string MeshNetwork) ResolveMeshConfiguration()
    {
        var options = _options.Value;
        var hubEndpoint = options.HubEndpoint;
        var hubPublicKey = options.HubPublicKey;
        var meshNetwork = options.MeshNetwork ?? "10.77.0.0/24";

        if (File.Exists(options.MeshEnvironmentPath))
        {
            foreach (var line in File.ReadLines(options.MeshEnvironmentPath))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                {
                    continue;
                }

                var separatorIndex = trimmed.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                var key = trimmed[..separatorIndex].Trim();
                var value = trimmed[(separatorIndex + 1)..].Trim();
                if (string.Equals(key, "HUB_ENDPOINT", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(hubEndpoint))
                {
                    hubEndpoint = value;
                }
                else if (string.Equals(key, "HUB_PUBLIC_KEY", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(hubPublicKey))
                {
                    hubPublicKey = value;
                }
                else if (string.Equals(key, "MESH_NETWORK", StringComparison.OrdinalIgnoreCase))
                {
                    meshNetwork = value;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(hubPublicKey) && File.Exists(options.HubPublicKeyPath))
        {
            hubPublicKey = File.ReadAllText(options.HubPublicKeyPath).Trim();
        }

        if (string.IsNullOrWhiteSpace(hubEndpoint) || string.IsNullOrWhiteSpace(hubPublicKey))
        {
            throw new InvalidOperationException("Mesh Hub is not initialized.");
        }

        return (hubEndpoint, hubPublicKey, meshNetwork);
    }

    private async Task<string> ReserveNodeAddressAsync(string nodeId, CancellationToken cancellationToken)
    {
        var path = _options.Value.MeshNodesPath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var lines = File.Exists(path)
            ? (await File.ReadAllLinesAsync(path, cancellationToken)).ToList()
            : new List<string>();

        var usedAddresses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split('\t');
            if (parts.Length >= 2)
            {
                var currentId = parts[0].Trim();
                var currentAddr = parts[1].Trim();
                if (string.Equals(currentId, nodeId, StringComparison.Ordinal))
                {
                    return currentAddr;
                }
                usedAddresses.Add(currentAddr);
            }
        }

        for (var host = 2; host <= 254; host++)
        {
            var candidate = $"10.77.0.{host}";
            if (!usedAddresses.Contains(candidate))
            {
                var record = $"{nodeId}\t{candidate}\t-\treserved";
                lines.Add(record);
                await File.WriteAllLinesAsync(path, lines, cancellationToken);
                return candidate;
            }
        }

        throw new InvalidOperationException("Mesh address pool is exhausted.");
    }

    public static string Base64UrlEncode(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public void Dispose()
    {
        _meshLock.Dispose();
    }
}
