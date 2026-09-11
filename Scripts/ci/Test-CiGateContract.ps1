#Requires -Version 7.0
<#
.SYNOPSIS
	Checks that the gate accepts a skip only when the change could not have affected what was
	skipped, and that the planner and the gate still agree about what "could not have affected"
	means.

.DESCRIPTION
	Two things are checked here, and the second is the one that rots quietly.

	The gate's own rules, driven with synthetic path lists: a documentation-only change keeps its
	fast path, a change that touches tested code fails rather than passing on skipped suites, a plan
	made from a different set of paths fails with both lists, and a job skipped while the plan
	expected it to run fails without any of that being consulted.

	And the agreement between the two sides. The gate keeps its own, shorter list of paths that
	could not have affected a suite, precisely so that it does not take the planner's word. Kept
	apart by hand, the two would drift: someone widens the planner, suites start being skipped for
	the new paths, and the gate — which has not heard of them — would fail every such change. So
	every path the planner treats as skippable is run past the gate here, and the pair has to agree.
#>
[CmdletBinding()]
param(
	[string] $GatePath = (Join-Path $PSScriptRoot 'Test-CiGateSkip.ps1'),
	[string] $PlannerModulePath = (Join-Path $PSScriptRoot 'CiChangePlanner.psm1')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$failures = [Collections.Generic.List[string]]::new()

function Test-Case {
	param(
		[string] $Name,
		[string[]] $ChangedPath,
		[string[]] $PlannerPath,
		[string[]] $SkippedJob,
		[string[]] $PlannerWillRunJob = @(),
		[switch] $ShouldPass,
		[string] $ExpectMessage
	)

	$json = if ($null -eq $PlannerPath) { '[]' } else { $PlannerPath | ConvertTo-Json -Compress -AsArray }
	$accepted = $true
	$message = ''
	try {
		& $GatePath `
			-ChangedPath $ChangedPath `
			-PlannerChangedPathJson $json `
			-SkippedJob $SkippedJob `
			-PlannerWillRunJob $PlannerWillRunJob | Out-Null
	}
	catch {
		$accepted = $false
		$message = $_.Exception.Message
	}

	if ($ShouldPass -and -not $accepted) {
		$failures.Add("$Name should have been accepted but was rejected: $message")
		return
	}
	if (-not $ShouldPass -and $accepted) {
		$failures.Add("$Name should have been rejected but was accepted.")
		return
	}
	if ($ExpectMessage -and -not $accepted -and $message -notmatch [regex]::Escape($ExpectMessage)) {
		$failures.Add("$Name was rejected for the wrong reason; expected '$ExpectMessage', got: $message")
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

# --- a change that could ------------------------------------------------------------------------
Test-Case -Name 'tested code skipped as if it were metadata' `
	-ChangedPath @('AGENTS.md', 'Application/Services/GitScopeFilter.cs') `
	-PlannerPath @('AGENTS.md', 'Application/Services/GitScopeFilter.cs') `
	-SkippedJob @('tests') `
	-ExpectMessage 'Application/Services/GitScopeFilter.cs'

Test-Case -Name 'a test project skipped as if it were metadata' `
	-ChangedPath @('Tests/DevProjex.Tests.Unit/McpInfrastructureTests.cs') `
	-PlannerPath @('Tests/DevProjex.Tests.Unit/McpInfrastructureTests.cs') `
	-SkippedJob @('tests') `
	-ExpectMessage 'could have'

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

# --- the two sides still agree about what is skippable -------------------------------------------
Import-Module $PlannerModulePath -Force
$skippableByPlanner = @(
	'README.md', 'Docs/McpServer.md', 'Docs/Media/readme-demo/frame.gif', 'Packaging/Notes.md',
	'.idea/workspace.xml', '.run/Configuration.xml', '.github/ISSUE_TEMPLATE/bug.md',
	'.github/PULL_REQUEST_TEMPLATE.md', '.github/FUNDING.yml', '.gitattributes', '.gitignore',
	'LICENSE', 'AGENTS.md', 'AboutProject.md', 'CODE_OF_CONDUCT.md', 'CONTRIBUTING.md',
	'SECURITY.md', 'SUPPORT.md', 'TRADEMARKS.md', 'Setup-AI-Agents.ps1', 'SetupAI.txt', 'notes.txt')

foreach ($path in $skippableByPlanner) {
	$plan = Get-CiChangePlan -ChangedPath @($path)
	if ($plan.HasTestMatrix) {
		# The planner runs suites for it, so the gate is never asked to vouch for it.
		continue
	}

	$accepted = $true
	try {
		& $GatePath -ChangedPath @($path) -PlannerChangedPathJson (@($path) | ConvertTo-Json -Compress -AsArray) `
			-SkippedJob @('tests') -PlannerWillRunJob @() | Out-Null
	}
	catch {
		$accepted = $false
	}

	if (-not $accepted) {
		$failures.Add(
			"the planner skips the test suites for '$path' but the gate will not vouch for it, " +
			"so every change touching that path would fail the gate")
	}
}

if ($failures.Count -gt 0) {
	Write-Host 'The CI gate no longer behaves as its own documentation says:'
	foreach ($failure in $failures) {
		Write-Host "  $failure"
	}
	throw "Test-CiGateSkip.ps1 failed $($failures.Count) of its own contract cases."
}

Write-Host 'CI gate skip contract cases passed.'
