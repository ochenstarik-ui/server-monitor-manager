using System.Diagnostics;
using Microsoft.Extensions.Options;
using ServerMonitorManager.Core;

namespace ServerMonitorManager.Control;

public interface ILinkPolicyApplier
{
    Task ApplyConnectAsync(LinkPolicy link, CancellationToken cancellationToken);
    Task ApplyDisconnectAsync(LinkPolicy link, CancellationToken cancellationToken);
    Task<bool> IsConnectedAsync(LinkPolicy link, CancellationToken cancellationToken);
    Task<string?> GetReconciliationRequestAsync(CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);
    Task CompleteReconciliationAsync(string generation, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed class MeshFirewallUnavailableException : InvalidOperationException
{
    public MeshFirewallUnavailableException(string message) : base(message)
    {
    }
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

    public async Task<bool> IsConnectedAsync(LinkPolicy link, CancellationToken cancellationToken)
    {
        var output = await RunAsync(
            [
                "link-status",
                link.SourceNodeId,
                link.TargetNodeId,
                link.Protocol,
                link.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ],
            cancellationToken);
        return output switch
        {
            "active" => true,
            "disabled" => false,
            _ => throw new InvalidOperationException("Hub policy helper returned an invalid link status.")
        };
    }

    public async Task<string?> GetReconciliationRequestAsync(CancellationToken cancellationToken)
        => await RunAsync(["reconcile-status"], cancellationToken) switch
        {
            "complete" => null,
            var status when status.StartsWith("requested:", StringComparison.Ordinal)
                && Guid.TryParseExact(status[10..], "D", out _) => status[10..],
            _ => throw new InvalidOperationException("Hub policy helper returned an invalid reconciliation marker status.")
        };

    public async Task CompleteReconciliationAsync(string generation, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(generation, "D", out _))
        {
            throw new ArgumentException("Invalid reconciliation generation.", nameof(generation));
        }
        _ = await RunAsync(["reconcile-complete", generation], cancellationToken);
    }

    private async Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = options.Value.PrivilegeEscalationPath,
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
            if (process.ExitCode == 79
                && string.Equals(message, LinkService.FirewallUnavailableCode, StringComparison.Ordinal))
            {
                throw new MeshFirewallUnavailableException(LinkService.FirewallUnavailableCode);
            }
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
                ? $"Hub policy helper exited with code {process.ExitCode}."
                : message);
        }
        return (await output).Trim();
    }
}
