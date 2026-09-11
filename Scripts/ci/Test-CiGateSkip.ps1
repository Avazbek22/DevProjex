#Requires -Version 7.0
<#
.SYNOPSIS
	Fails the gate when a job was skipped for a reason the gate cannot vouch for.

.DESCRIPTION
	A skipped job counts as a pass, which is right only when something has checked that the change
	could not have affected it. That check used to be the change planner's word: it classified the
	changed paths, whole suites were skipped on the strength of that classification, and the gate
	accepted the result without ever seeing the change.

	So this asks two questions the planner cannot answer about itself.

	Did the planner see the change that was actually made? The paths it decided from are compared
	against the paths the gate works out for itself from the same commits. Any difference fails,
	with both lists in the message, because a plan made from a different change is not a plan for
	this one.

	Is every changed path one that could not have affected a skipped job? That is answered here,
	from a list kept deliberately shorter than the planner's. Only documentation and repository
	metadata qualify. Anything this does not recognise fails the gate, so the two lists drifting
	apart shows up as a red gate rather than as a suite quietly not running — and
	`Test-CiGateContract.ps1` fails if the planner ever calls something skippable that this does
	not.

	A job skipped while the planner expected it to run is not the planner's doing at all, and fails
	without any of the above being consulted.
#>
[CmdletBinding()]
param(
	# The paths the gate worked out for itself, from the same commits the planner used.
	[Parameter(Mandatory)]
	[AllowEmptyCollection()]
	[string[]] $ChangedPath,

	# The paths the planner recorded having decided from, as the JSON array it wrote.
	[Parameter(Mandatory)]
	[AllowEmptyString()]
	[string] $PlannerChangedPathJson,

	# Jobs whose result was 'skipped'.
	[Parameter(Mandatory)]
	[AllowEmptyCollection()]
	[string[]] $SkippedJob,

	# Jobs the planner's own outputs said would run.
	[Parameter(Mandatory)]
	[AllowEmptyCollection()]
	[string[]] $PlannerWillRunJob
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-PathSet {
	param([AllowEmptyCollection()][string[]] $Path)

	return @($Path |
		Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
		ForEach-Object { $_.Trim() } |
		Sort-Object -Unique)
}

function Test-SkippablePath {
	param([string] $Path)

	# Documentation.
	if ($Path -eq 'README.md') { return $true }
	if ($Path.StartsWith('Docs/', [StringComparison]::OrdinalIgnoreCase)) { return $true }
	if ($Path.StartsWith('Packaging/', [StringComparison]::OrdinalIgnoreCase) -and
		$Path.EndsWith('.md', [StringComparison]::OrdinalIgnoreCase)) {
		return $true
	}

	# Repository metadata.
	foreach ($prefix in @('.idea/', '.run/', '.github/ISSUE_TEMPLATE/')) {
		if ($Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { return $true }
	}
	$metadataFiles = @(
		'.github/PULL_REQUEST_TEMPLATE.md', '.github/FUNDING.yml', '.gitattributes', '.gitignore',
		'LICENSE', 'AGENTS.md', 'AboutProject.md', 'CODE_OF_CONDUCT.md', 'CONTRIBUTING.md',
		'SECURITY.md', 'SUPPORT.md', 'TRADEMARKS.md', 'Setup-AI-Agents.ps1', 'SetupAI.txt')
	if ($metadataFiles -contains $Path) { return $true }
	if (-not $Path.Contains('/') -and $Path.EndsWith('.txt', [StringComparison]::OrdinalIgnoreCase)) {
		return $true
	}

	return $false
}

# Wrapped at the call: a function returning one item returns it as a scalar, and asking a
# scalar for Count is an error under strict mode.
$skipped = @(ConvertTo-PathSet -Path $SkippedJob)
if ($skipped.Count -eq 0) {
	Write-Host 'No job was skipped; nothing to vouch for.'
	exit 0
}

# A job the planner expected to run, and which did not, was not skipped by the plan. Whatever
# stopped it is outside what this can vouch for.
$unplanned = @($skipped | Where-Object { $PlannerWillRunJob -contains $_ })
if ($unplanned.Count -gt 0) {
	throw "These jobs were skipped although the plan said they would run, so the plan does not " +
	      "account for it: $($unplanned -join ', ')."
}

$actual = @(ConvertTo-PathSet -Path $ChangedPath)
$recorded = @()
if (-not [string]::IsNullOrWhiteSpace($PlannerChangedPathJson)) {
	try {
		$recorded = @(ConvertTo-PathSet -Path @($PlannerChangedPathJson | ConvertFrom-Json))
	}
	catch {
		throw "The plan recorded no readable list of the paths it decided from, so its decision " +
		      "cannot be checked against the change: $($_.Exception.Message)"
	}
}

$missing = @($actual | Where-Object { $recorded -notcontains $_ })
$extra = @($recorded | Where-Object { $actual -notcontains $_ })
if ($missing.Count -gt 0 -or $extra.Count -gt 0) {
	throw "The plan was decided from a different change than the one being gated, so its skips " +
	      "cannot be trusted." + [Environment]::NewLine +
	      "  changed, and not seen by the plan: $(if ($missing.Count) { $missing -join ', ' } else { '(none)' })" +
	      [Environment]::NewLine +
	      "  seen by the plan, and not changed: $(if ($extra.Count) { $extra -join ', ' } else { '(none)' })" +
	      [Environment]::NewLine +
	      "  gate saw $($actual.Count) path(s); the plan recorded $($recorded.Count)."
}

$unvouched = @($actual | Where-Object { -not (Test-SkippablePath -Path $_) })
if ($unvouched.Count -gt 0) {
	throw "These jobs were skipped: $($skipped -join ', '). That is only acceptable when the " +
	      "change could not have affected them, and these paths could have:" +
	      [Environment]::NewLine + "  $($unvouched -join [Environment]::NewLine + '  ')"
}

Write-Host ("Skipped " + ($skipped -join ', ') +
	"; all $($actual.Count) changed path(s) are documentation or repository metadata.")
