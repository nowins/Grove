[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$Baseline,
    [string]$Budgets,
    [ValidateRange(5,100)][int]$Runs = 9,
    [ValidateRange(1,100)][int]$Warmup = 3
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = 'artifacts/perf/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff') }
$output = [IO.Path]::GetFullPath($OutputDirectory, $projectRoot)
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
$comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
if (-not $output.StartsWith($artifactRoot, $comparison)) { throw 'Performance output must be inside this checkout artifacts directory.' }
if (Test-Path -LiteralPath $output) { throw 'Output already exists; choose a new directory.' }
if (-not $Budgets) { $Budgets = Join-Path $PSScriptRoot 'PerformanceProbe/budgets.json' }
if ($Baseline) { $Baseline = (Resolve-Path -LiteralPath $Baseline).Path }
$Budgets = (Resolve-Path -LiteralPath $Budgets).Path
New-Item -ItemType Directory -Path $output | Out-Null
Copy-Item -LiteralPath $Budgets -Destination (Join-Path $output 'budgets.json')
$Budgets = Join-Path $output 'budgets.json'
Push-Location $projectRoot
try {
    & dotnet restore tools/PerformanceProbe/PerformanceProbe.csproj --locked-mode 2>&1 | Tee-Object -FilePath (Join-Path $output 'restore.log')
    if ($LASTEXITCODE -ne 0) { throw 'Performance probe restore failed.' }
    & dotnet build tools/PerformanceProbe/PerformanceProbe.csproj -c Release --no-restore --warnaserror 2>&1 | Tee-Object -FilePath (Join-Path $output 'build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Performance probe build failed.' }
    $probeArgs = @('tools/PerformanceProbe/bin/Release/net10.0/PerformanceProbe.dll', '--output', $output, '--budgets', $Budgets, '--runs', "$Runs", '--warmup', "$Warmup")
    if ($Baseline) { $probeArgs += @('--baseline', $Baseline) }
    & dotnet @probeArgs 2>&1 | Tee-Object -FilePath (Join-Path $output 'run.log')
    if ($LASTEXITCODE -ne 0) { throw "Performance regression check failed. See $output/run.log and report.md or failure.json when generated." }
} finally { Pop-Location }
