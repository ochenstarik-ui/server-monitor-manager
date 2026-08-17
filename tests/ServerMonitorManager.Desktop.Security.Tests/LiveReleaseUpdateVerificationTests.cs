using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ServerMonitorManager_Desktop;
using Xunit;

namespace ServerMonitorManager.Desktop.Security.Tests;

[Trait("Category", "LiveRelease")]
public sealed class LiveReleaseUpdateVerificationTests : IAsyncDisposable
{
    private const string DefaultReleaseTag = "v0.1.0-alpha.14";
    private const string Repository = "ochenstarik-ui/server-monitor-manager";
    private readonly string _tempDir;
    private readonly HttpClient _http;
    private readonly string _tag;

    public LiveReleaseUpdateVerificationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"smm-live-release-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("User-Agent", "ServerMonitorManager.Desktop.Tests");
        _tag = Environment.GetEnvironmentVariable("SMM_TEST_RELEASE_TAG") ?? DefaultReleaseTag;
    }

    private async Task<(string ManifestPath, string SigPath, string PemPath)> DownloadReleaseArtifactsAsync()
    {
        var manifestUrl = $"https://github.com/{Repository}/releases/download/{_tag}/server-monitor-manager-manifest.json";
        var sigUrl = $"https://github.com/{Repository}/releases/download/{_tag}/server-monitor-manager-manifest.sig";
        var pemUrl = $"https://github.com/{Repository}/releases/download/{_tag}/server-monitor-manager-manifest.pem";

        var manifestPath = Path.Combine(_tempDir, "server-monitor-manager-manifest.json");
        var sigPath = Path.Combine(_tempDir, "server-monitor-manager-manifest.sig");
        var pemPath = Path.Combine(_tempDir, "server-monitor-manager-manifest.pem");

        var manifestBytes = await _http.GetByteArrayAsync(manifestUrl, TestContext.Current.CancellationToken);
        var sigBytes = await _http.GetByteArrayAsync(sigUrl, TestContext.Current.CancellationToken);
        var pemBytes = await _http.GetByteArrayAsync(pemUrl, TestContext.Current.CancellationToken);

        await File.WriteAllBytesAsync(manifestPath, manifestBytes, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(sigPath, sigBytes, TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(pemPath, pemBytes, TestContext.Current.CancellationToken);

        return (manifestPath, sigPath, pemPath);
    }

    [Fact]
    public async Task Acceptance_RealReleaseManifestAndSignatureAreAccepted()
    {
        var (manifestPath, sigPath, pemPath) = await DownloadReleaseArtifactsAsync();

        var fileStorage = new TestDirectoryFileStorage(_tempDir);
        var httpTransport = new DefaultHttpTransport();
        var verifier = new ProcessSignatureVerifier(fileStorage, httpTransport);

        // 1. ProcessSignatureVerifier directly verifies real release material
        await verifier.VerifySignatureAsync(sigPath, manifestPath, pemPath, TestContext.Current.CancellationToken);

        // 2. Parse and verify manifest content
        var manifestJson = await File.ReadAllTextAsync(manifestPath, TestContext.Current.CancellationToken);
        var node = JsonNode.Parse(manifestJson);
        Assert.NotNull(node);
        Assert.Equal(_tag, node["version"]?.GetValue<string>());

        var msixHash = node["hashes"]?["ServerMonitorManager-win-x64.msix"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(msixHash));
        Assert.Equal(64, msixHash.Length);

        // 3. UpdateService end-to-end against real release material
        var mockHttp = new MockHttpTransport
        {
            GetStringAsyncFunc = url =>
            {
                if (url.EndsWith("releases/latest", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult($@"{{
                        ""tag_name"": ""{_tag}"",
                        ""assets"": [
                            {{ ""name"": ""server-monitor-manager-manifest.json"", ""browser_download_url"": ""https://github.com/{Repository}/releases/download/{_tag}/server-monitor-manager-manifest.json"" }},
                            {{ ""name"": ""server-monitor-manager-manifest.sig"", ""browser_download_url"": ""https://github.com/{Repository}/releases/download/{_tag}/server-monitor-manager-manifest.sig"" }},
                            {{ ""name"": ""server-monitor-manager-manifest.pem"", ""browser_download_url"": ""https://github.com/{Repository}/releases/download/{_tag}/server-monitor-manager-manifest.pem"" }},
                            {{ ""name"": ""ServerMonitorManager-win-x64.msix"", ""browser_download_url"": ""https://github.com/{Repository}/releases/download/{_tag}/ServerMonitorManager-win-x64.msix"" }}
                        ]
                    }}");
                }
                if (url.EndsWith("server-monitor-manager-manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    return File.ReadAllTextAsync(manifestPath, TestContext.Current.CancellationToken);
                }
                if (url.EndsWith("server-monitor-manager-manifest.sig", StringComparison.OrdinalIgnoreCase))
                {
                    return File.ReadAllTextAsync(sigPath, TestContext.Current.CancellationToken);
                }
                if (url.EndsWith("server-monitor-manager-manifest.pem", StringComparison.OrdinalIgnoreCase))
                {
                    return File.ReadAllTextAsync(pemPath, TestContext.Current.CancellationToken);
                }
                throw new InvalidOperationException($"Unexpected URL: {url}");
            }
        };

        var service = new UpdateService(mockHttp, verifier, fileStorage);
        var updateInfo = await service.CheckForUpdatesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(updateInfo);
        Assert.Equal("v999.999.999-intentional-failure", updateInfo.Version);
        Assert.Equal(msixHash, updateInfo.ExpectedHash);
        Assert.Contains(_tag, updateInfo.DownloadUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Negative1_TamperedHashInRealManifestIsRejected()
    {
        var (manifestPath, sigPath, pemPath) = await DownloadReleaseArtifactsAsync();

        // Alter the manifest content by tampering with the hash
        var originalManifest = await File.ReadAllTextAsync(manifestPath, TestContext.Current.CancellationToken);
        var tamperedManifest = originalManifest.Replace(
            "710813668ac5efacc245472afd4da6dca6739c042b5189bd12c2bbbd3c6b7e19",
            "0000000000000000000000000000000000000000000000000000000000000000",
            StringComparison.Ordinal);

        var tamperedManifestPath = Path.Combine(_tempDir, "tampered-manifest.json");
        await File.WriteAllTextAsync(tamperedManifestPath, tamperedManifest, TestContext.Current.CancellationToken);

        var fileStorage = new TestDirectoryFileStorage(_tempDir);
        var httpTransport = new DefaultHttpTransport();
        var verifier = new ProcessSignatureVerifier(fileStorage, httpTransport);

        // Verification of tampered manifest with real signature must fail
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            verifier.VerifySignatureAsync(sigPath, tamperedManifestPath, pemPath, TestContext.Current.CancellationToken));
        Assert.Contains("Signature verification failed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Negative2_RealManifestWithSignatureFromDifferentIdentityIsRejected()
    {
        var (manifestPath, _, pemPath) = await DownloadReleaseArtifactsAsync();

        // Create a fake signature signed by a different key
        using var rsa = RSA.Create();
        var fakeSigBytes = rsa.SignData(
            await File.ReadAllBytesAsync(manifestPath, TestContext.Current.CancellationToken),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var fakeSigPath = Path.Combine(_tempDir, "fake-identity.sig");
        await File.WriteAllTextAsync(fakeSigPath, Convert.ToBase64String(fakeSigBytes), TestContext.Current.CancellationToken);

        var fileStorage = new TestDirectoryFileStorage(_tempDir);
        var httpTransport = new DefaultHttpTransport();
        var verifier = new ProcessSignatureVerifier(fileStorage, httpTransport);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            verifier.VerifySignatureAsync(fakeSigPath, manifestPath, pemPath, TestContext.Current.CancellationToken));
        Assert.Contains("Signature verification failed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Negative3_MissingCertificateIsRejected()
    {
        var (manifestPath, sigPath, _) = await DownloadReleaseArtifactsAsync();
        var nonExistentPemPath = Path.Combine(_tempDir, "non-existent-certificate.pem");

        var fileStorage = new TestDirectoryFileStorage(_tempDir);
        var httpTransport = new DefaultHttpTransport();
        var verifier = new ProcessSignatureVerifier(fileStorage, httpTransport);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            verifier.VerifySignatureAsync(sigPath, manifestPath, nonExistentPemPath, TestContext.Current.CancellationToken));
        Assert.Contains("Signature verification failed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Negative4_CertificateFromDifferentWorkflowIsRejected()
    {
        var (manifestPath, sigPath, _) = await DownloadReleaseArtifactsAsync();

        // Create a custom self-signed certificate with a different subject/workflow identity
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=Untrusted Workflow Fake Cert",
            ecdsa,
            HashAlgorithmName.SHA256);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var fakePemPath = Path.Combine(_tempDir, "fake-workflow-cert.pem");
        await File.WriteAllTextAsync(fakePemPath, cert.ExportCertificatePem(), TestContext.Current.CancellationToken);

        var fileStorage = new TestDirectoryFileStorage(_tempDir);
        var httpTransport = new DefaultHttpTransport();
        var verifier = new ProcessSignatureVerifier(fileStorage, httpTransport);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            verifier.VerifySignatureAsync(sigPath, manifestPath, fakePemPath, TestContext.Current.CancellationToken));
        Assert.Contains("Signature verification failed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
        await Task.CompletedTask;
    }

    private sealed class TestDirectoryFileStorage(string directory) : IFileStorage
    {
        public string GetTempFolder() => directory;
        public bool FileExists(string path) => File.Exists(path);
        public Task WriteAllBytesAsync(string path, byte[] bytes, System.Threading.CancellationToken cancellationToken = default) =>
            File.WriteAllBytesAsync(path, bytes, cancellationToken);
        public Task WriteAllTextAsync(string path, string text, System.Threading.CancellationToken cancellationToken = default) =>
            File.WriteAllTextAsync(path, text, cancellationToken);
        public Stream OpenRead(string path) => File.OpenRead(path);
        public void LaunchFile(string path) { }
    }
}
