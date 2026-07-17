param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [Parameter(Mandatory = $true)]
    [string]$CertificatePath
)

$ErrorActionPreference = 'Stop'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this test-certificate installer from an elevated PowerShell window (Run as administrator).'
}
$package = [System.IO.Path]::GetFullPath($PackagePath)
$certificate = [System.IO.Path]::GetFullPath($CertificatePath)
if (-not (Test-Path -LiteralPath $package -PathType Leaf)) { throw "Package not found: $package" }
if (-not (Test-Path -LiteralPath $certificate -PathType Leaf)) { throw "Certificate not found: $certificate" }

$trustedRoot = Import-Certificate -FilePath $certificate -CertStoreLocation 'Cert:\LocalMachine\Root'
$trustedPublisher = Import-Certificate -FilePath $certificate -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople'
try {
    Add-AppxPackage -Path $package -ForceApplicationShutdown -ForceUpdateFromAnyVersion
    $installed = Get-AppxPackage -Name '81AD4B9B-7597-44AB-93A0-D5A695B2D35E'
    if ($null -eq $installed) { throw 'Server Monitor Manager package was not installed.' }
    Write-Output "INSTALLED=$($installed.PackageFullName)"
}
catch {
    Remove-Item -LiteralPath "Cert:\LocalMachine\Root\$($trustedRoot.Thumbprint)" -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath "Cert:\LocalMachine\TrustedPeople\$($trustedPublisher.Thumbprint)" -Force -ErrorAction SilentlyContinue
    throw
}

Write-Warning 'This is a test certificate. Remove the package and certificate after testing if they are no longer needed.'
