using System.Security.Cryptography;
using System.Text;
using ServerMonitorManager.Agent;
using Xunit;

namespace ServerMonitorManager.Control.Tests;

public sealed class EnrollmentTokenTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"smm-enrollment-token-{Guid.NewGuid():N}");

    [Fact]
    public async Task TokenFileIsReadOnceAndDeleted()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "enroll-token");
        await File.WriteAllBytesAsync(
            path,
            Encoding.UTF8.GetBytes("one-time-token"),
            TestContext.Current.CancellationToken);
        RestrictTokenFile(path);

        var tokenBytes = await AgentClient.ReadAndDeleteEnrollmentTokenAsync(
            path,
            TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal("one-time-token", Encoding.UTF8.GetString(tokenBytes));
            Assert.False(File.Exists(path));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(tokenBytes);
        }
    }

    [Fact]
    public async Task OversizedTokenFileIsRejectedAndDeleted()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "enroll-token");
        await File.WriteAllBytesAsync(path, new byte[4097], TestContext.Current.CancellationToken);
        RestrictTokenFile(path);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AgentClient.ReadAndDeleteEnrollmentTokenAsync(
                path,
                TestContext.Current.CancellationToken));

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task RelativeTokenPathIsRejectedWithoutDeletingTheFile()
    {
        var fileName = $"smm-relative-token-{Guid.NewGuid():N}";
        await File.WriteAllTextAsync(
            fileName,
            "must-remain",
            TestContext.Current.CancellationToken);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                AgentClient.ReadAndDeleteEnrollmentTokenAsync(
                    fileName,
                    TestContext.Current.CancellationToken));

            Assert.True(File.Exists(fileName));
        }
        finally
        {
            File.Delete(fileName);
        }
    }

    [Fact]
    public async Task MultilineTokenIsRejectedAndDeleted()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "enroll-token");
        await File.WriteAllTextAsync(
            path,
            "first-line\nsecond-line",
            TestContext.Current.CancellationToken);
        RestrictTokenFile(path);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AgentClient.ReadAndDeleteEnrollmentTokenAsync(
                path,
                TestContext.Current.CancellationToken));

        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task EnrollmentDoesNotDeleteAnArbitraryAbsoluteFile()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(Path.GetTempPath(), $"smm-unrelated-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(path, "must-remain", TestContext.Current.CancellationToken);
        try
        {
            var client = new AgentClient(new AgentOptions
            {
                StateDirectory = _directory,
                EnrollmentTokenDirectory = _directory
            });

            await Assert.ThrowsAsync<InvalidDataException>(() =>
                client.EnrollFromFileAsync(path, TestContext.Current.CancellationToken));

            Assert.True(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void RestrictTokenFile(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}