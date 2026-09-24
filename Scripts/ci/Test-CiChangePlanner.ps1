$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'CiChangePlanner.psm1') -Force

function Assert-Plan {
	param(
		[Parameter(Mandatory)][string] $Name,
		[Parameter(Mandatory)][string[]] $Path,
		[string[]] $Enabled = @(),
		[string[]] $Disabled = @()
	)

	$plan = Get-CiChangePlan -ChangedPath $Path
	foreach ($property in $Enabled) {
		if (-not $plan.$property) {
			throw "[$Name] Expected '$property' to be enabled."
		}
	}

	foreach ($property in $Disabled) {
		if ($plan.$property) {
			throw "[$Name] Expected '$property' to be disabled."
		}
	}
}

$allHeavyTargets = @('Unit', 'Integration', 'Terminal', 'UI', 'TerminalCommand', 'IgnoreScanner', 'Release', 'Store', 'Full')

Assert-Plan -Name 'README only' -Path 'README.md' `
	-Enabled Documentation `
	-Disabled $allHeavyTargets

$staticDocumentationAssets = @(
	'.github/assets/support.svg',
	'.github/assets/screenshots/overview.PNG',
	'.github/assets/social-preview.jpg',
	'.github/assets/demo.jpeg',
	'.github/assets/demo.gif',
	'.github/assets/demo.webp'
)
foreach ($assetPath in $staticDocumentationAssets) {
	Assert-Plan -Name "Static documentation asset: $assetPath" -Path $assetPath `
		-Enabled Documentation `
		-Disabled $allHeavyTargets
}

Assert-Plan -Name 'README and static asset stay documentation-only' `
	-Path @('README.md', '.github/assets/boosty-support.svg') `
	-Enabled Documentation `
	-Disabled $allHeavyTargets

Assert-Plan -Name 'Executable under documentation assets fails safe' -Path '.github/assets/generate-banner.ps1' `
	-Enabled $allHeavyTargets

Assert-Plan -Name 'Unknown documentation asset format fails safe' -Path '.github/assets/banner.bin' `
	-Enabled $allHeavyTargets

Assert-Plan -Name 'Root text note only' -Path 'release-note.txt' `
	-Disabled ($allHeavyTargets + 'Documentation')

Assert-Plan -Name 'Embedded help text runs only content-coupled suites' -Path 'Assets/HelpContent/help.de.txt' `
	-Enabled @('Unit', 'Integration', 'Terminal', 'TerminalCommand', 'Documentation') `
	-Disabled @('UI', 'IgnoreScanner', 'Release', 'Store', 'Full')

Assert-Plan -Name 'Nested help text stays content-coupled' -Path 'Assets/HelpContent/en/overview.txt' `
	-Enabled @('Unit', 'Integration', 'Terminal', 'TerminalCommand', 'Documentation') `
	-Disabled @('UI', 'IgnoreScanner', 'Release', 'Store', 'Full')

Assert-Plan -Name 'Non-text file under help content fails safe to production' -Path 'Assets/HelpContent/generate-help.ps1' `
	-Enabled @('Unit', 'Integration', 'Terminal', 'UI', 'TerminalCommand', 'Release', 'Store') `
	-Disabled @('Full')

Assert-Plan -Name 'Desktop production change' -Path 'Apps/Avalonia/Views/MainWindow.axaml' `
	-Enabled @('Unit', 'Integration', 'Terminal', 'UI', 'TerminalCommand', 'Release', 'Store') `
	-Disabled @('IgnoreScanner', 'Full')

Assert-Plan -Name 'Terminal production change' -Path 'Apps/Terminal/Tui/TerminalWorkspace.cs' `
	-Enabled @('Unit', 'Integration', 'Terminal', 'TerminalCommand', 'Release', 'Store') `
	-Disabled @('UI', 'IgnoreScanner', 'Full')

Assert-Plan -Name 'Ignore production change' -Path 'Application/Services/IgnoreRulesService.cs' `
	-Enabled @('Unit', 'Integration', 'Terminal', 'UI', 'TerminalCommand', 'IgnoreScanner', 'Release', 'Store') `
	-Disabled @('Full')

