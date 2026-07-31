[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$pages = @('ServersPage', 'LinksPage', 'SessionsPage', 'SettingsPage')
foreach ($page in $pages) {
    foreach ($extension in @('.xaml', '.xaml.cs')) {
        $path = Join-Path $root "src\ServerMonitorManager.Desktop\Pages\$page$extension"
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Missing desktop page contract: $path"
        }
    }
}

$linksXaml = Get-Content -Raw -Encoding UTF8 -LiteralPath (
    Join-Path $root 'src\ServerMonitorManager.Desktop\Pages\LinksPage.xaml')
$linksCode = Get-Content -Raw -Encoding UTF8 -LiteralPath (
    Join-Path $root 'src\ServerMonitorManager.Desktop\Pages\LinksPage.xaml.cs')
$mainCode = Get-Content -Raw -Encoding UTF8 -LiteralPath (
    Join-Path $root 'src\ServerMonitorManager.Desktop\MainPage.xaml.cs')
$appCode = Get-Content -Raw -Encoding UTF8 -LiteralPath (
    Join-Path $root 'src\ServerMonitorManager.Desktop\App.xaml.cs')
$sshCode = Get-Content -Raw -Encoding UTF8 -LiteralPath (
    Join-Path $root 'src\ServerMonitorManager.Desktop\SshMonitorService.cs')
$sshConnectionCode = Get-Content -Raw -Encoding UTF8 -LiteralPath (
    Join-Path $root 'src\ServerMonitorManager.Desktop\SshConnectionArguments.cs')
$serverViewModelCode = Get-Content -Raw -Encoding UTF8 -LiteralPath (
    Join-Path $root 'src\ServerMonitorManager.Desktop\ServerViewModel.cs')
$serversXaml = Get-Content -Raw -Encoding UTF8 -LiteralPath (
    Join-Path $root 'src\ServerMonitorManager.Desktop\Pages\ServersPage.xaml')
$windowsWorkflow = Get-Content -Raw -Encoding UTF8 -LiteralPath (
    Join-Path $root '.github\workflows\windows-build.yml')

$requiredXamlContracts = @(
    'x:Name="LinksList"',
    'AutomationProperties.Name=',
    'Click="ConnectButton_Click"',
    'Click="DisconnectButton_Click"'
)
foreach ($contract in $requiredXamlContracts) {
    if ($linksXaml.IndexOf($contract, [StringComparison]::Ordinal) -lt 0) {
        throw "Links page is missing required UI contract: $contract"
    }
}
if ($linksCode.IndexOf(
        'LinksList.SelectedItem as MeshLinkViewModel', [StringComparison]::Ordinal) -lt 0) {
    throw 'Links page must pass its selected Link to the command handler.'
}
if ($mainCode.IndexOf(
        'MeshLinksList.SelectedItem = selectedLink;', [StringComparison]::Ordinal) -lt 0) {
    throw 'Main page must synchronize the selected Link before disconnecting it.'
}
if ($sshCode.IndexOf(
        'await using var privateKeySession = await MaterializePrivateKeyAsync(',
        [StringComparison]::Ordinal) -lt 0) {
    throw 'SSH private key materialization must be scoped to an async-disposable session.'
}
if ($sshCode.IndexOf('CreateFileAsync(', [StringComparison]::Ordinal) -ge 0) {
    throw 'SSH private key materialization must not use the legacy unprotected temporary file path.'
}
if ($appCode.IndexOf(
        'SshPrivateKeySession.CleanupOrphans(ApplicationData.Current.TemporaryFolder.Path);',
        [StringComparison]::Ordinal) -lt 0) {
    throw 'Desktop startup must clean orphaned SSH key session files.'
}
if ($windowsWorkflow.IndexOf(
        'tests/ServerMonitorManager.Desktop.Security.Tests/ServerMonitorManager.Desktop.Security.Tests.csproj',
        [StringComparison]::Ordinal) -lt 0) {
    throw 'Windows CI must execute the Desktop security tests.'
}
if ($sshConnectionCode.IndexOf('StrictHostKeyChecking=yes', [StringComparison]::Ordinal) -lt 0 -or
    $sshConnectionCode.IndexOf('StrictHostKeyChecking=accept-new', [StringComparison]::Ordinal) -ge 0) {
    throw 'SSH connections must use only explicitly pinned host keys.'
}
$isolatedSshOptions = @(
    '"-F", "none"',
    '"GlobalKnownHostsFile=none"',
    '"KnownHostsCommand=none"',
    '"UpdateHostKeys=no"',
    '"VerifyHostKeyDNS=no"',
    '"CanonicalizeHostname=no"',
    '"CheckHostIP=no"'
)
foreach ($option in $isolatedSshOptions) {
    if ($sshConnectionCode.IndexOf($option, [StringComparison]::Ordinal) -lt 0) {
        throw "SSH trust policy is missing isolation option: $option"
    }
}
$ssh = Join-Path $env:SystemRoot 'System32\OpenSSH\ssh.exe'
if (-not (Test-Path -LiteralPath $ssh)) {
    throw "Windows OpenSSH client is missing: $ssh"
}
$effectiveSsh = (& $ssh -G -F none `
    -o 'IdentitiesOnly=yes' `
    -o 'IdentityAgent=none' `
    -o 'StrictHostKeyChecking=yes' `
    -o 'UserKnownHostsFile=C:/Temp/app-exclusive-pin.known_hosts' `
    -o 'GlobalKnownHostsFile=none' `
    -o 'KnownHostsCommand=none' `
    -o 'UpdateHostKeys=no' `
    -o 'VerifyHostKeyDNS=no' `
    -o 'CanonicalizeHostname=no' `
    -o 'CheckHostIP=no' `
    example.invalid 2>&1) -join "`n"
if ($LASTEXITCODE -ne 0) {
    throw "Windows OpenSSH rejected restricted trust options: $effectiveSsh"
}
$requiredEffectiveSsh = @(
    'canonicalizehostname false',
    'checkhostip no',
    'identitiesonly yes',
    'stricthostkeychecking true',
    'verifyhostkeydns false',
    'updatehostkeys false',
    'identityagent none',
    'globalknownhostsfile none',
    'userknownhostsfile C:/Temp/app-exclusive-pin.known_hosts'
)
foreach ($option in $requiredEffectiveSsh) {
    if ($effectiveSsh.IndexOf($option, [StringComparison]::OrdinalIgnoreCase) -lt 0) {
        throw "Windows OpenSSH effective config is missing: $option"
    }
}
if ($effectiveSsh -match '(?m)^(hostkeyalias|knownhostscommand)\s+') {
    throw 'Windows OpenSSH effective config retained an alternate host-key trust source.'
}
if ($serverViewModelCode.IndexOf(
        'string? HostKeyFingerprint = null', [StringComparison]::Ordinal) -lt 0) {
    throw 'Server profiles must persist the explicitly confirmed host-key fingerprint.'
}
if ($mainCode.IndexOf('ConfirmHostKeyAsync(', [StringComparison]::Ordinal) -lt 0) {
    throw 'Add/edit flow must require explicit host-key fingerprint confirmation.'
}
if ($serversXaml.IndexOf('Click="ConfirmHostKeyButton_Click"', [StringComparison]::Ordinal) -lt 0 -or
    $serverViewModelCode.IndexOf('HostKeyPendingConfirmation', [StringComparison]::Ordinal) -lt 0) {
    throw 'Legacy profiles must expose direct host-key confirmation in the server card.'
}

Write-Host 'Windows desktop contracts passed.'
