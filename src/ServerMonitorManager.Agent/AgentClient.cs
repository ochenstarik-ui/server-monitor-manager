using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ServerMonitorManager.Core;

namespace ServerMonitorManager.Agent;

internal sealed class AgentClient(AgentOptions options)
{
    private const int MaximumEnrollmentTokenBytes = 4096;
    private readonly string _certificatePath = Path.Combine(options.StateDirectory, "agent.pfx");

    public async Task EnrollFromFileAsync(string path, CancellationToken cancellationToken)
    {
        var expectedPath = Path.GetFullPath(Path.Combine(options.EnrollmentTokenDirectory, "enroll-token"));
        var suppliedPath = Path.GetFullPath(path);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(suppliedPath, expectedPath, comparison))
        {
            throw new InvalidDataException(
                "Enrollment token file must be the dedicated file in the enrollment directory.");
        }

        var bytes = await ReadAndDeleteEnrollmentTokenAsync(path, cancellationToken);
        try
        {
            await EnrollAsync(Encoding.UTF8.GetString(bytes), cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    internal static async Task<byte[]> ReadAndDeleteEnrollmentTokenAsync(
        string path,
        CancellationToken cancellationToken)
    {
        byte[]? bytes = null;
        var deleteTokenFile = false;
        try
        {
            if (!Path.IsPathFullyQualified(path))
            {
                throw new InvalidDataException("Enrollment token file path must be absolute.");
            }
            deleteTokenFile = true;

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                const UnixFileMode forbidden = UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                    | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite
                    | UnixFileMode.OtherExecute;
                if ((File.GetUnixFileMode(path) & forbidden) != 0)
                {
                    throw new UnauthorizedAccessException(
                        "Enrollment token file must not be accessible by group or other users.");
                }
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true);
            if (stream.Length is <= 0 or > MaximumEnrollmentTokenBytes)
            {
                throw new InvalidDataException("Enrollment token file has an invalid size.");
            }

            bytes = new byte[stream.Length];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            if (bytes.Any(static value => value is not (
                    >= (byte)'A' and <= (byte)'Z'
                    or >= (byte)'a' and <= (byte)'z'
                    or >= (byte)'0' and <= (byte)'9'
                    or (byte)'-'
                    or (byte)'_')))
            {
                throw new InvalidDataException("Enrollment token file is not valid base64url data.");
            }

            return bytes;
        }
        catch
        {
            if (bytes is not null)
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
            throw;
        }
        finally
        {
            if (deleteTokenFile)
            {
                try
                {
                    File.Delete(path);
                }
                catch (UnauthorizedAccessException)
                {
                    // Production handoff lives in a root-owned directory. The root
                    // bootstrap owns final path cleanup on every exit path.
                }
            }
        }
    }

    public async Task EnrollAsync(string token, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.StateDirectory);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            $"CN={options.NodeId}",
            key,
            HashAlgorithmName.SHA256);
        var enrollment = new EnrollmentRequest(
            options.NodeId,
            token,
            request.CreateSigningRequestPem(),
            Guid.NewGuid().ToString());

