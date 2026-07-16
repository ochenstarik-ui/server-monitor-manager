using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ServerMonitorManager.Core;
using Windows.Storage;

namespace ServerMonitorManager_Desktop;

public sealed record ControlEnrollmentPreview(
    string DeviceId,
    Uri ControlUrl,
    string Token,
    byte[] CertificateAuthority,
    string Fingerprint);

public sealed partial class ControlClientService
{
    private const string FolderName = "control";
    private const string ProtectedCertificateName = "device.pfx.dpapi";
    private const string CertificateAuthorityName = "control-ca.crt";
    private const string ConfigurationName = "control.conf";

    public bool IsConfigured
    {
        get
        {
            var folder = Path.Combine(ApplicationData.Current.LocalFolder.Path, FolderName);
            return File.Exists(Path.Combine(folder, ConfigurationName))
                   && File.Exists(Path.Combine(folder, ProtectedCertificateName))
                   && File.Exists(Path.Combine(folder, CertificateAuthorityName));
        }
    }

    public ControlEnrollmentPreview ParseEnrollmentCode(string code)
    {
        code = code.Trim().Replace("\r", string.Empty, StringComparison.Ordinal);
        if (!code.StartsWith("SMMDEV1-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Ожидается код формата SMMDEV1-...");
        }

        var encoded = code[8..].Replace('-', '+').Replace('_', '/');
        encoded += (encoded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            0 => string.Empty,
            _ => throw new InvalidOperationException("Некорректный код устройства.")
        };
        string payload;
        try
        {
            payload = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("Не удалось декодировать код устройства.");
        }

        var values = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        if (values.GetValueOrDefault("VERSION") != "1"
            || !DeviceIdRegex().IsMatch(values.GetValueOrDefault("DEVICE", string.Empty))
            || !TokenRegex().IsMatch(values.GetValueOrDefault("TOKEN", string.Empty))
            || !Uri.TryCreate(values.GetValueOrDefault("URL"), UriKind.Absolute, out var controlUrl)
            || controlUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Поля SMMDEV1 не прошли проверку.");
        }

        byte[] certificateAuthority;
        try
        {
            certificateAuthority = Convert.FromBase64String(values["CA"]);
        }
        catch (Exception exception) when (exception is FormatException or KeyNotFoundException)
        {
            throw new InvalidOperationException("В коде отсутствует корректный Control CA.");
        }
        using var certificate = X509CertificateLoader.LoadCertificate(certificateAuthority);
        var now = DateTimeOffset.UtcNow;
        if (now < certificate.NotBefore.ToUniversalTime() || now >= certificate.NotAfter.ToUniversalTime())
        {
            throw new InvalidOperationException("Сертификат Control CA сейчас недействителен.");
        }
        var fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        fingerprint = string.Join(':', Enumerable.Range(0, fingerprint.Length / 2)
            .Select(index => fingerprint.Substring(index * 2, 2)));
        return new ControlEnrollmentPreview(
            values["DEVICE"], controlUrl, values["TOKEN"], certificateAuthority, fingerprint);
    }

    public async Task EnrollAsync(
        ControlEnrollmentPreview preview,
        CancellationToken cancellationToken = default)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var certificateRequest = new CertificateRequest(
            $"CN={preview.DeviceId}", key, HashAlgorithmName.SHA256);
        var request = new DeviceEnrollmentRequest(
            preview.DeviceId,
            preview.Token,
            certificateRequest.CreateSigningRequestPem(),
            Guid.NewGuid().ToString());
        using var client = CreateHttpClient(preview.ControlUrl, preview.CertificateAuthority, null);
        using var response = await client.PostAsJsonAsync(
            "api/v1/device-enroll",
            request,
            SmmJsonContext.Default.DeviceEnrollmentRequest,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var enrollment = await response.Content.ReadFromJsonAsync(
            SmmJsonContext.Default.DeviceEnrollmentResponse,
            cancellationToken) ?? throw new InvalidOperationException("Control Hub вернул пустой ответ регистрации.");
        using var certificate = X509Certificate2.CreateFromPem(
            enrollment.CertificatePem,
            key.ExportPkcs8PrivateKeyPem());
        var pfx = certificate.Export(X509ContentType.Pfx);
        try
        {
            var protectedPfx = ProtectedData.Protect(pfx, null, DataProtectionScope.CurrentUser);
            var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                FolderName, CreationCollisionOption.OpenIfExists);
            await File.WriteAllBytesAsync(
                Path.Combine(folder.Path, ProtectedCertificateName), protectedPfx, cancellationToken);
            await File.WriteAllBytesAsync(
                Path.Combine(folder.Path, CertificateAuthorityName), preview.CertificateAuthority, cancellationToken);
            await File.WriteAllLinesAsync(
                Path.Combine(folder.Path, ConfigurationName),
                [preview.ControlUrl.AbsoluteUri, preview.DeviceId],
                cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    public async Task ListenAsync(
        Func<ControlEvent, Task> onEvent,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var session = await CreateAuthenticatedSessionAsync(cancellationToken);
                if (session is null)
                {
                    return;
                }
                using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/control/events");
                using var response = await session.Client.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken);
                    if (line is null)
                    {
                        break;
                    }
                    var controlEvent = JsonSerializer.Deserialize(line, SmmJsonContext.Default.ControlEvent);
                    if (controlEvent is not null)
                    {
                        await onEvent(controlEvent);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }
        }
    }

