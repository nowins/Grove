# Requires PowerShell 7, Git, Node from .node-version, and the SDK in global.json.
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64', 'osx-arm64', 'osx-x64')]
    [string]$RuntimeIdentifier,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$projectRoot = Split-Path $PSScriptRoot -Parent
$hostArchitecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
$osArchitecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
if ($hostArchitecture -ne $osArchitecture) { throw "Native runner process required; process $hostArchitecture, OS $osArchitecture." }
$hostRid = if ($IsWindows) { "win-$hostArchitecture" } elseif ($IsMacOS) { "osx-$hostArchitecture" } else { throw 'This CI entry supports Windows and macOS hosts.' }
if (-not $RuntimeIdentifier) { $RuntimeIdentifier = $hostRid }
if ($RuntimeIdentifier -ne $hostRid) { throw "Run $RuntimeIdentifier on its native host; this process is $hostRid." }
if (-not $OutputDirectory) { $OutputDirectory = "artifacts/ci/$RuntimeIdentifier" }
$output = [IO.Path]::GetFullPath($OutputDirectory, $projectRoot)
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
$comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
if (-not $output.StartsWith($artifactRoot, $comparison)) { throw 'CI output must be inside this checkout artifacts directory.' }
if (Test-Path -LiteralPath $output) { throw "Output already exists: $output. Choose a new directory; CI never deletes existing output." }
$results = Join-Path $output 'results'
$publish = Join-Path $output 'publish'
New-Item -ItemType Directory -Path $results | Out-Null
$savedEnvironment = @{}
foreach ($name in @('GIT_TERMINAL_PROMPT', 'GCM_INTERACTIVE', 'GITX_DATA_DIR', 'TMPDIR')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
function Invoke-CiCommand([string]$File, [string[]]$Arguments, [string]$Log) {
    Write-Host "> $File $($Arguments -join ' ')"
    & $File @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $results $Log)
    if ($LASTEXITCODE -ne 0) { throw "$File exited $LASTEXITCODE. See $Log." }
}
Push-Location $projectRoot
try {
    if ($IsMacOS) {
        # Hosted macOS exposes /var/... while Git/libgit2 return /private/var/...
        # for the same directory. Start child processes with one physical temp
        # root; retain the product's protections against symlinked write paths.
        Push-Location -LiteralPath ([IO.Path]::GetTempPath())
        try {
            $physicalTemp = (& /bin/pwd -P).Trim()
            if ($LASTEXITCODE -ne 0 -or -not [IO.Path]::IsPathFullyQualified($physicalTemp) -or
                -not (Test-Path -LiteralPath $physicalTemp -PathType Container)) {
                throw 'Cannot resolve the physical macOS temporary directory.'
            }
            $env:TMPDIR = $physicalTemp.TrimEnd('/') + '/'
            Write-Host 'Using the physical macOS temporary directory for child processes.'
        } finally { Pop-Location }
    }
    # Hosted CI uses a fresh runner. Keep Git's standard source discovery: custom
    # GIT_CONFIG_* overrides intentionally disable the product's blame cache.
    # TestRepository creates disposable repos and sets its own author/config.
    $env:GIT_TERMINAL_PROMPT = '0'
    $env:GCM_INTERACTIVE = 'never'
    $env:GITX_DATA_DIR = Join-Path $output 'application-data'
    Invoke-CiCommand dotnet @('--version') 'dotnet-version.txt'
    Invoke-CiCommand dotnet @('--info') 'dotnet-info.txt'
    Invoke-CiCommand dotnet @('--list-sdks') 'dotnet-sdks.txt'
    Invoke-CiCommand dotnet @('--list-runtimes') 'dotnet-runtimes.txt'
    Invoke-CiCommand node @('--version') 'node-version.txt'
    Invoke-CiCommand git @('--version') 'git-version.txt'
    $nodeVersion = (node --version).TrimStart('v').Trim()
    $nodeArchitecture = (node -p 'process.arch').Trim()
    if ($LASTEXITCODE -ne 0 -or $nodeArchitecture -ne $hostArchitecture) { throw "Native Node $hostArchitecture required, found $nodeArchitecture." }
    $expectedNode = (Get-Content -LiteralPath '.node-version' -Raw).Trim()
    if ($nodeVersion -ne $expectedNode) { throw "Expected Node $expectedNode, found $nodeVersion." }
    Invoke-CiCommand npm @('ci', '--prefix', 'src/GitX.Desktop/Editor', '--no-audit', '--no-fund') 'npm-ci.txt'
    Invoke-CiCommand dotnet @('restore', 'Grove.slnx', '--locked-mode') 'restore.txt'
    Invoke-CiCommand npm @('run', 'build', '--prefix', 'src/GitX.Desktop/Editor') 'editor-build.txt'
    Invoke-CiCommand dotnet @('build', 'Grove.slnx', '-c', 'Release', '--no-restore', '--warnaserror', '-p:ContinuousIntegrationBuild=true') 'build.txt'
    Invoke-CiCommand npm @('test', '--prefix', 'src/GitX.Desktop/Editor') 'editor-tests.txt'
    Invoke-CiCommand dotnet @('test', 'tests/GitX.Core.Tests/GitX.Core.Tests.csproj', '-c', 'Release', '--no-build', '--no-restore', '--logger', 'trx;LogFileName=core.trx', '--results-directory', $results, '--blame-hang', '--blame-hang-timeout', '3m', '--blame-hang-dump-type', 'none') 'core-tests.txt'
    [xml]$trx = Get-Content -LiteralPath (Join-Path $results 'core.trx') -Raw
    $counts = $trx.TestRun.ResultSummary.Counters
    if ([int]$counts.total -lt 1 -or [int]$counts.failed -ne 0 -or [int]$counts.error -ne 0 -or [int]$counts.executed -lt 1) { throw 'Core test results are empty or unsuccessful.' }
    Invoke-CiCommand dotnet @('publish', 'src/GitX.Desktop/GitX.Desktop.csproj', '-c', 'Release', '-r', $RuntimeIdentifier, '--self-contained', 'true', '-p:RestoreLockedMode=true', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:CopyOutputSymbolsToPublishDirectory=false', '-p:ContinuousIntegrationBuild=true', '--output', $publish, '--warnaserror') 'publish.txt'
    $appHost = if ($IsWindows) { 'Grove.exe' } else { 'Grove' }
    $required = @($appHost, 'LICENSE.txt', 'Grove.dll', 'GitX.Core.dll', 'Avalonia.Controls.WebView.dll', 'Grove.deps.json', 'Grove.runtimeconfig.json', 'Editor/dist/index.html', 'Editor/dist/editor.js', 'Editor/dist/editor.css', 'Editor/dist/merge.html', 'Editor/dist/merge.js', 'Editor/dist/merge.css', 'Editor/dist/source-symbols.worker.js', 'Editor/dist/tree-sitter-runtime.js', 'Editor/dist/tree-sitter.wasm', 'Editor/dist/MONACO-LICENSE.txt', 'Editor/dist/MARKED-LICENSE.txt', 'Editor/dist/DOMPURIFY-LICENSE.txt', 'Editor/dist/INTER-OFL.txt', 'Editor/dist/JETBRAINS-MONO-OFL.txt', 'Editor/dist/TREE-SITTER-GRAMMAR-NOTICES.txt')
    foreach ($grammar in @('java', 'c-sharp', 'python', 'javascript', 'typescript', 'tsx')) { $required += "Editor/dist/tree-sitter-$grammar.wasm" }
    foreach ($relative in $required) { if (-not (Test-Path -LiteralPath (Join-Path $publish $relative) -PathType Leaf)) { throw "Missing published resource: $relative" } }
    $nativePattern = if ($IsWindows) { '*git2*.dll' } else { '*git2*.dylib' }
    if (-not (Get-ChildItem -LiteralPath $publish -Recurse -File -Filter $nativePattern)) { throw 'Published LibGit2 native library is missing.' }
    $platformResult = Join-Path $results 'platform.json'
    Invoke-CiCommand dotnet @('run', '--project', 'tools/PlatformProbe/PlatformProbe.csproj', '-c', 'Release', '--no-build', '--no-restore', '--', $hostArchitecture, $publish, $platformResult) 'platform-probe.txt'
    $platform = Get-Content -LiteralPath $platformResult -Raw | ConvertFrom-Json
    if ($IsMacOS) {
        # Exercise the actual OS WKWebView API, including the Promise baseline.
        # This is an offscreen native engine check, not desktop visual acceptance.
        $nativeTools = Join-Path $output 'validation-tools'
        New-Item -ItemType Directory -Path $nativeTools | Out-Null
        $webkitProbe = Join-Path $nativeTools 'webkit-probe'
        Invoke-CiCommand /usr/bin/swiftc @('-parse-as-library', 'tools/WebKitProbe/main.swift', '-o', $webkitProbe) 'webkit-build.txt'
        Invoke-CiCommand $webkitProbe @((Join-Path $projectRoot 'src/GitX.Desktop/Editor/native-bridge.js'), (Join-Path $results 'webkit-native.json')) 'webkit-native.txt'
        $chromeHelper = Join-Path $nativeTools 'libgrove-chrome-probe.dylib'
        Invoke-CiCommand /usr/bin/clang @('-fobjc-arc', '-dynamiclib', '-framework', 'AppKit', 'tools/ChromeProbe/NativeChrome.m', '-o', $chromeHelper) 'chrome-native-build.txt'
        Invoke-CiCommand /usr/bin/codesign @('--force', '--sign', '-', $chromeHelper) 'chrome-native-sign.txt'
        Invoke-CiCommand dotnet @('run', '--project', 'tools/ChromeProbe/ChromeProbe.csproj', '-c', 'Release', '--no-build', '--no-restore', '--', (Join-Path $results 'chrome-native.json'), $chromeHelper) 'chrome-native.txt'
        $chrome = Get-Content -LiteralPath (Join-Path $results 'chrome-native.json') -Raw | ConvertFrom-Json
        if (-not $chrome.passed) { throw 'Native Grove titlebar regression failed.' }
    }
    $publishedFiles = @(Get-ChildItem -LiteralPath $publish -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
        @{ path = [IO.Path]::GetRelativePath($publish, $_.FullName).Replace('\', '/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    @{ runtimeIdentifier = $RuntimeIdentifier; sdk = (dotnet --version).Trim(); node = $nodeVersion; nodeArchitecture = $nodeArchitecture; nativeExecution = $true; platform = $platform; coreTests = @{ total = [int]$counts.total; passed = [int]$counts.passed; skipped = [int]$counts.notExecuted }; nativeGuiTested = $false; nativeChromeTested = $IsMacOS; physicalInputTested = $false; selfContained = $true; signedRelease = $false; files = $publishedFiles } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'metadata.json') -Encoding utf8
    & (Join-Path $PSScriptRoot 'package.ps1') -PublishDirectory $publish -OutputDirectory $output -RuntimeIdentifier $RuntimeIdentifier
    if ($IsWindows) {
        & (Join-Path $PSScriptRoot 'test-windows-package.ps1') -PackageDirectory $output -TestShellIntegration:($env:GITHUB_ACTIONS -eq 'true')
    } else {
        & (Join-Path $PSScriptRoot 'test-macos-package.ps1') -PackageDirectory $output
    }
    Write-Host "Validated Grove packages; native engine and chrome probe coverage is recorded separately."
} finally {
    Pop-Location
    foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
}
