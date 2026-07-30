using System.Security.AccessControl;
using System.Security.Principal;
using ServerMonitorManager_Desktop;
using Xunit;

namespace ServerMonitorManager.Desktop.Security.Tests;

public sealed class SshPrivateKeySessionTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"smm-desktop-security-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task CreateAsyncWritesOwnerOnlyKeyAndDisposeDeletesIt()
    {
        Directory.CreateDirectory(_directory);
        var key = "test-private-key"u8.ToArray();
        string path;

        await using (var session = await SshPrivateKeySession.CreateAsync(
            _directory,
            key,
            TestContext.Current.CancellationToken))
        {
            path = session.Path;
            Assert.Equal(key, await File.ReadAllBytesAsync(
                path,
                TestContext.Current.CancellationToken));

            var currentUser = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("Current Windows identity has no SID.");
            var security = new FileInfo(path).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected);
            var rules = security
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToArray();
            var rule = Assert.Single(rules);
            Assert.Equal(currentUser, rule.IdentityReference);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights & FileSystemRights.FullControl);
        }

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void CleanupOrphansDeletesOnlyManagedKeyFiles()
    {
        Directory.CreateDirectory(_directory);
        var orphan = Path.Combine(_directory, $"{SshPrivateKeySession.FilePrefix}{Guid.NewGuid():N}");
        var unrelated = Path.Combine(_directory, "unrelated-file");
        File.WriteAllText(orphan, "secret");
        File.WriteAllText(unrelated, "keep");

        SshPrivateKeySession.CleanupOrphans(_directory);
        SshPrivateKeySession.CleanupOrphans(_directory);

        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(unrelated));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
