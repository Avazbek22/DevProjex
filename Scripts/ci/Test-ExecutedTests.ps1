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

	Each results directory is judged on its own. A job that writes two of them has to have run
	something in each, because one full directory saying nothing about the other is exactly how a
	suite that enumerated nothing would pass unnoticed.

	`Scripts/ci/Test-ExecutedTestsContract.ps1` is what keeps this honest in both directions: it
	fails if this stops accepting a run that happened, and it fails if this stops rejecting one that
	did not.
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

$reported = [Collections.Generic.List[string]]::new()

foreach ($path in $ResultsPath) {
	if (-not (Test-Path -LiteralPath $path)) {
		throw "$JobName wrote no results directory at '$path', so the step that should have filled it " +
		      "produced nothing. A run that executes nothing cannot report success; see Docs/CI-Known-Failures.md."
	}

	$results = @(Get-ChildItem -LiteralPath $path -Recurse -File -Filter '*.trx')
	if ($results.Count -eq 0) {
		throw "$JobName wrote no .trx file under '$path'. That is the signature of an enumeration that " +
		      "failed, and it is accepted silently by the artifact upload; see Docs/CI-Known-Failures.md."
	}

	$executed = 0
	$total = 0
	foreach ($file in $results) {
		$document = [xml](Get-Content -LiteralPath $file.FullName -Raw)
		$summary = $document.TestRun.ResultSummary
		if (-not $summary -or -not $summary.Counters) {
			throw "$JobName wrote '$($file.Name)' without a result summary, so the run cannot be shown " +
			      "to have executed anything."
		}
		# An aborted run still reports the tests it managed to finish, and the console line for one
		# has read `Passed!  - Failed:     0`. The outcome is the only place it admits what happened.
		if ($summary.outcome -in @('Aborted', 'Error', 'Timeout')) {
			throw "$JobName did not run to completion: '$($file.Name)' reports outcome " +
			      "'$($summary.outcome)'. See Docs/CI-Known-Failures.md."
		}
		$executed += [int] $summary.Counters.executed
		$total += [int] $summary.Counters.total
	}

	if ($executed -le 0) {
		throw "$JobName executed 0 of $total test(s) under '$path'. A run that executes nothing cannot " +
		      "report success; see Docs/CI-Known-Failures.md."
	}

	$reported.Add("$path`: $executed of $total")
}

Write-Host "$JobName executed tests in every results directory — $($reported -join '; ')."