        using var client = CreateHttpClient(clientCertificate: null);
        using var response = await client.PostAsJsonAsync(
            "api/v1/enroll",
            enrollment,
            SmmJsonContext.Default.EnrollmentRequest,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync(
            SmmJsonContext.Default.EnrollmentResponse,
            cancellationToken)
            ?? throw new InvalidOperationException("Control service returned an empty enrollment response.");
        using var certificate = X509Certificate2.CreateFromPem(result.CertificatePem, key.ExportPkcs8PrivateKeyPem());
        await File.WriteAllBytesAsync(_certificatePath, certificate.Export(X509ContentType.Pfx), cancellationToken);
        await File.WriteAllTextAsync(options.CertificateAuthorityPath, result.CertificateAuthorityPem, cancellationToken);
        SetOwnerOnlyPermissions(_certificatePath);
        SetOwnerOnlyPermissions(options.CertificateAuthorityPath);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var certificate = X509CertificateLoader.LoadPkcs12FromFile(
            _certificatePath,
            password: null,
            X509KeyStorageFlags.EphemeralKeySet);
        using var client = CreateHttpClient(certificate);
        var buffer = new MetricBuffer(options);
        var collectionDelay = TimeSpan.FromSeconds(options.HeartbeatSeconds);
        var retryDelay = collectionDelay;
        var nextUploadAttempt = DateTimeOffset.MinValue;
        while (!cancellationToken.IsCancellationRequested)
        {
            var iterationStartedAt = DateTimeOffset.UtcNow;
            try
            {
                await buffer.EnqueueAsync(
                    LinuxMetrics.Collect(options.NodeId, "0.1.0"),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Metric collection failed: {exception.Message}");
            }

            if (DateTimeOffset.UtcNow >= nextUploadAttempt)
            {
                try
                {
                    var pending = await buffer.PeekAsync(options.UploadBatchSize, cancellationToken);
                    foreach (var heartbeat in pending)
                    {
                        try
                        {
                            var accepted = await SendHeartbeatAsync(client, heartbeat, cancellationToken);
                            collectionDelay = TimeSpan.FromSeconds(
                                Math.Clamp(accepted.NextHeartbeatSeconds, 10, 300));
                        }
                        catch (PermanentHeartbeatException exception)
                        {
                            Console.Error.WriteLine(
                                $"Dropping rejected metric {heartbeat.SentAt:O}: {exception.Message}");
                        }

                        await buffer.AcknowledgeAsync(heartbeat.IdempotencyKey, cancellationToken);
                    }

                    retryDelay = collectionDelay;
                    nextUploadAttempt = DateTimeOffset.MinValue;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    var jitter = TimeSpan.FromSeconds(Random.Shared.Next(0, 6));
                    nextUploadAttempt = DateTimeOffset.UtcNow + retryDelay + jitter;
                    retryDelay = TimeSpan.FromSeconds(Math.Min(
                        options.MaxRetrySeconds,
                        Math.Max(10, retryDelay.TotalSeconds * 2)));
                    Console.Error.WriteLine(
                        $"Control Hub unavailable; metrics remain buffered until {nextUploadAttempt:O}: "
                        + exception.Message);
                }
            }

            try
            {
                await ExecuteNextProvisioningJobAsync(client, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Provisioning poll failed: {exception.Message}");
            }

            var elapsed = DateTimeOffset.UtcNow - iterationStartedAt;
            var remaining = collectionDelay - elapsed;
            await Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private async Task ExecuteNextProvisioningJobAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("api/v1/agents/provisioning/jobs/next", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent)
        {
            return;
        }
        response.EnsureSuccessStatusCode();
        var job = await response.Content.ReadFromJsonAsync(
            SmmJsonContext.Default.ProvisioningJob,
            cancellationToken)
            ?? throw new InvalidOperationException("Control service returned an empty provisioning job.");

        if (job.SchemaVersion != 1)
        {
            await ReportProvisioningAsync(
                client, job, ProvisioningJobStates.Failed, job.ProgressPercent,
                "dispatch", "action.unsupported", "Unsupported provisioning action.", cancellationToken);
            return;
        }

        if (job.ActionType == "system.base-install"
            && job.State == ProvisioningJobStates.Preflight)
        {
            try
            {
                var helper = new ProvisioningHelperClient(options.ProvisioningSocketPath);
                var plan = await helper.CreateBaseInstallPlanAsync(job, cancellationToken);
                await ReportBaseInstallPlanAsync(client, job, plan, cancellationToken);
                Console.WriteLine($"Base installation plan {job.Id} is awaiting confirmation.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await ReportProvisioningAsync(
                    client, job, ProvisioningJobStates.Failed, job.ProgressPercent,
                    "preflight", "base-install.plan-failed",
                    "Base installation plan generation failed.", cancellationToken);
                Console.Error.WriteLine($"Base installation plan {job.Id} failed: {exception.Message}");
            }
            return;
        }

        if (job.ActionType == "system.base-install"
            && job.State == ProvisioningJobStates.Running)
        {
            await ExecuteBaseInstallAsync(client, job, cancellationToken);
            return;
        }

        if (job.ActionType != "preflight")
        {
            await ReportProvisioningAsync(
                client, job, ProvisioningJobStates.Failed, job.ProgressPercent,
                "dispatch", "action.unsupported", "Unsupported provisioning action.", cancellationToken);
            return;
        }

        ProvisioningPreflightResult result;
        try
        {
            var helper = new ProvisioningHelperClient(options.ProvisioningSocketPath);
            result = await helper.RunPreflightAsync(job, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await ReportProvisioningAsync(
                client, job, ProvisioningJobStates.Failed, job.ProgressPercent,
                "preflight", "preflight.failed", "Preflight helper failed.", cancellationToken);
            Console.Error.WriteLine($"Preflight {job.Id} failed: {exception.Message}");
            return;
        }

        await ReportPreflightFactsAsync(client, job, result, cancellationToken);
        await ReportProvisioningAsync(
            client, job, ProvisioningJobStates.Running, 40,
            "inspect-host", "preflight.inspected", "Host inspection completed.", cancellationToken);
        await ReportProvisioningAsync(
            client, job, ProvisioningJobStates.Verifying, 80,
            "verify-host", "preflight.verifying", "Verifying preflight result.", cancellationToken);
        await ReportProvisioningAsync(
            client, job, ProvisioningJobStates.Completed, 100,
            "completed", "preflight.completed", "Preflight completed.", cancellationToken);
        Console.WriteLine(
            $"Preflight {job.Id} completed: {result.OperatingSystem} "
            + $"{result.OperatingSystemVersion} {result.Architecture}.");
    }

    private async Task ExecuteBaseInstallAsync(
        HttpClient client,
        ProvisioningJob job,
        CancellationToken cancellationToken)
    {
        ProvisioningBaseInstallExecutionAuthorization authorization;
        try
        {
            authorization = await RequestBaseInstallExecutionAuthorizationAsync(
                client, job, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await ReportProvisioningAsync(
                client, job, ProvisioningJobStates.Failed, job.ProgressPercent,
                "authorize", "base-install.authorization-failed",
                "Base installation authorization failed before mutation.", cancellationToken);
            Console.Error.WriteLine($"Base installation {job.Id} authorization failed: {exception.Message}");
            return;
        }

        ProvisioningBaseInstallExecutionResult result;
        try
        {
            var helper = new ProvisioningHelperClient(options.ProvisioningSocketPath);
            result = await helper.ExecuteBaseInstallAsync(job, authorization, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await ReportProvisioningAsync(
                client, job, ProvisioningJobStates.NeedsReconciliation, job.ProgressPercent,
                "reconcile", "base-install.execution-uncertain",
                "Base installation outcome is uncertain and requires reconciliation.", cancellationToken);
            Console.Error.WriteLine($"Base installation {job.Id} outcome is uncertain: {exception.Message}");
            return;
        }

        if (result.Success)
        {
            await ReportProvisioningAsync(
                client, job, ProvisioningJobStates.Verifying, 90,
                "verify", "base-install.verifying", "Verifying applied configuration.", cancellationToken);
            await ReportProvisioningAsync(
                client, job, ProvisioningJobStates.Completed, 100,
                "completed", "base-install.completed", "Base installation change verified.", cancellationToken);
            Console.WriteLine($"Base installation {job.Id} completed and verified.");
            return;
        }

        if (!result.RollbackAttempted)
        {
            await ReportProvisioningAsync(
                client, job, ProvisioningJobStates.Failed, job.ProgressPercent,
                "execute", result.Code, "Base installation was rejected before mutation.", cancellationToken);
            return;
        }

        await ReportProvisioningAsync(
            client, job, ProvisioningJobStates.RollingBack, 90,
            "rollback", "base-install.rollback", "Restoring the previous configuration.", cancellationToken);
        await ReportProvisioningAsync(
            client, job,
            result.RollbackSucceeded ? ProvisioningJobStates.RolledBack : ProvisioningJobStates.RollbackFailed,
            100,
            result.RollbackSucceeded ? "rolled-back" : "rollback-failed",
            result.RollbackSucceeded ? "base-install.rolled-back" : "base-install.rollback-failed",
            result.RollbackSucceeded
                ? "Previous configuration restored and verified."
                : "Previous configuration could not be verified after rollback.",
            cancellationToken);
    }

    private static async Task<ProvisioningBaseInstallExecutionAuthorization>
        RequestBaseInstallExecutionAuthorizationAsync(
            HttpClient client,
            ProvisioningJob job,
            CancellationToken cancellationToken)
    {
        var request = new ProvisioningExecutionGrantRequest(
            CreateOperationId(job.Id, "execution-grant"));
        using var response = await client.PostAsJsonAsync(
            $"api/v1/agents/provisioning/jobs/{job.Id}/execution-grant",
            request,
            SmmJsonContext.Default.ProvisioningExecutionGrantRequest,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(
            SmmJsonContext.Default.ProvisioningBaseInstallExecutionAuthorization,
            cancellationToken)
            ?? throw new InvalidOperationException(
                "Control service returned an empty execution authorization.");
    }

    private static async Task ReportProvisioningAsync(
        HttpClient client,
        ProvisioningJob job,
        string state,
        int progress,
        string step,
        string eventCode,
        string message,
        CancellationToken cancellationToken)
    {
        var request = new ProvisioningJobProgressRequest(
            state, progress, step, eventCode, message, CreateOperationId(job.Id, state));
        using var response = await client.PostAsJsonAsync(
            $"api/v1/agents/provisioning/jobs/{job.Id}/progress",
            request,
            SmmJsonContext.Default.ProvisioningJobProgressRequest,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task ReportPreflightFactsAsync(
        HttpClient client,
        ProvisioningJob job,
        ProvisioningPreflightResult facts,
        CancellationToken cancellationToken)
    {
        var request = new ProvisioningPreflightReportRequest(
            facts, DateTimeOffset.UtcNow, CreateOperationId(job.Id, "facts"));
        using var response = await client.PostAsJsonAsync(
            $"api/v1/agents/provisioning/jobs/{job.Id}/preflight-facts",
            request,
            SmmJsonContext.Default.ProvisioningPreflightReportRequest,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static async Task ReportBaseInstallPlanAsync(
        HttpClient client,
        ProvisioningJob job,
        SystemBaseInstallPlan plan,
        CancellationToken cancellationToken)
    {
        var request = new SystemBaseInstallPlanReportRequest(
            plan, CreateOperationId(job.Id, "base-install-plan"));
        using var response = await client.PostAsJsonAsync(
            $"api/v1/agents/provisioning/jobs/{job.Id}/base-install-plan",
            request,
            SmmJsonContext.Default.SystemBaseInstallPlanReportRequest,
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static string CreateOperationId(string jobId, string state)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"{jobId}:{state}"));
        return new Guid(digest.AsSpan(0, 16)).ToString();
    }

    private static async Task<AgentHeartbeatResponse> SendHeartbeatAsync(
        HttpClient client,
        AgentHeartbeat heartbeat,
        CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync(
            "api/v1/agents/heartbeat",
            heartbeat,
            SmmJsonContext.Default.AgentHeartbeat,
            cancellationToken);
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict)
        {
            var details = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new PermanentHeartbeatException(
                string.IsNullOrWhiteSpace(details)
                    ? $"Control service returned {(int)response.StatusCode}."
                    : details);
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(
            SmmJsonContext.Default.AgentHeartbeatResponse,
            cancellationToken)
            ?? throw new InvalidOperationException("Control service returned an empty heartbeat response.");
    }

    private HttpClient CreateHttpClient(X509Certificate2? clientCertificate)
    {
        using var root = X509CertificateLoader.LoadCertificateFromFile(options.CertificateAuthorityPath);
        var rootBytes = root.Export(X509ContentType.Cert);
        var handler = new HttpClientHandler();
        if (clientCertificate is not null)
        {
            handler.ClientCertificates.Add(clientCertificate);
        }
        handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is null
                || errors.HasFlag(System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch)
                || errors.HasFlag(System.Net.Security.SslPolicyErrors.RemoteCertificateNotAvailable))
            {
                return false;
            }
            using var trustedRoot = X509CertificateLoader.LoadCertificate(rootBytes);
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            return chain.Build(new X509Certificate2(certificate));
        };
        return new HttpClient(handler) { BaseAddress = options.ControlUrl };
    }

    private static void SetOwnerOnlyPermissions(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}

internal sealed class PermanentHeartbeatException(string message) : Exception(message);
