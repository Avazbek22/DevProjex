#Requires -Version 7.0
<#
.SYNOPSIS
	Checks that the gate accepts a skip only when the change could not have affected what was
	skipped, and that the planner and the gate still agree, job by job, about what "could not have
	affected" means.

.DESCRIPTION
	Three things are checked here, and the last is the one that rots quietly.

	The gate's own rules, driven with synthetic path lists: a documentation-only change keeps its
	fast path, a change that touches tested code fails rather than passing on skipped suites, a plan
	made from a different set of paths fails with both lists, and a job skipped while the plan
	expected it to run fails without any of that being consulted.

	That the gate still tells the jobs apart. A gate that answered "unreachable" to everything would
	pass every case below that expects a pass, so each job also gets a case where the change does
	reach it and the gate has to refuse — including one that checks the reaching paths are printed,
	because that list is the whole content of the failure.

	And the agreement between the two sides, per job. The gate works out for itself which jobs a
	path can reach, precisely so that it does not take the planner's word for it. Kept apart by
	hand, the two would drift: someone narrows the planner, a job starts being skipped for paths
	that reach it, and the gate — which has not heard of the narrowing — either fails every such
	change or waves it through. A plan is rarely skipped whole; far more often it is narrowed to one
	suite, which is exactly the case a "was it skipped entirely" check cannot see. So a file from
	every test project, and from every other kind of path the planner classifies, is put to both
	sides, and for every job the planner skips the gate has to agree that the change cannot reach
	it.
