<#
.SYNOPSIS
Runs opt-in DevProjex performance scenarios with isolated environment flags.

.EXAMPLE
./Scripts/perf-local.ps1

.EXAMPLE
./Scripts/perf-local.ps1 -Scenario Large,TreeMetrics

.EXAMPLE
./Scripts/perf-local.ps1 -Scenario AuditRound2 -KeepResults
#>
param(
    [ValidateSet(
        "Smoke",
        "Large",
        "AuditRound2",
        "Compression",
        "TreeMetrics",
        "PreviewStorage",
        "SelectedContent",
        "SecretInspection",
        "SecretHmac",
        "ContentSelection",
        "SelectionHash",
        "All")]
    [string[]]$Scenario = @("Smoke"),
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$KeepResults
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPaths = @{
    Integration = Join-Path $repositoryRoot "Tests/DevProjex.Tests.Integration/DevProjex.Tests.Integration.csproj"
    Unit = Join-Path $repositoryRoot "Tests/DevProjex.Tests.Unit/DevProjex.Tests.Unit.csproj"
    Terminal = Join-Path $repositoryRoot "Tests/DevProjex.Tests.Terminal/DevProjex.Tests.Terminal.csproj"
}

$knownOptInVariables = @(
    "DEVPROJEX_RUN_LARGE_PERF_TESTS",
    "DEVPROJEX_RUN_PERFORMANCE_AUDIT_ROUND2",
    "DEVPROJEX_RUN_COMPRESSION_OPT_BENCHMARK",
    "DEVPROJEX_RUN_TREE_METRICS_BENCHMARK",
    "DEVPROJEX_RUN_PREVIEW_STORAGE_BENCHMARK",
    "DEVPROJEX_RUN_SELECTED_CONTENT_BENCHMARK",
    "DEVPROJEX_RUN_SECRET_INSPECTION_BENCHMARK",
    "DEVPROJEX_RUN_SECRET_HMAC_BENCHMARK",
    "DEVPROJEX_RUN_CONTENT_SELECTION_BENCHMARK",
    "DEVPROJEX_RUN_SELECTION_HASH_BENCHMARK"
)

$orderedScenarios = @(
    "Smoke",
    "Large",
    "TreeMetrics",
    "PreviewStorage",
    "SelectedContent",
    "ContentSelection",
    "SelectionHash",
    "SecretInspection",
    "SecretHmac",
    "Compression",
    "AuditRound2"
)

$scenarioFlags = @{
    Smoke = @()
    Large = @("DEVPROJEX_RUN_LARGE_PERF_TESTS")
    AuditRound2 = @("DEVPROJEX_RUN_PERFORMANCE_AUDIT_ROUND2")
    Compression = @("DEVPROJEX_RUN_COMPRESSION_OPT_BENCHMARK")
    TreeMetrics = @("DEVPROJEX_RUN_TREE_METRICS_BENCHMARK")
    PreviewStorage = @("DEVPROJEX_RUN_PREVIEW_STORAGE_BENCHMARK")
    SelectedContent = @("DEVPROJEX_RUN_SELECTED_CONTENT_BENCHMARK")
    SecretInspection = @("DEVPROJEX_RUN_SECRET_INSPECTION_BENCHMARK")
    SecretHmac = @("DEVPROJEX_RUN_SECRET_HMAC_BENCHMARK")
    ContentSelection = @("DEVPROJEX_RUN_CONTENT_SELECTION_BENCHMARK")
    SelectionHash = @("DEVPROJEX_RUN_SELECTION_HASH_BENCHMARK")
}

$scenarioRuns = @{
    Smoke = @(
        [pscustomobject]@{
            Project = "Integration"
            Filter = "FullyQualifiedName~CodeCompressionPerformanceCharacterizationTests|FullyQualifiedName~SmartSecretsPerformanceCharacterizationTests|FullyQualifiedName~IgnorePipelinePerformanceSmokeIntegrationTests"
        }
    )
    Large = @(
        [pscustomobject]@{ Project = "Integration"; Filter = "Category=LocalPerformance" },
        [pscustomobject]@{ Project = "Unit"; Filter = "Category=LocalPerformance" },
        [pscustomobject]@{ Project = "Terminal"; Filter = "FullyQualifiedName~TerminalWorkspaceCommandCompletionPerformanceTests" }
    )
    AuditRound2 = @(
        [pscustomobject]@{ Project = "Integration"; Filter = "FullyQualifiedName~PerformanceAuditRound2Tests" }
    )
    Compression = @(
        [pscustomobject]@{ Project = "Integration"; Filter = "FullyQualifiedName~CompressionOptimizationBenchmarkTests" }
    )
    TreeMetrics = @(
        [pscustomobject]@{ Project = "Integration"; Filter = "FullyQualifiedName~TreeExportMetricsPerformanceTests" }
    )
    PreviewStorage = @(
        [pscustomobject]@{ Project = "Integration"; Filter = "FullyQualifiedName~PreviewDocumentBuilderPerformanceTests" }
    )
    SelectedContent = @(
        [pscustomobject]@{ Project = "Integration"; Filter = "FullyQualifiedName~SelectedContentExportPerformanceTests" }
    )
    SecretInspection = @(
        [pscustomobject]@{ Project = "Unit"; Filter = "FullyQualifiedName~GitleaksSecretDetectorAcceptanceBenchmarkTests" }
    )
    SecretHmac = @(
        [pscustomobject]@{ Project = "Unit"; Filter = "FullyQualifiedName~PersistentSecretIdentityConcurrencyBenchmarkTests" }
    )
    ContentSelection = @(
        [pscustomobject]@{ Project = "Unit"; Filter = "FullyQualifiedName~ContentSelectionSnapshotPerformanceTests" }
    )
    SelectionHash = @(
        [pscustomobject]@{ Project = "Unit"; Filter = "FullyQualifiedName~PreviewSelectionHashPerformanceTests" }
    )
}

function Set-ScenarioOptInVariables {
    param([string[]]$EnabledVariables)

    foreach ($name in $knownOptInVariables) {
        [Environment]::SetEnvironmentVariable(
            $name,
            $null,
            [EnvironmentVariableTarget]::Process)
    }
    foreach ($name in $EnabledVariables) {
        [Environment]::SetEnvironmentVariable(
            $name,
            "1",
            [EnvironmentVariableTarget]::Process)
    }
}

function Get-TestCounters {
    param([string]$TrxPath)

    [xml]$document = Get-Content -LiteralPath $TrxPath -Raw
    $counters = $document.SelectSingleNode("//*[local-name()='Counters']")
    if ($null -eq $counters) {
        throw "TRX result does not contain test counters: $TrxPath"
    }
    return [pscustomobject]@{
        Executed = [int]$counters.GetAttribute("executed")
        Passed = [int]$counters.GetAttribute("passed")
        Skipped = [int]$counters.GetAttribute("notExecuted")
    }
}

function Invoke-PerformanceRun {
    param(
        [string]$ScenarioName,
        [pscustomobject]$Run,
        [string]$ResultDirectory
    )

    $projectPath = $projectPaths[$Run.Project]
    if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
        throw "Performance test project was not found: $projectPath"
    }

    $trxName = "$($ScenarioName)-$($Run.Project).trx"
    $arguments = @(
        "test",
        $projectPath,
        "-c", $Configuration,
        "--filter", $Run.Filter,
        "--logger", "trx;LogFileName=$trxName",
        "--results-directory", $ResultDirectory,
        "--verbosity", "minimal"
    )

    Write-Host "[$ScenarioName/$($Run.Project)] $($Run.Filter)"
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Performance test process failed with exit code $LASTEXITCODE."
    }

    $trx = Get-ChildItem -LiteralPath $ResultDirectory -Filter $trxName -Recurse -File |
        Select-Object -First 1
    if ($null -eq $trx) {
        throw "Performance test process did not produce the expected TRX result: $trxName"
    }

    $counters = Get-TestCounters $trx.FullName
    if ($counters.Executed -le 0 -or $counters.Passed -le 0) {
        throw "Performance scenario '$ScenarioName' executed no tests; the selection contained only skipped or missing tests."
    }
    Write-Host (
        "[$ScenarioName/$($Run.Project)] executed: $($counters.Executed); " +
        "passed: $($counters.Passed); skipped: $($counters.Skipped)")
}

