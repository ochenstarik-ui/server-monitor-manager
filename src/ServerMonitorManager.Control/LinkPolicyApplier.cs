using System.Diagnostics;
using Microsoft.Extensions.Options;
using ServerMonitorManager.Core;

namespace ServerMonitorManager.Control;

public interface ILinkPolicyApplier
{
    Task ApplyConnectAsync(LinkPolicy link, CancellationToken cancellationToken);
    Task ApplyDisconnectAsync(LinkPolicy link, CancellationToken cancellationToken);
}

public sealed class LinkPolicyApplier(IOptions<ControlOptions> options) : ILinkPolicyApplier
{
    public Task ApplyConnectAsync(LinkPolicy link, CancellationToken cancellationToken)
        => RunAsync(
            [
                "link-connect",
                link.SourceNodeId,
                link.TargetNodeId,
                link.Protocol,
                link.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                link.TtlMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ],
            cancellationToken);

    public Task ApplyDisconnectAsync(LinkPolicy link, CancellationToken cancellationToken)
        => RunAsync(
            [
                "link-disconnect",
                link.SourceNodeId,
                link.TargetNodeId,
                link.Protocol,
                link.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ],
            cancellationToken);

    private async Task RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/sudo",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-n");
        startInfo.ArgumentList.Add(options.Value.HubHelperPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the Hub policy helper.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            var message = (await error).Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
                ? $"Hub policy helper exited with code {process.ExitCode}."
                : message);
        }
        _ = await output;
    }
}
