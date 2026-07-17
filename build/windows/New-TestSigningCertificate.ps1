param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [Parameter(Mandatory = $true)]
    [string]$Password
)

$ErrorActionPreference = 'Stop'
$output = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$securePassword = ConvertTo-SecureString -String $Password -AsPlainText -Force
$certificate = New-SelfSignedCertificate `
    -Type Custom `
    -Subject 'CN=AppPublisher' `
    -FriendlyName 'Server Monitor Manager test signing' `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -KeyAlgorithm RSA `
    -KeyLength 3072 `
    -HashAlgorithm SHA256 `
    -KeyExportPolicy Exportable `
    -KeyUsage DigitalSignature `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3') `
    -NotAfter (Get-Date).AddYears(1)

try {
    $pfxPath = Join-Path $output 'server-monitor-manager-test-signing.pfx'
    $cerPath = Join-Path $output 'server-monitor-manager-test-signing.cer'
    Export-PfxCertificate -Cert $certificate -FilePath $pfxPath -Password $securePassword | Out-Null
    Export-Certificate -Cert $certificate -FilePath $cerPath | Out-Null
    Write-Output $pfxPath
    Write-Output $cerPath
}
finally {
    Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($certificate.Thumbprint)" -Force
}
