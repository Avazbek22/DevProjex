#Requires -Version 7.0
<#
.SYNOPSIS
	Fails the gate when a job was skipped for a reason the gate cannot vouch for.

.DESCRIPTION
	A skipped job counts as a pass, which is right only when something has checked that the change
	could not have affected it. That check used to be the change planner's word: it classified the
	changed paths, whole suites were skipped on the strength of that classification, and the gate
	accepted the result without ever seeing the change.

	So this asks three questions the planner cannot answer about itself.

	Did the planner see the change that was actually made? The paths it decided from are compared
	against the paths the gate works out for itself from the same commits. Any difference fails,
	with both lists in the message, because a plan made from a different change is not a plan for
	this one.

	Was a job skipped although the plan expected it to run? That is not the planner's doing at all,
	and fails before anything else is consulted.

	And could the change have reached each skipped job? That is answered per job, from the gate's
	own reading of what the workflow builds and runs rather than from the planner's opinion of the
	same paths. A change can legitimately narrow the plan without emptying it — a file in one test
	project runs that project's suite and nothing else — so asking it of the change as a whole,
	rather than of every job separately, cannot tell a narrowed plan from a wrong one.

	The two readings are kept apart on purpose, and `Test-CiGateContract.ps1` drives a file from
	every test project past both of them and fails if they disagree about any single job. So the
	pair drifting apart shows up there, rather than as a suite quietly not running or as a gate
	that fails every change.
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
	[string[]] $PlannerWillRunJob,

	# Test suites the plan put in the matrix. Three of the jobs below run tests that a suite also
	# runs, so which suites ran decides whether skipping those jobs leaves anything uncovered.
	[AllowEmptyCollection()]
	[string[]] $PlannerWillRunSuite = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Which test project a path is built into, or read by, taken from the build graph rather than from
# the plan. The .csproj files say which project compiles a folder and which projects reference each
# other, and a job that builds a project is broken by any file in that project, not only by the
# files its own filter selects. A `Tests/Shared` folder is compiled into several projects by
# <Compile Include>, and a referenced project is built along with the project referencing it, so
# each is listed under every project it ends up inside.
#
# Anything absent from this table is treated as reaching all four, so a path nobody has classified
# fails a skip rather than passing one.
$testProjectsByPrefix = [ordered] @{
	'Tests/DevProjex.Tests.Unit/' = @('Unit')
	'Tests/DevProjex.Tests.Integration/' = @('Integration')
	'Tests/DevProjex.Tests.Terminal.ProgressHost/' = @('Unit', 'Terminal')
	'Tests/DevProjex.Tests.Terminal/' = @('Terminal')
	'Tests/DevProjex.Tests.UI/' = @('UI')
	'Tests/Shared/TerminalProgress/' = @('Unit', 'Terminal')
	'Tests/Shared/TerminalHost/' = @('Unit', 'Terminal')
	'Tests/Shared/ProjectLoadWorkflow/' = @('Unit', 'Integration', 'UI')
	'Tests/Shared/StoreListing/' = @('Unit', 'Integration')
	# Production projects nothing else reaches. Apps/TerminalHost is referenced only by the Terminal
	# test project; Apps/Terminal is reached by the Unit tests through their progress host as well.
	'Apps/TerminalHost/' = @('Terminal')
	'Apps/Terminal/' = @('Unit', 'Integration', 'Terminal')
	# Not compiled anywhere. Packaging files are read by the Store listing tests and by the
	# documentation and packaging contracts; Scripts/ReleasePayloadInspection.cs is linked into the
	# integration tests.
	'Packaging/' = @('Unit', 'Integration', 'Terminal')
	'Scripts/' = @('Integration')
}

$allJobs = @('tests', 'terminalCommand', 'ignoreScanner', 'documentation')
$allTestProjects = @('Unit', 'Integration', 'Terminal', 'UI')

function Test-DocumentPath {
	param([string] $Path)

	if ($Path.Equals('README.md', [StringComparison]::OrdinalIgnoreCase)) { return $true }
	if ($Path.StartsWith('Docs/', [StringComparison]::OrdinalIgnoreCase)) { return $true }
	if ($Path.StartsWith('Packaging/', [StringComparison]::OrdinalIgnoreCase) -and
		$Path.EndsWith('.md', [StringComparison]::OrdinalIgnoreCase)) {
		return $true
	}
	if ($Path.StartsWith('.github/assets/', [StringComparison]::OrdinalIgnoreCase) -and
		[IO.Path]::GetExtension($Path) -in @('.svg', '.png', '.jpg', '.jpeg', '.gif', '.webp')) {
		return $true
	}

	return $false
}

