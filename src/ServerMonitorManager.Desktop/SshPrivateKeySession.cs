using System.Security.AccessControl;
using System.Security.Principal;

namespace ServerMonitorManager_Desktop;

internal sealed class SshPrivateKeySession : IAsyncDisposable
{
    internal const string FilePrefix = "server-monitor-manager-ed25519-session-";

    private string? _path;

    private SshPrivateKeySession(string path)
    {
        _path = path;
    }

    internal string Path
        => _path ?? throw new ObjectDisposedException(nameof(SshPrivateKeySession));

    internal static async Task<SshPrivateKeySession> CreateAsync(
        string directory,
        ReadOnlyMemory<byte> privateKey,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("Current Windows identity has no SID.");
        var security = new FileSecurity();
        security.SetOwner(currentUser);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            AccessControlType.Allow));

        var path = System.IO.Path.Combine(directory, $"{FilePrefix}{Guid.NewGuid():N}");
        try
        {
            await using var stream = FileSystemAclExtensions.Create(
                new FileInfo(path),
                FileMode.CreateNew,
                FileSystemRights.FullControl,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough,
                security);
            await stream.WriteAsync(privateKey, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            return new SshPrivateKeySession(path);
        }
        catch
        {
            File.Delete(path);
            throw;
        }
    }

    internal static void CleanupOrphans(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory, $"{FilePrefix}*"))
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // An active SSH process can still hold a current session file.
            }
            catch (UnauthorizedAccessException)
            {
                // Leave files not owned by the current identity untouched.
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        var path = Interlocked.Exchange(ref _path, null);
        if (path is not null)
        {
            File.Delete(path);
        }
        return ValueTask.CompletedTask;
    }
}
