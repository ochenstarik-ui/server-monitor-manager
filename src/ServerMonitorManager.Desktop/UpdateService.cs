using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Windows.Storage;

namespace ServerMonitorManager_Desktop;

public class UpdateService
{
    private const string CosignVersion = "v2.4.0";
    private const string CosignUrl = $"https://github.com/sigstore/cosign/releases/download/{CosignVersion}/cosign-windows-amd64.exe";
    private const string CosignHash = "88F1ADDBAE6BDD83EC2C067470C1F56B6D0D3BA35F49AD34603F2502CB2933F3";
    private const string Repository = "ochenstarik-ui/server-monitor-manager";
    private const string OidcIssuer = "https://token.actions.githubusercontent.com";
    private const string OidcIdentityRegexp = "^https://github.com/ochenstarik-ui/server-monitor-manager/\\.github/workflows/linux-release\\.yml@refs/tags/v.*$";
    
    private readonly HttpClient _http = new();

    public UpdateService()
    {
        _http.DefaultRequestHeaders.Add("User-Agent", "ServerMonitorManager.Desktop");
    }

    public async Task<UpdateInfo?> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        // 1. Get latest release
        var releaseJson = await _http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest", cancellationToken);
        var releaseNode = JsonNode.Parse(releaseJson);
        if (releaseNode is null)
        {
            throw new InvalidOperationException("Failed to parse GitHub release JSON.");
        }

        var assets = releaseNode["assets"]?.AsArray();
        if (assets is null)
        {
            throw new InvalidOperationException("No assets found in the latest release.");
        }

        var manifestUrl = assets.FirstOrDefault(a => a?["name"]?.GetValue<string>() == "server-monitor-manager-manifest.json")?["browser_download_url"]?.GetValue<string>();
        var sigUrl = assets.FirstOrDefault(a => a?["name"]?.GetValue<string>() == "server-monitor-manager-manifest.sig")?["browser_download_url"]?.GetValue<string>();
        
        if (manifestUrl is null || sigUrl is null)
        {
            throw new InvalidOperationException("Manifest or signature not found in the latest release. Update rejected.");
        }

        var manifestJson = await _http.GetStringAsync(manifestUrl, cancellationToken);
        var manifestSig = await _http.GetStringAsync(sigUrl, cancellationToken);

        var manifestNode = JsonNode.Parse(manifestJson);
        if (manifestNode is null)
        {
            throw new InvalidOperationException("Failed to parse manifest JSON.");
        }

        var msixHash = manifestNode["hashes"]?["ServerMonitorManager-win-x64.msix"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(msixHash))
        {
            throw new InvalidOperationException("MSIX hash not found in manifest.");
        }
        
        var version = manifestNode["version"]?.GetValue<string>();

        // 2. Download and verify cosign
        var tempFolder = ApplicationData.Current.TemporaryFolder.Path;
        var cosignPath = Path.Combine(tempFolder, "cosign.exe");
        if (!File.Exists(cosignPath) || !VerifyFileHash(cosignPath, CosignHash))
        {
            var cosignBytes = await _http.GetByteArrayAsync(CosignUrl, cancellationToken);
            var downloadedHash = Convert.ToHexString(SHA256.HashData(cosignBytes));
            if (!string.Equals(downloadedHash, CosignHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Cosign binary hash mismatch. Update rejected.");
            }
            await File.WriteAllBytesAsync(cosignPath, cosignBytes, cancellationToken);
        }

        // 3. Verify manifest signature
        var manifestPath = Path.Combine(tempFolder, "server-monitor-manager-manifest.json");
        var sigPath = Path.Combine(tempFolder, "server-monitor-manager-manifest.sig");
        await File.WriteAllTextAsync(manifestPath, manifestJson, cancellationToken);
        await File.WriteAllTextAsync(sigPath, manifestSig, cancellationToken);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = cosignPath,
                Arguments = $"verify-blob --certificate-oidc-issuer \"{OidcIssuer}\" --certificate-identity-regexp \"{OidcIdentityRegexp}\" --signature \"{sigPath}\" \"{manifestPath}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            }
        };
        
        process.Start();
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            throw new InvalidOperationException($"Signature verification failed: {error}");
        }
        
        var msixUrl = assets.FirstOrDefault(a => a?["name"]?.GetValue<string>() == "ServerMonitorManager-win-x64.msix")?["browser_download_url"]?.GetValue<string>();
        if (msixUrl is null)
        {
            throw new InvalidOperationException("MSIX asset not found in the latest release.");
        }

        return new UpdateInfo(version ?? "Unknown", msixUrl, msixHash);
    }
    
    public async Task DownloadAndInstallUpdateAsync(UpdateInfo updateInfo, Action<double>? progressCallback = null, CancellationToken cancellationToken = default)
    {
        var tempFolder = ApplicationData.Current.TemporaryFolder.Path;
        var msixPath = Path.Combine(tempFolder, "ServerMonitorManager-win-x64.msix");

        using var response = await _http.GetAsync(updateInfo.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var fileStream = new FileStream(msixPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[8192];
        var totalRead = 0L;
        var bytesRead = 0;
        while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalRead += bytesRead;
            if (totalBytes != -1)
            {
                progressCallback?.Invoke((double)totalRead / totalBytes * 100);
            }
        }
        
        fileStream.Close();
        
        if (!VerifyFileHash(msixPath, updateInfo.ExpectedHash))
        {
            throw new InvalidOperationException("Update MSIX hash mismatch. Update rejected.");
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = msixPath,
                UseShellExecute = true
            }
        };
        process.Start();
    }

    private static bool VerifyFileHash(string filePath, string expectedHash)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            var hashBytes = SHA256.HashData(stream);
            var hashHex = Convert.ToHexString(hashBytes);
            return string.Equals(hashHex, expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

public record UpdateInfo(string Version, string DownloadUrl, string ExpectedHash);
