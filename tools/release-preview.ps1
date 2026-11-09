param(
    [string]$ArtifactDirectory,
    [string]$OutputDirectory,
    [string]$SourceRevision = $env:GITHUB_SHA,
    [string]$Repository = $env:GITHUB_REPOSITORY,
    [string]$RunId = $env:GITHUB_RUN_ID,
    [switch]$CheckVersion,
    [switch]$PrepareOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[xml]$props = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../Directory.Build.props') -Raw
$version = "$($props.Project.PropertyGroup.VersionPrefix)-$($props.Project.PropertyGroup.VersionSuffix)"
if ($version -cnotmatch '^\d+\.\d+\.\d+-preview\.\d+$') { throw 'Only numbered preview versions may be published automatically.' }
$tag = "v$version"
$api = 'https://api.github.com'
function Release-Request([string]$Path, [string]$Method = 'Get', $Body = $null) {
    if (-not $env:GH_TOKEN) { throw 'GH_TOKEN is required for publication.' }
    $headers = @{ Authorization = "Bearer $env:GH_TOKEN"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
    $options = @{ Uri = "$api/repos/$Repository/$Path"; Method = $Method; Headers = $headers }
    if ($null -ne $Body) { $options.Body = ConvertTo-Json -InputObject $Body -Depth 12; $options.ContentType = 'application/json' }
    Invoke-RestMethod @options
}
function Existing-Release {
    try { Release-Request "releases/tags/$tag" }
    catch { if ([int]$_.Exception.Response.StatusCode -ne 404) { throw }; return $null }
}
if ($Repository -cnotmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid repository.' }
if ($CheckVersion) {
    $existing = Existing-Release
    $needed = $null -eq $existing -or $existing.draft
    if (-not $needed) { Write-Host "$tag already published; preserving its tag and assets." }
    "publish=$($needed.ToString().ToLowerInvariant())" | Add-Content -LiteralPath $env:GITHUB_OUTPUT
    exit 0
}
if ($SourceRevision -cnotmatch '^[0-9a-f]{40}$' -or $RunId -cnotmatch '^\d+$') { throw 'An exact source revision and CI run are required.' }
if (-not $ArtifactDirectory -or -not $OutputDirectory -or (Test-Path -LiteralPath $OutputDirectory)) { throw 'Use an existing artifact input and a fresh output directory.' }
$ArtifactDirectory = (Resolve-Path -LiteralPath $ArtifactDirectory).Path
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$assets = @(); $platforms = @()
foreach ($rid in @('win-x64', 'win-arm64', 'osx-x64', 'osx-arm64', 'linux-x64', 'linux-arm64')) {
    $desktop = Join-Path $ArtifactDirectory "desktop-$rid"
    $results = Join-Path $ArtifactDirectory "results-$rid"
    $metadataFiles = @(Get-ChildItem -LiteralPath $results -Recurse -File -Filter 'metadata.json')
    $packageFiles = @(Get-ChildItem -LiteralPath $results -Recurse -File -Filter 'package-metadata.json')
    if ($metadataFiles.Count -ne 1 -or $packageFiles.Count -ne 1) { throw "Missing or ambiguous metadata for $rid." }
    $metadata = Get-Content -LiteralPath $metadataFiles[0].FullName -Raw | ConvertFrom-Json
    $package = Get-Content -LiteralPath $packageFiles[0].FullName -Raw | ConvertFrom-Json
    if ($metadata.runtimeIdentifier -cne $rid -or $package.runtimeIdentifier -cne $rid -or $package.version -cne $version -or
        $metadata.sourceRevision -cne $SourceRevision -or -not $metadata.nativeExecution -or -not $metadata.selfContained -or
        $metadata.coreTests.total -lt 1 -or $metadata.coreTests.total -ne $metadata.coreTests.passed -or $metadata.coreTests.skipped -ne 0) {
        throw "Unsuccessful, mismatched or stale CI provenance for $rid."
    }
    $names = if ($rid.StartsWith('win-')) {
        @{ "desktop-$rid.zip" = "Grove-$version-$rid.zip"; "Grove-$version-$rid-setup.exe" = "Grove-$version-$rid-setup.exe" }
    } elseif ($rid.StartsWith('osx-')) {
        @{ "desktop-$rid.tar.gz" = "Grove-$version-$rid.app.tar.gz"; "Grove-$version-$rid.dmg" = "Grove-$version-$rid.dmg" }
    } else { @{ "desktop-$rid.tar.gz" = "Grove-$version-$rid.tar.gz" } }
    $sums = @{}
    foreach ($line in Get-Content -LiteralPath (Join-Path $desktop 'SHA256SUMS.txt')) {
        if ($line -cnotmatch '^([0-9a-f]{64})  ([A-Za-z0-9._-]+)$' -or $sums.ContainsKey($Matches[2])) { throw "Invalid checksum list for $rid." }
        $sums[$Matches[2]] = $Matches[1]
    }
    if ($sums.Count -ne $names.Count) { throw "Unexpected packages for $rid." }
    foreach ($name in @($names.Keys | Sort-Object)) {
        $file = Get-Item -LiteralPath (Join-Path $desktop $name)
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $entry = @($package.assets | Where-Object { $_.path -ceq $name })
        if ($file.Length -lt 1 -or $hash -cne $sums[$name] -or $entry.Count -ne 1 -or $entry[0].sha256 -cne $hash -or $entry[0].bytes -ne $file.Length) { throw "Package integrity mismatch: $rid/$name" }
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $OutputDirectory $names[$name])
        $assets += @{ name = $names[$name]; bytes = $file.Length; sha256 = $hash }
    }
    $platforms += @{ runtimeIdentifier = $rid; sdk = $metadata.sdk; node = $metadata.node; coreTests = $metadata.coreTests; nativeExecution = $metadata.nativeExecution;
        nativeGuiTested = $metadata.nativeGuiTested; nativeChromeTested = $metadata.nativeChromeTested; physicalInputTested = $metadata.physicalInputTested;
        developerSigned = $package.developerSigned; notarized = $package.notarized; macAdHocSigned = $package.macAdHocSigned;
        files = $package.files; bundleFiles = $package.bundleFiles }
}
@{ version = $version; sourceRevision = $SourceRevision; ciRun = "https://github.com/$Repository/actions/runs/$RunId"; platforms = $platforms; assets = $assets } |
    ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'BUILD-INFO.json') -Encoding utf8NoBOM
$files = @(Get-ChildItem -LiteralPath $OutputDirectory -File | Sort-Object Name)
@($files | ForEach-Object { "$((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)" }) |
    Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding utf8NoBOM
$files = @(Get-ChildItem -LiteralPath $OutputDirectory -File | Sort-Object Name)
if ($files.Count -ne 12) { throw 'Expected exactly twelve release assets.' }
if ($PrepareOnly) { Write-Host "Validated $version from six native CI artifacts; twelve assets prepared."; exit 0 }
$existing = Existing-Release
if ($null -ne $existing) {
    if (-not $existing.draft) { Write-Host "$tag already published; preserving its tag and assets."; exit 0 }
    throw 'A draft already exists. Inspect it before retrying; automatic publication never overwrites partial assets.'
}
# Never move an existing tag, including a tag without a release.
try { $ref = Release-Request "git/ref/tags/$tag" }
catch { if ([int]$_.Exception.Response.StatusCode -ne 404) { throw }; $ref = $null }
if ($null -ne $ref) { throw 'Tag already exists; automatic publication will not move it.' }
$notes = @"
Windows、macOS 和 Linux 两架构预览包。包含连续 Diff 刷新／跨文件导航与 Git Log 常用操作改进。

来源：$SourceRevision
六平台 CI：https://github.com/$Repository/actions/runs/$RunId
请按处理器架构下载；Mac 用户下载对应 DMG 后拖入 Applications 替换旧版。

构建、测试和包检查由原生 runner 完成，详见 BUILD-INFO.json；不等同于完整人工 GUI 验收。Windows 无发行证书；Mac 仅 ad-hoc 签名，无 Developer ID／公证。Linux 为实验性便携包。
"@
$release = Release-Request 'releases' 'Post' @{ tag_name = $tag; target_commitish = $SourceRevision; name = "Grove $version"; body = $notes; draft = $true; prerelease = $true; make_latest = 'false' }
foreach ($file in $files) {
    $upload = $release.upload_url -replace '\{.*$', ''
    Invoke-RestMethod -Uri "$($upload)?name=$([Uri]::EscapeDataString($file.Name))" -Method Post -InFile $file.FullName -ContentType 'application/octet-stream' -Headers @{
        Authorization = "Bearer $env:GH_TOKEN"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'
    } | Out-Null
}
$remoteAssets = @(Release-Request "releases/$($release.id)/assets")
if ($remoteAssets.Count -ne $files.Count) { throw 'Uploaded asset count mismatch; draft remains unpublished.' }
foreach ($file in $files) {
    $remote = @($remoteAssets | Where-Object { $_.name -ceq $file.Name })
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($remote.Count -ne 1 -or $remote[0].size -ne $file.Length -or $remote[0].digest -cne "sha256:$hash") { throw 'Server asset checksum mismatch; draft remains unpublished.' }
}
$published = Release-Request "releases/$($release.id)" 'Patch' @{ draft = $false; prerelease = $true; make_latest = 'false' }
$publishedRef = Release-Request "git/ref/tags/$tag"
if ($publishedRef.object.type -cne 'commit' -or $publishedRef.object.sha -cne $SourceRevision) { throw 'Published tag does not match the validated source revision.' }
Write-Host "Published $($published.html_url)"
# Public distribution check deliberately sends no authentication header.
foreach ($file in $files) {
    $download = Join-Path $OutputDirectory "$($file.Name).download"
    $url = "https://github.com/$Repository/releases/download/$tag/$($file.Name)"
    Invoke-WebRequest -Uri $url -OutFile $download -MaximumRetryCount 3 -RetryIntervalSec 3
    if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -cne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) { throw "Public asset checksum mismatch: $($file.Name)" }
    Remove-Item -LiteralPath $download
}
Write-Host 'All twelve assets verified through anonymous downloads.'