function Test-MetadataPath {
	param([string] $Path)

	foreach ($prefix in @('.idea/', '.run/', '.github/ISSUE_TEMPLATE/')) {
		if ($Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { return $true }
	}
	$metadataFiles = @(
		'.github/PULL_REQUEST_TEMPLATE.md', '.github/FUNDING.yml', '.gitattributes', '.gitignore',
		'LICENSE', 'AGENTS.md', 'AboutProject.md', 'CODE_OF_CONDUCT.md', 'CONTRIBUTING.md',
		'SECURITY.md', 'SUPPORT.md', 'TRADEMARKS.md', 'Setup-AI-Agents.ps1', 'SetupAI.txt')
	if ($metadataFiles -contains $Path) { return $true }

	return (-not $Path.Contains('/')) -and $Path.EndsWith('.txt', [StringComparison]::OrdinalIgnoreCase)
}

<#
.SYNOPSIS
	The jobs a change to one path could make fail, given which suites the plan put in the matrix.

.DESCRIPTION
	What each job builds and runs, and what already covers it:

	tests            the suite matrix. Every selected test project, on all three operating systems,
	                 filtered only against the performance categories. Nothing else covers it.

	terminalCommand  builds the Unit and Integration test projects and runs Category=TerminalCommand
	                 in both. The Unit suite runs that category too, so the Unit half is covered
	                 whenever the Unit suite runs. The Integration suite excludes the category, so
	                 the Integration half is covered by nothing and this job is the only place those
	                 tests run at all.

	ignoreScanner    builds the Integration test project and runs the ignore and scanner names in it
	                 on the same three operating systems the Integration suite uses, excluding
	                 nothing that suite includes. Whenever the Integration suite runs, this job adds
	                 no coverage.

	documentation    builds the Terminal test project and runs two contract classes from it. The
	                 Terminal suite runs the whole project, so whenever it runs, this job adds no
	                 coverage. When it does not, these contracts are the only thing reading the
	                 documents and packaging files.
#>
function Get-ReachableJob {
	param(
		[Parameter(Mandatory)][string] $Path,
		[Parameter(Mandatory)][AllowEmptyCollection()][string[]] $PlannedSuite
	)

	if (Test-MetadataPath -Path $Path) { return @() }
	if (Test-DocumentPath -Path $Path) { return @('documentation') }

	$projects = $allTestProjects
	foreach ($prefix in $testProjectsByPrefix.Keys) {
		if ($Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
			$projects = @($testProjectsByPrefix[$prefix])
			break
		}
	}

	$jobs = [Collections.Generic.List[string]]::new()
	$jobs.Add('tests')
	if (($projects -contains 'Integration') -or
		(($projects -contains 'Unit') -and ($PlannedSuite -notcontains 'Unit'))) {
		$jobs.Add('terminalCommand')
	}
	if (($projects -contains 'Integration') -and ($PlannedSuite -notcontains 'Integration')) {
		$jobs.Add('ignoreScanner')
	}
	if (($projects -contains 'Terminal') -and ($PlannedSuite -notcontains 'Terminal')) {
		$jobs.Add('documentation')
	}

	return @($jobs)
}

function ConvertTo-TrimmedSet {
	param([AllowEmptyCollection()][string[]] $Value)

	return @($Value |
		Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
		ForEach-Object { $_.Trim() } |
		Sort-Object -Unique)
}

# Wrapped at the call: a function returning one item returns it as a scalar, and asking a
# scalar for Count is an error under strict mode.
$skipped = @(ConvertTo-TrimmedSet -Value $SkippedJob)
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

$actual = @(ConvertTo-TrimmedSet -Value $ChangedPath)
$recorded = @()
if (-not [string]::IsNullOrWhiteSpace($PlannerChangedPathJson)) {
	try {
		$recorded = @(ConvertTo-TrimmedSet -Value @($PlannerChangedPathJson | ConvertFrom-Json))
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

$plannedSuites = @(ConvertTo-TrimmedSet -Value $PlannerWillRunSuite)
$unknownJobs = @($skipped | Where-Object { $allJobs -notcontains $_ })
if ($unknownJobs.Count -gt 0) {
	throw "These skipped jobs are not ones this knows how to place against a change, so it cannot " +
	      "say whether skipping them was safe: $($unknownJobs -join ', ')."
}

$unjustified = [Collections.Generic.List[string]]::new()
foreach ($job in $skipped) {
	$reachedBy = @($actual | Where-Object { (Get-ReachableJob -Path $_ -PlannedSuite $plannedSuites) -contains $job })
	if ($reachedBy.Count -eq 0) {
		continue
	}

	$unjustified.Add($job)
	# Written out here rather than gathered into the thrown message. The list of paths is what the
	# message is for, and it is the long thrown message that gets cut off part-way through a path
	# where the failure is surfaced.
	Write-Host "$job was skipped, and the change reaches it through:"
	foreach ($path in $reachedBy) {
		Write-Host "  $path"
	}
}

if ($unjustified.Count -gt 0) {
	throw "These jobs were skipped although the change reaches them: $($unjustified -join ', '). " +
	      "The paths that reach each one are listed above."
}

Write-Host ("Skipped " + ($skipped -join ', ') +
	"; none of the $($actual.Count) changed path(s) reach them" +
	$(if ($plannedSuites.Count) { " beyond the suites that ran ($($plannedSuites -join ', '))" } else { '' }) +
	'.')
