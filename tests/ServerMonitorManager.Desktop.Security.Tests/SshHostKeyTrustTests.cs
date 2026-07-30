using ServerMonitorManager_Desktop;
using Xunit;

namespace ServerMonitorManager.Desktop.Security.Tests;

public sealed class SshHostKeyTrustTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"smm-host-key-tests-{Guid.NewGuid():N}");

    [Fact]
    public void ParseCandidatePrefersEd25519AndComputesSha256Fingerprint()
    {
        var candidate = SshHostKeyTrust.ParseCandidate(
            "server.example",
            22,
            "server.example ssh-rsa AQIDBA==\nserver.example ssh-ed25519 AQIDBA==\n");

        Assert.Equal("ssh-ed25519", candidate.KeyType);
        Assert.Equal("SHA256:n2SnR+G5fxMfq7a0Rylsm28CAeefs8U1bmx36JtqgGo", candidate.Fingerprint);
        Assert.Equal("server.example ssh-ed25519 AQIDBA==", candidate.KnownHostsLine);
    }

    [Fact]
    public void ParseCandidateRejectsUnexpectedEndpoint()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SshHostKeyTrust.ParseCandidate(
                "server.example",
                2222,
                "[other.example]:2222 ssh-ed25519 AQIDBA==\n"));

        Assert.Contains("endpoint", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WriteAsyncReplacesLegacyPatternsWithOneExclusivePin()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "known_hosts");
        await File.WriteAllTextAsync(
            path,
            "|1|legacy-salt|legacy-hash ssh-rsa BQYHCA==\n@cert-authority *.example ssh-ed25519 BQYHCA==\n[server.example]:2222\tssh-rsa BQYHCA==\n",
            TestContext.Current.CancellationToken);
        var candidate = SshHostKeyTrust.ParseCandidate(
            "server.example",
            2222,
            "[server.example]:2222 ssh-ed25519 AQIDBA==\n");

        await SshHostKeyTrust.WriteAsync(
            path,
            candidate,
            TestContext.Current.CancellationToken);

        var lines = await File.ReadAllLinesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal([candidate.KnownHostsLine], lines);
        Assert.True(SshHostKeyTrust.IsTrusted(
            path,
            "server.example",
            2222,
            candidate.Fingerprint));
        Assert.False(SshHostKeyTrust.IsTrusted(
            path,
            "server.example",
            2222,
            "SHA256:wrong"));
    }

    [Fact]
    public void GetPinPathIsEndpointScopedAndDoesNotExposeHostname()
    {
        var first = SshHostKeyTrust.GetPinPath(_directory, "server.example", 22);
        var second = SshHostKeyTrust.GetPinPath(_directory, "server.example", 2222);

        Assert.NotEqual(first, second);
        Assert.Equal(_directory, Path.GetDirectoryName(first));
        Assert.DoesNotContain("server.example", Path.GetFileName(first), StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
