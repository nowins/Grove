[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidateSet('win-x64','win-arm64','osx-x64','osx-arm64')][string]$RuntimeIdentifier,
    [string]$NsisCompiler
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$projectRoot = Split-Path $PSScriptRoot -Parent
$publish = [IO.Path]::GetFullPath($PublishDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
$stage = Join-Path $output 'packaging'
if (Test-Path -LiteralPath $stage) { throw "Packaging stage exists: $stage" }
[xml]$properties = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
$numericVersion = [string]$properties.Project.PropertyGroup.VersionPrefix
$suffix = [string]$properties.Project.PropertyGroup.VersionSuffix
$bundleVersion = [string]$properties.Project.PropertyGroup.MacBundleVersion
if ($numericVersion -notmatch '^\d+\.\d+\.\d+$' -or $suffix -notmatch '^[0-9A-Za-z.-]*$' -or $bundleVersion -notmatch '^\d+$') { throw 'Invalid packaging version.' }
$version = if ($suffix) { "$numericVersion-$suffix" } else { $numericVersion }
$isWindowsPackage = $RuntimeIdentifier.StartsWith('win-')
$appHost = if ($isWindowsPackage) { 'Grove.exe' } else { 'Grove' }
foreach ($relative in @($appHost,'Grove.dll','Grove.deps.json','Grove.runtimeconfig.json','Editor/dist/index.html')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publish $relative) -PathType Leaf)) { throw "Missing package input: $relative" }
}
$runtime = Join-Path $stage 'runtime'
New-Item -ItemType Directory -Path $runtime | Out-Null
# Copy only the fresh publish directory; debug symbols stay in CI diagnostic
# output, not in the application. No source archive, user data or history.
$files = @(Get-ChildItem -LiteralPath $publish -Recurse -File -Force | Where-Object Extension -NotIn '.pdb','.dbg')
$manifest = @()
foreach ($file in $files) {
    $relative = [IO.Path]::GetRelativePath($publish,$file.FullName)
    if ($relative -match '(^|[\\/])\.\.([\\/]|$)' -or $relative.Contains('"') -or $relative.Contains('$')) { throw 'Unsafe published filename.' }
    $target = Join-Path $runtime $relative
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target
    $manifest += @{path=$relative.Replace('\','/'); bytes=$file.Length; sha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()}
}
$assets = @()
function Run-PackageCommand([string]$File,[string[]]$Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File exited $LASTEXITCODE" }
}
if ($isWindowsPackage) {
    if (-not $IsWindows) { throw 'Build Windows installers on Windows.' }
    if (-not $NsisCompiler) { $NsisCompiler = & (Join-Path $PSScriptRoot 'ensure-nsis.ps1') -Destination (Join-Path $output 'nsis-tool') }
    $payload = [Collections.Generic.List[string]]::new()
    $uninstall = [Collections.Generic.List[string]]::new()
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($publish,$file.FullName)
        $directory = [IO.Path]::GetDirectoryName($relative)
        $payload.Add(('SetOutPath "$INSTDIR\' + $directory + '"'))
        $payload.Add(('File "' + (Join-Path $runtime $relative) + '"'))
        $uninstall.Add(('Delete "$INSTDIR\' + $relative + '"'))
    }
    $directories = @(Get-ChildItem -LiteralPath $runtime -Recurse -Directory | Sort-Object { $_.FullName.Length } -Descending)
    foreach ($directory in $directories) { $uninstall.Add(('RMDir "$INSTDIR\' + [IO.Path]::GetRelativePath($runtime,$directory.FullName) + '"')) }
    $payloadPath = Join-Path $stage 'payload.nsh'; $uninstallPath = Join-Path $stage 'uninstall.nsh'
    $payload | Set-Content -LiteralPath $payloadPath -Encoding utf8BOM
    $uninstall | Set-Content -LiteralPath $uninstallPath -Encoding utf8BOM
    $installer = Join-Path $output "Grove-$version-$RuntimeIdentifier-setup.exe"
    $architecture = $RuntimeIdentifier.Substring(4)
    Run-PackageCommand $NsisCompiler @('/V2','/WX',"/DAPP_VERSION=$version","/DFILE_VERSION=$numericVersion.0","/DAPP_ARCH=$architecture","/DINSTALLER_OUTPUT=$installer","/DAPP_ICON=$(Join-Path $projectRoot 'src/GitX.Desktop/Assets/gitx.ico')","/DPAYLOAD_FILES=$payloadPath","/DUNINSTALL_FILES=$uninstallPath",(Join-Path $projectRoot 'packaging/windows/Grove.nsi'))
    $portable = Join-Path $output "desktop-$RuntimeIdentifier.zip"
    Compress-Archive -Path (Join-Path $runtime '*') -DestinationPath $portable
    $assets += $installer,$portable
} else {
    if (-not $IsMacOS) { throw 'Build and verify macOS bundles on macOS.' }
    $bundleRoot = Join-Path $stage 'dmg'; $app = Join-Path $bundleRoot 'Grove.app'
    $macos = Join-Path $app 'Contents/MacOS'; $resources = Join-Path $app 'Contents/Resources'
    New-Item -ItemType Directory -Path $macos,$resources | Out-Null
    Run-PackageCommand /bin/cp @('-a',"$runtime/.",$macos)
    Run-PackageCommand /bin/chmod @('+x',(Join-Path $macos 'Grove'))
    $plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>Grove</string>
<key>CFBundleName</key><string>Grove</string>
<key>CFBundleDisplayName</key><string>Grove</string>
<key>CFBundleIdentifier</key><string>io.github.nowins.grove</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleIconFile</key><string>Grove.icns</string>
<key>CFBundleShortVersionString</key><string>$numericVersion</string>
<key>CFBundleVersion</key><string>$bundleVersion</string>
<key>GroveReleaseVersion</key><string>$version</string>
<key>LSMinimumSystemVersion</key><string>14.0</string>
<key>NSHighResolutionCapable</key><true/>
<key>NSAppTransportSecurity</key><dict><key>NSAllowsLocalNetworking</key><true/></dict>
</dict></plist>
"@
    $plist | Set-Content -LiteralPath (Join-Path $app 'Contents/Info.plist') -Encoding utf8NoBOM
    $iconset = Join-Path $stage 'Grove.iconset'; New-Item -ItemType Directory -Path $iconset | Out-Null
    foreach ($size in @(16,32,128,256,512)) {
        foreach ($scale in @(1,2)) {
            $pixels = $size*$scale; $tail = if ($scale -eq 2) { '@2x' } else { '' }
            Run-PackageCommand /usr/bin/sips @('-z',"$pixels","$pixels",(Join-Path $projectRoot 'src/GitX.Desktop/Assets/gitx.png'),'--out',(Join-Path $iconset "icon_${size}x${size}$tail.png"))
        }
    }
    Run-PackageCommand /usr/bin/iconutil @('-c','icns',$iconset,'-o',(Join-Path $resources 'Grove.icns'))
    Run-PackageCommand /usr/bin/plutil @('-lint',(Join-Path $app 'Contents/Info.plist'))
    # Ad-hoc integrity signatures are not Developer ID signing or notarization.
    # Do not enable hardened runtime without a tested signing/entitlement plan.
    Run-PackageCommand /usr/bin/codesign @('--force','--deep','--sign','-',$app)
    Run-PackageCommand /usr/bin/codesign @('--verify','--deep','--strict',$app)
    New-Item -ItemType SymbolicLink -Path (Join-Path $bundleRoot 'Applications') -Target '/Applications' | Out-Null
    $dmg = Join-Path $output "Grove-$version-$RuntimeIdentifier.dmg"
    Run-PackageCommand /usr/bin/hdiutil @('create','-volname','Grove','-srcfolder',$bundleRoot,'-ov','-format','UDZO',$dmg)
    Run-PackageCommand /usr/bin/hdiutil @('verify',$dmg)
    $portable = Join-Path $output "desktop-$RuntimeIdentifier.tar.gz"
    Run-PackageCommand /usr/bin/tar @('-czf',$portable,'-C',$bundleRoot,'Grove.app')
    $assets += $dmg,$portable
}
$bundleFiles = @()
if (-not $isWindowsPackage) {
    $bundleFiles = @(Get-ChildItem -LiteralPath $app -Recurse -File -Force | ForEach-Object { @{path=[IO.Path]::GetRelativePath($app,$_.FullName); bytes=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()} })
}
$packageMetadata = @{ bundleFiles=$bundleFiles; version=$version; runtimeIdentifier=$RuntimeIdentifier; appHost=$appHost; developerSigned=$false; notarized=$false; macAdHocSigned=(-not $isWindowsPackage); installerBootstrapper= $(if ($isWindowsPackage) {'NSIS x86 Unicode; payload native to RID'} else {$null}); files=$manifest }
$packageMetadata | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $output 'package-metadata.json') -Encoding utf8
$assets | ForEach-Object { "$((Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_))" } | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding utf8
Write-Host "Packaged $version $RuntimeIdentifier (not Developer-signed)."
