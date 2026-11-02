param([string]$Repository = '', [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
try {
    if (-not $SkipBuild) {
        & npm ci --prefix src/GitX.Desktop/Editor
        if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' }
        & npm run build --prefix src/GitX.Desktop/Editor
        if ($LASTEXITCODE -ne 0) { throw 'Editor build failed' }
        & dotnet restore Grove.slnx --locked-mode
        if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed' }
        & dotnet build Grove.slnx -c Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
    }
    $appHost = if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) { 'GitX.Desktop.exe' } else { 'GitX.Desktop' }
    $executable = Join-Path $projectRoot ('src/GitX.Desktop/bin/Release/net10.0/' + $appHost)
    if ($Repository) { & $executable $Repository } else { & $executable }
} finally { Pop-Location }