#>
[CmdletBinding()]
param(
	[string] $GatePath = (Join-Path $PSScriptRoot 'Test-CiGateSkip.ps1'),
	[string] $PlannerModulePath = (Join-Path $PSScriptRoot 'CiChangePlanner.psm1')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$failures = [Collections.Generic.List[string]]::new()
$gatedJobs = @('tests', 'terminalCommand', 'ignoreScanner', 'documentation')
$suiteNames = @('Unit', 'Integration', 'Terminal', 'UI')

function ConvertTo-PlannerJson {
	param([AllowEmptyCollection()][string[]] $Path)

	# Passed as a typed array rather than piped, the way the planner writes it: an empty pipeline
	# reaches ConvertTo-Json as no input at all and comes back as an empty string.
	return (ConvertTo-Json -Compress -InputObject ([string[]] @($Path)))
}

function Invoke-Gate {
	param(
		[AllowEmptyCollection()][string[]] $ChangedPath,
		[string] $PlannerChangedPathJson,
		[AllowEmptyCollection()][string[]] $SkippedJob,
		[AllowEmptyCollection()][string[]] $PlannerWillRunJob,
		[AllowEmptyCollection()][string[]] $PlannerWillRunSuite
	)

	$printed = [Collections.Generic.List[string]]::new()
	$accepted = $true
	$message = ''
	try {
		# The printed lines are collected through the pipeline rather than read off the thrown
		# message: the gate prints each reaching path as it finds it, and that is the part of the
		# failure that has to survive being surfaced in full.
		& $GatePath `
			-ChangedPath $ChangedPath `
			-PlannerChangedPathJson $PlannerChangedPathJson `
			-SkippedJob $SkippedJob `
			-PlannerWillRunJob $PlannerWillRunJob `
			-PlannerWillRunSuite $PlannerWillRunSuite 6>&1 |
			ForEach-Object { $printed.Add([string] $_) }
	}
	catch {
		$accepted = $false
		$message = $_.Exception.Message
	}

	return [pscustomobject]@{
		Accepted = $accepted
		Message = $message
		Printed = $printed.ToArray()
	}
}

function Test-Case {
	param(
		[string] $Name,
		[string[]] $ChangedPath,
		[string[]] $PlannerPath,
		[string[]] $SkippedJob,
		[string[]] $PlannerWillRunJob = @(),
		[string[]] $PlannerWillRunSuite = @(),
		[switch] $ShouldPass,
		[string] $ExpectMessage,
		[string[]] $ExpectPrintedPath = @()
	)

	$result = Invoke-Gate `
		-ChangedPath $ChangedPath `
		-PlannerChangedPathJson (ConvertTo-PlannerJson -Path $PlannerPath) `
		-SkippedJob $SkippedJob `
		-PlannerWillRunJob $PlannerWillRunJob `
		-PlannerWillRunSuite $PlannerWillRunSuite

	if ($ShouldPass -and -not $result.Accepted) {
		$failures.Add("$Name should have been accepted but was rejected: $($result.Message)")
		return
	}
	if (-not $ShouldPass -and $result.Accepted) {
		$failures.Add("$Name should have been rejected but was accepted.")
		return
	}
	if ($ExpectMessage -and -not $result.Accepted -and
		$result.Message -notmatch [regex]::Escape($ExpectMessage)) {
		$failures.Add("$Name was rejected for the wrong reason; expected '$ExpectMessage', got: $($result.Message)")
	}

	foreach ($expected in $ExpectPrintedPath) {
		$printedLine = @($result.Printed | Where-Object { $_.Trim() -eq $expected })
		if ($printedLine.Count -eq 0) {
			$failures.Add(
				"$Name did not name '$expected' among the paths it printed, so the reason for the " +
				"failure is not in the log: $($result.Printed -join ' | ')")
		}
	}
}

# --- a change that could not have affected what was skipped -----------------------------------
Test-Case -Name 'documentation only keeps its fast path' `
	-ChangedPath @('Docs/McpServer.md', 'README.md') `
	-PlannerPath @('Docs/McpServer.md', 'README.md') `
	-SkippedJob @('tests', 'terminalCommand') -ShouldPass

Test-Case -Name 'repository metadata only keeps its fast path' `
	-ChangedPath @('AGENTS.md', '.gitignore', 'notes.txt') `
	-PlannerPath @('AGENTS.md', '.gitignore', 'notes.txt') `
	-SkippedJob @('tests') -ShouldPass

Test-Case -Name 'nothing skipped needs no justification' `
	-ChangedPath @('Apps/Mcp/McpProjectService.cs') `
	-PlannerPath @('Apps/Mcp/McpProjectService.cs') `
	-SkippedJob @() -ShouldPass

# The change this gate was first asked about: a file in one test project narrows the plan to that
# project's suite, and the three jobs that build the other projects are skipped for a reason the
# gate can state on its own.
Test-Case -Name 'a narrowed plan keeps the jobs it cannot reach skipped' `
	-ChangedPath @('Tests/DevProjex.Tests.Terminal/McpServerProcessTests.cs') `
	-PlannerPath @('Tests/DevProjex.Tests.Terminal/McpServerProcessTests.cs') `
	-SkippedJob @('terminalCommand', 'ignoreScanner', 'documentation') `
	-PlannerWillRunJob @('tests') `
	-PlannerWillRunSuite @('Terminal') -ShouldPass

# --- a change that could ------------------------------------------------------------------------
Test-Case -Name 'tested code skipped as if it were metadata' `
	-ChangedPath @('AGENTS.md', 'Application/Services/GitScopeFilter.cs') `
	-PlannerPath @('AGENTS.md', 'Application/Services/GitScopeFilter.cs') `
	-SkippedJob @('tests') `
	-ExpectMessage 'the change reaches them: tests' `
	-ExpectPrintedPath @('Application/Services/GitScopeFilter.cs')

Test-Case -Name 'a test project skipped as if it were metadata' `
	-ChangedPath @('Tests/DevProjex.Tests.Unit/McpInfrastructureTests.cs') `
	-PlannerPath @('Tests/DevProjex.Tests.Unit/McpInfrastructureTests.cs') `
	-SkippedJob @('tests') `
	-ExpectMessage 'the change reaches them'

# --- each job separately, so "unreachable" cannot become the answer to everything ---------------
# The integration suite excludes Category=TerminalCommand, so the terminal command matrix is the
# only place those tests run and no suite can stand in for it.
Test-Case -Name 'the terminal command matrix skipped for an integration test change' `
	-ChangedPath @('Tests/DevProjex.Tests.Integration/StoreListingParityTests.cs') `
	-PlannerPath @('Tests/DevProjex.Tests.Integration/StoreListingParityTests.cs') `
	-SkippedJob @('terminalCommand') `
	-PlannerWillRunJob @('tests') `
	-PlannerWillRunSuite @('Integration') `
	-ExpectMessage 'terminalCommand' `
	-ExpectPrintedPath @('Tests/DevProjex.Tests.Integration/StoreListingParityTests.cs')

# The unit suite runs that category itself, so the unit half of the same job is covered only while
# the unit suite is in the matrix.
Test-Case -Name 'the terminal command matrix skipped while the unit suite is not running' `
	-ChangedPath @('Tests/DevProjex.Tests.Unit/McpInfrastructureTests.cs') `
	-PlannerPath @('Tests/DevProjex.Tests.Unit/McpInfrastructureTests.cs') `
	-SkippedJob @('terminalCommand') `
	-PlannerWillRunJob @() `
	-PlannerWillRunSuite @() `
	-ExpectMessage 'terminalCommand'

Test-Case -Name 'the ignore/scanner matrix skipped while the integration suite is not running' `
	-ChangedPath @('Tests/DevProjex.Tests.Integration/IgnoreContractTests.cs') `
	-PlannerPath @('Tests/DevProjex.Tests.Integration/IgnoreContractTests.cs') `
	-SkippedJob @('ignoreScanner') `
	-PlannerWillRunJob @() `
	-PlannerWillRunSuite @() `
	-ExpectMessage 'ignoreScanner'

Test-Case -Name 'the documentation contracts skipped while the terminal suite is not running' `
	-ChangedPath @('Tests/DevProjex.Tests.Terminal/McpServerProcessTests.cs') `
	-PlannerPath @('Tests/DevProjex.Tests.Terminal/McpServerProcessTests.cs') `
	-SkippedJob @('documentation') `
	-PlannerWillRunJob @() `
	-PlannerWillRunSuite @() `
	-ExpectMessage 'documentation'

Test-Case -Name 'a job the gate cannot place is never vouched for' `
	-ChangedPath @('Docs/McpServer.md') `
	-PlannerPath @('Docs/McpServer.md') `
	-SkippedJob @('releaseValidation') `
	-ExpectMessage 'not ones this knows how to place'

# --- the plan does not match the change ---------------------------------------------------------
Test-Case -Name 'the plan saw a path that did not change' `
	-ChangedPath @('Docs/McpServer.md') `
	-PlannerPath @('Docs/McpServer.md', 'Docs/Security.md') `
	-SkippedJob @('tests') `
	-ExpectMessage 'seen by the plan, and not changed'

Test-Case -Name 'the plan did not see a path that changed' `
	-ChangedPath @('Docs/McpServer.md', 'Kernel/Models/ProjectRelativeGlob.cs') `
	-PlannerPath @('Docs/McpServer.md') `
	-SkippedJob @('tests') `
	-ExpectMessage 'changed, and not seen by the plan'

Test-Case -Name 'the plan recorded nothing at all' `
	-ChangedPath @('Docs/McpServer.md') `
	-PlannerPath @() `
	-SkippedJob @('tests') `
	-ExpectMessage 'changed, and not seen by the plan'

# --- skipped for a reason the plan never gave ----------------------------------------------------
Test-Case -Name 'a job the plan expected to run was skipped' `
	-ChangedPath @('Docs/McpServer.md') `
	-PlannerPath @('Docs/McpServer.md') `
	-SkippedJob @('documentation') `
	-PlannerWillRunJob @('documentation') `
	-ExpectMessage 'the plan said they would run'

# --- the two sides still agree, job by job -------------------------------------------------------
Import-Module $PlannerModulePath -Force

function Test-PerJobAgreement {
	param(
		[Parameter(Mandatory)][string] $Kind,
		[Parameter(Mandatory)][string[]] $Path
	)

	foreach ($path in $Path) {
		$plan = Get-CiChangePlan -ChangedPath @($path)
		$runs = [ordered] @{
			tests = [bool] $plan.HasTestMatrix
			terminalCommand = [bool] $plan.TerminalCommand
			ignoreScanner = [bool] $plan.IgnoreScanner
			documentation = [bool] $plan.Documentation
		}
		$willRun = @($runs.GetEnumerator() | Where-Object { $_.Value } | ForEach-Object { $_.Key })
		$suites = @($suiteNames | Where-Object { $plan.$_ })
		$json = ConvertTo-PlannerJson -Path @($path)

		foreach ($job in $gatedJobs) {
			if ($runs[$job]) {
				# The planner runs it, so the gate is never asked to vouch for it.
				continue
			}

			$result = Invoke-Gate `
				-ChangedPath @($path) `
				-PlannerChangedPathJson $json `
				-SkippedJob @($job) `
				-PlannerWillRunJob $willRun `
				-PlannerWillRunSuite $suites
			if (-not $result.Accepted) {
				$failures.Add(
					"$Kind '$path': the planner skips '$job' while the gate says the change reaches " +
					"it. Either the planner has to run that job for this path, or the gate's " +
					"reachability has to learn why it cannot be reached.")
			}
		}
	}
}

