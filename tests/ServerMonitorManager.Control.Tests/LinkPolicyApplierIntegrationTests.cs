using System.Runtime.Versioning;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using ServerMonitorManager.Control;
using ServerMonitorManager.Core;
using Xunit;

namespace ServerMonitorManager.Control.Tests;

public sealed class LinkPolicyApplierIntegrationTests : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"smm-helper-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task LinuxHelperFailureKeepsKillSwitchPendingAcrossControlRestart()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_directory);
        var sudoPath = Path.Combine(_directory, "sudo");
        var helperPath = Path.Combine(_directory, "policy-helper");
        var failureMarkerPath = Path.Combine(_directory, "fail-disconnect");
        var invocationLogPath = Path.Combine(_directory, "helper.log");
        await WriteExecutableAsync(
            sudoPath,
            """
            #!/bin/sh
            set -eu
            if [ "${1:-}" != "-n" ]; then
                echo "sudo must be non-interactive" >&2
                exit 90
            fi
            shift
            exec "$@"
            """,
            cancellationToken);
        await WriteExecutableAsync(
            helperPath,
            $$"""
            #!/bin/sh
            set -eu
            printf '%s\n' "$*" >> '{{ShellQuote(invocationLogPath)}}'
            if [ "${1:-}" = "link-disconnect" ] && [ -f '{{ShellQuote(failureMarkerPath)}}' ]; then
                echo "nftables validation failed" >&2
                exit 23
            fi
            """,
            cancellationToken);

        var store = CreateStore();
        await store.InitializeAsync(cancellationToken);
        await EnrollAgentAsync(store, "ai-agent", "AABB", cancellationToken);
        await EnrollAgentAsync(store, "home", "CCDD", cancellationToken);
        var service = CreateLinkService(store, sudoPath, helperPath);
        var active = await service.CreateAsync(
            new LinkPolicyCreateRequest(
                "ai-agent", "home", "tcp", 22, 60, "integration", Guid.NewGuid().ToString()),
            "windows-pc",
            cancellationToken);
        Assert.Equal("Active", active.ActualState);

        await File.WriteAllTextAsync(failureMarkerPath, "fail", cancellationToken);
        var partial = await service.DisableAsync(
            active.Id,
            new LinkPolicyDisableRequest(Guid.NewGuid().ToString()),
            "windows-pc",
            cancellationToken);
        Assert.NotNull(partial);
        Assert.Equal("Disabled", partial.DesiredState);
        Assert.Equal("Partial", partial.ActualState);
        Assert.Contains("nftables validation failed", partial.LastError);

        var restartedStore = CreateStore();
        await restartedStore.InitializeAsync(cancellationToken);
        var restartedService = CreateLinkService(restartedStore, sudoPath, helperPath);
        var failedReconciliation = await restartedService.ReconcileDisabledLinksForNodeAsync(
            "home", cancellationToken);
        Assert.Equal(new LinkReconciliationResult(1, 1), failedReconciliation);

        File.Delete(failureMarkerPath);
        var secondRestartStore = CreateStore();
        await secondRestartStore.InitializeAsync(cancellationToken);
        var secondRestartService = CreateLinkService(secondRestartStore, sudoPath, helperPath);
        var successfulReconciliation = await secondRestartService.ReconcileDisabledLinksForNodeAsync(
            "home", cancellationToken);
        Assert.Equal(new LinkReconciliationResult(1, 0), successfulReconciliation);
        var persisted = Assert.Single(await secondRestartStore.ListEffectiveLinksForNodeAsync(
            "home", cancellationToken));
        Assert.Equal("Disabled", persisted.DesiredState);
        Assert.Equal("Disabled", persisted.ActualState);

        var invocations = await File.ReadAllLinesAsync(invocationLogPath, cancellationToken);
        Assert.Equal(4, invocations.Length);
        Assert.Equal("link-connect ai-agent home tcp 22 60", invocations[0]);
        Assert.All(invocations.Skip(1), invocation =>
            Assert.Equal("link-disconnect ai-agent home tcp 22", invocation));
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
        return ValueTask.CompletedTask;
    }

    private ControlStore CreateStore()
        => new(Options.Create(new ControlOptions
        {
            DatabasePath = Path.Combine(_directory, "control.db"),
            CertificateAuthorityPath = Path.Combine(_directory, "unused.pfx")
        }));

    private static LinkService CreateLinkService(
        ControlStore store,
        string sudoPath,
        string helperPath)
    {
        var applier = new LinkPolicyApplier(Options.Create(new ControlOptions
        {
            HubHelperPath = helperPath,
            PrivilegeEscalationPath = sudoPath
        }));
        return new LinkService(store, applier, new ControlEventBroker());
    }

    private static async Task EnrollAgentAsync(
        ControlStore store,
        string nodeId,
        string thumbprint,
        CancellationToken cancellationToken)
    {
        var token = await store.CreateEnrollmentTokenAsync(
            nodeId, TimeSpan.FromMinutes(10), cancellationToken);
        var enrolled = await store.EnrollAsync(
            new EnrollmentRequest(nodeId, token, "csr", Guid.NewGuid().ToString()),
            () => new IssuedCertificate(
                "certificate", "ca", thumbprint, DateTimeOffset.UtcNow.AddYears(1)),
            cancellationToken);
        Assert.NotNull(enrolled);
    }

    [SupportedOSPlatform("linux")]
    private static async Task WriteExecutableAsync(
        string path,
        string contents,
        CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(
            path,
            contents.Replace("\r\n", "\n"),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string ShellQuote(string value)
        => value.Replace("'", "'\\''", StringComparison.Ordinal);
}
