using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace ServerMonitorManager_Desktop;

public interface IHttpTransport
{
    Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default);
    Task<byte[]> GetByteArrayAsync(string url, CancellationToken cancellationToken = default);
    Task DownloadFileAsync(string url, string destinationPath, Action<double>? progressCallback = null, CancellationToken cancellationToken = default);
}

public interface ISignatureVerifier
{
    Task VerifySignatureAsync(string signaturePath, string manifestPath, CancellationToken cancellationToken = default);
}

public interface IFileStorage
{
    string GetTempFolder();
    bool FileExists(string path);
    Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken = default);
    Task WriteAllTextAsync(string path, string text, CancellationToken cancellationToken = default);
    Stream OpenRead(string path);
    void LaunchFile(string path);
}

public class DefaultHttpTransport : IHttpTransport
{
    private readonly HttpClient _http = new();

    public DefaultHttpTransport()
    {
        _http.DefaultRequestHeaders.Add("User-Agent", "ServerMonitorManager.Desktop");
    }

    public Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default) => _http.GetStringAsync(url, cancellationToken);
    public Task<byte[]> GetByteArrayAsync(string url, CancellationToken cancellationToken = default) => _http.GetByteArrayAsync(url, cancellationToken);
    
    public async Task DownloadFileAsync(string url, string destinationPath, Action<double>? progressCallback = null, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

        var buffer = new byte[8192];
        var totalRead = 0L;
        int bytesRead;
        while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            totalRead += bytesRead;
            if (totalBytes != -1)
            {
                progressCallback?.Invoke((double)totalRead / totalBytes * 100);
            }
        }
    }
}

public class ProcessSignatureVerifier : ISignatureVerifier
{
    private readonly IFileStorage _fileStorage;
    private readonly IHttpTransport _httpTransport;
    
    private const string CosignVersion = "v2.4.0";
    private const string CosignUrl = $"https://github.com/sigstore/cosign/releases/download/{CosignVersion}/cosign-windows-amd64.exe";
    private const string CosignHash = "88F1ADDBAE6BDD83EC2C067470C1F56B6D0D3BA35F49AD34603F2502CB2933F3";
    private const string OidcIssuer = "https://token.actions.githubusercontent.com";
    private const string OidcIdentityRegexp = "^https://github.com/ochenstarik-ui/server-monitor-manager/\\.github/workflows/linux-release\\.yml@refs/tags/v.*$";

    public ProcessSignatureVerifier(IFileStorage fileStorage, IHttpTransport httpTransport)
    {
        _fileStorage = fileStorage;
        _httpTransport = httpTransport;
    }

    public async Task VerifySignatureAsync(string signaturePath, string manifestPath, CancellationToken cancellationToken = default)
    {
        var cosignPath = Path.Combine(_fileStorage.GetTempFolder(), "cosign.exe");
        if (!_fileStorage.FileExists(cosignPath) || !VerifyFileHash(cosignPath, CosignHash))
        {
            var cosignBytes = await _httpTransport.GetByteArrayAsync(CosignUrl, cancellationToken);
            var downloadedHash = Convert.ToHexString(SHA256.HashData(cosignBytes));
            if (!string.Equals(downloadedHash, CosignHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Cosign binary hash mismatch. Update rejected.");
            }
            await _fileStorage.WriteAllBytesAsync(cosignPath, cosignBytes, cancellationToken);
        }

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = cosignPath,
                Arguments = $"verify-blob --certificate-oidc-issuer \"{OidcIssuer}\" --certificate-identity-regexp \"{OidcIdentityRegexp}\" --signature \"{signaturePath}\" \"{manifestPath}\"",
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
    }

    private bool VerifyFileHash(string filePath, string expectedHash)
    {
        try
        {
            using var stream = _fileStorage.OpenRead(filePath);
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

public class DefaultFileStorage : IFileStorage
{
    public string GetTempFolder() => ApplicationData.Current.TemporaryFolder.Path;
    public bool FileExists(string path) => File.Exists(path);
    public Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken = default) => File.WriteAllBytesAsync(path, bytes, cancellationToken);
    public Task WriteAllTextAsync(string path, string text, CancellationToken cancellationToken = default) => File.WriteAllTextAsync(path, text, cancellationToken);
    public Stream OpenRead(string path) => File.OpenRead(path);
    public void LaunchFile(string path)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            }
        };
        process.Start();
    }
}

public class UpdateService
{
    private const string Repository = "ochenstarik-ui/server-monitor-manager";
    
    private readonly IHttpTransport _http;
    private readonly ISignatureVerifier _signatureVerifier;
    private readonly IFileStorage _fileStorage;

