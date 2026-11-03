[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageDirectory, [switch]$TestShellIntegration)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Windows package validation requires Windows.' }
$output = [IO.Path]::GetFullPath($PackageDirectory)
$testRoot = Join-Path $output 'installer-test'
if (Test-Path -LiteralPath $testRoot) { throw 'Installer test directory already exists.' }
$metadata = Get-Content -LiteralPath (Join-Path $output 'package-metadata.json') -Raw | ConvertFrom-Json
$installer = @(Get-ChildItem -LiteralPath $output -Filter '*-setup.exe')
if ($installer.Count -ne 1) { throw 'Expected exactly one installer.' }
$install = Join-Path $testRoot 'path with spaces/Grove'
$profile = Join-Path $testRoot 'existing-user-data'
New-Item -ItemType Directory -Path $profile | Out-Null
$sentinel = Join-Path $profile 'preserve.txt'
'Existing settings must survive install and uninstall.' | Set-Content -LiteralPath $sentinel
$sentinelHash = (Get-FileHash -LiteralPath $sentinel).Hash
function Wait-Installer([string]$File,[string]$Arguments) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -PassThru
    if (-not $process.WaitForExit(120000)) { throw "Installer timeout: $($process.Id)" }
    if ($process.ExitCode -ne 0) { throw "Installer exited $($process.ExitCode)" }
}
# No shell/registry integration in local tests. The same actual installer
# supports this explicit option; ordinary users get current-user integration.
Wait-Installer $installer[0].FullName "/S /NOINTEGRATION /D=$install"
foreach ($entry in $metadata.files) {
    $file = Join-Path $install $entry.path
    if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or
        (Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant() -ne $entry.sha256) { throw "Installed file mismatch: $($entry.path)" }
}
$details = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $install 'Grove.exe'))
if ($details.ProductName -ne 'Grove' -or $details.FileDescription -ne 'Grove') { throw 'Application metadata still uses old branding.' }
$extra = Join-Path $install 'user-created.txt'
'Uninstaller must preserve files outside its manifest.' | Set-Content -LiteralPath $extra
Wait-Installer $installer[0].FullName "/S /NOINTEGRATION /D=$install"
Wait-Installer (Join-Path $install 'Uninstall.exe') "/S _?=$install"
if (Test-Path -LiteralPath (Join-Path $install 'Grove.exe')) { throw 'Uninstaller left the application executable.' }
if (-not (Test-Path -LiteralPath $extra) -or (Get-FileHash -LiteralPath $sentinel).Hash -ne $sentinelHash) { throw 'Uninstaller changed user data.' }
$integrationChecked = $false
if ($TestShellIntegration) {
    $architecture = $metadata.runtimeIdentifier.Substring(4)
    $registry = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Grove-$architecture"
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Grove.lnk'
    if ((Test-Path -LiteralPath $registry) -or (Test-Path -LiteralPath $shortcut)) { throw 'Existing Grove integration found; refusing to overwrite it during tests.' }
    Wait-Installer $installer[0].FullName "/S /D=$install"
    try {
        $registered = Get-ItemProperty -LiteralPath $registry
        $expectedCommand = '"' + (Join-Path $install 'Uninstall.exe') + '"'
        if ($registered.UninstallString -ne $expectedCommand -or -not (Test-Path -LiteralPath $shortcut)) { throw 'Invalid current-user uninstall registration or shortcut.' }
    } finally {
        Wait-Installer (Join-Path $install 'Uninstall.exe') "/S _?=$install"
    }
    if ((Test-Path -LiteralPath $registry) -or (Test-Path -LiteralPath $shortcut)) { throw 'Uninstaller left current-user integration.' }
    if (-not (Test-Path -LiteralPath $extra) -or (Get-FileHash -LiteralPath $sentinel).Hash -ne $sentinelHash) { throw 'Integrated uninstall changed user data.' }
    $integrationChecked = $true
}
@{ install=$true; reinstall=$true; uninstall=$true; manifestFiles=$metadata.files.Count; extraUserFilePreserved=$true; existingDataPreserved=$true; shellIntegrationTested=$integrationChecked; guiTested=$false } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'results/installer-test.json') -Encoding utf8
Write-Host 'Windows install, reinstall, manifest and safe uninstall checks passed (no GUI claim).'
