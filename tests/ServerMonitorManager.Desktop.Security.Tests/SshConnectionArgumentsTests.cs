using ServerMonitorManager_Desktop;
using Xunit;

namespace ServerMonitorManager.Desktop.Security.Tests;

public sealed class SshConnectionArgumentsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"smm-ssh-arguments-{Guid.NewGuid():N}");

    [Fact]
    public async Task InteractiveTerminalUsesOnlyTheConfirmedEndpointPin()
    {
        var candidate = SshHostKeyTrust.ParseCandidate(
            "server.example",
            2222,
            "[server.example]:2222 ssh-ed25519 AQIDBA==\n");
        var path = SshHostKeyTrust.GetPinPath(_directory, candidate.Host, candidate.Port);
        await SshHostKeyTrust.WriteAsync(path, candidate, TestContext.Current.CancellationToken);

        var arguments = SshConnectionArguments.BuildInteractive(
            "server.example",
            2222,
            "operator",
            path,
            candidate.Fingerprint);

        Assert.Equal("none", ValueAfter(arguments, "-F"));
        Assert.Contains("StrictHostKeyChecking=yes", arguments);
        Assert.Contains($"UserKnownHostsFile={path}", arguments);
        Assert.Contains("GlobalKnownHostsFile=none", arguments);
        Assert.Contains("KnownHostsCommand=none", arguments);
        Assert.Contains("UpdateHostKeys=no", arguments);
        Assert.Contains("CheckHostIP=no", arguments);
        Assert.Equal("operator@server.example", arguments[^1]);
    }

    [Fact]
    public void InteractiveTerminalRejectsProfileWithoutConfirmedFingerprint()
    {
        var path = SshHostKeyTrust.GetPinPath(_directory, "server.example", 22);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            SshConnectionArguments.BuildInteractive(
                "server.example", 22, "operator", path, expectedFingerprint: null));

        Assert.Contains("Подтвердить host key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnchangedEndpointCanReuseItsPersistedPin()
    {
        var candidate = SshHostKeyTrust.ParseCandidate(
            "server.example",
            22,
            "server.example ssh-ed25519 AQIDBA==\n");
        var path = SshHostKeyTrust.GetPinPath(_directory, candidate.Host, candidate.Port);
        await SshHostKeyTrust.WriteAsync(path, candidate, TestContext.Current.CancellationToken);
        var current = new ServerProfileData(
            "id", "Old name", candidate.Host, candidate.Port, "monitor", false, candidate.Fingerprint);
        var renamed = current with { Name = "New name", User = "other" };
        var moved = renamed with { Host = "other.example" };

        Assert.True(SshHostKeyTrust.CanReusePin(_directory, current, renamed));
        Assert.False(SshHostKeyTrust.CanReusePin(_directory, current, moved));
    }

    [Fact]
    public void LegacyProfileHasDedicatedPendingConfirmationState()
    {
        var server = new ServerViewModel(
            new ServerProfileData("id", "Legacy", "server.example", 22, "monitor"));

        Assert.True(server.HostKeyPendingConfirmation);
        Assert.Equal("Требуется подтверждение host key", server.Status);
    }

    private static string ValueAfter(IReadOnlyList<string> arguments, string option)
        => arguments[arguments.ToList().IndexOf(option) + 1];

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}