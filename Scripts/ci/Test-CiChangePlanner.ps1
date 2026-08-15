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
	-Enabled @('Unit', 'Integration', 'Terminal', 'Documentation') `
	-Disabled @('UI', 'TerminalCommand', 'IgnoreScanner', 'Release', 'Store', 'Full')

Assert-Plan -Name 'Nested help text stays content-coupled' -Path 'Assets/HelpContent/en/overview.txt' `
	-Enabled @('Unit', 'Integration', 'Terminal', 'Documentation') `
	-Disabled @('UI', 'TerminalCommand', 'IgnoreScanner', 'Release', 'Store', 'Full')

Assert-Plan -Name 'Non-text file under help content fails safe to production' -Path 'Assets/HelpContent/generate-help.ps1' `
	-Enabled @('Unit', 'Integration', 'Terminal', 'UI', 'TerminalCommand', 'Release', 'Store') `
	-Disabled @('Full')

Assert-Plan -Name 'Desktop production change' -Path 'Apps/Avalonia/Views/MainWindow.axaml' `
	-Enabled @('Unit', 'Integration', 'UI', 'Release', 'Store') `
	-Disabled @('Terminal', 'TerminalCommand', 'IgnoreScanner', 'Full')

Assert-Plan -Name 'Terminal production change' -Path 'Apps/Terminal/Tui/TerminalWorkspace.cs' `
	-Enabled @('Integration', 'Terminal', 'TerminalCommand', 'Release', 'Store') `
	-Disabled @('Unit', 'UI', 'IgnoreScanner', 'Full')

Assert-Plan -Name 'Ignore production change' -Path 'Application/Services/IgnoreRulesService.cs' `
	-Enabled @('Unit', 'Integration', 'Terminal', 'UI', 'TerminalCommand', 'IgnoreScanner', 'Release', 'Store') `
	-Disabled @('Full')

Assert-Plan -Name 'Unit test only' -Path 'Tests/DevProjex.Tests.Unit/FooTests.cs' `
	-Enabled Unit `
	-Disabled @('Integration', 'Terminal', 'UI', 'TerminalCommand', 'IgnoreScanner', 'Documentation', 'Release', 'Store', 'Full')

Assert-Plan -Name 'Integration test covers excluded terminal category' -Path 'Tests/DevProjex.Tests.Integration/FooTests.cs' `
	-Enabled @('Integration', 'TerminalCommand') `
	-Disabled @('Unit', 'Terminal', 'UI', 'IgnoreScanner', 'Documentation', 'Release', 'Store', 'Full')

Assert-Plan -Name 'Ignore integration test' -Path 'Tests/DevProjex.Tests.Integration/IgnoreContractTests.cs' `
	-Enabled @('Integration', 'TerminalCommand', 'IgnoreScanner') `
	-Disabled @('Unit', 'Terminal', 'UI', 'Documentation', 'Release', 'Store', 'Full')

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

$unitPlan = Get-CiChangePlan -ChangedPath 'Tests/DevProjex.Tests.Unit/FooTests.cs'
if ($unitPlan.TestMatrix.include.Count -ne 3) {
	throw "[Unit matrix] Expected one Unit job per OS, found $($unitPlan.TestMatrix.include.Count)."
}

$fullPlan = Get-CiChangePlan -Full
if ($fullPlan.TestMatrix.include.Count -ne 12) {
	throw "[Full matrix] Expected twelve suite/OS jobs, found $($fullPlan.TestMatrix.include.Count)."
}

$beforeSha = '1111111111111111111111111111111111111111'
$afterSha = '2222222222222222222222222222222222222222'
$syncComparison = Get-CiEventComparison -EventName pull_request -Event ([pscustomobject]@{
	action = 'synchronize'
	before = $beforeSha
	after = $afterSha
})
if ($syncComparison.Full -or $syncComparison.MergeBase -or $syncComparison.BaseSha -ne $beforeSha -or $syncComparison.HeadSha -ne $afterSha) {
	throw '[PR synchronize] Expected an incremental previous-HEAD to new-HEAD comparison.'
}

$openedComparison = Get-CiEventComparison -EventName pull_request -Event ([pscustomobject]@{
	action = 'opened'
	pull_request = [pscustomobject]@{
		base = [pscustomobject]@{ sha = $beforeSha }
		head = [pscustomobject]@{ sha = $afterSha }
	}
})
if ($openedComparison.Full -or -not $openedComparison.MergeBase) {
	throw '[PR opened] Expected a complete merge-base comparison.'
}

$newBranchComparison = Get-CiEventComparison -EventName push -Event ([pscustomobject]@{
	before = '0000000000000000000000000000000000000000'
	after = $afterSha
})
if (-not $newBranchComparison.Full) {
	throw '[New branch push] Expected safe full validation when no previous commit exists.'
}

$metadataPlan = Get-CiChangePlan -ChangedPath 'release-note.txt'
if ($metadataPlan.HasTestMatrix) {
	throw '[Metadata only] Expected an empty heavy-test matrix.'
}

Write-Host 'CI change planner contract tests passed.'
