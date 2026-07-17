# Windows installer

Server Monitor Manager uses a packaged MSIX deployment model. The release workflow builds an x64 MSIX, verifies that it has a signing certificate, and publishes `SHA256SUMS` beside it.

## Test-signed builds

When trusted signing secrets are not configured, CI creates a temporary self-signed certificate and publishes its public `.cer` file with the MSIX. This is intended only for testing. Open PowerShell as administrator, then run:

```powershell
.\build\windows\Install-TestPackage.ps1 `
  -PackagePath .\ServerMonitorManager-win-x64.msix `
  -CertificatePath .\ServerMonitorManager-test-signing.cer
```

The private test key is never uploaded. Each CI run creates a new certificate, so it is not a replacement for a stable publisher certificate.

## Trusted release signing

Configure both repository secrets before creating a public release:

- `WINDOWS_SIGNING_CERTIFICATE_BASE64`: base64-encoded PFX whose subject matches `CN=AppPublisher`;
- `WINDOWS_SIGNING_CERTIFICATE_PASSWORD`: PFX password.

For a publicly trusted installer, the PFX must come from a suitable code-signing provider. When these secrets exist, the workflow uses that PFX and does not publish a test `.cer`.

Verify the downloaded package before installation:

```powershell
$expected = ((Get-Content .\SHA256SUMS) -split ' ')[0]
$actual = (Get-FileHash .\ServerMonitorManager-win-x64.msix -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw 'Checksum mismatch' }
```
