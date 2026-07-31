using System.Security.Cryptography.X509Certificates;
using ServerMonitorManager.Provisioning.Helper;

if (!OperatingSystem.IsLinux())
{
    Console.Error.WriteLine("The provisioning helper is supported only on Linux.");
    return 2;
}

if (args.Length != 0)
{
    Console.Error.WriteLine("The provisioning helper does not accept command-line arguments.");
    return 2;
}
const string socketPath = "/run/ochenstarik-server-monitor-manager/provisioning.sock";
const string controlAuthorityPath = "/etc/ochenstarik-server-monitor-manager/control-ca.crt";
const string rollbackDirectory =
    "/var/lib/ochenstarik-server-monitor-manager/provisioning/rollback";
var localNodeId = Environment.GetEnvironmentVariable("SMM_NodeId");
if (localNodeId is not { Length: >= 1 and <= 63 }
    || !localNodeId.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
{
    Console.Error.WriteLine("SMM_NodeId must identify the local enrolled Node.");
    return 2;
}
if (!uint.TryParse(Environment.GetEnvironmentVariable("SMM_AgentUid"), out var agentUserId))
{
    Console.Error.WriteLine("SMM_AgentUid must identify the enrolled Agent user.");
    return 2;
}
if (!ProvisioningAgentIdentity.MatchesConfiguredUid("/etc/passwd", agentUserId))
{
    Console.Error.WriteLine(
        $"SMM_AgentUid={agentUserId} does not match the installed ochenstarik-smm-agent user; update agent.env before starting the provisioning helper.");
    return 2;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

using var controlAuthority = X509CertificateLoader.LoadCertificateFromFile(controlAuthorityPath);
var timezoneExecutor = new TimezoneProvisioningExecutor(
    controlAuthority,
    localNodeId,
    new ProvisioningFileSystem(),
    new ProvisioningProcessRunner(),
    TimeProvider.System,
    rollbackDirectory);
await new ProvisioningHelperServer(socketPath, agentUserId, timezoneExecutor).RunAsync(shutdown.Token);
return 0;
