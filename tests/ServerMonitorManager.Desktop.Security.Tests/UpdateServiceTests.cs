using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using ServerMonitorManager_Desktop;

namespace ServerMonitorManager.Desktop.Security.Tests
{
    public class MockHttpTransport : IHttpTransport
    {
        public Func<string, Task<string>> GetStringAsyncFunc { get; set; } = _ => Task.FromResult("");
        public Func<string, Task<byte[]>> GetByteArrayAsyncFunc { get; set; } = _ => Task.FromResult(Array.Empty<byte>());
        public Func<string, string, Action<double>?, Task> DownloadFileAsyncFunc { get; set; } = (_, _, _) => Task.CompletedTask;

        public Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default) => GetStringAsyncFunc(url);
        public Task<byte[]> GetByteArrayAsync(string url, CancellationToken cancellationToken = default) => GetByteArrayAsyncFunc(url);
        public Task DownloadFileAsync(string url, string destinationPath, Action<double>? progressCallback = null, CancellationToken cancellationToken = default) => DownloadFileAsyncFunc(url, destinationPath, progressCallback);
    }

    public class MockSignatureVerifier : ISignatureVerifier
    {
        public Func<string, string, Task> VerifySignatureAsyncFunc { get; set; } = (_, _) => Task.CompletedTask;
        public Task VerifySignatureAsync(string signaturePath, string manifestPath, CancellationToken cancellationToken = default) => VerifySignatureAsyncFunc(signaturePath, manifestPath);
    }

    public class MockFileStorage : IFileStorage
    {
        public string GetTempFolder() => Path.GetTempPath();
        public bool FileExists(string path) => true;
        public Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task WriteAllTextAsync(string path, string text, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Func<string, Stream> OpenReadFunc { get; set; } = _ => new MemoryStream();
        public Stream OpenRead(string path) => OpenReadFunc(path);
        public Action<string> LaunchFileFunc { get; set; } = _ => { };
        public void LaunchFile(string path) => LaunchFileFunc(path);
    }

    public class UpdateServiceTests
    {
        private const string ValidReleaseJson = @"
        {
            ""tag_name"": ""v0.1.0-alpha.9"",
            ""assets"": [
                { ""name"": ""server-monitor-manager-manifest.json"", ""browser_download_url"": ""https://example.com/releases/download/v0.1.0-alpha.9/server-monitor-manager-manifest.json"" },
                { ""name"": ""server-monitor-manager-manifest.sig"", ""browser_download_url"": ""https://example.com/releases/download/v0.1.0-alpha.9/server-monitor-manager-manifest.sig"" },
                { ""name"": ""ServerMonitorManager-win-x64.msix"", ""browser_download_url"": ""https://example.com/releases/download/v0.1.0-alpha.9/ServerMonitorManager-win-x64.msix"" }
            ]
        }";

        // Release JSON without the .sig asset — used by Test4
        private const string NoSigReleaseJson = @"
        {
            ""tag_name"": ""v0.1.0-alpha.9"",
            ""assets"": [
                { ""name"": ""server-monitor-manager-manifest.json"", ""browser_download_url"": ""https://example.com/releases/download/v0.1.0-alpha.9/server-monitor-manager-manifest.json"" },
                { ""name"": ""ServerMonitorManager-win-x64.msix"", ""browser_download_url"": ""https://example.com/releases/download/v0.1.0-alpha.9/ServerMonitorManager-win-x64.msix"" }
            ]
        }";

        private const string ValidManifestJson = @"
        {
            ""version"": ""v0.1.0-alpha.9"",
            ""hashes"": {
                ""ServerMonitorManager-win-x64.msix"": ""e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855""
            }
        }"; // Hash for empty string

        [Fact]
        public async Task Test1_ValidSignatureAndHash_Accepted()
        {
            var http = new MockHttpTransport
            {
                GetStringAsyncFunc = url =>
                {
                    if (url.EndsWith("releases/latest")) return Task.FromResult(ValidReleaseJson);
                    if (url.EndsWith(".json")) return Task.FromResult(ValidManifestJson);
                    if (url.EndsWith(".sig")) return Task.FromResult("valid-sig");
                    return Task.FromResult("");
                }
            };
            var verifier = new MockSignatureVerifier();
            var storage = new MockFileStorage
            {
                OpenReadFunc = _ => new MemoryStream(Array.Empty<byte>()) // Hash of empty matches e3b0c4...
            };

            var service = new UpdateService(http, verifier, storage);
            var update = await service.CheckForUpdatesAsync();

            Assert.NotNull(update);
            Assert.Equal("v0.1.0-alpha.9", update.Version);

            await service.DownloadAndVerifyUpdateAsync(update);
        }

        [Fact]
        public async Task Test2_ManifestWithWrongIdentity_Rejected()
        {
            var http = new MockHttpTransport { GetStringAsyncFunc = url => Task.FromResult(url.EndsWith("releases/latest") ? ValidReleaseJson : ValidManifestJson) };
            var verifier = new MockSignatureVerifier
            {
                VerifySignatureAsyncFunc = (_, _) => throw new InvalidOperationException("Signature verification failed: identity mismatch")
            };
            var storage = new MockFileStorage();

            var service = new UpdateService(http, verifier, storage);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckForUpdatesAsync());
            Assert.Contains("identity mismatch", ex.Message);
        }

        [Fact]
        public async Task Test3_ManifestHashMismatch_Rejected()
        {
            var http = new MockHttpTransport
            {
                GetStringAsyncFunc = url =>
                {
                    if (url.EndsWith("releases/latest")) return Task.FromResult(ValidReleaseJson);
                    if (url.EndsWith(".json")) return Task.FromResult(ValidManifestJson);
                    if (url.EndsWith(".sig")) return Task.FromResult("valid-sig");
                    return Task.FromResult("");
                }
            };
            var verifier = new MockSignatureVerifier();
            var storage = new MockFileStorage
            {
                OpenReadFunc = _ => new MemoryStream(Encoding.UTF8.GetBytes("wrong content"))
            };

            var service = new UpdateService(http, verifier, storage);
            var update = await service.CheckForUpdatesAsync();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAndVerifyUpdateAsync(update));
            Assert.Contains("hash mismatch", ex.Message);
        }

        [Fact]
        public async Task Test4_MissingSignature_Rejected()
        {
            var http = new MockHttpTransport { GetStringAsyncFunc = _ => Task.FromResult(NoSigReleaseJson) };
            var service = new UpdateService(http, new MockSignatureVerifier(), new MockFileStorage());

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckForUpdatesAsync());
            Assert.Contains("Manifest or signature not found", ex.Message);
        }

        [Fact]
        public async Task Test5_InvalidSignature_Rejected()
        {
            var http = new MockHttpTransport { GetStringAsyncFunc = url => Task.FromResult(url.EndsWith("releases/latest") ? ValidReleaseJson : ValidManifestJson) };
            var verifier = new MockSignatureVerifier
            {
                VerifySignatureAsyncFunc = (_, _) => throw new InvalidOperationException("Signature verification failed: invalid signature format")
            };
            var storage = new MockFileStorage();

            var service = new UpdateService(http, verifier, storage);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckForUpdatesAsync());
            Assert.Contains("invalid signature format", ex.Message);
        }

        [Fact]
        public async Task Test6_UpdateActionNotShownUntilVerified()
        {
            var http = new MockHttpTransport { GetStringAsyncFunc = url => Task.FromResult(url.EndsWith("releases/latest") ? ValidReleaseJson : ValidManifestJson) };
            bool signatureVerified = false;
            var verifier = new MockSignatureVerifier
            {
                VerifySignatureAsyncFunc = (_, _) => { signatureVerified = true; return Task.CompletedTask; }
            };
            var storage = new MockFileStorage();

            var service = new UpdateService(http, verifier, storage);
            var update = await service.CheckForUpdatesAsync();
            
            // CheckForUpdatesAsync returns the update ONLY AFTER signature is verified
            Assert.True(signatureVerified);
            Assert.NotNull(update);
        }

        [Fact]
        public async Task Test7_PreReleaseChannel_Used()
        {
            var preReleaseJson = "[" + ValidReleaseJson.Replace("v0.1.0-alpha.9", "v0.1.0-alpha.10") + "]";
            var preReleaseManifest = ValidManifestJson.Replace("v0.1.0-alpha.9", "v0.1.0-alpha.10");
            
            var http = new MockHttpTransport
            {
                GetStringAsyncFunc = url =>
                {
                    if (url.EndsWith("releases")) return Task.FromResult(preReleaseJson); // Note: releases array
                    if (url.EndsWith(".json")) return Task.FromResult(preReleaseManifest);
                    if (url.EndsWith(".sig")) return Task.FromResult("valid-sig");
                    return Task.FromResult("");
                }
            };
            var service = new UpdateService(http, new MockSignatureVerifier(), new MockFileStorage());

            var update = await service.CheckForUpdatesAsync(usePreRelease: true);
            Assert.Equal("v0.1.0-alpha.10", update!.Version);
        }

        [Fact]
        public async Task Test8_UrlAssetFromAnotherTag_Rejected()
        {
            // Msix URL points to a different tag
            var maliciousRelease = ValidReleaseJson.Replace("v0.1.0-alpha.9/ServerMonitorManager-win-x64.msix", "v0.1.0-alpha.8/ServerMonitorManager-win-x64.msix");
            var http = new MockHttpTransport { GetStringAsyncFunc = url => Task.FromResult(url.EndsWith("releases/latest") ? maliciousRelease : ValidManifestJson) };
            
            var service = new UpdateService(http, new MockSignatureVerifier(), new MockFileStorage());

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckForUpdatesAsync());
            Assert.Contains("does not match the release tag", ex.Message);
        }

        [Fact]
        public async Task Test9_ManifestVersionMismatch_Rejected()
        {
            // Manifest says v0.1.0-alpha.8 but release tag says v0.1.0-alpha.9
            var mismatchedManifest = ValidManifestJson.Replace("v0.1.0-alpha.9", "v0.1.0-alpha.8");
            var http = new MockHttpTransport
            {
                GetStringAsyncFunc = url =>
                {
                    if (url.EndsWith("releases/latest")) return Task.FromResult(ValidReleaseJson);
                    if (url.EndsWith(".json")) return Task.FromResult(mismatchedManifest);
                    if (url.EndsWith(".sig")) return Task.FromResult("valid-sig");
                    return Task.FromResult("");
                }
            };
            var service = new UpdateService(http, new MockSignatureVerifier(), new MockFileStorage());

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckForUpdatesAsync());
            Assert.Contains("version", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Test10_MissingMsixAsset_Rejected()
        {
            // Release JSON without the MSIX asset
            var noMsixRelease = @"
            {
                ""tag_name"": ""v0.1.0-alpha.9"",
                ""assets"": [
                    { ""name"": ""server-monitor-manager-manifest.json"", ""browser_download_url"": ""https://example.com/releases/download/v0.1.0-alpha.9/server-monitor-manager-manifest.json"" },
                    { ""name"": ""server-monitor-manager-manifest.sig"", ""browser_download_url"": ""https://example.com/releases/download/v0.1.0-alpha.9/server-monitor-manager-manifest.sig"" }
                ]
            }";
            var http = new MockHttpTransport
            {
                GetStringAsyncFunc = url =>
                {
                    if (url.EndsWith("releases/latest")) return Task.FromResult(noMsixRelease);
                    if (url.EndsWith(".json")) return Task.FromResult(ValidManifestJson);
                    if (url.EndsWith(".sig")) return Task.FromResult("valid-sig");
                    return Task.FromResult("");
                }
            };
            var service = new UpdateService(http, new MockSignatureVerifier(), new MockFileStorage());

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckForUpdatesAsync());
            Assert.Contains("MSIX", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }
}