Assert-Plan -Name 'Unit test carries the matrix that builds its project' -Path 'Tests/DevProjex.Tests.Unit/FooTests.cs' `
	-Enabled @('Unit', 'TerminalCommand') `
	-Disabled @('Integration', 'Terminal', 'UI', 'IgnoreScanner', 'Documentation', 'Release', 'Store', 'Full')

Assert-Plan -Name 'Integration test covers excluded terminal category' -Path 'Tests/DevProjex.Tests.Integration/FooTests.cs' `
	-Enabled @('Integration', 'TerminalCommand') `
	-Disabled @('Unit', 'Terminal', 'UI', 'IgnoreScanner', 'Documentation', 'Release', 'Store', 'Full')

Assert-Plan -Name 'Ignore integration test' -Path 'Tests/DevProjex.Tests.Integration/IgnoreContractTests.cs' `
	-Enabled @('Integration', 'TerminalCommand', 'IgnoreScanner') `
	-Disabled @('Unit', 'Terminal', 'UI', 'Documentation', 'Release', 'Store', 'Full')

Assert-Plan -Name 'Terminal test infrastructure reaches the unit tests too' `
	-Path 'Tests/DevProjex.Tests.Terminal.ProgressHost/Program.cs' `
	-Enabled @('Unit', 'Terminal') `
	-Disabled @('Integration', 'UI', 'TerminalCommand', 'IgnoreScanner', 'Documentation', 'Release', 'Store', 'Full')

foreach ($sharedTerminalPath in @('Tests/Shared/TerminalProgress/Expectations.cs', 'Tests/Shared/TerminalHost/Harness.cs')) {
	Assert-Plan -Name "Shared terminal test source: $sharedTerminalPath" -Path $sharedTerminalPath `
		-Enabled @('Unit', 'Terminal') `
		-Disabled @('Integration', 'UI', 'TerminalCommand', 'IgnoreScanner', 'Documentation', 'Release', 'Store', 'Full')
}

Assert-Plan -Name 'Shared project-load source carries the excluded terminal category' `
	-Path 'Tests/Shared/ProjectLoadWorkflow/Steps.cs' `
	-Enabled @('Unit', 'Integration', 'UI', 'TerminalCommand') `
	-Disabled @('Terminal', 'IgnoreScanner', 'Documentation', 'Release', 'Store', 'Full')

Assert-Plan -Name 'Shared Store-listing source carries the excluded terminal category' `
	-Path 'Tests/Shared/StoreListing/Facts.cs' `
	-Enabled @('Unit', 'Integration', 'TerminalCommand') `
	-Disabled @('Terminal', 'UI', 'IgnoreScanner', 'Documentation', 'Release', 'Store', 'Full')

Assert-Plan -Name 'Packaging files are read by the documentation contracts' `
	-Path 'Packaging/Windows/StoreListing/listingData.csv' `
	-Enabled @('Unit', 'Integration', 'TerminalCommand', 'Documentation', 'Release', 'Store') `
	-Disabled @('Terminal', 'UI', 'IgnoreScanner', 'Full')

Assert-Plan -Name 'Release tooling is linked into the integration tests' -Path 'Scripts/Build-Release.ps1' `
	-Enabled @('Unit', 'Integration', 'TerminalCommand', 'Release', 'Store') `
	-Disabled @('Terminal', 'UI', 'IgnoreScanner', 'Documentation', 'Full')

Assert-Plan -Name 'Windows path normalization' -Path 'Tests\DevProjex.Tests.UI\MainWindowTests.cs' `
	-Enabled UI `
	-Disabled @('Unit', 'Integration', 'Terminal', 'Release', 'Store', 'Full')

Assert-Plan -Name 'Published single-file process test' `
	-Path 'Tests/DevProjex.Tests.Terminal/PublishedSingleFileExtractionProcessTests.cs' `
	-Enabled @('Terminal', 'Release') `
	-Disabled @('Unit', 'Integration', 'UI', 'TerminalCommand', 'IgnoreScanner', 'Documentation', 'Store', 'Full')

Assert-Plan -Name 'Mixed changes union their targets' -Path @('README.md', 'Tests/DevProjex.Tests.Unit/FooTests.cs') `
	-Enabled @('Documentation', 'Unit') `
	-Disabled @('Integration', 'Terminal', 'UI', 'Release', 'Store', 'Full')

Assert-Plan -Name 'Build graph uses full validation' -Path 'Directory.Build.props' `
	-Enabled $allHeavyTargets

