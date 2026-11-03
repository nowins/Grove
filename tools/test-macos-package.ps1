[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
if (-not $IsMacOS) { throw 'macOS package validation requires macOS.' }
$output = [IO.Path]::GetFullPath($PackageDirectory)
$testRoot = Join-Path $output 'bundle-test'
if (Test-Path -LiteralPath $testRoot) { throw 'Bundle test directory already exists.' }
$mount = Join-Path $testRoot 'mounted'; $install = Join-Path $testRoot 'path with spaces'
New-Item -ItemType Directory -Path $mount,$install | Out-Null
$dmg = @(Get-ChildItem -LiteralPath $output -Filter '*.dmg')
if ($dmg.Count -ne 1) { throw 'Expected exactly one DMG.' }
function Run-Check([string]$File,[string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File exited $LASTEXITCODE" }
}
$attached = $false
try {
    Run-Check /usr/bin/hdiutil @('attach','-readonly','-nobrowse','-mountpoint',$mount,$dmg[0].FullName)
    $attached = $true
    Run-Check /usr/bin/ditto @((Join-Path $mount 'Grove.app'),(Join-Path $install 'Grove.app'))
    $app = Join-Path $install 'Grove.app'
    Run-Check /usr/bin/plutil @('-lint',(Join-Path $app 'Contents/Info.plist'))
    Run-Check /usr/bin/codesign @('--verify','--deep','--strict',$app)
    $actual = (& /usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' (Join-Path $app 'Contents/Info.plist')).Trim()
    if ($LASTEXITCODE -ne 0 -or $actual -ne 'Grove') { throw 'Wrong bundle executable.' }
    $metadata = Get-Content -LiteralPath (Join-Path $output 'package-metadata.json') -Raw | ConvertFrom-Json
    foreach ($entry in $metadata.bundleFiles) {
        if ((Get-FileHash -LiteralPath (Join-Path $app $entry.path)).Hash.ToLowerInvariant() -ne $entry.sha256) { throw "Bundle copy mismatch: $($entry.path)" }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $mount 'Applications'))) { throw 'Missing Applications shortcut.' }
    @{ dmgVerified=$true; mountedReadOnly=$true; copyToPathWithSpaces=$true; bundleFiles=$metadata.bundleFiles.Count; adHocSignatureVerified=$true; developerSigned=$false; notarized=$false; guiTested=$false } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'results/bundle-test.json') -Encoding utf8
} finally {
    if ($attached) { Run-Check /usr/bin/hdiutil @('detach',$mount) }
}
Write-Host 'DMG mount/copy, bundle manifest and ad-hoc signature checks passed (no GUI claim).'
