[CmdletBinding()]
param(
    [Parameter()]
    [ValidateRange(1, 20)]
    [int]$Repetitions = 3,

    [Parameter()]
    [string]$OutputPath = "artifacts/scan-benchmark/results.json",

    [Parameter()]
    [string]$DevProjexPath,

    [Parameter()]
    [string]$CorpusName,

    [Parameter()]
    [switch]$KeepWorkspace
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if ([string]::IsNullOrWhiteSpace($DevProjexPath)) {
    $executableName = if ($IsWindows) { 'devprojex.exe' } else { 'devprojex' }
    $rid = if ($IsWindows) { 'win-x64' } elseif ($IsMacOS) { 'osx-x64' } else { 'linux-x64' }
    $DevProjexPath = Join-Path $repositoryRoot "Apps/TerminalHost/bin/Release/net10.0/$rid/$executableName"
}
$DevProjexPath = [System.IO.Path]::GetFullPath($DevProjexPath)
if (-not (Test-Path -LiteralPath $DevProjexPath -PathType Leaf)) {
    throw "Release CLI was not found at '$DevProjexPath'. Build Apps/TerminalHost in Release first."
}

$nodePath = (Get-Command node -CommandType Application -ErrorAction Stop).Source
$gitPath = (Get-Command git -CommandType Application -ErrorAction Stop).Source
$npxPath = if ($IsWindows) {
    (Get-Command npx.cmd -CommandType Application -ErrorAction Stop).Source
} else {
    (Get-Command npx -CommandType Application -ErrorAction Stop).Source
}

$corpora = @(
    [pscustomobject]@{
        Name = 'pallets/flask'
        Url = 'https://github.com/pallets/flask.git'
        Sha = 'd318b683471101618febed18996405ad26462110'
    },
    [pscustomobject]@{
        Name = 'yamadashy/repomix'
        Url = 'https://github.com/yamadashy/repomix.git'
        Sha = '85e3969b010c72b905203812d1a3f5beb84a2102'
    },
    [pscustomobject]@{
        Name = 'godotengine/godot'
        Url = 'https://github.com/godotengine/godot.git'
        Sha = '34d06658a85845111a50db9e485ec4a0701d4298'
    }
)
if (-not [string]::IsNullOrWhiteSpace($CorpusName)) {
    $corpora = @($corpora | Where-Object Name -eq $CorpusName)
    if ($corpora.Count -ne 1) {
        throw "Unknown corpus '$CorpusName'."
    }
}

$series = @(
    [pscustomobject]@{
        Name = 'defaults'
        DevProjexArguments = @()
        RepomixArguments = @()
    },
    [pscustomobject]@{
        Name = 'gitignore-only-with-secrets'
        DevProjexArguments = @('--exclude', 'none', '--git-mode', 'gitignore', '--hide-secrets')
        RepomixArguments = @('--no-default-patterns', '--no-dot-ignore')
    }
)

function Invoke-CheckedProcess {
    param(
        [Parameter(Mandatory)] [string]$FilePath,
        [Parameter(Mandatory)] [string[]]$ArgumentList,
        [Parameter(Mandatory)] [string]$WorkingDirectory
    )

    $process = Start-Process -FilePath $FilePath `
        -ArgumentList $ArgumentList `
        -WorkingDirectory $WorkingDirectory `
        -NoNewWindow `
        -Wait `
        -PassThru
    if ($process.ExitCode -ne 0) {
        throw "Process '$FilePath' exited with code $($process.ExitCode): $($ArgumentList -join ' ')"
    }
}

function Invoke-MeasuredProcess {
    param(
        [Parameter(Mandatory)] [string]$FilePath,
        [Parameter(Mandatory)] [string[]]$ArgumentList,
        [Parameter(Mandatory)] [string]$WorkingDirectory,
        [Parameter(Mandatory)] [string]$CacheDirectory
    )

    [System.IO.Directory]::CreateDirectory($CacheDirectory) | Out-Null
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $ArgumentList) {
        $startInfo.ArgumentList.Add($argument)
    }
    foreach ($name in @('HOME', 'USERPROFILE', 'LOCALAPPDATA', 'APPDATA', 'XDG_CACHE_HOME', 'XDG_CONFIG_HOME')) {
        $startInfo.Environment[$name] = $CacheDirectory
    }
    $startInfo.Environment['NO_COLOR'] = '1'
    $startInfo.Environment['CI'] = '1'

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    if (-not $process.Start()) {
        throw "Could not start '$FilePath'."
    }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    [long]$peakWorkingSet = 0
    while (-not $process.WaitForExit(20)) {
        $process.Refresh()
        $peakWorkingSet = [Math]::Max($peakWorkingSet, $process.WorkingSet64)
    }
    $process.Refresh()
    $peakWorkingSet = [Math]::Max($peakWorkingSet, $process.PeakWorkingSet64)
    $stopwatch.Stop()
    $stdout = $stdoutTask.GetAwaiter().GetResult()
    $stderr = $stderrTask.GetAwaiter().GetResult()
    $exitCode = $process.ExitCode
    $process.Dispose()
    if ($exitCode -ne 0) {
        throw "Process '$FilePath' exited with code ${exitCode}.`nstdout:`n${stdout}`nstderr:`n${stderr}"
    }
    [pscustomobject]@{
        ElapsedMilliseconds = [Math]::Round($stopwatch.Elapsed.TotalMilliseconds, 3)
        PeakRssBytes = $peakWorkingSet
        StandardOutput = $stdout
        StandardError = $stderr
    }
}

function Get-Median {
    param([Parameter(Mandatory)] [double[]]$Values)
    $ordered = @($Values | Sort-Object)
    $middle = [int][Math]::Floor($ordered.Count / 2)
    if (($ordered.Count % 2) -eq 1) {
        return $ordered[$middle]
    }
    return ($ordered[$middle - 1] + $ordered[$middle]) / 2
}

function Get-RepomixMetric {
    param(
        [Parameter(Mandatory)] [string]$Output,
        [Parameter(Mandatory)] [string]$Label
    )
    $match = [regex]::Match($Output, "${Label}:\s*(?<value>[0-9\s\u00a0\u202f]+)", 'IgnoreCase')
    if (-not $match.Success) {
        throw "Repomix did not report '$Label'."
    }
    $digits = [regex]::Replace($match.Groups['value'].Value, '[^0-9]', '')
    return [long]::Parse($digits, [Globalization.CultureInfo]::InvariantCulture)
}

function New-IsolatedCache {
    param([Parameter(Mandatory)] [string]$Parent, [Parameter(Mandatory)] [string]$Name)
    $path = Join-Path $Parent $Name
    [System.IO.Directory]::CreateDirectory($path) | Out-Null
    return $path
}

function Invoke-DevProjexPair {
    param(
        [Parameter(Mandatory)] [string]$Worktree,
        [Parameter(Mandatory)] [string]$RunRoot,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]]$SeriesArguments
    )
    $cache = New-IsolatedCache $RunRoot 'devprojex-cache'
    $measurements = @()
    foreach ($temperature in @('cold', 'warm')) {
        $analysisArguments = @('analyze', $Worktree, '--format', 'json', '-o', '-', '--top-files', '1') + $SeriesArguments
        if ($SeriesArguments -contains '--hide-secrets') {
            $analysisArguments += '--findings'
        }
        $analysis = Invoke-MeasuredProcess $DevProjexPath $analysisArguments $RunRoot $cache
        $analysisJson = $analysis.StandardOutput | ConvertFrom-Json
        $outputFile = Join-Path $RunRoot "devprojex-${temperature}.md"
        $exportArguments = @(
            'export', 'context', $Worktree,
            '--view', 'tree-content',
            '--format', 'markdown',
            '-o', $outputFile,
            '--force'
        ) + $SeriesArguments
        $export = Invoke-MeasuredProcess $DevProjexPath $exportArguments $RunRoot $cache
        $measurements += [pscustomobject]@{
            Temperature = $temperature
            ElapsedMilliseconds = [Math]::Round($analysis.ElapsedMilliseconds + $export.ElapsedMilliseconds, 3)
            AnalyzeMilliseconds = $analysis.ElapsedMilliseconds
            ExportMilliseconds = $export.ElapsedMilliseconds
            PeakRssBytes = [Math]::Max($analysis.PeakRssBytes, $export.PeakRssBytes)
            IncludedFiles = [long]$analysisJson.inventory.files
            OutputBytes = (Get-Item -LiteralPath $outputFile).Length
            EstimatedTokens = [long]$analysisJson.metrics.tree.tokens + [long]$analysisJson.metrics.content.tokens
        }
    }
    return $measurements
}

function Invoke-RepomixPair {
    param(
        [Parameter(Mandatory)] [string]$Worktree,
        [Parameter(Mandatory)] [string]$RunRoot,
        [Parameter(Mandatory)] [AllowEmptyCollection()] [string[]]$SeriesArguments,
        [Parameter(Mandatory)] [string]$RepomixCli,
        [Parameter(Mandatory)] [string]$EmptyConfig
    )
    $cache = New-IsolatedCache $RunRoot 'repomix-cache'
    $measurements = @()
    foreach ($temperature in @('cold', 'warm')) {
        $outputFile = Join-Path $RunRoot "repomix-${temperature}.md"
        $arguments = @(
            $RepomixCli,
            $Worktree,
            '--config', $EmptyConfig,
            '--style', 'markdown',
            '--output', $outputFile
        ) + $SeriesArguments
        $measurement = Invoke-MeasuredProcess $nodePath $arguments $RunRoot $cache
        $console = $measurement.StandardOutput + "`n" + $measurement.StandardError
        $measurements += [pscustomobject]@{
            Temperature = $temperature
            ElapsedMilliseconds = $measurement.ElapsedMilliseconds
            AnalyzeMilliseconds = $null
            ExportMilliseconds = $measurement.ElapsedMilliseconds
            PeakRssBytes = $measurement.PeakRssBytes
            IncludedFiles = Get-RepomixMetric $console 'Total Files'
            OutputBytes = (Get-Item -LiteralPath $outputFile).Length
            EstimatedTokens = Get-RepomixMetric $console 'Total Tokens'
        }
    }
    return $measurements
}

$systemTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$workspace = Join-Path $systemTemp ("DevProjex-ScanBenchmark-" + [Guid]::NewGuid().ToString('N'))
$workspace = [System.IO.Path]::GetFullPath($workspace)
if (-not $workspace.StartsWith($systemTemp, [StringComparison]::OrdinalIgnoreCase) -or
    -not ([System.IO.Path]::GetFileName($workspace)).StartsWith('DevProjex-ScanBenchmark-', [StringComparison]::Ordinal)) {
    throw "Refusing unsafe benchmark workspace '$workspace'."
}
[System.IO.Directory]::CreateDirectory($workspace) | Out-Null

try {
    $npmCache = Join-Path $workspace 'npm-cache'
    [System.IO.Directory]::CreateDirectory($npmCache) | Out-Null
    Invoke-CheckedProcess $npxPath @('--yes', '--cache', $npmCache, 'repomix@1.17.0', '--version') $workspace
    $repomixCli = Get-ChildItem -LiteralPath (Join-Path $npmCache '_npx') -Recurse -File -Filter 'repomix.cjs' |
        Where-Object { $_.FullName -match '[\\/]node_modules[\\/]repomix[\\/]bin[\\/]repomix\.cjs$' } |
        Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($repomixCli)) {
        throw 'Could not locate the npx-acquired Repomix 1.17.0 entry point.'
    }
    $repomixPackage = Get-Content -LiteralPath (Join-Path (Split-Path (Split-Path $repomixCli)) 'package.json') -Raw | ConvertFrom-Json
    if ($repomixPackage.version -ne '1.17.0') {
        throw "Expected Repomix 1.17.0, found '$($repomixPackage.version)'."
    }
    $emptyConfig = Join-Path $workspace 'repomix.empty.json'
    [System.IO.File]::WriteAllText($emptyConfig, "{}`n", [System.Text.UTF8Encoding]::new($false))

    $rawResults = @()
    $retryEvents = @()
    $corpusMetadata = @()
    foreach ($corpus in $corpora) {
        $slug = $corpus.Name.Replace('/', '-')
        $bare = Join-Path $workspace "${slug}.git"
        Invoke-CheckedProcess $gitPath @('init', '--bare', $bare) $workspace
        Invoke-CheckedProcess $gitPath @('-C', $bare, 'config', 'core.autocrlf', 'false') $workspace
        if ($IsWindows) {
            Invoke-CheckedProcess $gitPath @('-C', $bare, 'config', 'core.longpaths', 'true') $workspace
        }
        Invoke-CheckedProcess $gitPath @('-C', $bare, 'fetch', '--depth', '1', '--no-tags', $corpus.Url, $corpus.Sha) $workspace
        $trackedCount = [long]((& $gitPath --git-dir $bare ls-tree -r --name-only $corpus.Sha | Measure-Object -Line).Lines)
        $corpusMetadata += [pscustomobject]@{
            Name = $corpus.Name
            Sha = $corpus.Sha
            TrackedFiles = $trackedCount
        }

        foreach ($currentSeries in $series) {
            foreach ($repetition in 1..$Repetitions) {
                foreach ($tool in @('devprojex', 'repomix')) {
                    $worktree = Join-Path $workspace ("worktree-{0}-{1}-{2}-{3}" -f $slug, $currentSeries.Name, $repetition, $tool)
                    Invoke-CheckedProcess $gitPath @('--git-dir', $bare, 'worktree', 'add', '--quiet', '--detach', $worktree, $corpus.Sha) $workspace
                    try {
                        $runRoot = Join-Path $workspace ("run-{0}-{1}-{2}-{3}" -f $slug, $currentSeries.Name, $repetition, $tool)
                        $pair = $null
                        foreach ($attempt in 1..2) {
                            if (Test-Path -LiteralPath $runRoot) {
                                Remove-Item -LiteralPath $runRoot -Recurse -Force
                            }
                            [System.IO.Directory]::CreateDirectory($runRoot) | Out-Null
                            try {
                                $pair = if ($tool -eq 'devprojex') {
                                    Invoke-DevProjexPair $worktree $runRoot $currentSeries.DevProjexArguments
                                } else {
                                    Invoke-RepomixPair $worktree $runRoot $currentSeries.RepomixArguments $repomixCli $emptyConfig
                                }
                                break
                            } catch {
                                if ($attempt -eq 2) {
                                    throw
                                }
                                $retryEvents += [pscustomobject]@{
                                    Corpus = $corpus.Name
                                    Series = $currentSeries.Name
                                    Repetition = $repetition
                                    Tool = $tool
                                    Reason = ($_.Exception.Message -split "`r?`n", 2)[0]
                                }
                                Write-Warning "Discarding failed $tool measurement and retrying with a clean cache: $($_.Exception.Message)"
                            }
                        }
                        foreach ($measurement in $pair) {
                            $rawResults += [pscustomobject]@{
                                Corpus = $corpus.Name
                                Sha = $corpus.Sha
                                Series = $currentSeries.Name
                                Repetition = $repetition
                                Tool = $tool
                                Temperature = $measurement.Temperature
                                ElapsedMilliseconds = $measurement.ElapsedMilliseconds
                                AnalyzeMilliseconds = $measurement.AnalyzeMilliseconds
                                ExportMilliseconds = $measurement.ExportMilliseconds
                                PeakRssBytes = $measurement.PeakRssBytes
                                IncludedFiles = $measurement.IncludedFiles
                                OutputBytes = $measurement.OutputBytes
                                EstimatedTokens = $measurement.EstimatedTokens
                            }
                        }
                    } finally {
                        Invoke-CheckedProcess $gitPath @('--git-dir', $bare, 'worktree', 'remove', '--force', $worktree) $workspace
                    }
                }
                Write-Host "Measured $($corpus.Name), $($currentSeries.Name), repetition $repetition/$Repetitions."
            }
        }
    }

    $medians = @($rawResults |
        Group-Object Corpus, Series, Tool, Temperature |
        ForEach-Object {
            $items = @($_.Group)
            [pscustomobject]@{
                Corpus = $items[0].Corpus
                Series = $items[0].Series
                Tool = $items[0].Tool
                Temperature = $items[0].Temperature
                ElapsedMilliseconds = Get-Median ([double[]]$items.ElapsedMilliseconds)
                AnalyzeMilliseconds = if ($items[0].AnalyzeMilliseconds -is [double]) {
                    Get-Median ([double[]]$items.AnalyzeMilliseconds)
                } else { $null }
                ExportMilliseconds = Get-Median ([double[]]$items.ExportMilliseconds)
                PeakRssBytes = [long](Get-Median ([double[]]$items.PeakRssBytes))
                IncludedFiles = [long](Get-Median ([double[]]$items.IncludedFiles))
                OutputBytes = [long](Get-Median ([double[]]$items.OutputBytes))
                EstimatedTokens = [long](Get-Median ([double[]]$items.EstimatedTokens))
            }
        })

    $output = [pscustomobject]@{
        SchemaVersion = 1
        MeasuredUtc = [DateTimeOffset]::UtcNow.ToString('O')
        Platform = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
        Repetitions = $Repetitions
        DevProjexVersion = (& $DevProjexPath --version).Trim()
        RepomixVersion = [string]$repomixPackage.version
        ColdDefinition = 'new process, fresh worktree path, and fresh per-tool application/config cache; operating-system page cache is not flushed'
        TimingDefinition = 'DevProjex elapsed time is analyze plus export context; Repomix elapsed time is one pack command'
        FailurePolicy = 'discard a failed tool pair and retry once with a clean application cache; fail without writing a partial report after a second failure'
        Retries = $retryEvents
        Corpora = $corpusMetadata
        Medians = $medians
        Raw = $rawResults
    }
    $resolvedOutput = if ([System.IO.Path]::IsPathRooted($OutputPath)) {
        [System.IO.Path]::GetFullPath($OutputPath)
    } else {
        [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputPath))
    }
    [System.IO.Directory]::CreateDirectory((Split-Path $resolvedOutput)) | Out-Null
    [System.IO.File]::WriteAllText(
        $resolvedOutput,
        ($output | ConvertTo-Json -Depth 8) + "`n",
        [System.Text.UTF8Encoding]::new($false))
    Write-Host "Benchmark results: $resolvedOutput"
} finally {
    if ($KeepWorkspace) {
        Write-Host "Benchmark workspace retained: $workspace"
    } elseif (Test-Path -LiteralPath $workspace) {
        Remove-Item -LiteralPath $workspace -Recurse -Force
    }
}
