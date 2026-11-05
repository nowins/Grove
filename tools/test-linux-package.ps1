[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
if (-not $IsLinux) { throw 'Validate Linux packages on Linux.' }
$root = [IO.Path]::GetFullPath($PackageDirectory)
$metadata = Get-Content -LiteralPath (Join-Path $root 'package-metadata.json') -Raw | ConvertFrom-Json
if ($metadata.runtimeIdentifier -notin @('linux-x64', 'linux-arm64')) { throw 'Not a Linux package.' }
$archive = Join-Path $root "desktop-$($metadata.runtimeIdentifier).tar.gz"
$expectedHash = (Get-Content -LiteralPath (Join-Path $root 'SHA256SUMS.txt') | Where-Object { $_ -match "  $([regex]::Escape([IO.Path]::GetFileName($archive)))$" })
if (@($expectedHash).Count -ne 1 -or (Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant() -ne $expectedHash.Split(' ')[0]) { throw 'Linux archive checksum mismatch.' }
$target = Join-Path $root 'package test with spaces'
if (Test-Path -LiteralPath $target) { throw 'Package test directory already exists.' }
New-Item -ItemType Directory -Path $target | Out-Null
& tar -xzf $archive -C $target
if ($LASTEXITCODE -ne 0) { throw 'Linux archive extraction failed.' }
foreach ($file in $metadata.files) {
    $path = Join-Path $target $file.path
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $file.sha256) { throw "Package file differs: $($file.path)" }
}
if (@(Get-ChildItem -LiteralPath $target -Recurse -File -Force).Count -ne @($metadata.files).Count) { throw 'Linux archive contains unexpected files.' }
& test -x (Join-Path $target 'Grove')
if ($LASTEXITCODE -ne 0) { throw 'Linux apphost is not executable.' }
@{ passed = $true; runtimeIdentifier = $metadata.runtimeIdentifier; files = @($metadata.files).Count; executable = $true; nativeGuiTested = $false } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'results/package-linux.json') -Encoding utf8
