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

Write-Host 'Windows desktop contracts passed.'
