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

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};

await new ProvisioningHelperServer(socketPath).RunAsync(shutdown.Token);
return 0;