Assert-Plan -Name 'Workflow changes use full validation' -Path '.github/workflows/dotnet.yml' `
	-Enabled $allHeavyTargets

Assert-Plan -Name 'Unknown path fails safe' -Path 'NewSubsystem/Feature.cs' `
	-Enabled $allHeavyTargets

$emptyPlan = Get-CiChangePlan -ChangedPath @()
foreach ($target in $allHeavyTargets) {
	if (-not $emptyPlan.$target) {
		throw "[Empty diff] Expected '$target' to be enabled by the safe fallback."
	}
}
if (-not $emptyPlan.Documentation) {
	throw "[Empty diff] Expected 'Documentation' to be enabled by the safe fallback."
}

$unitPlan = Get-CiChangePlan -ChangedPath 'Tests/DevProjex.Tests.Unit/FooTests.cs'
if ($unitPlan.TestMatrix.include.Count -ne 3) {
	throw "[Unit matrix] Expected one Unit job per OS, found $($unitPlan.TestMatrix.include.Count)."
}

$fullPlan = Get-CiChangePlan -Full
if ($fullPlan.TestMatrix.include.Count -ne 12) {
	throw "[Full matrix] Expected twelve suite/OS jobs, found $($fullPlan.TestMatrix.include.Count)."
}
if (-not $fullPlan.Documentation) {
	throw "[Full matrix] Expected 'Documentation' to be enabled."
}

$beforeSha = '1111111111111111111111111111111111111111'
$afterSha = '2222222222222222222222222222222222222222'
$baseSha = '3333333333333333333333333333333333333333'
# None of these commits exist in any checkout, so what the comparison would find is said here
# instead of looked up.
$everythingExists = { param([string] $Sha) $true }
$beforeIsGone = { param([string] $Sha) $Sha -ne $beforeSha }
$nothingExists = { param([string] $Sha) $false }
$syncComparison = Get-CiEventComparison -EventName pull_request -CommitReachable $everythingExists -Event ([pscustomobject]@{
	action = 'synchronize'
	before = $beforeSha
	after = $afterSha
})
if ($syncComparison.Full -or $syncComparison.MergeBase -or $syncComparison.BaseSha -ne $beforeSha -or $syncComparison.HeadSha -ne $afterSha) {
	throw '[PR synchronize] Expected an incremental previous-HEAD to new-HEAD comparison.'
}

$openedComparison = Get-CiEventComparison -EventName pull_request -CommitReachable $everythingExists -Event ([pscustomobject]@{
	action = 'opened'
	pull_request = [pscustomobject]@{
		base = [pscustomobject]@{ sha = $beforeSha }
		head = [pscustomobject]@{ sha = $afterSha }
	}
})
if ($openedComparison.Full -or -not $openedComparison.MergeBase) {
	throw '[PR opened] Expected a complete merge-base comparison.'
}

$newBranchComparison = Get-CiEventComparison -EventName push -CommitReachable $everythingExists -Event ([pscustomobject]@{
	before = '0000000000000000000000000000000000000000'
	after = $afterSha
})
if (-not $newBranchComparison.Full) {
	throw '[New branch push] Expected safe full validation when no previous commit exists.'
}

# A rebase or an amended push leaves the event naming a commit that is on no ref. The delta
# cannot be built from it, and the pull request still has a base to compare against.
$forcePushComparison = Get-CiEventComparison -EventName pull_request -CommitReachable $beforeIsGone -Event ([pscustomobject]@{
	action = 'synchronize'
	before = $beforeSha
	after = $afterSha
	pull_request = [pscustomobject]@{
		base = [pscustomobject]@{ sha = $baseSha }
		head = [pscustomobject]@{ sha = $afterSha }
	}
})
if ($forcePushComparison.Full) {
	throw '[PR synchronize after a force-push] Expected the merge-base comparison, not the full plan.'
}
if (-not $forcePushComparison.MergeBase -or
	$forcePushComparison.BaseSha -ne $baseSha -or
	$forcePushComparison.HeadSha -ne $afterSha) {
	throw '[PR synchronize after a force-push] Expected a base-to-head merge-base comparison.'
}

