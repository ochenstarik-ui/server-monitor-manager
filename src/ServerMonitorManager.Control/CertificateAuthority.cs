using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using ServerMonitorManager.Core;

namespace ServerMonitorManager.Control;

public sealed class CertificateAuthority : IDisposable
{
    private readonly X509Certificate2 _issuer;

    public CertificateAuthority(IOptions<ControlOptions> options)
    {
        var value = options.Value;
        _issuer = X509CertificateLoader.LoadPkcs12FromFile(
            value.CertificateAuthorityPath,
            value.CertificateAuthorityPassword,
            X509KeyStorageFlags.EphemeralKeySet);
        if (!_issuer.HasPrivateKey)
        {
            throw new InvalidOperationException("Control CA certificate must contain its private key.");
        }
    }

    public X509Certificate2 PublicCertificate => _issuer;

    public IssuedCertificate IssueClientCertificate(string nodeId, string csrPem)
    {
        var request = CertificateRequest.LoadSigningRequestPem(
            csrPem,
            HashAlgorithmName.SHA256,
            CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
        if (!string.Equals(request.SubjectName.Name, $"CN={nodeId}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("CSR subject does not match the requested node id.");
        }
        if (request.CertificateExtensions.Count != 0)
        {
            throw new InvalidOperationException("CSR extensions are not accepted.");
        }

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.2") },
            true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var serial = RandomNumberGenerator.GetBytes(20);
        var issuerNotBefore = new DateTimeOffset(_issuer.NotBefore.ToUniversalTime(), TimeSpan.Zero);
        var issuerNotAfter = new DateTimeOffset(_issuer.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        var notBefore = DateTimeOffset.UtcNow.AddMinutes(-2) > issuerNotBefore
            ? DateTimeOffset.UtcNow.AddMinutes(-2)
            : issuerNotBefore;
        var requestedNotAfter = DateTimeOffset.UtcNow.AddYears(1);
        var notAfter = requestedNotAfter < issuerNotAfter ? requestedNotAfter : issuerNotAfter;
        if (notAfter <= notBefore)
        {
            throw new InvalidOperationException("Control CA certificate is expired or not yet valid.");
        }
        using var certificate = request.Create(_issuer, notBefore, notAfter, serial);
        return new IssuedCertificate(
            certificate.ExportCertificatePem(),
            _issuer.ExportCertificatePem(),
            certificate.Thumbprint,
            notAfter);
    }

    public ProvisioningExecutionGrant SignProvisioningExecutionGrant(
        ProvisioningJob job,
        SystemBaseInstallPlan plan,
        DateTimeOffset issuedAt,
        TimeSpan lifetime)
    {
        if (job.ActionType != "system.base-install"
            || job.SchemaVersion != 1
            || job.State != ProvisioningJobStates.Queued
            || !job.ConfirmationRequired
            || job.ConfirmedAt is null
            || lifetime <= TimeSpan.Zero
            || lifetime > ProvisioningExecutionGrantCodec.MaximumLifetime)
        {
            throw new InvalidOperationException(
                "Only a confirmed queued base installation job can receive an execution grant.");
        }

        var grant = new ProvisioningExecutionGrant(
            ProvisioningExecutionGrantCodec.ProtocolVersion,
            job.Id,
            job.NodeId,
            job.ActionType,
            job.SchemaVersion,
            ProvisioningExecutionGrantCodec.ComputePlanSha256(plan),
            issuedAt.ToUnixTimeSeconds(),
            issuedAt.Add(lifetime).ToUnixTimeSeconds(),
            Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
            ProvisioningExecutionGrantCodec.SignatureAlgorithm,
            string.Empty);
        using var key = _issuer.GetECDsaPrivateKey();
        if (key is not { KeySize: 256 })
        {
            throw new InvalidOperationException(
                "Control CA must use an ECDSA P-256 key to sign provisioning execution grants.");
        }
        var signature = key.SignData(
            ProvisioningExecutionGrantCodec.CreateSigningPayload(grant),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return grant with { Signature = ProvisioningExecutionGrantCodec.EncodeBase64Url(signature) };
    }

    public void Dispose() => _issuer.Dispose();
}

public sealed record IssuedCertificate(
    string CertificatePem,
    string CertificateAuthorityPem,
    string Thumbprint,
    DateTimeOffset ExpiresAt);
