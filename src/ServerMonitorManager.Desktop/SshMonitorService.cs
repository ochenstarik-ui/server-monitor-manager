using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Windows.Storage;

namespace ServerMonitorManager_Desktop;

public sealed record ServerMetrics(
    string Hostname,
    double CpuPercent,
    long MemoryUsedKb,
    long MemoryTotalKb,
    long DiskUsedKb,
    long DiskTotalKb,
    long SwapUsedKb,
    long SwapTotalKb,
    long InodesUsed,
    long InodesTotal,
    long NetworkRxBytes,
    long NetworkTxBytes,
    string SshState,
    string WireGuardState,
    TimeSpan Uptime,
    TimeSpan Latency);

public sealed partial class SshMonitorService
{
    private const string KeyFileName = "server-monitor-manager-ed25519";
    private const string ProtectedKeySuffix = ".dpapi";

    public async Task<string> EnsureKeyPairAsync(CancellationToken cancellationToken = default)
    {
        var keyFolder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
            "ssh",
            CreationCollisionOption.OpenIfExists);
        var privateKeyPath = Path.Combine(keyFolder.Path, KeyFileName);
        var publicKeyPath = privateKeyPath + ".pub";
        var protectedKeyPath = privateKeyPath + ProtectedKeySuffix;

        if (!File.Exists(publicKeyPath) || (!File.Exists(privateKeyPath) && !File.Exists(protectedKeyPath)))
        {
            File.Delete(protectedKeyPath);
            var arguments = new[]
            {
                "-q", "-t", "ed25519", "-a", "64", "-N", string.Empty,
                "-C", "server-monitor-manager", "-f", privateKeyPath
            };
            await RunProcessAsync(ResolveOpenSshTool("ssh-keygen.exe"), arguments, cancellationToken);
        }

        if (File.Exists(privateKeyPath))
        {
            var privateKey = await File.ReadAllBytesAsync(privateKeyPath, cancellationToken);
            var protectedKey = ProtectedData.Protect(privateKey, null, DataProtectionScope.CurrentUser);
            await File.WriteAllBytesAsync(protectedKeyPath, protectedKey, cancellationToken);
            CryptographicOperations.ZeroMemory(privateKey);
            File.Delete(privateKeyPath);
        }