# With no pull request on the event there is nothing left to compare against.
$forcePushWithoutPullRequest = Get-CiEventComparison -EventName pull_request -CommitReachable $beforeIsGone -Event ([pscustomobject]@{
	action = 'synchronize'
	before = $beforeSha
	after = $afterSha
})
if (-not $forcePushWithoutPullRequest.Full) {
	throw '[PR synchronize after a force-push] Expected the full plan when no base remains.'
}

# The same commit can be missing on an opened event, and on a branch push, where no merge base
# is on offer at all.
$openedWithoutBase = Get-CiEventComparison -EventName pull_request -CommitReachable $nothingExists -Event ([pscustomobject]@{
	action = 'opened'
	pull_request = [pscustomobject]@{
		base = [pscustomobject]@{ sha = $baseSha }
		head = [pscustomobject]@{ sha = $afterSha }
	}
})
if (-not $openedWithoutBase.Full) {
	throw '[PR opened with a missing base] Expected safe full validation.'
}

$forcePushToBranch = Get-CiEventComparison -EventName push -CommitReachable $beforeIsGone -Event ([pscustomobject]@{
	before = $beforeSha
	after = $afterSha
})
if (-not $forcePushToBranch.Full) {
	throw '[Branch force-push] Expected safe full validation when the previous commit is gone.'
}

