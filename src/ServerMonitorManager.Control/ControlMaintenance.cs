using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace ServerMonitorManager.Control;

public sealed record ControlMaintenanceResult(
    int MetricsDeleted,
    int IdempotencyDeleted,
    int AuditDeleted,
    int TokensDeleted,
    int ProvisioningJobsCancelled,
    int ProvisioningJobsNeedingReconciliation);

public sealed class LinkExpirationBackgroundService(
    LinkService links,
    IOptions<ControlOptions> options,
    TimeProvider timeProvider,
    ILogger<LinkExpirationBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(options.Value.LinkExpirationPollSeconds), timeProvider);
        do
        {
            try
            {
                var result = await links.ExpireDueLinksAsync(timeProvider.GetUtcNow(), stoppingToken);
                if (result.Disabled > 0 || result.Failed > 0)
                {
                    logger.LogInformation(
                        "TTL reconciliation completed: {Disabled} disabled, {Failed} failed.",
                        result.Disabled,
                        result.Failed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "TTL reconciliation failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

public sealed class ControlMaintenanceBackgroundService(
    ControlStore store,
    ControlBackupService backups,
    IOptions<ControlOptions> options,
    TimeProvider timeProvider,
    ILogger<ControlMaintenanceBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromMinutes(options.Value.MaintenanceIntervalMinutes), timeProvider);
        do
        {
            try
            {
                var result = await store.MaintainAsync(timeProvider.GetUtcNow(), stoppingToken);
                await backups.CreateIfDueAsync(timeProvider.GetUtcNow(), stoppingToken);
                if (result.MetricsDeleted + result.IdempotencyDeleted + result.AuditDeleted
                    + result.TokensDeleted + result.ProvisioningJobsCancelled
                    + result.ProvisioningJobsNeedingReconciliation > 0)
                {
                    logger.LogInformation(
                        "Control maintenance removed {Metrics} metrics, {Idempotency} replay records, "
                        + "{Audit} audit records, and {Tokens} enrollment tokens; cancelled {Cancelled} "
                        + "expired jobs and marked {Reconciliation} jobs for reconciliation.",
                        result.MetricsDeleted,
                        result.IdempotencyDeleted,
                        result.AuditDeleted,
                        result.TokensDeleted,
                        result.ProvisioningJobsCancelled,
                        result.ProvisioningJobsNeedingReconciliation);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Control database maintenance failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

public sealed class ControlBackupService(
    ControlStore store,
    IOptions<ControlOptions> options,
    ILogger<ControlBackupService> logger)
{
    private const string ManifestFileName = "manifest.json";
    private readonly ControlOptions _options = options.Value;

    public async Task<string> CreateAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_options.BackupDirectory);
        SetDirectoryPermissions(_options.BackupDirectory);
        var name = $"backup-{now:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
        var temporary = Path.Combine(_options.BackupDirectory, $".{name}.tmp");
        var destination = Path.Combine(_options.BackupDirectory, name);
        Directory.CreateDirectory(temporary);
        try
        {
            var databaseName = "control.db";
            var authorityName = "control-ca.pfx";
            var databasePath = Path.Combine(temporary, databaseName);
            var authorityPath = Path.Combine(temporary, authorityName);
            await store.BackupDatabaseAsync(databasePath, cancellationToken);
            File.Copy(_options.CertificateAuthorityPath, authorityPath, overwrite: false);
            SetFilePermissions(databasePath);
            SetFilePermissions(authorityPath);
            var manifest = new ControlBackupManifest(
                1,
                now,
                databaseName,
                await ComputeSha256Async(databasePath, cancellationToken),
                authorityName,
                await ComputeSha256Async(authorityPath, cancellationToken));
            await File.WriteAllTextAsync(
                Path.Combine(temporary, ManifestFileName),
                JsonSerializer.Serialize(manifest, ControlMaintenanceJsonContext.Default.ControlBackupManifest),
                cancellationToken);
            SetFilePermissions(Path.Combine(temporary, ManifestFileName));
            Directory.Move(temporary, destination);
            SetDirectoryPermissions(destination);
            TrimOldBackups();
            logger.LogInformation("Control backup created at {BackupPath}.", destination);
            return destination;
        }
        catch
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }
            throw;
        }
    }

    public async Task<string?> CreateIfDueAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_options.BackupDirectory);
        var newest = Directory.EnumerateDirectories(_options.BackupDirectory, "backup-*")
            .Select(Directory.GetLastWriteTimeUtc)
            .OrderByDescending(value => value)
            .FirstOrDefault();
        return newest == default || now - newest >= TimeSpan.FromHours(_options.BackupIntervalHours)
            ? await CreateAsync(now, cancellationToken)
            : null;
    }

    public async Task RestoreAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        var root = Path.GetFullPath(backupPath);
        var manifestPath = Path.Combine(root, ManifestFileName);
        var manifest = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(manifestPath, cancellationToken),
            ControlMaintenanceJsonContext.Default.ControlBackupManifest)
            ?? throw new InvalidDataException("Backup manifest is empty.");
        if (manifest.Version != 1
            || Path.GetFileName(manifest.DatabaseFile) != manifest.DatabaseFile
            || Path.GetFileName(manifest.CertificateAuthorityFile) != manifest.CertificateAuthorityFile)
        {
            throw new InvalidDataException("Unsupported or unsafe backup manifest.");
        }

        var databaseSource = Path.Combine(root, manifest.DatabaseFile);
        var authoritySource = Path.Combine(root, manifest.CertificateAuthorityFile);
        await VerifyHashAsync(databaseSource, manifest.DatabaseSha256, cancellationToken);
        await VerifyHashAsync(authoritySource, manifest.CertificateAuthoritySha256, cancellationToken);
        await VerifyDatabaseAsync(databaseSource, cancellationToken);

        var safetyDirectory = Path.Combine(
            _options.BackupDirectory,
            $"pre-restore-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(safetyDirectory);
        if (File.Exists(_options.DatabasePath))
        {
            var safetyDatabase = Path.Combine(safetyDirectory, "control.db");
            File.Copy(_options.DatabasePath, safetyDatabase);
            SetFilePermissions(safetyDatabase);
        }
        if (File.Exists(_options.CertificateAuthorityPath))
        {
            var safetyAuthority = Path.Combine(safetyDirectory, "control-ca.pfx");
            File.Copy(_options.CertificateAuthorityPath, safetyAuthority);
            SetFilePermissions(safetyAuthority);
        }
        SetDirectoryPermissions(safetyDirectory);

        await ReplaceFileAsync(databaseSource, _options.DatabasePath, cancellationToken);
        await ReplaceFileAsync(authoritySource, _options.CertificateAuthorityPath, cancellationToken);
        File.Delete(_options.DatabasePath + "-wal");
        File.Delete(_options.DatabasePath + "-shm");
        SetFilePermissions(_options.DatabasePath);
        SetFilePermissions(_options.CertificateAuthorityPath);
        logger.LogWarning(
            "Control state restored from {BackupPath}. Previous files are in {SafetyPath}.",
            root,
            safetyDirectory);
    }

    private void TrimOldBackups()
    {
        foreach (var directory in Directory.EnumerateDirectories(_options.BackupDirectory, "backup-*")
                     .OrderByDescending(Directory.GetLastWriteTimeUtc)
                     .Skip(_options.BackupRetentionCount))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task ReplaceFileAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var temporary = destination + $".restore-{Guid.NewGuid():N}.tmp";
        await using (var input = File.OpenRead(source))
        await using (var output = new FileStream(
                         temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
        File.Move(temporary, destination, overwrite: true);
    }

    private static async Task VerifyDatabaseAsync(string path, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Backup database integrity check failed: {result}");
        }
    }

    private static async Task VerifyHashAsync(
        string path,
        string expected,
        CancellationToken cancellationToken)
    {
        var actual = await ComputeSha256Async(path, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actual), Convert.FromHexString(expected)))
        {
            throw new InvalidDataException($"Backup checksum mismatch for {Path.GetFileName(path)}.");
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    private static void SetDirectoryPermissions(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void SetFilePermissions(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}

internal sealed record ControlBackupManifest(
    int Version,
    DateTimeOffset CreatedAt,
    string DatabaseFile,
    string DatabaseSha256,
    string CertificateAuthorityFile,
    string CertificateAuthoritySha256);

[JsonSerializable(typeof(ControlBackupManifest))]
internal sealed partial class ControlMaintenanceJsonContext : JsonSerializerContext;
