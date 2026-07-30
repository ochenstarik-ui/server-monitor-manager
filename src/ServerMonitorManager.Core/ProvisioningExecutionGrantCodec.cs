using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace ServerMonitorManager.Core;

public static class ProvisioningExecutionGrantCodec
{
    public const string ProtocolVersion = "1";
    public const string SignatureAlgorithm = "ECDSA-P256-SHA256-P1363";
    public static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(5);

    public static string ComputePlanSha256(SystemBaseInstallPlan plan)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(
            plan, SmmJsonContext.Default.SystemBaseInstallPlan);
        return Convert.ToHexStringLower(SHA256.HashData(json));
    }

    public static byte[] CreateSigningPayload(ProvisioningExecutionGrant grant)
        => Encoding.UTF8.GetBytes(string.Join('\n',
        [
            "SMM-PROVISIONING-GRANT-V1",
            grant.ProtocolVersion,
            grant.JobId,
            grant.NodeId,
            grant.ActionType,
            grant.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            grant.PlanSha256,
            grant.IssuedAtUnixSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            grant.ExpiresAtUnixSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            grant.Nonce,
            grant.SignatureAlgorithm
        ]));

    public static bool Verify(
        ProvisioningExecutionGrant grant,
        X509Certificate2 controlAuthority,
        string expectedJobId,
        string expectedNodeId,
        SystemBaseInstallPlan expectedPlan,
        DateTimeOffset now)
    {
        if (grant.ProtocolVersion != ProtocolVersion
            || grant.SignatureAlgorithm != SignatureAlgorithm
            || grant.JobId != expectedJobId
            || grant.NodeId != expectedNodeId
            || grant.ActionType != "system.base-install"
            || grant.SchemaVersion != 1
            || grant.JobId is not { Length: 32 }
            || !grant.JobId.All(Uri.IsHexDigit)
            || grant.Nonce is not { Length: 32 }
            || !grant.Nonce.All(Uri.IsHexDigit)
            || grant.PlanSha256 is not { Length: 64 }
            || !grant.PlanSha256.All(Uri.IsHexDigit)
            || grant.Signature is not { Length: >= 1 and <= 128 })
        {
            return false;
        }

        DateTimeOffset issuedAt;
        DateTimeOffset expiresAt;
        try
        {
            issuedAt = DateTimeOffset.FromUnixTimeSeconds(grant.IssuedAtUnixSeconds);
            expiresAt = DateTimeOffset.FromUnixTimeSeconds(grant.ExpiresAtUnixSeconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        if (issuedAt > now.AddSeconds(30)
            || expiresAt <= now
            || expiresAt <= issuedAt
            || expiresAt - issuedAt > MaximumLifetime)
        {
            return false;
        }

        var expectedHash = ComputePlanSha256(expectedPlan);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expectedHash),
                Encoding.ASCII.GetBytes(grant.PlanSha256.ToLowerInvariant())))
        {
            return false;
        }

        byte[] signature;
        try
        {
            signature = DecodeBase64Url(grant.Signature);
        }
        catch (FormatException)
        {
            return false;
        }
        if (signature.Length != 64)
        {
            return false;
        }

        using var key = controlAuthority.GetECDsaPublicKey();
        return key is { KeySize: 256 }
            && key.VerifyData(
                CreateSigningPayload(grant), signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public static string EncodeBase64Url(ReadOnlySpan<byte> value)
        => Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] DecodeBase64Url(string value)
    {
        if (value.Length is < 1 or > 128
            || value.Any(character => !char.IsAsciiLetterOrDigit(character)
                && character is not '-' and not '_'))
        {
            throw new FormatException("Invalid base64url value.");
        }
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            0 => string.Empty,
            _ => throw new FormatException("Invalid base64url value.")
        };
        return Convert.FromBase64String(padded);
    }
}