# A plan that was built has to end its step successfully, whichever plan it is. GitHub's pwsh shell
# ends a step with whatever the last native command left in $LASTEXITCODE, so git failing on an
# unbuildable range used to fail the step that had already fallen back to the full plan -- and every
# job waiting on that plan with it. The step is composed here the way the runner composes one, with
# the same prologue, epilogue and invocation.
$planScriptPath = Join-Path $PSScriptRoot 'Select-CiPlan.ps1'
$stepIdentifier = [Guid]::NewGuid().ToString('N')
$stepOutputPath = Join-Path ([IO.Path]::GetTempPath()) "devprojex-ci-step-$stepIdentifier.txt"
$stepScriptPath = Join-Path ([IO.Path]::GetTempPath()) "devprojex-ci-step-$stepIdentifier.ps1"
$unreachableSha = 'deadbeefdeadbeefdeadbeefdeadbeefdeadbeef'
try {
	Set-Content -LiteralPath $stepScriptPath -Encoding utf8 -Value @(
		'$ErrorActionPreference = ''stop''',
		"& '$planScriptPath' -BaseSha '$unreachableSha' -HeadSha '$unreachableSha' -GitHubOutputPath '$stepOutputPath'",
		'if ((Test-Path -LiteralPath variable:\LASTEXITCODE)) { exit $LASTEXITCODE }')

	$shellCommand = Get-Command pwsh -ErrorAction SilentlyContinue
	$shellPath = if ($shellCommand) {
		$shellCommand.Source
	}
	else {
		[Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
	}

	& $shellPath -NoProfile -Command ". '$stepScriptPath'" *> $null
	$stepExitCode = $LASTEXITCODE
	# Cleared for the same reason the plan clears it: this file is a CI step too.
	$global:LASTEXITCODE = 0

	if ($stepExitCode -ne 0) {
		throw "[Unbuildable range] The step ended with exit code $stepExitCode although it produced a plan."
	}

	$stepOutput = @(Get-Content -LiteralPath $stepOutputPath)
	if ($stepOutput -notcontains 'full=true') {
		throw '[Unbuildable range] Expected the safe full plan in the step outputs.'
	}
	if ($stepOutput -notcontains 'has_test_matrix=true') {
		throw '[Unbuildable range] Expected the safe full plan to carry a test matrix.'
	}
}
finally {
	Remove-Item -LiteralPath $stepScriptPath, $stepOutputPath -Force -ErrorAction SilentlyContinue
}

$metadataPlan = Get-CiChangePlan -ChangedPath 'release-note.txt'
if ($metadataPlan.HasTestMatrix) {
	throw '[Metadata only] Expected an empty heavy-test matrix.'
}

$multiBytePathSegment = ((1..48 | ForEach-Object { [char]::ConvertFromUtf32(0x1F680) }) -join '')
$largeChangedPathSet = @(1..4000 | ForEach-Object { "Generated/$multiBytePathSegment-$_.cs" })
$largePlan = Get-CiChangePlan -ChangedPath $largeChangedPathSet
$uncappedSummary = $largePlan.Reasons -join '; '
$uncappedMarkdownSummary = "## Selected CI plan reasons`n$(($largePlan.Reasons | ForEach-Object { "- $_" }) -join "`n")`n"
$utf8Encoding = [Text.UTF8Encoding]::new($false)
if ($utf8Encoding.GetByteCount($uncappedSummary) -le 128KB) {
	throw '[Large summary] Test data must exceed the Linux per-environment-entry limit.'
}
if ($utf8Encoding.GetByteCount($uncappedMarkdownSummary) -le 512KB) {
	throw '[Large summary] Test data must exceed the step-summary limit.'
}

$temporaryOutputPath = Join-Path ([IO.Path]::GetTempPath()) "devprojex-ci-plan-$([Guid]::NewGuid().ToString('N')).txt"
$temporaryStepSummaryPath = Join-Path ([IO.Path]::GetTempPath()) "devprojex-ci-step-summary-$([Guid]::NewGuid().ToString('N')).md"
$originalStepSummaryPath = $env:GITHUB_STEP_SUMMARY
try {
	$env:GITHUB_STEP_SUMMARY = $temporaryStepSummaryPath
	& (Join-Path $PSScriptRoot 'Select-CiPlan.ps1') `
		-ChangedPath $largeChangedPathSet `
		-GitHubOutputPath $temporaryOutputPath

	$summaryPrefix = 'summary='
	$summaryLine = @(Get-Content -LiteralPath $temporaryOutputPath | Where-Object { $_.StartsWith($summaryPrefix) })
	if ($summaryLine.Count -ne 1) {
		throw "[Large summary] Expected one summary output, found $($summaryLine.Count)."
	}

	$cappedSummary = $summaryLine[0].Substring($summaryPrefix.Length)
	if ($utf8Encoding.GetByteCount($cappedSummary) -gt 16KB) {
		throw '[Large summary] Output exceeded the 16 KiB UTF-8 limit.'
	}

	$omissionMatch = [regex]::Match(
		$cappedSummary,
		'^(?<included>.*?)(?:; )?\.\.\. \(\+(?<omitted>\d+) more reasons\)$')
	if (-not $omissionMatch.Success) {
		throw '[Large summary] Expected the output to end with an omission marker.'
	}

	$includedText = $omissionMatch.Groups['included'].Value
	$includedReasonCount = if ([string]::IsNullOrEmpty($includedText)) {
		0
	}
	else {
		@($includedText -split '; ').Count
	}
	$reportedOmittedReasonCount = [int]$omissionMatch.Groups['omitted'].Value
	$expectedOmittedReasonCount = $largePlan.Reasons.Count - $includedReasonCount
	if ($reportedOmittedReasonCount -ne $expectedOmittedReasonCount) {
		throw "[Large summary] Expected $expectedOmittedReasonCount omitted reasons, reported $reportedOmittedReasonCount."
	}

	$markdownSummaryBytes = [IO.File]::ReadAllBytes($temporaryStepSummaryPath)
	if ($markdownSummaryBytes.Length -gt 512KB) {
		throw '[Large summary] Step summary exceeded the 512 KiB UTF-8 limit.'
	}

	$markdownSummary = $utf8Encoding.GetString($markdownSummaryBytes)
	$markdownReasonLines = @(($markdownSummary -split "`n") | Where-Object { $_.StartsWith('- ') })
	$markdownOmissionMatch = [regex]::Match(
		$markdownReasonLines[-1],
		'^\- \.\.\. \(\+(?<omitted>\d+) more reasons\)$')
	if (-not $markdownOmissionMatch.Success) {
		throw '[Large summary] Expected the step summary to end with an omission marker.'
	}

	$reportedMarkdownOmissionCount = [int]$markdownOmissionMatch.Groups['omitted'].Value
	$expectedMarkdownOmissionCount = $largePlan.Reasons.Count - ($markdownReasonLines.Count - 1)
	if ($reportedMarkdownOmissionCount -ne $expectedMarkdownOmissionCount) {
		throw "[Large summary] Expected $expectedMarkdownOmissionCount omitted Markdown reasons, reported $reportedMarkdownOmissionCount."
	}
}
finally {
	$env:GITHUB_STEP_SUMMARY = $originalStepSummaryPath
	Remove-Item -LiteralPath $temporaryOutputPath, $temporaryStepSummaryPath -Force -ErrorAction SilentlyContinue
}

Write-Host 'CI change planner contract tests passed.'
