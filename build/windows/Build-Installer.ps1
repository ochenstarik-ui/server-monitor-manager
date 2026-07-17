param(
    [Parameter(Mandatory = $true)]
    [string]$CertificatePath,

    [Parameter(Mandatory = $true)]
    [string]$CertificatePassword,

    [string]$OutputDirectory = 'artifacts/windows-installer'
)

$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$project = Join-Path $root 'src\ServerMonitorManager.Desktop\ServerMonitorManager.Desktop.csproj'
$certificate = [System.IO.Path]::GetFullPath($CertificatePath)
$output = [System.IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
$appPackages = Join-Path $output 'AppPackages'

if (-not (Test-Path -LiteralPath $certificate -PathType Leaf)) {
    throw "Signing certificate not found: $certificate"
}

if (Test-Path -LiteralPath $output) {
    Remove-Item -LiteralPath $output -Recurse -Force
}
New-Item -ItemType Directory -Path $appPackages -Force | Out-Null

$securePassword = ConvertTo-SecureString -String $CertificatePassword -AsPlainText -Force
$signingCertificate = Import-PfxCertificate `
    -FilePath $certificate `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -Password $securePassword `
    -Exportable
try {
    if ($signingCertificate.Subject -ne 'CN=AppPublisher') {
        throw "The certificate subject must match Package.appxmanifest Publisher=CN=AppPublisher; actual: $($signingCertificate.Subject)"
    }

    dotnet restore $project -r win-x64 -p:Platform=x64 -p:PublishReadyToRun=false
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed' }

    dotnet publish $project `
        --configuration Release `
        --runtime win-x64 `
        --no-restore `
        -p:Platform=x64 `
        -p:PublishReadyToRun=false `
        -p:GenerateAppxPackageOnBuild=true `
        -p:AppxPackageSigningEnabled=true `
        -p:PackageCertificateThumbprint=$($signingCertificate.Thumbprint) `
        -p:AppxBundle=Never `
        -p:AppxSymbolPackageEnabled=false `
        -p:UapAppxPackageBuildMode=SideloadOnly `
        -p:AppxPackageDir="$appPackages\"
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

    $package = Get-ChildItem -LiteralPath $appPackages -Recurse -File |
        Where-Object { $_.Extension -in @('.msix', '.appx', '.msixbundle', '.appxbundle') } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $package) {
        throw "No MSIX/AppX package was produced under $appPackages"
    }

    $finalPackage = Join-Path $output 'ServerMonitorManager-win-x64.msix'
    Copy-Item -LiteralPath $package.FullName -Destination $finalPackage -Force
    $signature = Get-AuthenticodeSignature -LiteralPath $finalPackage
    if ($null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Thumbprint -ne $signingCertificate.Thumbprint) {
        throw 'The generated MSIX is not signed by the requested certificate.'
    }

    $hash = Get-FileHash -LiteralPath $finalPackage -Algorithm SHA256
    $checksumPath = Join-Path $output 'SHA256SUMS'
    "$($hash.Hash.ToLowerInvariant()) *$([System.IO.Path]::GetFileName($finalPackage))" |
        Set-Content -LiteralPath $checksumPath -Encoding ascii

    Write-Output "PACKAGE=$finalPackage"
    Write-Output "CHECKSUM=$checksumPath"
    Write-Output "SIGNER=$($signature.SignerCertificate.Subject)"
}
finally {
    Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($signingCertificate.Thumbprint)" -Force -ErrorAction SilentlyContinue
}
