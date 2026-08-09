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
$meshModelsCode = Get-Content -Raw -Encoding UTF8 -LiteralPath (
    Join-Path $root 'src\ServerMonitorManager.Desktop\MeshModels.cs')
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
    'Click="DisconnectButton_Click"',
    'Text="{x:Bind DesiredStatusText}"',
    'Text="{x:Bind ActualStatusText}"',
    'Text="{x:Bind DriftText}"',
    'Text="{x:Bind ErrorText}"',
    'Text="{x:Bind VersionText}"'
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
if ($linksXaml.IndexOf('x:Name="FirewallUnavailableInfo"', [StringComparison]::Ordinal) -lt 0 -or
    $linksXaml.IndexOf('Message="Mesh firewall ', [StringComparison]::Ordinal) -lt 0) {
    throw 'Links page must expose one persistent Mesh firewall unavailable banner.'
}
if ($linksCode.IndexOf('SetFirewallUnavailable', [StringComparison]::Ordinal) -lt 0 -or
    $mainCode.IndexOf('MeshLinkViewModel.FirewallUnavailableErrorCode', [StringComparison]::Ordinal) -lt 0) {
    throw 'Desktop must project both event and persisted Mesh firewall unavailable state.'
}
if ($meshModelsCode.IndexOf(
        'LastError is FirewallUnavailableErrorCode or NodeNotActivatedErrorCode ? string.Empty',
        [StringComparison]::Ordinal) -lt 0) {
    throw 'Shared Mesh firewall and expected Node activation states must be suppressed from individual Link errors.'
}
if ($linksXaml.IndexOf('x:Name="ShowHistoryToggle"', [StringComparison]::Ordinal) -lt 0 -or
    $linksXaml.IndexOf('Toggled="ShowHistoryToggle_Toggled"', [StringComparison]::Ordinal) -lt 0 -or
    $linksCode.IndexOf('ShowHistoryToggle.IsOn', [StringComparison]::Ordinal) -lt 0) {
    throw 'Links page must expose an explicit history toggle.'
}
if ($linksCode.IndexOf(
        'effective.Where(link => link.DesiredState == "Active" || link.HasDrift)',
        [StringComparison]::Ordinal) -lt 0) {
    throw 'Links page default view must show only effective Active or drifted policies.'
}
if ($linksCode.IndexOf('LinksCountText.Text = $', [StringComparison]::Ordinal) -lt 0 -or
    $linksCode.IndexOf('DisplayedLinks.Count(link => link.ActualState == "Active")', [StringComparison]::Ordinal) -lt 0 -or
    $linksCode.IndexOf('DisplayedLinks.Count(link => link.HasDrift)', [StringComparison]::Ordinal) -lt 0) {
    throw 'Links page counters must unambiguously distinguish shown, factual Active, and drifted policies.'
}
if ($meshModelsCode.IndexOf('public string DesiredStatusText', [StringComparison]::Ordinal) -lt 0 -or
    $meshModelsCode.IndexOf('public string ActualStatusText', [StringComparison]::Ordinal) -lt 0 -or
    $meshModelsCode.IndexOf('LastError == NodeNotActivatedErrorCode', [StringComparison]::Ordinal) -lt 0) {
    throw 'Links page must use typed desired/factual/expected-activation wording.'
}
if ($mainCode.IndexOf(
        'link.LastError == MeshLinkViewModel.NodeNotActivatedErrorCode',
        [StringComparison]::Ordinal) -lt 0 -or
    $mainCode.IndexOf('? InfoBarSeverity.Informational', [StringComparison]::Ordinal) -lt 0) {
    throw 'Expected Node activation state must use non-error informational severity.'
}
if ($mainCode.IndexOf('Servers.Count > 0 || _control.IsConfigured', [StringComparison]::Ordinal) -lt 0 -or
    $mainCode.IndexOf('await RefreshControlMeshAsync(showSuccess: false);', [StringComparison]::Ordinal) -lt 0) {
    throw 'Configured Control state must refresh even when no SSH profiles exist.'
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
