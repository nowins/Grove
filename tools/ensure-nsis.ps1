[CmdletBinding()]
param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
# Official portable distribution; no system installation or PATH changes.
$version = '3.13'
$expectedHash = 'ba63dffc4410ee89193e1cb5a41989991bd77c61068da17e3156d136b7b0b3d8'
$root = [IO.Path]::GetFullPath($Destination)
$compiler = Join-Path $root "nsis-$version/makensis.exe"
if (-not (Test-Path -LiteralPath $compiler)) {
    if (Test-Path -LiteralPath $root) { throw 'NSIS tool destination already exists without the compiler.' }
    New-Item -ItemType Directory -Path $root | Out-Null
    $archive = Join-Path $root "nsis-$version.zip"
    & curl.exe --location --fail --retry 2 --connect-timeout 15 --max-time 120 "https://sourceforge.net/projects/nsis/files/NSIS%203/$version/nsis-$version.zip/download" --output $archive
    if ($LASTEXITCODE -ne 0) { throw 'Official NSIS download failed.' }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) { throw 'NSIS download hash mismatch.' }
    Expand-Archive -LiteralPath $archive -DestinationPath $root
}
$actualVersion = (& $compiler /VERSION).Trim()
if ($LASTEXITCODE -ne 0 -or $actualVersion -ne "v$version") { throw "Unexpected NSIS compiler: $actualVersion" }
Write-Output $compiler
