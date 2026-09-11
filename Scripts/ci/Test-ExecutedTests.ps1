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

	Every result file is judged on its own, and so is every results directory. Anything that
	aggregates first lets a healthy sibling vouch for an empty one, which is exactly the shape the
	recorded failure had: the terminal command job writes two directories, and the suite that
	enumerated nothing sat beside one that had not.

	Only the files written directly into the directory are read. Recursing found stale results left
	under it by an earlier run and counted them as this one's.

.NOTES
	One limit, stated because it is not visible from here. The outcome attribute is how a completed
	run admits it was cut short, and the two writers do not agree on the vocabulary: the VSTest
	logger emits `Aborted` and `Error`, while the xUnit report writer behind `--report-xunit-trx`
	emits neither — only `Timeout` of the three is reachable for the UI suite. For that suite an
	aborted host is caught by the step's own exit code instead, which is why this check runs only
	when the steps before it succeeded.
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

function Read-ResultDocument {
	param([string] $Path)

	# Test output reaches the result file verbatim, and this repository drives terminals, so a raw
	# control byte in a captured stream is ordinary rather than exceptional. XML 1.0 forbids those
	# bytes and the parser refuses the whole document over one, which would fail a complete run for
	# a character in output nobody is reading. Only the counters are wanted here, so the bytes the
	# format does not allow are dropped before parsing rather than the file being rejected.
	# `CheckCharacters` is not the lever it looks like: the reader raises on them regardless.
	$text = [IO.File]::ReadAllText($Path)
	$text = [regex]::Replace($text, '[\x00-\x08\x0B\x0C\x0E-\x1F]', '')
	$document = [System.Xml.XmlDocument]::new()
	$document.LoadXml($text)
	return $document
}

function Get-Counter {
	param([System.Xml.XmlElement] $Counters, [string] $Name)

	$value = $Counters.GetAttribute($Name)
	if ([string]::IsNullOrWhiteSpace($value)) {
		return 0
	}
	return [int] $value
}

$reported = [Collections.Generic.List[string]]::new()

foreach ($path in $ResultsPath) {
	if (-not (Test-Path -LiteralPath $path)) {
		throw "$JobName wrote no results directory at '$path', so the step that should have filled it " +
		      "produced nothing. A run that executes nothing cannot report success; see Docs/CI-Known-Failures.md."
	}

	$results = @(Get-ChildItem -LiteralPath $path -File -Filter '*.trx')
	if ($results.Count -eq 0) {
		throw "$JobName wrote no .trx file in '$path'. That is the signature of an enumeration that " +
		      "failed, and it is accepted silently by the artifact upload; see Docs/CI-Known-Failures.md."
	}

	$executedHere = 0
	$totalHere = 0
	foreach ($file in $results) {
		$document = Read-ResultDocument -Path $file.FullName
		$summary = $document.TestRun.ResultSummary
		if (-not $summary -or -not $summary.Counters) {
			throw "$JobName wrote '$($file.Name)' without a result summary, so the run cannot be shown " +
			      "to have executed anything."
		}
		# An aborted run still reports the tests it managed to finish, and the console line for one
		# has read `Passed!  - Failed:     0`. See the note above on which writer says what.
		$outcome = $summary.GetAttribute('outcome')
		if ($outcome -in @('Aborted', 'Error', 'Timeout')) {
			throw "$JobName did not run to completion: '$($file.Name)' reports outcome '$outcome'. " +
			      "See Docs/CI-Known-Failures.md."
		}

		$counters = $summary.Counters
		$executed = Get-Counter -Counters $counters -Name 'executed'
		$total = Get-Counter -Counters $counters -Name 'total'
		$outcomes = @('passed', 'failed', 'error', 'timeout', 'aborted') |
			ForEach-Object { Get-Counter -Counters $counters -Name $_ } |
			Measure-Object -Sum |
			Select-Object -ExpandProperty Sum

		if ($executed -le 0) {
			throw "$JobName executed 0 of $total test(s) in '$($file.Name)'. A run that executes " +
			      "nothing cannot report success; see Docs/CI-Known-Failures.md."
		}
		if ($executed -gt $total) {
			throw "$JobName wrote '$($file.Name)' claiming $executed of $total test(s) executed, which " +
			      "cannot be true, so its counters cannot be used to show anything ran."
		}
		# Skipped tests are counted in the total and not in `executed`, which is why a lower executed
		# count is ordinary. A test that executed and then reported no outcome at all is not.
		if ($outcomes -le 0) {
			throw "$JobName wrote '$($file.Name)' claiming $executed test(s) executed while none " +
			      "passed, failed, errored, timed out or aborted. Nothing was actually run."
		}

		$executedHere += $executed
		$totalHere += $total
	}

	$reported.Add("$path`: $executedHere of $totalHere")
}

Write-Host "$JobName executed tests in every results directory — $($reported -join '; ')."
