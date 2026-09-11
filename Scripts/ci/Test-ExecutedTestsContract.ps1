#Requires -Version 7.0
<#
.SYNOPSIS
	Checks that Test-ExecutedTests.ps1 still accepts a healthy run and still rejects an empty one.

.DESCRIPTION
	A check that silently stops checking is worse than no check, because it keeps reporting success
	while the thing it guards has gone. The zero-test check has already failed in both directions
	once: it passed nothing it should have caught while it was only reading a console summary, and
	it failed a completed run of 433 tests because it looked for the results in the wrong place.

	So the cases below run in both directions. The accepting cases are the positive control: if the
	check stops finding results, they fail rather than the check quietly passing everything. The
	rejecting cases are the negative control: if the check becomes a no-op, they fail too. Between
	them there is no state in which the check does nothing and this script still passes.
#>
[CmdletBinding()]
param(
	[string] $CheckPath = (Join-Path $PSScriptRoot 'Test-ExecutedTests.ps1')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$failures = [Collections.Generic.List[string]]::new()
$root = Join-Path ([IO.Path]::GetTempPath()) ("devprojex-executed-" + [Guid]::NewGuid().ToString('N'))

function New-VsTestResult {
	param([string] $Path, [int] $Total, [int] $Executed, [string] $Outcome = 'Completed')

	New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
	@"
<?xml version="1.0" encoding="UTF-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <ResultSummary outcome="$Outcome">
    <Counters total="$Total" executed="$Executed" passed="$Executed" failed="0" error="0" timeout="0"
              aborted="0" inconclusive="0" passedButRunAborted="0" notRunnable="0" notExecuted="0"
              disconnected="0" warning="0" completed="0" inProgress="0" pending="0" />
  </ResultSummary>
</TestRun>
"@ | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Test-Case {
	param([string] $Name, [string[]] $ResultsPath, [switch] $ShouldPass)

	$accepted = $true
	$message = ''
	try {
		& $CheckPath -ResultsPath $ResultsPath -JobName "contract: $Name" | Out-Null
	}
	catch {
		$accepted = $false
		$message = $_.Exception.Message
	}

	if ($ShouldPass -and -not $accepted) {
		$failures.Add("$Name should have been accepted but was rejected: $message")
	}
	elseif (-not $ShouldPass -and $accepted) {
		$failures.Add("$Name should have been rejected but was accepted.")
	}
}

try {
	# --- accepted: the check must still recognise a run that happened -------------------------
	$healthy = Join-Path $root 'healthy/TestResults/unit'
	New-VsTestResult -Path (Join-Path $healthy 'unit.trx') -Total 12 -Executed 12
	Test-Case -Name 'a completed run' -ResultsPath $healthy -ShouldPass

	$twoDirectories = @(
		(Join-Path $root 'two/TestResults/terminal-command-unit'),
		(Join-Path $root 'two/TestResults/terminal-command-integration'))
	New-VsTestResult -Path (Join-Path $twoDirectories[0] 'a.trx') -Total 4 -Executed 4
	New-VsTestResult -Path (Join-Path $twoDirectories[1] 'b.trx') -Total 7 -Executed 7
	Test-Case -Name 'two results directories, both with tests' -ResultsPath $twoDirectories -ShouldPass

	# The xUnit report writer emits the same schema; a suite on that runner must be readable too.
	$xunit = Join-Path $root 'xunit/TestResults/ui'
	New-VsTestResult -Path (Join-Path $xunit 'ui.trx') -Total 433 -Executed 433
	Test-Case -Name 'a run reported by the xunit trx writer' -ResultsPath $xunit -ShouldPass

	# --- rejected: the check must still catch a run that proves nothing ------------------------
	$empty = Join-Path $root 'empty/TestResults/unit'
	New-VsTestResult -Path (Join-Path $empty 'unit.trx') -Total 0 -Executed 0
	Test-Case -Name 'a run that executed nothing' -ResultsPath $empty

	$noFile = Join-Path $root 'nofile/TestResults/unit'
	New-Item -ItemType Directory -Path $noFile -Force | Out-Null
	Test-Case -Name 'a results directory with no result file' -ResultsPath $noFile

	Test-Case -Name 'a results directory that was never written' -ResultsPath (Join-Path $root 'absent/TestResults/unit')

	$abortedPath = Join-Path $root 'aborted/TestResults/terminal'
	New-VsTestResult -Path (Join-Path $abortedPath 'terminal.trx') -Total 1508 -Executed 1471 -Outcome 'Aborted'
	Test-Case -Name 'a run the host aborted' -ResultsPath $abortedPath

	# One full directory must not vouch for an empty one beside it.
	$masked = @(
		(Join-Path $root 'masked/TestResults/full'),
		(Join-Path $root 'masked/TestResults/hollow'))
	New-VsTestResult -Path (Join-Path $masked[0] 'full.trx') -Total 99 -Executed 99
	New-VsTestResult -Path (Join-Path $masked[1] 'hollow.trx') -Total 0 -Executed 0
	Test-Case -Name 'an empty results directory beside a full one' -ResultsPath $masked

	$noSummary = Join-Path $root 'nosummary/TestResults/unit'
	New-Item -ItemType Directory -Path $noSummary -Force | Out-Null
	'<?xml version="1.0" encoding="UTF-8"?><TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010" />' |
		Set-Content -LiteralPath (Join-Path $noSummary 'unit.trx') -Encoding UTF8
	Test-Case -Name 'a result file with no summary' -ResultsPath $noSummary
}
finally {
	Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
	Write-Host 'The zero-test check no longer behaves as its own documentation says:'
	$failures | ForEach-Object { Write-Host "  $_" }
	throw "Test-ExecutedTests.ps1 failed $($failures.Count) of its own contract cases."
}

Write-Host 'Zero-test check contract cases passed.'
