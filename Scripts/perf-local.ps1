param(
    [switch]$UpdateBaseline,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$env:DEVPROJEX_RUN_LOCAL_PERF = "1"
$env:DEVPROJEX_PERF_UPDATE_BASELINE = if ($UpdateBaseline) { "1" } else { "0" }
$env:DEVPROJEX_PERF_ENFORCE_BASELINE = "0"

if (-not $env:DEVPROJEX_PERF_BASELINE_PATH) {
    $env:DEVPROJEX_PERF_BASELINE_PATH = Join-Path $env:LOCALAPPDATA "DevProjex\Performance\perf-baseline.local.json"
}

Write-Host "DEVPROJEX_RUN_LOCAL_PERF=$($env:DEVPROJEX_RUN_LOCAL_PERF)"
Write-Host "DEVPROJEX_PERF_UPDATE_BASELINE=$($env:DEVPROJEX_PERF_UPDATE_BASELINE)"
Write-Host "DEVPROJEX_PERF_ENFORCE_BASELINE=$($env:DEVPROJEX_PERF_ENFORCE_BASELINE)"
Write-Host "DEVPROJEX_PERF_BASELINE_PATH=$($env:DEVPROJEX_PERF_BASELINE_PATH)"

dotnet test "Tests/DevProjex.Tests.Integration/DevProjex.Tests.Integration.csproj" `
    -c $Configuration `
    --filter "Category=LocalPerformance" `
    --verbosity minimal
