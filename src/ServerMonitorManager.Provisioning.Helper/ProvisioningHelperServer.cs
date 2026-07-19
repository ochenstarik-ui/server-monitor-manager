using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using ServerMonitorManager.Core;

namespace ServerMonitorManager.Provisioning.Helper;

public sealed class ProvisioningHelperServer(string socketPath)
{
    private const int MaximumRequestBytes = 16 * 1024;

    [SupportedOSPlatform("linux")]
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
        File.Delete(socketPath);
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        File.SetUnixFileMode(socketPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        listener.Listen(8);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var connection = await listener.AcceptAsync(cancellationToken);
                await HandleAsync(connection, cancellationToken);
            }
        }
        finally
        {
            File.Delete(socketPath);
        }
    }

    public static ProvisioningHelperResponse Execute(ProvisioningHelperRequest request)
    {
        if (request.ProtocolVersion != "1")
        {
            return Failure("protocol.unsupported", "Unsupported helper protocol version.");
        }
        if (request.JobId.Length != 32 || !request.JobId.All(Uri.IsHexDigit))
        {
            return Failure("request.invalid-job", "Invalid provisioning job identifier.");
        }
        if (request.SchemaVersion != 1 || request.Parameters.ValueKind != JsonValueKind.Object)
        {
            return Failure("action.denied", "The requested action is not allowed.");
        }

        return request.ActionType switch
        {
            "preflight" => ExecutePreflight(request),
            "system.base-install" => CreateBaseInstallPlan(request),
            _ => Failure("action.denied", "The requested action is not allowed.")
        };
    }

    private static ProvisioningHelperResponse ExecutePreflight(ProvisioningHelperRequest request)
    {
        if (request.ModuleHash != ProvisioningActionCatalog.PreflightModuleHash
            || request.Parameters.EnumerateObject().Any())
        {
            return Failure("action.denied", "The requested action is not allowed.");
        }

        var release = ReadOperatingSystemRelease();
        var result = new ProvisioningPreflightResult(
            release.GetValueOrDefault("ID", "linux"),
            release.GetValueOrDefault("VERSION_ID", "unknown"),
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            Directory.Exists("/run/systemd/system"),
            Exists("/usr/sbin/sshd", "/usr/bin/sshd", "/sbin/sshd"),
            Exists("/usr/sbin/nft", "/usr/bin/nft", "/sbin/nft"),
            Exists("/usr/bin/wg", "/usr/sbin/wg", "/bin/wg"),
            Exists("/usr/bin/apt-get", "/bin/apt-get"));
        return new ProvisioningHelperResponse(
            true, "preflight.completed", "Preflight completed.", result, null);
    }

    private static ProvisioningHelperResponse CreateBaseInstallPlan(ProvisioningHelperRequest request)
    {
        if (request.ModuleHash != ProvisioningActionCatalog.SystemBaseInstallModuleHash
            || !SystemBaseInstallSchema.TryParse(request.Parameters, out var parameters))
        {
            return Failure("action.denied", "The requested action is not allowed.");
        }

        var warnings = new List<string>();
        if (!Exists("/usr/bin/apt-get", "/bin/apt-get"))
        {
            warnings.Add("apt.missing");
        }
        if (!File.Exists(Path.Combine("/usr/share/zoneinfo", parameters!.Timezone)))
        {
            warnings.Add("timezone.missing");
        }
        var plan = new SystemBaseInstallPlan(
            parameters.Timezone,
            parameters.Locale,
            parameters.AptUpdate,
            parameters.AptUpgrade,
            SystemBaseInstallCatalogDefinition.ExpandGroups(parameters.PackageGroupIds),
            parameters.SwapMode,
            parameters.SwapSizeMiB,
            parameters.VmSwappiness,
            parameters.EnableUnattendedUpgrades,
            parameters.RebootPolicy,
            [.. warnings]);
        return new ProvisioningHelperResponse(
            true, "system.base-install.plan-ready", "Base install plan is ready.", null, plan);
    }

    private static async Task HandleAsync(Socket socket, CancellationToken cancellationToken)
    {
        using (socket)
        await using (var stream = new NetworkStream(socket, ownsSocket: false))
        {
            ProvisioningHelperResponse response;
            try
            {
                var payload = await ReadRequestAsync(stream, cancellationToken);
                var request = JsonSerializer.Deserialize(payload, SmmJsonContext.Default.ProvisioningHelperRequest)
                    ?? throw new JsonException("Empty request.");
                response = Execute(request);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException)
            {
                response = Failure("request.invalid", "Invalid helper request.");
            }
            var json = JsonSerializer.Serialize(response, SmmJsonContext.Default.ProvisioningHelperResponse) + "\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(json), cancellationToken);
        }
    }

    private static async Task<byte[]> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var singleByte = new byte[1];
        while (buffer.Length <= MaximumRequestBytes)
        {
            var count = await stream.ReadAsync(singleByte, cancellationToken);
            if (count == 0 || singleByte[0] == (byte)'\n')
            {
                break;
            }
            buffer.WriteByte(singleByte[0]);
        }
        if (buffer.Length == 0 || buffer.Length > MaximumRequestBytes)
        {
            throw new InvalidDataException("Request size is invalid.");
        }
        return buffer.ToArray();
    }

    private static Dictionary<string, string> ReadOperatingSystemRelease()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!File.Exists("/etc/os-release"))
        {
            return result;
        }
        foreach (var line in File.ReadLines("/etc/os-release"))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }
            var key = line[..separator];
            if (key is "ID" or "VERSION_ID")
            {
                result[key] = line[(separator + 1)..].Trim().Trim('"');
            }
        }
        return result;
    }

    private static bool Exists(params string[] paths) => paths.Any(File.Exists);

    private static ProvisioningHelperResponse Failure(string code, string message)
        => new(false, code, message, null, null);
}
