using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using ServerMonitorManager.Core;

namespace ServerMonitorManager.Provisioning.Helper;

public interface IProvisioningFileSystem
{
    bool FileExists(string path);
    bool IsSymbolicLink(string path);
    void CreateOwnerOnlyDirectory(string path);
    void WriteOwnerOnlyFile(string path, string content);
}

public interface IProvisioningProcessRunner
{
    ProvisioningProcessResult Run(string fileName, IReadOnlyList<string> arguments);
}

public sealed record ProvisioningProcessResult(int ExitCode, string StandardOutput, string StandardError);

public sealed class TimezoneProvisioningExecutor(
    X509Certificate2 controlAuthority,
    string localNodeId,
    IProvisioningFileSystem fileSystem,
    IProvisioningProcessRunner processRunner,
    TimeProvider timeProvider,
    string rollbackDirectory)
{
    private const string TimedatectlPath = "/usr/bin/timedatectl";
    private const string ZoneinfoRoot = "/usr/share/zoneinfo";

    public ProvisioningBaseInstallExecutionResult Execute(ProvisioningHelperRequest request)
    {
        var authorization = request.Execution;
        if (authorization is null
            || request.ProtocolVersion != ProvisioningExecutionGrantCodec.ProtocolVersion
            || request.JobId is not { Length: 32 }
            || !request.JobId.All(Uri.IsHexDigit)
            || request.ActionType != "system.base-install"
            || request.SchemaVersion != 1
            || request.ModuleHash != ProvisioningActionCatalog.SystemBaseInstallModuleHash
            || !string.Equals(authorization.NodeId, localNodeId, StringComparison.Ordinal)
            || !ProvisioningExecutionGrantCodec.Verify(
                authorization.Grant,
                controlAuthority,
                request.JobId,
                localNodeId,
                authorization.Plan,
                timeProvider.GetUtcNow()))
        {
            return Failure("execution.authorization-denied", "Execution authorization was rejected.");
        }

        var plan = authorization.Plan;
        if (!IsTimezoneOnly(plan))
        {
            return Failure(
                "system.base-install.unsupported-fields",
                "Only timezone changes are supported by this execution increment.");
        }
        if (!IsSafeTimezone(plan.Timezone)
            || !fileSystem.FileExists($"{ZoneinfoRoot}/{plan.Timezone}"))
        {
            return Failure("timezone.invalid", "The requested timezone is unavailable.");
        }
        if (!rollbackDirectory.StartsWith("/", StringComparison.Ordinal)
            || HasSymlinkedManagedPath(rollbackDirectory))
        {
            return Failure("backup.unsafe-path", "The rollback path is unsafe.");
        }

        try
        {
            fileSystem.CreateOwnerOnlyDirectory(rollbackDirectory);
            var nonceHash = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(authorization.Grant.Nonce)));
            var consumptionPath = $"{rollbackDirectory}/grant-{nonceHash}.consumed.json";
            if (fileSystem.IsSymbolicLink(consumptionPath))
            {
                return Failure("backup.unsafe-path", "The grant consumption path is unsafe.");
            }
            fileSystem.WriteOwnerOnlyFile(
                consumptionPath,
                JsonSerializer.Serialize(
                    new ProvisioningExecutionConsumptionRecord(
                        1,
                        request.JobId,
                        localNodeId,
                        authorization.Grant.Nonce,
                        timeProvider.GetUtcNow()),
                    SmmJsonContext.Default.ProvisioningExecutionConsumptionRecord));
        }
        catch
        {
            return Failure(
                "execution.grant-consumed",
                "Execution grant was already consumed or could not be recorded.");
        }

        ProvisioningProcessResult observedBefore;
        try
        {
            observedBefore = QueryTimezone();
        }
        catch
        {
            return Failure("timezone.inspect-failed", "The current timezone could not be inspected.");
        }
        var previousTimezone = NormalizeTimezone(observedBefore);
        if (previousTimezone is null || !IsSafeTimezone(previousTimezone))
        {
            return Failure("timezone.inspect-failed", "The current timezone could not be inspected.");
        }
        if (string.Equals(previousTimezone, plan.Timezone, StringComparison.Ordinal))
        {
            return new ProvisioningBaseInstallExecutionResult(
                true, "timezone.already-configured", "Timezone is already configured.",
                false, true, false, false, previousTimezone);
        }

        try
        {
            var rollbackPath = $"{rollbackDirectory}/{request.JobId}.json";
            if (fileSystem.IsSymbolicLink(rollbackPath))
            {
                return Failure("backup.unsafe-path", "The rollback path is unsafe.");
            }
            fileSystem.WriteOwnerOnlyFile(
                rollbackPath,
                CreateRollbackRecord(request, authorization, previousTimezone));
        }
        catch
        {
            return Failure("backup.create-failed", "The rollback record could not be created.");
        }

        var failureCode = "timezone.mutation-failed";
        try
        {
            var mutation = SetTimezone(plan.Timezone);
            if (mutation.ExitCode == 0)
            {
                failureCode = "timezone.verification-failed";
                var observedAfter = NormalizeTimezone(QueryTimezone());
                if (string.Equals(observedAfter, plan.Timezone, StringComparison.Ordinal))
                {
                    return new ProvisioningBaseInstallExecutionResult(
                        true, "timezone.changed", "Timezone was changed and verified.",
                        true, true, false, false, observedAfter);
                }
            }
        }
        catch
        {
            // A failed process or verification can still have partially changed the host.
        }

        var rollbackSucceeded = false;
        string? observedRollback = null;
        try
        {
            var rollback = SetTimezone(previousTimezone);
            if (rollback.ExitCode == 0)
            {
                observedRollback = NormalizeTimezone(QueryTimezone());
                rollbackSucceeded = string.Equals(
                    observedRollback, previousTimezone, StringComparison.Ordinal);
            }
        }
        catch
        {
            rollbackSucceeded = false;
        }

        return new ProvisioningBaseInstallExecutionResult(
            false,
            failureCode,
            failureCode == "timezone.verification-failed"
                ? "Timezone verification failed; rollback was attempted."
                : "Timezone mutation failed; rollback was attempted.",
            false,
            false,
            true,
            rollbackSucceeded,
            observedRollback);
    }

    private ProvisioningProcessResult QueryTimezone()
        => processRunner.Run(
            TimedatectlPath,
            ["show", "--property=Timezone", "--value"]);

    private ProvisioningProcessResult SetTimezone(string timezone)
        => processRunner.Run(TimedatectlPath, ["set-timezone", timezone]);

    private bool HasSymlinkedManagedPath(string path)
    {
        var current = string.Empty;
        foreach (var component in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += $"/{component}";
            if (fileSystem.IsSymbolicLink(current))
            {
                return true;
            }
        }
        return false;
    }

    private static string CreateRollbackRecord(
        ProvisioningHelperRequest request,
        ProvisioningBaseInstallExecutionAuthorization authorization,
        string previousTimezone)
        => JsonSerializer.Serialize(
               new ProvisioningTimezoneRollbackRecord(
                   1,
                   request.JobId,
                   authorization.NodeId,
                   authorization.Grant.PlanSha256,
                   previousTimezone,
                   authorization.Plan.Timezone),
               SmmJsonContext.Default.ProvisioningTimezoneRollbackRecord)
           + "\n";

    private static bool IsTimezoneOnly(SystemBaseInstallPlan plan)
        => string.Equals(plan.Locale, "unchanged", StringComparison.Ordinal)
           && !plan.AptUpdate
           && !plan.AptUpgrade
           && plan.Packages is { Length: 0 }
           && string.Equals(plan.SwapMode, "unchanged", StringComparison.Ordinal)
           && plan.SwapSizeMiB is null
           && plan.VmSwappiness == 60
           && !plan.EnableUnattendedUpgrades
           && string.Equals(plan.RebootPolicy, "never", StringComparison.Ordinal);

    private static bool IsSafeTimezone(string? value)
        => value is { Length: >= 1 and <= 64 }
           && value[0] is not '/' and not '.'
           && !value.Contains("..", StringComparison.Ordinal)
           && value.All(character => char.IsAsciiLetterOrDigit(character)
               || character is '/' or '_' or '-' or '+');

    private static string? NormalizeTimezone(ProvisioningProcessResult result)
    {
        if (result.ExitCode != 0)
        {
            return null;
        }
        var value = result.StandardOutput.Trim();
        return value.Length == 0 || value.Contains('\n') || value.Contains('\r')
            ? null
            : value;
    }

    private static ProvisioningBaseInstallExecutionResult Failure(string code, string message)
        => new(false, code, message, false, false, false, false, null);
}

