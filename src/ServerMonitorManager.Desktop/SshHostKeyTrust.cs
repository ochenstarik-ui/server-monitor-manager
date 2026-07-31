using System.Security.Cryptography;
using System.Text;

namespace ServerMonitorManager_Desktop;

internal sealed record SshHostKeyCandidate(
    string Host,
    int Port,
    string KeyType,
    string KeyData,
    string Fingerprint,
    string KnownHostsLine);

internal static class SshHostKeyTrust
{
    private static readonly string[] PreferredKeyTypes =
    [
        "ssh-ed25519",
        "ecdsa-sha2-nistp256",
        "ssh-rsa"
    ];

    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    internal static string GetPinPath(string directory, string host, int port)
    {
        var endpoint = Encoding.UTF8.GetBytes(FormatEndpoint(host, port));
        try
        {
            var fileName = $"{Convert.ToHexString(SHA256.HashData(endpoint))}.known_hosts";
            return Path.Combine(directory, fileName);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(endpoint);
        }
    }

    internal static SshHostKeyCandidate ParseCandidate(string host, int port, string keyScanOutput)
    {
        var endpoint = FormatEndpoint(host, port);
        var candidates = keyScanOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#'))
            .Select(SplitFields)
            .Where(parts => parts.Length >= 3 && string.Equals(parts[0], endpoint, StringComparison.Ordinal))
            .ToArray();

        foreach (var keyType in PreferredKeyTypes)
        {
            var parts = candidates.FirstOrDefault(parts => string.Equals(
                parts[1],
                keyType,
                StringComparison.Ordinal));
            if (parts is null)
            {
                continue;
            }

            byte[] keyBlob;
            try
            {
                keyBlob = Convert.FromBase64String(parts[2]);
            }
            catch (FormatException exception)
            {
                throw new InvalidOperationException("SSH host key contains invalid base64 data.", exception);
            }

            try
            {
                var fingerprint = Convert.ToBase64String(SHA256.HashData(keyBlob)).TrimEnd('=');
                return new SshHostKeyCandidate(
                    host,
                    port,
                    keyType,
                    parts[2],
                    $"SHA256:{fingerprint}",
                    $"{endpoint} {keyType} {parts[2]}");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(keyBlob);
            }
        }

        throw new InvalidOperationException(
            $"SSH key scan returned no supported key for the expected endpoint {endpoint}.");
    }

    internal static async Task WriteAsync(
        string path,
        SshHostKeyCandidate candidate,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("known_hosts path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".known_hosts-{Guid.NewGuid():N}.tmp");
        await WriteLock.WaitAsync(cancellationToken);
        try
        {
            await File.WriteAllLinesAsync(
                temporaryPath,
                [candidate.KnownHostsLine],
                cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            finally
            {
                WriteLock.Release();
            }
        }
    }

    internal static bool IsTrusted(
        string path,
        string host,
        int port,
        string? expectedFingerprint)
    {
        if (string.IsNullOrWhiteSpace(expectedFingerprint) || !File.Exists(path))
        {
            return false;
        }

        string? pin = null;
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }
            if (pin is not null)
            {
                return false;
            }
            pin = trimmed;
        }

        var parts = pin is null ? [] : SplitFields(pin);
        if (parts.Length != 3
            || !string.Equals(parts[0], FormatEndpoint(host, port), StringComparison.Ordinal)
            || !PreferredKeyTypes.Contains(parts[1], StringComparer.Ordinal))
        {
            return false;
        }

        try
        {
            var keyBlob = Convert.FromBase64String(parts[2]);
            try
            {
                var actual = $"SHA256:{Convert.ToBase64String(SHA256.HashData(keyBlob)).TrimEnd('=')}";
                return string.Equals(actual, expectedFingerprint, StringComparison.Ordinal);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(keyBlob);
            }
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal static bool CanReusePin(
        string directory,
        ServerProfileData current,
        ServerProfileData updated)
    {
        if (!string.Equals(current.Host, updated.Host, StringComparison.Ordinal)
            || current.Port != updated.Port)
        {
            return false;
        }

        var path = GetPinPath(directory, current.Host, current.Port);
        return IsTrusted(
            path,
            current.Host,
            current.Port,
            current.HostKeyFingerprint);
    }


    private static string[] SplitFields(string line)
        => line.Split(
            [' ', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string FormatEndpoint(string host, int port)
        => port == 22 ? host : $"[{host}]:{port}";
}
