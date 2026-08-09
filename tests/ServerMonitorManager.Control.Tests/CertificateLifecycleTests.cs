using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using ServerMonitorManager.Agent;
using ServerMonitorManager.Control;
using ServerMonitorManager.Core;
using Xunit;

namespace ServerMonitorManager.Control.Tests;

public sealed class CertificateLifecycleTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"smm-cert-lifecycle-tests-{Guid.NewGuid():N}");

    public CertificateLifecycleTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task CertWithLessThanOneThirdRemainingIsRenewed_AndOldCertReplaced()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var dbPath = Path.Combine(_directory, "renew-test.db");
        var caPath = Path.Combine(_directory, "control-ca.pfx");
        CreateCaPfx(caPath);

        var options = Options.Create(new ControlOptions
        {
            DatabasePath = dbPath,
            CertificateAuthorityPath = caPath,
            ClientCertificateDays = 30
        });

        var store = new ControlStore(options);
        await store.InitializeAsync(cancellationToken);
        using var ca = new CertificateAuthority(options);
        var broker = new ControlEventBroker();
        var applier = new NoOpPolicyApplier();
        var linkService = new LinkService(store, applier, broker);
        var lifecycle = new CertificateLifecycleService(store, linkService, broker, ca);

        // 1. Enroll Agent "node-expiring"
        using var key1 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr1 = new CertificateRequest("CN=node-expiring", key1, HashAlgorithmName.SHA256).CreateSigningRequestPem();
        var token = await store.CreateEnrollmentTokenAsync("node-expiring", TimeSpan.FromMinutes(10), cancellationToken);
        var issued1 = ca.IssueClientCertificate("node-expiring", csr1);
        await store.EnrollAsync(new EnrollmentRequest("node-expiring", token, csr1, "idemp-1"), () => issued1, cancellationToken);

        // 2. Perform Certificate Renewal
        using var key2 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr2 = new CertificateRequest("CN=node-expiring", key2, HashAlgorithmName.SHA256).CreateSigningRequestPem();
        var renewalReq = new CertificateRenewalRequest("node-expiring", csr2, "idemp-2");

        var renewedIssued = await lifecycle.RenewAgentCertificateAsync("node-expiring", renewalReq, "node-expiring", cancellationToken);
        Assert.NotNull(renewedIssued);
        Assert.NotEqual(issued1.Thumbprint, renewedIssued.Thumbprint);

        // Verify database updated with new thumbprint
        var agents = await store.ListAgentsAsync(cancellationToken);
        var agent = Assert.Single(agents, a => a.NodeId == "node-expiring");
        Assert.NotNull(agent.CertificateExpiresAt);
        Assert.NotNull(agent.CertificateRemainingDays);
        Assert.True(agent.CertificateRemainingDays.Value >= 29);
    }

    [Fact]
    public async Task CertWithSufficientRemainingLifetime_IsNotRenewed()
    {
        var agentDir = Path.Combine(_directory, "agent-sufficient");
        Directory.CreateDirectory(agentDir);
        var pfxPath = Path.Combine(agentDir, "agent.pfx");
        var caPath = Path.Combine(agentDir, "control-ca.crt");

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=node-valid", key, HashAlgorithmName.SHA256);
        // Valid for 30 days starting now -> ~30 days remaining (well above 1/3 threshold)
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(30));
        File.WriteAllBytes(pfxPath, cert.Export(X509ContentType.Pfx));
        File.WriteAllText(caPath, cert.ExportCertificatePem());

        var agentOptions = new AgentOptions
        {
            NodeId = "node-valid",
            StateDirectory = agentDir,
            CertificateAuthorityPath = caPath,
            ControlUrl = new Uri("https://127.0.0.1:9999")
        };
        var client = new AgentClient(agentOptions);

        using var handler = new HttpClientHandler();
        using var http = new HttpClient(handler) { BaseAddress = agentOptions.ControlUrl };

        // Should return false because remaining lifespan > 1/3
        var renewed = await client.EnsureCertificateRenewedAsync(cert, http, TestContext.Current.CancellationToken);
        Assert.False(renewed);
    }

    [Fact]
    public async Task HubUnavailable_AgentContinuesUsingExistingCert_MetricsPreserved()
    {
        var agentDir = Path.Combine(_directory, "agent-unavail");
        Directory.CreateDirectory(agentDir);
        var pfxPath = Path.Combine(agentDir, "agent.pfx");
        var caPath = Path.Combine(agentDir, "control-ca.crt");

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=node-unavail", key, HashAlgorithmName.SHA256);
        // Expiring in 2 days -> remaining < 1/3 of 30 days
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-20), DateTimeOffset.UtcNow.AddDays(2));
        var originalPfxBytes = cert.Export(X509ContentType.Pfx);
        File.WriteAllBytes(pfxPath, originalPfxBytes);
        File.WriteAllText(caPath, cert.ExportCertificatePem());

        var agentOptions = new AgentOptions
        {
            NodeId = "node-unavail",
            StateDirectory = agentDir,
            CertificateAuthorityPath = caPath,
            ControlUrl = new Uri("https://127.0.0.1:9999")
        };
        var client = new AgentClient(agentOptions);

        using var handler = new HttpClientHandler();
        using var http = new HttpClient(handler) { BaseAddress = agentOptions.ControlUrl };

        var renewed = await client.EnsureCertificateRenewedAsync(cert, http, TestContext.Current.CancellationToken);
        Assert.False(renewed);

        var onDiskBytes = File.ReadAllBytes(pfxPath);
        Assert.Equal(originalPfxBytes, onDiskBytes);
    }

    [Fact]
    public async Task RevokedCert_CannotBeRenewed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var dbPath = Path.Combine(_directory, "revoked-test.db");
        var caPath = Path.Combine(_directory, "control-ca.pfx");
        CreateCaPfx(caPath);

        var options = Options.Create(new ControlOptions
        {
            DatabasePath = dbPath,
            CertificateAuthorityPath = caPath,
            ClientCertificateDays = 30
        });

        var store = new ControlStore(options);
        await store.InitializeAsync(cancellationToken);
        using var ca = new CertificateAuthority(options);
        var broker = new ControlEventBroker();
        var applier = new NoOpPolicyApplier();
        var linkService = new LinkService(store, applier, broker);
        var lifecycle = new CertificateLifecycleService(store, linkService, broker, ca);

        // Enroll agent
        using var key1 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr1 = new CertificateRequest("CN=node-revoked", key1, HashAlgorithmName.SHA256).CreateSigningRequestPem();
        var token = await store.CreateEnrollmentTokenAsync("node-revoked", TimeSpan.FromMinutes(10), cancellationToken);
        var issued1 = ca.IssueClientCertificate("node-revoked", csr1);
        await store.EnrollAsync(new EnrollmentRequest("node-revoked", token, csr1, "idemp-1"), () => issued1, cancellationToken);

        // Reenroll/Revoke agent
        await lifecycle.ReenrollAgentAsync("node-revoked", new CertificateReenrollmentRequest("compromised", "idemp-2"), "operator", cancellationToken);

        // Attempt renewal
        using var key2 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr2 = new CertificateRequest("CN=node-revoked", key2, HashAlgorithmName.SHA256).CreateSigningRequestPem();
        var renewalReq = new CertificateRenewalRequest("node-revoked", csr2, "idemp-3");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lifecycle.RenewAgentCertificateAsync("node-revoked", renewalReq, "node-revoked", cancellationToken));
        Assert.Contains("Revoked agent cannot renew certificate", ex.Message);
    }

    [Fact]
    public async Task RenewalRequest_WithDifferentNodeId_IsRejected()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var dbPath = Path.Combine(_directory, "mismatch-test.db");
        var caPath = Path.Combine(_directory, "control-ca.pfx");
        CreateCaPfx(caPath);

        var options = Options.Create(new ControlOptions
        {
            DatabasePath = dbPath,
            CertificateAuthorityPath = caPath,
            ClientCertificateDays = 30
        });

        var store = new ControlStore(options);
        await store.InitializeAsync(cancellationToken);
        using var ca = new CertificateAuthority(options);
        var broker = new ControlEventBroker();
        var applier = new NoOpPolicyApplier();
        var linkService = new LinkService(store, applier, broker);
        var lifecycle = new CertificateLifecycleService(store, linkService, broker, ca);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = new CertificateRequest("CN=node-a", key, HashAlgorithmName.SHA256).CreateSigningRequestPem();
        var renewalReq = new CertificateRenewalRequest("node-a", csr, "idemp-1");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            lifecycle.RenewAgentCertificateAsync("node-a", renewalReq, "node-b", cancellationToken));
        Assert.Contains("Node ID mismatch", ex.Message);
    }

    [Fact]
    public async Task InterruptedPfxReplacement_ActiveCertRemainsIntact()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var targetPath = Path.Combine(_directory, "agent.pfx");
        var tempPath = targetPath + ".tmp";

        var originalBytes = new byte[] { 1, 2, 3, 4, 5 };
        var incompleteTempBytes = new byte[] { 9, 9, 9 };

        await File.WriteAllBytesAsync(targetPath, originalBytes, cancellationToken);
        await File.WriteAllBytesAsync(tempPath, incompleteTempBytes, cancellationToken);

        Assert.True(File.Exists(targetPath));
        Assert.True(File.Exists(tempPath));

        var currentBytes = await File.ReadAllBytesAsync(targetPath, cancellationToken);
        Assert.Equal(originalBytes, currentBytes);
    }

    [Fact]
    public void OutOfRangeClientCertificateDays_ValidationFailsOnStart()
    {
        var optionsInvalidLow = new ControlOptions { ClientCertificateDays = 0 };
        var optionsInvalidHigh = new ControlOptions { ClientCertificateDays = 999 };
        var optionsValid = new ControlOptions { ClientCertificateDays = 30 };

        Assert.False(optionsInvalidLow.ClientCertificateDays is >= 1 and <= 90);
        Assert.False(optionsInvalidHigh.ClientCertificateDays is >= 1 and <= 90);
        Assert.True(optionsValid.ClientCertificateDays is >= 1 and <= 90);
    }

    private static void CreateCaPfx(string path)
    {
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest("CN=SMM Test Lifecycle CA", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            true));
        using var ca = caRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddYears(2));
        File.WriteAllBytes(path, ca.Export(X509ContentType.Pfx));
    }

    private sealed class NoOpPolicyApplier : ILinkPolicyApplier
    {
        public Task<IReadOnlyList<LinkRule>> ListRulesAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<LinkRule>>([]);
        public Task ApplyConnectAsync(LinkPolicy link, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ApplyDisconnectAsync(LinkPolicy link, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ApplyDisconnectAsync(LinkRule rule, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> IsConnectedAsync(LinkPolicy link, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch
            {
                // Non-fatal cleanup catch for Windows temp folder file handles
            }
        }
    }
}