# One file from every test project, and from every shared folder compiled into one, because a
# change to any of them narrows the plan rather than emptying it.
Test-PerJobAgreement -Kind 'test project' -Path @(
	'Tests/DevProjex.Tests.Unit/McpInfrastructureTests.cs',
	'Tests/DevProjex.Tests.Integration/IgnoreContractTests.cs',
	'Tests/DevProjex.Tests.Integration/StoreListingParityTests.cs',
	'Tests/DevProjex.Tests.Terminal/McpServerProcessTests.cs',
	'Tests/DevProjex.Tests.Terminal/PublishedSingleFileExtractionProcessTests.cs',
	'Tests/DevProjex.Tests.UI/ProjectTreeViewTests.cs',
	'Tests/DevProjex.Tests.Terminal.ProgressHost/Program.cs',
	'Tests/Shared/TerminalProgress/ProgressExpectations.cs',
	'Tests/Shared/TerminalHost/TerminalHostHarness.cs',
	'Tests/Shared/ProjectLoadWorkflow/ProjectLoadSteps.cs',
	'Tests/Shared/StoreListing/StoreListingFacts.cs',
	'Tests/Fixtures/DependencyFacts/Sample.json')

# And one of every other kind the planner classifies, since the same drift reaches them all.
Test-PerJobAgreement -Kind 'path' -Path @(
	'Kernel/Models/ProjectRelativeGlob.cs',
	'Application/Services/GitScopeFilter.cs',
	'Infrastructure/Scanning/FileSystemScanner.cs',
	'Assets/HelpContent/help.en.txt',
	'Assets/Icons/app.ico',
	'Apps/Mcp/McpProjectService.cs',
	'Apps/Avalonia/Views/MainWindow.axaml.cs',
	'Apps/Terminal/Commands/ScanCommand.cs',
	'Apps/TerminalHost/Program.cs',
	'Packaging/Windows/StoreListing/listingData.csv',
	'Packaging/Notes.md',
	'Scripts/Build-Release.ps1',
	'Scripts/ReleasePayloadInspection.cs',
	'Docs/McpServer.md',
	'README.md',
	'.github/assets/banner.svg',
	'AGENTS.md',
	'tools/RankingEval/Program.cs',
	'DevProjex.sln')

if ($failures.Count -gt 0) {
	Write-Host 'The CI gate no longer behaves as its own documentation says:'
	foreach ($failure in $failures) {
		Write-Host "  $failure"
	}
	throw "Test-CiGateSkip.ps1 failed $($failures.Count) of its own contract cases."
}

Write-Host 'CI gate skip contract cases passed.'
