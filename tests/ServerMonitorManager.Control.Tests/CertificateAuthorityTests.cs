using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using ServerMonitorManager.Control;
using Xunit;

namespace ServerMonitorManager.Control.Tests;

public sealed class CertificateAuthorityTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"smm-ca-tests-{Guid.NewGuid():N}");

    [Fact]
    public void IssuedAgentCertificateChainsToControlAuthority()
    {
        Directory.CreateDirectory(_directory);
        var caPath = Path.Combine(_directory, "control-ca.pfx");
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest("CN=SMM Test CA", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign,
            true));
        using var ca = caRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddYears(2));
        File.WriteAllBytes(caPath, ca.Export(X509ContentType.Pfx));

        using var authority = new CertificateAuthority(Options.Create(new ControlOptions
        {
            DatabasePath = Path.Combine(_directory, "unused.db"),
            CertificateAuthorityPath = caPath
        }));
        using var agentKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var agentRequest = new CertificateRequest("CN=home", agentKey, HashAlgorithmName.SHA256);
        var issued = authority.IssueClientCertificate("home", agentRequest.CreateSigningRequestPem());
        using var agentCertificate = X509Certificate2.CreateFromPem(
            issued.CertificatePem,
            agentKey.ExportPkcs8PrivateKeyPem());
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority.PublicCertificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        Assert.True(chain.Build(agentCertificate));
        Assert.Contains(
            agentCertificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
                .SelectMany(extension => extension.EnhancedKeyUsages.Cast<Oid>()),
            oid => oid.Value == "1.3.6.1.5.5.7.3.2");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
