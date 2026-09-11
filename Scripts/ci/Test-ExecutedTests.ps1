#Requires -Version 7.0
<#
.SYNOPSIS
	Fails a CI job that reported success without executing any test.

.DESCRIPTION
	`dotnet test` exits zero when no test matched, and the xUnit VSTest adapter yields zero test
	cases rather than raising when it cannot parse what the test assembly printed during
	enumeration. Both produce a job that proves nothing, which is worse than a failing job because
	nobody looks at it. See Docs/CI-Known-Failures.md for the runs where this happened.

	The result file is what is read, not the console summary. In the recorded occurrence no
	`Passed!` or `Failed!` line was printed at all, so a check that parsed the summary would have
	missed it entirely; and in a separate pair of runs a literal `Passed!  - Failed:     0` line was
	printed for a run the test host had crashed out of. The `.trx` carries both facts honestly.

	A missing file is the precise signature of an enumeration that failed, so it is a failure here
	rather than something to ignore — the artifact upload accepts it silently.
#>
[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string[]] $ResultsPath,

	[Parameter(Mandatory)]
	[string] $JobName
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$executed = 0
$total = 0
$aborted = [Collections.Generic.List[string]]::new()

foreach ($path in $ResultsPath) {
	if (-not (Test-Path -LiteralPath $path)) {
		throw "$JobName produced no results directory at '$path'. A run that executes nothing cannot report success; see Docs/CI-Known-Failures.md."
	}

	$results = @(Get-ChildItem -LiteralPath $path -Recurse -File -Filter '*.trx')
	if ($results.Count -eq 0) {
		throw "$JobName produced no .trx file under '$path'. A run that executes nothing cannot report success; see Docs/CI-Known-Failures.md."
	}

	foreach ($file in $results) {
		$document = [xml](Get-Content -LiteralPath $file.FullName -Raw)
		$summary = $document.TestRun.ResultSummary
		if (-not $summary -or -not $summary.Counters) {
			throw "$JobName wrote '$($file.Name)' without a result summary, so the run cannot be shown to have executed anything."
		}
		# An aborted run still reports the tests it managed to finish, and the console line for one
		# has read `Passed!  - Failed:     0`. The outcome is the only place it admits what happened.
		if ($summary.outcome -in @('Aborted', 'Error', 'Timeout')) {
			$aborted.Add("$($file.Name) reports outcome '$($summary.outcome)'")
		}
		$executed += [int] $summary.Counters.executed
		$total += [int] $summary.Counters.total
	}
}

if ($aborted.Count -gt 0) {
	throw "$JobName did not run to completion: $($aborted -join '; '). See Docs/CI-Known-Failures.md."
}

if ($executed -le 0) {
	throw "$JobName executed 0 of $total test(s). A run that executes nothing cannot report success; see Docs/CI-Known-Failures.md."
}

Write-Host "$JobName executed $executed of $total test(s)."