$requestedScenarios = if ($Scenario -contains "All") { $orderedScenarios } else { $Scenario }
$selectedScenarios = @()
foreach ($name in $requestedScenarios) {
    if ($selectedScenarios -notcontains $name) {
        $selectedScenarios += $name
    }
}

$originalVariables = @{}
foreach ($name in $knownOptInVariables) {
    $originalVariables[$name] = [Environment]::GetEnvironmentVariable(
        $name,
        [EnvironmentVariableTarget]::Process)
}

$resultDirectory = Join-Path (
    [IO.Path]::GetTempPath()) ("devprojex-perf-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $resultDirectory | Out-Null
$succeeded = $false

try {
    foreach ($scenarioName in $selectedScenarios) {
        $flags = @($scenarioFlags[$scenarioName])
        Set-ScenarioOptInVariables $flags
        $flagDisplay = if ($flags.Count -eq 0) { "none" } else { $flags -join ", " }
        Write-Host "Scenario: $scenarioName; opt-in: $flagDisplay"
        foreach ($run in @($scenarioRuns[$scenarioName])) {
            Invoke-PerformanceRun $scenarioName $run $resultDirectory
        }
    }
    $succeeded = $true
}
finally {
    foreach ($name in $knownOptInVariables) {
        [Environment]::SetEnvironmentVariable(
            $name,
            $originalVariables[$name],
            [EnvironmentVariableTarget]::Process)
    }

    if ($succeeded -and -not $KeepResults) {
        Remove-Item -LiteralPath $resultDirectory -Recurse -Force
    }
    else {
        Write-Host "Performance results: $resultDirectory"
    }
}