        return (await File.ReadAllTextAsync(publicKeyPath, cancellationToken)).Trim();
    }

    public async Task<ServerMetrics> QueryAsync(
        ServerProfileData profile,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var output = await RunRestrictedCommandAsync(profile, "metrics", cancellationToken);
        stopwatch.Stop();

        var values = output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);

        var cpuCount = ReadLong(values, "CPU_COUNT", 1);
        var load1 = ReadDouble(values, "LOAD1");
        var cpuPercent = Math.Clamp(load1 / Math.Max(1, cpuCount) * 100, 0, 100);
        var memoryTotal = ReadLong(values, "MEM_TOTAL_KB");
        var memoryAvailable = ReadLong(values, "MEM_AVAILABLE_KB");
        var diskTotal = ReadLong(values, "DISK_TOTAL_KB");
        var diskAvailable = ReadLong(values, "DISK_AVAILABLE_KB");
        var swapTotal = ReadLong(values, "SWAP_TOTAL_KB");
        var swapFree = ReadLong(values, "SWAP_FREE_KB");
        var inodesTotal = ReadLong(values, "DISK_INODES_TOTAL");
        var inodesFree = ReadLong(values, "DISK_INODES_FREE");

        return new ServerMetrics(
            values.GetValueOrDefault("HOSTNAME", profile.Name),
            cpuPercent,
            Math.Max(0, memoryTotal - memoryAvailable),
            memoryTotal,
            Math.Max(0, diskTotal - diskAvailable),
            diskTotal,
            Math.Max(0, swapTotal - swapFree),
            swapTotal,
            Math.Max(0, inodesTotal - inodesFree),
            inodesTotal,
            ReadLong(values, "NETWORK_RX_BYTES"),
            ReadLong(values, "NETWORK_TX_BYTES"),
            values.GetValueOrDefault("SYSTEMD_SSH", "unknown"),
            values.GetValueOrDefault("SYSTEMD_WIREGUARD", "unknown"),
            TimeSpan.FromSeconds(ReadLong(values, "UPTIME_SECONDS")),
            stopwatch.Elapsed);
    }

    public async Task<string> RunRestrictedCommandAsync(
        ServerProfileData profile,
        string command,
        CancellationToken cancellationToken = default)
    {
        ValidateProfile(profile);
        if (!SafeRestrictedCommandRegex().IsMatch(command))
        {
            throw new InvalidOperationException("Некорректная команда управления Mesh.");
        }

        var localFolder = ApplicationData.Current.LocalFolder.Path;
        var knownHostsPath = SshHostKeyTrust.GetPinPath(
            Path.Combine(localFolder, "ssh", "known-hosts"),
            profile.Host,
            profile.Port);
        if (!SshHostKeyTrust.IsTrusted(
                knownHostsPath,
                profile.Host,
                profile.Port,
                profile.HostKeyFingerprint))
        {
            throw new InvalidOperationException(
                "SSH host key is not explicitly confirmed for this server profile.");
        }

        await EnsureKeyPairAsync(cancellationToken);
        await using var privateKeySession = await MaterializePrivateKeyAsync(cancellationToken);
        var target = $"{profile.User}@{profile.Host}";
        var arguments = new[]
        {
            "-F", "none",
            "-i", privateKeySession.Path,
            "-p", profile.Port.ToString(CultureInfo.InvariantCulture),
            "-o", "BatchMode=yes",
            "-o", "ConnectTimeout=8",
            "-o", "IdentitiesOnly=yes",
            "-o", "IdentityAgent=none",
            "-o", "StrictHostKeyChecking=yes",
            "-o", $"UserKnownHostsFile={knownHostsPath}",
            "-o", "GlobalKnownHostsFile=none",
            "-o", "KnownHostsCommand=none",
            "-o", "UpdateHostKeys=no",
            "-o", "VerifyHostKeyDNS=no",
            "-o", "CanonicalizeHostname=no",
            "-o", "CheckHostIP=no",
            target,
            command
        };
        return await RunProcessAsync(
            ResolveOpenSshTool("ssh.exe"),
            arguments,
            cancellationToken);
    }

    internal async Task<SshHostKeyCandidate> ScanHostKeyAsync(
        ServerProfileData profile,
        CancellationToken cancellationToken = default)
    {
        ValidateProfile(profile);
        var output = await RunProcessAsync(
            ResolveOpenSshTool("ssh-keyscan.exe"),
            [
                "-T", "8",
                "-p", profile.Port.ToString(CultureInfo.InvariantCulture),
                profile.Host
            ],
            cancellationToken);
        return SshHostKeyTrust.ParseCandidate(profile.Host, profile.Port, output);
    }

    internal async Task TrustHostKeyAsync(
        SshHostKeyCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        var knownHostsPath = SshHostKeyTrust.GetPinPath(
            Path.Combine(
                ApplicationData.Current.LocalFolder.Path,
                "ssh",
                "known-hosts"),
            candidate.Host,
            candidate.Port);
        await SshHostKeyTrust.WriteAsync(knownHostsPath, candidate, cancellationToken);
    }

    public void OpenInteractiveTerminal(ServerProfileData profile, string terminalUser)
    {
        ValidateProfile(profile);
        if (!SafeUserRegex().IsMatch(terminalUser))
        {
            throw new InvalidOperationException("Некорректное имя пользователя терминала.");
        }

        var ssh = ResolveOpenSshTool("ssh.exe");
        var sshArguments = new[]
        {
            "-p", profile.Port.ToString(CultureInfo.InvariantCulture),
            $"{terminalUser}@{profile.Host}"
        };
        var windowsTerminal = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps",
            "wt.exe");
        var startInfo = new ProcessStartInfo
        {
            FileName = File.Exists(windowsTerminal) ? windowsTerminal : ssh,
            UseShellExecute = true
        };
        if (File.Exists(windowsTerminal))
        {
            startInfo.ArgumentList.Add("new-tab");
            startInfo.ArgumentList.Add(ssh);
        }
        foreach (var argument in sshArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Не удалось открыть SSH-терминал.");
    }

    private static async Task<SshPrivateKeySession> MaterializePrivateKeyAsync(
        CancellationToken cancellationToken)
    {
        var localFolder = ApplicationData.Current.LocalFolder.Path;
        var protectedKeyPath = Path.Combine(localFolder, "ssh", KeyFileName + ProtectedKeySuffix);
        var protectedKey = await File.ReadAllBytesAsync(protectedKeyPath, cancellationToken);
        byte[]? privateKey = null;
        try
        {
            privateKey = ProtectedData.Unprotect(
                protectedKey,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            return await SshPrivateKeySession.CreateAsync(
                ApplicationData.Current.TemporaryFolder.Path,
                privateKey,
                cancellationToken);
        }
        finally
        {
            if (privateKey is not null)
            {
                CryptographicOperations.ZeroMemory(privateKey);
            }
            CryptographicOperations.ZeroMemory(protectedKey);
        }
    }

    private static void ValidateProfile(ServerProfileData profile)
    {
        if (!SafeHostRegex().IsMatch(profile.Host))
        {
            throw new InvalidOperationException("Некорректный адрес сервера.");
        }

        if (!SafeUserRegex().IsMatch(profile.User))
        {
            throw new InvalidOperationException("Некорректное имя SSH-пользователя.");
        }

        if (profile.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException("SSH-порт должен быть от 1 до 65535.");
        }
    }

    private static async Task<string> RunProcessAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Не удалось запустить {Path.GetFileName(fileName)}.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw;
        }

        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(error)
                    ? $"SSH завершился с кодом {process.ExitCode}."
                    : error.Trim());
        }

        return output;
    }

    private static string ResolveOpenSshTool(string fileName)
    {
        var systemTool = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "OpenSSH",
            fileName);
        return File.Exists(systemTool) ? systemTool : fileName;
    }

    private static long ReadLong(IReadOnlyDictionary<string, string> values, string key, long fallback = 0)
        => values.TryGetValue(key, out var value)
            && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : fallback;

    private static double ReadDouble(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var value)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,255}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeHostRegex();

    [GeneratedRegex("^[a-z_][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeUserRegex();

    [GeneratedRegex("^(metrics|mesh (nodes|links|status|connect [a-z0-9][a-z0-9-]{0,31} [a-z0-9][a-z0-9-]{0,31} (tcp|udp) [0-9]{1,5} [0-9]{1,6}|disconnect [a-z0-9][a-z0-9-]{0,31} [a-z0-9][a-z0-9-]{0,31} (tcp|udp) [0-9]{1,5}))$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeRestrictedCommandRegex();
}
