param(
    [Parameter(Mandatory = $true)]
    [string] $BaselineHost,

    [string] $CurrentHost = "Apps/TerminalHost/bin/Release/net10.0/win-x64/devprojex.dll",

    [string] $ProjectRoot = ".",

    [ValidateRange(5, 100)]
    [int] $Repetitions = 5
)

$ErrorActionPreference = "Stop"

$currentHostPath = (Resolve-Path -LiteralPath $CurrentHost).Path
$baselineHostPath = (Resolve-Path -LiteralPath $BaselineHost).Path
$projectRootPath = (Resolve-Path -LiteralPath $ProjectRoot).Path
$scratch = Join-Path ([System.IO.Path]::GetTempPath()) ("DevProjex-SecretsOperations-" + [Guid]::NewGuid().ToString("N"))
[void][System.IO.Directory]::CreateDirectory($scratch)

function Invoke-MeasuredOperation {
    param(
        [string] $Build,
        [string] $HostPath,
        [string] $Operation,
        [int] $Iteration,
        [bool] $Warmup
    )

    $outputPath = Join-Path $scratch "$Build-$Operation-$Iteration.out"
    $arguments = switch ($Operation) {
        "analyze" {
            @($HostPath, "analyze", $projectRootPath, "--hide-secrets", "on", "--format", "json",
                "-o", $outputPath, "--force", "--progress", "never", "--plain")
        }
        "export" {
            @($HostPath, "export", "context", $projectRootPath, "--hide-secrets", "on", "--format", "markdown",
                "-o", $outputPath, "--force", "--progress", "never", "--plain")
        }
        "measure" {
            @($HostPath, "export", "context", $projectRootPath, "--hide-secrets", "on", "--format", "markdown",
                "--dry-run", "--progress", "never", "--plain")
        }
        default { throw "Unknown operation '$Operation'." }
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = "dotnet"
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $arguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    if (-not $process.Start()) {
        throw "Could not start $Build $Operation."
    }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    [long] $peakWorkingSet = 0
    while (-not $process.WaitForExit(25)) {
        $process.Refresh()
        $peakWorkingSet = [Math]::Max($peakWorkingSet, $process.WorkingSet64)
    }
    $process.Refresh()
    $peakWorkingSet = [Math]::Max($peakWorkingSet, $process.PeakWorkingSet64)
    $stopwatch.Stop()
    $stdoutText = $stdout.GetAwaiter().GetResult()
    $stderrText = $stderr.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) {
        throw "$Build $Operation failed with exit code $($process.ExitCode): $stderrText"
    }

    if ($Warmup) {
        return
    }
    [pscustomobject]@{
        Build = $Build
        Operation = $Operation
        Iteration = $Iteration
        Milliseconds = [Math]::Round($stopwatch.Elapsed.TotalMilliseconds, 2)
        PeakMiB = [Math]::Round($peakWorkingSet / 1MB, 2)
        ReplyBytes = [Text.Encoding]::UTF8.GetByteCount($stdoutText + $stderrText)
    }
}

try {
    $hosts = [ordered]@{ baseline = $baselineHostPath; current = $currentHostPath }
    foreach ($operation in @("analyze", "export", "measure")) {
        foreach ($entry in $hosts.GetEnumerator()) {
            Invoke-MeasuredOperation $entry.Key $entry.Value $operation 0 $true
        }
    }

    $measurements = @()
    foreach ($iteration in 1..$Repetitions) {
        $orderedBuilds = if ($iteration % 2 -eq 0) { @("current", "baseline") } else { @("baseline", "current") }
        foreach ($operation in @("analyze", "export", "measure")) {
            foreach ($build in $orderedBuilds) {
                $measurements += Invoke-MeasuredOperation $build $hosts[$build] $operation $iteration $false
            }
        }
    }

    $measurements | ConvertTo-Json -Depth 3
}
finally {
    if (Test-Path -LiteralPath $scratch) {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
}