    public UpdateService(IHttpTransport http, ISignatureVerifier signatureVerifier, IFileStorage fileStorage)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _signatureVerifier = signatureVerifier ?? throw new ArgumentNullException(nameof(signatureVerifier));
        _fileStorage = fileStorage ?? throw new ArgumentNullException(nameof(fileStorage));
    }

    public async Task<UpdateInfo?> CheckForUpdatesAsync(bool usePreRelease = false, CancellationToken cancellationToken = default)
    {
        // 1. Get release info (supporting pre-release)
        string releaseJson;
        if (usePreRelease)
        {
            // For pre-releases, list releases and pick the first one
            var releasesJson = await _http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases", cancellationToken);
            var releasesArray = JsonNode.Parse(releasesJson)?.AsArray();
            if (releasesArray is null || releasesArray.Count == 0)
                throw new InvalidOperationException("No releases found.");
            
            // Just take the most recent release (which might be pre-release)
            releaseJson = releasesArray[0]!.ToJsonString();
        }
        else
        {
            releaseJson = await _http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest", cancellationToken);
        }

        var releaseNode = JsonNode.Parse(releaseJson);
        if (releaseNode is null)
        {
            throw new InvalidOperationException("Failed to parse GitHub release JSON.");
        }

        var releaseTagName = releaseNode["tag_name"]?.GetValue<string>();

        var assets = releaseNode["assets"]?.AsArray();
        if (assets is null)
        {
            throw new InvalidOperationException("No assets found in the release.");
        }

        var manifestUrl = assets.FirstOrDefault(a => a?["name"]?.GetValue<string>() == "server-monitor-manager-manifest.json")?["browser_download_url"]?.GetValue<string>();
        var sigUrl = assets.FirstOrDefault(a => a?["name"]?.GetValue<string>() == "server-monitor-manager-manifest.sig")?["browser_download_url"]?.GetValue<string>();
        
        if (manifestUrl is null || sigUrl is null)
        {
            throw new InvalidOperationException("Manifest or signature not found in the release. Update rejected.");
        }

        // Validate URLs belong to the same tag!
        if (!manifestUrl.Contains($"/releases/download/{releaseTagName}/") || !sigUrl.Contains($"/releases/download/{releaseTagName}/"))
        {
             throw new InvalidOperationException("Manifest or signature URL does not match the release tag. Update rejected.");
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

        // 3. Verify manifest signature BEFORE showing update action
        var tempFolder = _fileStorage.GetTempFolder();
        var manifestPath = Path.Combine(tempFolder, "server-monitor-manager-manifest.json");
        var sigPath = Path.Combine(tempFolder, "server-monitor-manager-manifest.sig");
        await _fileStorage.WriteAllTextAsync(manifestPath, manifestJson, cancellationToken);
        await _fileStorage.WriteAllTextAsync(sigPath, manifestSig, cancellationToken);

        await _signatureVerifier.VerifySignatureAsync(sigPath, manifestPath, cancellationToken);
        
        var msixUrl = assets.FirstOrDefault(a => a?["name"]?.GetValue<string>() == "ServerMonitorManager-win-x64.msix")?["browser_download_url"]?.GetValue<string>();
        if (msixUrl is null)
        {
            throw new InvalidOperationException("MSIX asset not found in the latest release.");
        }
        
        if (!msixUrl.Contains($"/releases/download/{releaseTagName}/"))
        {
             throw new InvalidOperationException("MSIX URL does not match the release tag. Update rejected.");
        }

        return new UpdateInfo(version ?? "Unknown", msixUrl, msixHash);
    }
    
    public async Task DownloadAndVerifyUpdateAsync(UpdateInfo updateInfo, Action<double>? progressCallback = null, CancellationToken cancellationToken = default)
    {
        if (updateInfo == null) throw new ArgumentNullException(nameof(updateInfo));
        
        var tempFolder = _fileStorage.GetTempFolder();
        var msixPath = Path.Combine(tempFolder, "ServerMonitorManager-win-x64.msix");

        await _http.DownloadFileAsync(updateInfo.DownloadUrl, msixPath, progressCallback, cancellationToken);
        
        if (!VerifyFileHash(msixPath, updateInfo.ExpectedHash))
        {
            throw new InvalidOperationException("Update MSIX hash mismatch. Update rejected.");
        }
    }
    
    public void InstallUpdate()
    {
        var tempFolder = _fileStorage.GetTempFolder();
        var msixPath = Path.Combine(tempFolder, "ServerMonitorManager-win-x64.msix");
        if (!_fileStorage.FileExists(msixPath))
        {
            throw new InvalidOperationException("Downloaded update file not found.");
        }
        _fileStorage.LaunchFile(msixPath);
    }

    private bool VerifyFileHash(string filePath, string expectedHash)
    {
        try
        {
            using var stream = _fileStorage.OpenRead(filePath);
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