public sealed class ProvisioningFileSystem : IProvisioningFileSystem
{
    public bool FileExists(string path) => File.Exists(path);

    public bool IsSymbolicLink(string path)
    {
        var info = File.Exists(path)
            ? (FileSystemInfo)new FileInfo(path)
            : Directory.Exists(path)
                ? new DirectoryInfo(path)
                : new FileInfo(path);
        return info.Exists && info.LinkTarget is not null;
    }

    public void CreateOwnerOnlyDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public void WriteOwnerOnlyFile(string path, string content)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 4096,
            Options = FileOptions.WriteThrough
        };
        if (OperatingSystem.IsLinux())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
        writer.Flush();
        stream.Flush(flushToDisk: true);
    }
}

public sealed class ProvisioningProcessRunner : IProvisioningProcessRunner
{
    public ProvisioningProcessResult Run(string fileName, IReadOnlyList<string> arguments)
    {
        if (!string.Equals(fileName, "/usr/bin/timedatectl", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Executable is outside the provisioning allowlist.");
        }
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Provisioning process could not be started.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException("Provisioning process timed out.");
        }
        Task.WaitAll(standardOutput, standardError);
        return new ProvisioningProcessResult(
            process.ExitCode,
            standardOutput.GetAwaiter().GetResult(),
            standardError.GetAwaiter().GetResult());
    }
}
