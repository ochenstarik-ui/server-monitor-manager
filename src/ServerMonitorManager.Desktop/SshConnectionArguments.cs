using System.Globalization;

namespace ServerMonitorManager_Desktop;

internal static class SshConnectionArguments
{
    internal static string[] BuildRestricted(
        string host,
        int port,
        string user,
        string knownHostsPath,
        string? expectedFingerprint,
        string privateKeyPath,
        string command)
    {
        var arguments = BuildTrusted(host, port, user, knownHostsPath, expectedFingerprint);
        arguments.InsertRange(4,
        [
            "-i", privateKeyPath,
            "-o", "BatchMode=yes",
            "-o", "ConnectTimeout=8",
            "-o", "IdentitiesOnly=yes",
            "-o", "IdentityAgent=none"
        ]);
        arguments.Add(command);
        return [.. arguments];
    }

    internal static string[] BuildInteractive(
        string host,
        int port,
        string user,
        string knownHostsPath,
        string? expectedFingerprint)
        => [.. BuildTrusted(host, port, user, knownHostsPath, expectedFingerprint)];

    private static List<string> BuildTrusted(
        string host,
        int port,
        string user,
        string knownHostsPath,
        string? expectedFingerprint)
    {
        if (!SshHostKeyTrust.IsTrusted(
                knownHostsPath,
                host,
                port,
                expectedFingerprint))
        {
            throw new InvalidOperationException(
                "SSH host key не подтверждён. Нажмите «Подтвердить host key» в карточке сервера.");
        }

        return
        [
            "-F", "none",
            "-p", port.ToString(CultureInfo.InvariantCulture),
            "-o", "StrictHostKeyChecking=yes",
            "-o", $"UserKnownHostsFile={knownHostsPath}",
            "-o", "GlobalKnownHostsFile=none",
            "-o", "KnownHostsCommand=none",
            "-o", "UpdateHostKeys=no",
            "-o", "VerifyHostKeyDNS=no",
            "-o", "CanonicalizeHostname=no",
            "-o", "CheckHostIP=no",
            $"{user}@{host}"
        ];
    }
}
