using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ServerMonitorManager.Core;

namespace ServerMonitorManager.Agent;

internal sealed class AgentClient(AgentOptions options)
{
    private readonly string _certificatePath = Path.Combine(options.StateDirectory, "agent.pfx");

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

        if (job.ActionType != "preflight" || job.SchemaVersion != 1)
        {
            await ReportProvisioningAsync(
                client, job, ProvisioningJobStates.Failed, job.ProgressPercent,
                "dispatch", "action.unsupported", "Unsupported provisioning action.", cancellationToken);
            return;
        }

        var currentProgress = job.ProgressPercent;
        try
        {
            var helper = new ProvisioningHelperClient(options.ProvisioningSocketPath);
            var result = await helper.RunPreflightAsync(job, cancellationToken);
            await ReportProvisioningAsync(
                client, job, ProvisioningJobStates.Running, 40,
                "inspect-host", "preflight.inspected", "Host inspection completed.", cancellationToken);
            currentProgress = 40;
            await ReportProvisioningAsync(
                client, job, ProvisioningJobStates.Verifying, 80,
                "verify-host", "preflight.verifying", "Verifying preflight result.", cancellationToken);
            currentProgress = 80;
            await ReportProvisioningAsync(
                client, job, ProvisioningJobStates.Completed, 100,
                "completed", "preflight.completed", "Preflight completed.", cancellationToken);
            Console.WriteLine(
                $"Preflight {job.Id} completed: {result.OperatingSystem} "
                + $"{result.OperatingSystemVersion} {result.Architecture}.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await ReportProvisioningAsync(
                client, job, ProvisioningJobStates.Failed, currentProgress,
                "preflight", "preflight.failed", "Preflight helper failed.", cancellationToken);
            Console.Error.WriteLine($"Preflight {job.Id} failed: {exception.Message}");
        }
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