    public async Task<IReadOnlyList<AgentSummary>> GetAgentsAsync(CancellationToken cancellationToken)
    {
        using var session = await RequireAuthenticatedSessionAsync(cancellationToken);
        return await session.Client.GetFromJsonAsync(
            "api/v1/control/agents",
            SmmJsonContext.Default.AgentSummaryArray,
            cancellationToken) ?? [];
    }

    public async Task<IReadOnlyList<LinkPolicy>> GetLinksAsync(CancellationToken cancellationToken)
    {
        using var session = await RequireAuthenticatedSessionAsync(cancellationToken);
        return await session.Client.GetFromJsonAsync(
            "api/v1/control/links",
            SmmJsonContext.Default.LinkPolicyArray,
            cancellationToken) ?? [];
    }

    public async Task<LinkPolicy> CreateLinkAsync(
        LinkPolicyCreateRequest request,
        CancellationToken cancellationToken)
    {
        using var session = await RequireAuthenticatedSessionAsync(cancellationToken);
        using var response = await session.Client.PostAsJsonAsync(
            "api/v1/control/links",
            request,
            SmmJsonContext.Default.LinkPolicyCreateRequest,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(
            SmmJsonContext.Default.LinkPolicy,
            cancellationToken) ?? throw new InvalidOperationException("Control Hub вернул пустой Link.");
    }

    public async Task<LinkPolicy> DisableLinkAsync(string id, CancellationToken cancellationToken)
    {
        using var session = await RequireAuthenticatedSessionAsync(cancellationToken);
        using var response = await session.Client.PostAsJsonAsync(
            $"api/v1/control/links/{id}/disable",
            new LinkPolicyDisableRequest(Guid.NewGuid().ToString()),
            SmmJsonContext.Default.LinkPolicyDisableRequest,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(
            SmmJsonContext.Default.LinkPolicy,
            cancellationToken) ?? throw new InvalidOperationException("Control Hub вернул пустой Link.");
    }

    public async Task<CertificateReenrollmentTicket> ReenrollAgentAsync(
        string nodeId,
        string reason,
        CancellationToken cancellationToken)
    {
        using var session = await RequireAuthenticatedSessionAsync(cancellationToken);
        using var response = await session.Client.PostAsJsonAsync(
            $"api/v1/control/agents/{Uri.EscapeDataString(nodeId)}/reenroll",
            new CertificateReenrollmentRequest(reason, Guid.NewGuid().ToString()),
            SmmJsonContext.Default.CertificateReenrollmentRequest,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync(
            SmmJsonContext.Default.CertificateReenrollmentTicket,
            cancellationToken)
            ?? throw new InvalidOperationException("Control Hub вернул пустой token перерегистрации.");
    }

    private static HttpClient CreateHttpClient(
        Uri baseAddress,
        byte[] rootBytes,
        X509Certificate2? clientCertificate)
    {
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
        return new HttpClient(handler) { BaseAddress = baseAddress };
    }

    private static async Task<AuthenticatedControlSession?> CreateAuthenticatedSessionAsync(
        CancellationToken cancellationToken)
    {
        var folder = Path.Combine(ApplicationData.Current.LocalFolder.Path, FolderName);
        var configurationPath = Path.Combine(folder, ConfigurationName);
        var protectedPath = Path.Combine(folder, ProtectedCertificateName);
        var caPath = Path.Combine(folder, CertificateAuthorityName);
        if (!File.Exists(configurationPath) || !File.Exists(protectedPath) || !File.Exists(caPath))
        {
            return null;
        }
        var configuration = await File.ReadAllLinesAsync(configurationPath, cancellationToken);
        if (configuration.Length < 1 || !Uri.TryCreate(configuration[0], UriKind.Absolute, out var controlUrl))
        {
            throw new InvalidOperationException("Сохранённая конфигурация Control Hub повреждена.");
        }
        var protectedPfx = await File.ReadAllBytesAsync(protectedPath, cancellationToken);
        var pfx = ProtectedData.Unprotect(protectedPfx, null, DataProtectionScope.CurrentUser);
        try
        {
            var certificate = X509CertificateLoader.LoadPkcs12(
                pfx, password: null, X509KeyStorageFlags.EphemeralKeySet);
            var ca = await File.ReadAllBytesAsync(caPath, cancellationToken);
            return new AuthenticatedControlSession(
                CreateHttpClient(controlUrl, ca, certificate), certificate);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    private static async Task<AuthenticatedControlSession> RequireAuthenticatedSessionAsync(
        CancellationToken cancellationToken)
        => await CreateAuthenticatedSessionAsync(cancellationToken)
           ?? throw new InvalidOperationException("Сначала подключите Control Hub через код SMMDEV1.");

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceIdRegex();

    [GeneratedRegex("^[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();
}

internal sealed class AuthenticatedControlSession(HttpClient client, X509Certificate2 certificate) : IDisposable
{
    public HttpClient Client { get; } = client;

    public void Dispose()
    {
        Client.Dispose();
        certificate.Dispose();
    }
}
