param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a fresh test directory.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$inputDirectory = Join-Path $OutputDirectory 'synthetic-artifacts'
[xml]$props = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../Directory.Build.props') -Raw
$version = "$($props.Project.PropertyGroup.VersionPrefix)-$($props.Project.PropertyGroup.VersionSuffix)"
$revision = 'a' * 40
foreach ($rid in @('win-x64', 'win-arm64', 'osx-x64', 'osx-arm64', 'linux-x64', 'linux-arm64')) {
    $desktop = Join-Path $inputDirectory "desktop-$rid"; $results = Join-Path $inputDirectory "results-$rid"
    New-Item -ItemType Directory -Path $desktop, $results -Force | Out-Null
    $names = if ($rid.StartsWith('win-')) { @("desktop-$rid.zip", "Grove-$version-$rid-setup.exe") }
        elseif ($rid.StartsWith('osx-')) { @("desktop-$rid.tar.gz", "Grove-$version-$rid.dmg") }
        else { @("desktop-$rid.tar.gz") }
    $assets = @(); $sums = @()
    foreach ($name in $names) {
        $path = Join-Path $desktop $name
        "Synthetic validation data for $rid; not an application package." | Set-Content -LiteralPath $path -Encoding utf8NoBOM
        $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        $assets += @{ path = $name; bytes = (Get-Item -LiteralPath $path).Length; sha256 = $hash }; $sums += "$hash  $name"
    }
    $sums | Set-Content -LiteralPath (Join-Path $desktop 'SHA256SUMS.txt') -Encoding utf8NoBOM
    @{ runtimeIdentifier = $rid; sourceRevision = $revision; sdk = 'test'; node = 'test'; nativeExecution = $true; selfContained = $true;
        coreTests = @{ total = 2; passed = 2; skipped = 0 }; nativeGuiTested = $false; nativeChromeTested = $rid.StartsWith('osx-'); physicalInputTested = $false } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $results 'metadata.json') -Encoding utf8NoBOM
    @{ runtimeIdentifier = $rid; version = $version; assets = $assets; files = @(); bundleFiles = @(); developerSigned = $false; notarized = $false; macAdHocSigned = $rid.StartsWith('osx-') } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $results 'package-metadata.json') -Encoding utf8NoBOM
}
$script = Join-Path $PSScriptRoot 'release-preview.ps1'
function Prepare([string]$Name) {
    & $script -ArtifactDirectory $inputDirectory -OutputDirectory (Join-Path $OutputDirectory $Name) -SourceRevision $revision -Repository 'nowins/Grove' -RunId 123 -PrepareOnly
}
Prepare 'valid'
$published = @(Get-ChildItem -LiteralPath (Join-Path $OutputDirectory 'valid') -File)
if ($published.Count -ne 12 -or @($published | Where-Object Name -Like '*.app.tar.gz').Count -ne 2) { throw 'Expected twelve assets including both Mac archives.' }
$info = Get-Content -LiteralPath (Join-Path $OutputDirectory 'valid/BUILD-INFO.json') -Raw | ConvertFrom-Json
if ($info.sourceRevision -cne $revision -or $info.platforms.Count -ne 6 -or $info.assets.Count -ne 10) { throw 'Provenance mismatch.' }
$checks = [Collections.Generic.List[string]]::new(); $checks.Add('valid-six-platform-manifest')
function Reject([string]$Name, [string]$Path, [scriptblock]$Change) {
    $before = [IO.File]::ReadAllBytes($Path)
    try {
        & $Change $Path
        $rejected = $false
        try { Prepare $Name } catch { $rejected = $true }
        if (-not $rejected) { throw "Accepted unsafe release input: $Name" }
        $checks.Add($Name)
    } finally { [IO.File]::WriteAllBytes($Path, $before) }
}
$metadata = Join-Path $inputDirectory 'results-win-x64/metadata.json'
Reject 'wrong-source' $metadata { param($p) $m = Get-Content -LiteralPath $p -Raw | ConvertFrom-Json; $m.sourceRevision = 'b' * 40; $m | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $p }
Reject 'failed-tests' $metadata { param($p) $m = Get-Content -LiteralPath $p -Raw | ConvertFrom-Json; $m.coreTests.passed = 1; $m | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $p }
Reject 'skipped-tests' $metadata { param($p) $m = Get-Content -LiteralPath $p -Raw | ConvertFrom-Json; $m.coreTests.skipped = 1; $m | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $p }
Reject 'cross-architecture' $metadata { param($p) $m = Get-Content -LiteralPath $p -Raw | ConvertFrom-Json; $m.runtimeIdentifier = 'osx-x64'; $m | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $p }
$package = Join-Path $inputDirectory 'results-win-x64/package-metadata.json'
Reject 'wrong-version' $package { param($p) $m = Get-Content -LiteralPath $p -Raw | ConvertFrom-Json; $m.version = '0.0.0-preview.0'; $m | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $p }
Reject 'wrong-package-metadata-hash' $package { param($p) $m = Get-Content -LiteralPath $p -Raw | ConvertFrom-Json; $m.assets[0].sha256 = '0' * 64; $m | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $p }
$sumPath = Join-Path $inputDirectory 'desktop-win-x64/SHA256SUMS.txt'
Reject 'checksum-path-traversal' $sumPath { param($p) ('0' * 64) + '  ../escape.zip' | Set-Content -LiteralPath $p }
Reject 'duplicate-checksum' $sumPath { param($p) $s = Get-Content -LiteralPath $p; @($s) + @($s[0]) | Set-Content -LiteralPath $p }
Reject 'corrupted-package' (Join-Path $inputDirectory 'desktop-win-x64/desktop-win-x64.zip') { param($p) 'tampered' | Add-Content -LiteralPath $p }
$missing = Join-Path $inputDirectory 'results-osx-arm64/metadata.json'
$held = "$missing.held"; Move-Item -LiteralPath $missing -Destination $held
try {
    $rejected = $false; try { Prepare 'missing-platform' } catch { $rejected = $true }
    if (-not $rejected) { throw 'Accepted missing platform.' }; $checks.Add('missing-platform')
} finally { Move-Item -LiteralPath $held -Destination $missing }
@{ passed = $checks.Count; checks = $checks; syntheticInputs = $true; githubPublicationTested = $false } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'result.json') -Encoding utf8NoBOM
Write-Host "$($checks.Count) release preparation checks passed; no GitHub writes performed."
