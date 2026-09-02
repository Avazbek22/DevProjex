Set-StrictMode -Version Latest

function New-CiChangePlan {
	return [ordered]@{
		Unit = $false
		Integration = $false
		Terminal = $false
		UI = $false
		TerminalCommand = $false
		IgnoreScanner = $false
		Documentation = $false
		Release = $false
		Store = $false
		Full = $false
		Reasons = [System.Collections.Generic.List[string]]::new()
	}
}

function Enable-CiTargets {
	param(
		[Parameter(Mandatory)]
		[System.Collections.IDictionary] $Plan,

		[Parameter(Mandatory)]
		[string[]] $Targets,

		[Parameter(Mandatory)]
		[string] $Reason
	)

	foreach ($target in $Targets) {
		if (-not $Plan.Contains($target)) {
			throw "Unknown CI target '$target'."
		}

		$Plan[$target] = $true
	}

	if (-not $Plan.Reasons.Contains($Reason)) {
		$Plan.Reasons.Add($Reason)
	}
}

function Enable-FullCiPlan {
	param(
		[Parameter(Mandatory)]
		[System.Collections.IDictionary] $Plan,

		[Parameter(Mandatory)]
		[string] $Reason
	)

	Enable-CiTargets -Plan $Plan -Targets @(
		'Unit',
		'Integration',
		'Terminal',
		'UI',
		'TerminalCommand',
		'IgnoreScanner',
		'Release',
		'Store',
		'Full'
	) -Reason $Reason
}

function ConvertTo-RepositoryPath {
	param([Parameter(Mandatory)][string] $Path)

	$normalized = $Path.Replace('\', '/').Trim()
	while ($normalized.StartsWith('./', [StringComparison]::Ordinal)) {
		$normalized = $normalized.Substring(2)
	}

	return $normalized.TrimStart('/')
}

function Test-PathStartsWith {
	param(
		[Parameter(Mandatory)][string] $Path,
		[Parameter(Mandatory)][string] $Prefix
	)

	return $Path.StartsWith($Prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Test-PathEquals {
	param(
		[Parameter(Mandatory)][string] $Path,
		[Parameter(Mandatory)][string] $Expected
	)

	return $Path.Equals($Expected, [StringComparison]::OrdinalIgnoreCase)
}

function Test-StaticDocumentationAsset {
	param([Parameter(Mandatory)][string] $Path)

	if (-not (Test-PathStartsWith $Path '.github/assets/')) {
		return $false
	}

	# Keep this allowlist deliberately narrow. Renderable repository artwork cannot affect
	# the product, while scripts or unfamiliar formats in the same directory must fail safe.
	$extension = [IO.Path]::GetExtension($Path)
	return $extension -in @('.svg', '.png', '.jpg', '.jpeg', '.gif', '.webp')
}

function Add-PathToCiPlan {
	param(
		[Parameter(Mandatory)]
		[System.Collections.IDictionary] $Plan,

		[Parameter(Mandatory)]
		[string] $Path
	)

	$normalized = ConvertTo-RepositoryPath -Path $Path
	if ([string]::IsNullOrWhiteSpace($normalized)) {
		return
	}

	# CI infrastructure is deliberately fail-safe: its own changes exercise every gate once.
	if ((Test-PathStartsWith $normalized '.github/workflows/') -or
		(Test-PathStartsWith $normalized 'Scripts/ci/')) {
		Enable-FullCiPlan -Plan $Plan -Reason "CI infrastructure: $normalized"
		return
	}

	if ((Test-PathEquals $normalized 'DevProjex.sln') -or
		(Test-PathEquals $normalized 'Directory.Build.props') -or
		(Test-PathEquals $normalized 'Directory.Packages.props') -or
		(Test-PathEquals $normalized 'global.json')) {
		Enable-FullCiPlan -Plan $Plan -Reason "Build graph: $normalized"
		return
	}

	if ((Test-PathEquals $normalized 'README.md') -or
		(Test-StaticDocumentationAsset $normalized) -or
		(Test-PathStartsWith $normalized 'Docs/') -or
		((Test-PathStartsWith $normalized 'Packaging/') -and $normalized.EndsWith('.md', [StringComparison]::OrdinalIgnoreCase))) {
		Enable-CiTargets -Plan $Plan -Targets @('Documentation') -Reason "Documentation contract: $normalized"
		return
	}

	if ((Test-PathStartsWith $normalized '.idea/') -or
		(Test-PathStartsWith $normalized '.run/') -or
		(Test-PathStartsWith $normalized '.github/ISSUE_TEMPLATE/') -or
		(Test-PathEquals $normalized '.github/PULL_REQUEST_TEMPLATE.md') -or
		(Test-PathEquals $normalized '.github/FUNDING.yml') -or
		(Test-PathEquals $normalized '.gitattributes') -or
		(Test-PathEquals $normalized '.gitignore') -or
		(Test-PathEquals $normalized 'LICENSE') -or
		(Test-PathEquals $normalized 'AGENTS.md') -or
		(Test-PathEquals $normalized 'AboutProject.md') -or
		(Test-PathEquals $normalized 'CODE_OF_CONDUCT.md') -or
		(Test-PathEquals $normalized 'CONTRIBUTING.md') -or
		(Test-PathEquals $normalized 'SECURITY.md') -or
		(Test-PathEquals $normalized 'SUPPORT.md') -or
		(Test-PathEquals $normalized 'TRADEMARKS.md') -or
		(Test-PathEquals $normalized 'Setup-AI-Agents.ps1') -or
		(Test-PathEquals $normalized 'SetupAI.txt') -or
		(($normalized -notmatch '/') -and $normalized.EndsWith('.txt', [StringComparison]::OrdinalIgnoreCase))) {
		$Plan.Reasons.Add("Repository metadata only: $normalized")
		return
	}

	if (Test-PathStartsWith $normalized 'Tests/DevProjex.Tests.Unit/') {
		Enable-CiTargets -Plan $Plan -Targets @('Unit') -Reason "Unit tests: $normalized"
		return
	}

	if (Test-PathStartsWith $normalized 'Tests/DevProjex.Tests.Integration/') {
		# The main Integration matrix excludes TerminalCommand tests, so both paths are required.
		Enable-CiTargets -Plan $Plan -Targets @('Integration', 'TerminalCommand') -Reason "Integration tests: $normalized"
		if ($normalized -match '(?i)(ignore|scanner|reparsepoint|filesystemmutation)') {
			Enable-CiTargets -Plan $Plan -Targets @('IgnoreScanner') -Reason "Ignore/scanner integration tests: $normalized"
		}
		return
	}

	if (Test-PathEquals $normalized 'Tests/DevProjex.Tests.Terminal/PublishedSingleFileExtractionProcessTests.cs') {
		# These tests skip without the RID-specific single-file artifact produced by Release Validation.
		# Routing them only to the ordinary Terminal suite would report green without executing them.
		Enable-CiTargets -Plan $Plan -Targets @('Terminal', 'Release') -Reason "Published process tests: $normalized"
		return
	}

	if (Test-PathStartsWith $normalized 'Tests/DevProjex.Tests.Terminal/') {
		Enable-CiTargets -Plan $Plan -Targets @('Terminal') -Reason "Terminal tests: $normalized"
		return
	}

	if (Test-PathStartsWith $normalized 'Tests/DevProjex.Tests.UI/') {
		Enable-CiTargets -Plan $Plan -Targets @('UI') -Reason "UI tests: $normalized"
		return
	}

	if ((Test-PathStartsWith $normalized 'Tests/DevProjex.Tests.Terminal.ProgressHost/') -or
		(Test-PathStartsWith $normalized 'Tests/Shared/TerminalProgress/')) {
		Enable-CiTargets -Plan $Plan -Targets @('Terminal') -Reason "Terminal test infrastructure: $normalized"
		return
	}

	if (Test-PathStartsWith $normalized 'Tests/Shared/ProjectLoadWorkflow/') {
		Enable-CiTargets -Plan $Plan -Targets @('Unit', 'Integration', 'UI') -Reason "Shared project-load tests: $normalized"
		return
	}

	if (Test-PathStartsWith $normalized 'Tests/Shared/StoreListing/') {
		Enable-CiTargets -Plan $Plan -Targets @('Unit', 'Integration') -Reason "Shared Store-listing tests: $normalized"
		return
	}

	if (Test-PathStartsWith $normalized 'Tests/') {
		Enable-CiTargets -Plan $Plan -Targets @('Unit', 'Integration', 'Terminal', 'UI', 'TerminalCommand', 'IgnoreScanner') -Reason "Unclassified test infrastructure: $normalized"
		return
	}

	if (Test-PathStartsWith $normalized 'Apps/Avalonia/') {
		Enable-CiTargets -Plan $Plan -Targets @('Unit', 'Integration', 'UI', 'Release', 'Store') -Reason "Desktop production surface: $normalized"
		return
	}

	if (Test-PathStartsWith $normalized 'Apps/Terminal/') {
		Enable-CiTargets -Plan $Plan -Targets @('Integration', 'Terminal', 'TerminalCommand', 'Release', 'Store') -Reason "Terminal production surface: $normalized"
		return
	}

	if ((Test-PathStartsWith $normalized 'Assets/HelpContent/') -and
		$normalized.EndsWith('.txt', [StringComparison]::OrdinalIgnoreCase)) {
		# Help text ships inside every product, but an edit can only break the suites that
		# read it: embedded-resource loading (Unit), per-language coverage (Integration)
		# and the documentation contracts (Terminal, Documentation). Non-text files in the
		# same directory stay on the shared production layer plan below.
		Enable-CiTargets -Plan $Plan -Targets @('Unit', 'Integration', 'Terminal', 'Documentation') -Reason "Help content: $normalized"
		return
	}

	if ((Test-PathStartsWith $normalized 'Kernel/') -or
		(Test-PathStartsWith $normalized 'Application/') -or
		(Test-PathStartsWith $normalized 'Infrastructure/') -or
		(Test-PathStartsWith $normalized 'Assets/')) {
		Enable-CiTargets -Plan $Plan -Targets @('Unit', 'Integration', 'Terminal', 'UI', 'TerminalCommand', 'Release', 'Store') -Reason "Shared production layer: $normalized"
		if ($normalized -match '(?i)(ignore|scanner|projectscope|projectrootfacts|selection|gittracked|filesystem|reparse)') {
			Enable-CiTargets -Plan $Plan -Targets @('IgnoreScanner') -Reason "Ignore/scanner production surface: $normalized"
		}
		return
	}

	if (Test-PathStartsWith $normalized 'Packaging/') {
		Enable-CiTargets -Plan $Plan -Targets @('Unit', 'Integration', 'Release', 'Store') -Reason "Packaging contract: $normalized"
		return
	}

	if (Test-PathStartsWith $normalized 'Scripts/') {
		Enable-CiTargets -Plan $Plan -Targets @('Unit', 'Integration', 'Release', 'Store') -Reason "Build/release tooling: $normalized"
		return
	}

	# New top-level directories and unclassified files must never silently bypass validation.
	Enable-FullCiPlan -Plan $Plan -Reason "Unclassified path (safe full plan): $normalized"
}

function New-CiTestMatrix {
	param([Parameter(Mandatory)][System.Collections.IDictionary] $Plan)

	$suites = @(
		@{ Name = 'Unit'; Id = 'unit'; Project = 'Tests/DevProjex.Tests.Unit/DevProjex.Tests.Unit.csproj'; Trx = 'unit.trx'; Filter = '' },
		@{ Name = 'Integration'; Id = 'integration'; Project = 'Tests/DevProjex.Tests.Integration/DevProjex.Tests.Integration.csproj'; Trx = 'integration.trx'; Filter = '--filter Category!=TerminalCommand' },
		@{ Name = 'Terminal'; Id = 'terminal'; Project = 'Tests/DevProjex.Tests.Terminal/DevProjex.Tests.Terminal.csproj'; Trx = 'terminal.trx'; Filter = '' },
		@{ Name = 'UI'; Id = 'ui'; Project = 'Tests/DevProjex.Tests.UI/DevProjex.Tests.UI.csproj'; Trx = 'ui.trx'; Filter = '' }
	)
	$operatingSystems = @(
		@{ Name = 'Windows'; Runner = 'windows-latest'; Id = 'windows' },
		@{ Name = 'Linux'; Runner = 'ubuntu-latest'; Id = 'linux' },
		@{ Name = 'macOS'; Runner = 'macos-latest'; Id = 'macos' }
	)
	$include = [System.Collections.Generic.List[object]]::new()

	foreach ($operatingSystem in $operatingSystems) {
		foreach ($suite in $suites) {
			if (-not $Plan[$suite.Name]) {
				continue
			}

			$include.Add([ordered]@{
				os_name = $operatingSystem.Name
				runner = $operatingSystem.Runner
				os_id = $operatingSystem.Id
				suite_name = $suite.Name
				suite_id = $suite.Id
				project_path = $suite.Project
				trx_name = $suite.Trx
				test_filter_args = $suite.Filter
			})
		}
	}

	return @{ include = $include.ToArray() }
}

function Get-CiChangePlan {
	[CmdletBinding()]
	param(
		[string[]] $ChangedPath = @(),
		[switch] $Full
	)

	$plan = New-CiChangePlan
	if ($Full) {
		Enable-FullCiPlan -Plan $plan -Reason 'Full validation requested.'
	}
	else {
		foreach ($path in $ChangedPath) {
			Add-PathToCiPlan -Plan $plan -Path $path
		}

		if ($ChangedPath.Count -eq 0) {
			Enable-FullCiPlan -Plan $plan -Reason 'No changed paths were detected; using the safe full plan.'
		}
	}

	$matrix = New-CiTestMatrix -Plan $plan
	return [pscustomobject]@{
		Unit = [bool]$plan.Unit
		Integration = [bool]$plan.Integration
		Terminal = [bool]$plan.Terminal
		UI = [bool]$plan.UI
		TerminalCommand = [bool]$plan.TerminalCommand
		IgnoreScanner = [bool]$plan.IgnoreScanner
		Documentation = [bool]$plan.Documentation
		Release = [bool]$plan.Release
		Store = [bool]$plan.Store
		Full = [bool]$plan.Full
		HasTestMatrix = $matrix.include.Count -gt 0
		TestMatrix = $matrix
		Reasons = $plan.Reasons.ToArray()
	}
}

function Get-CiEventComparison {
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)][string] $EventName,
		[Parameter(Mandatory)][psobject] $Event
	)

	$validSha = '^[0-9a-fA-F]{40}$'
	$eventBefore = if ($null -ne $Event.PSObject.Properties['before']) { [string]$Event.before } else { '' }
	$eventAfter = if ($null -ne $Event.PSObject.Properties['after']) { [string]$Event.after } else { '' }
	$action = if ($null -ne $Event.PSObject.Properties['action']) { [string]$Event.action } else { '' }
	$pullRequest = if ($null -ne $Event.PSObject.Properties['pull_request']) { $Event.pull_request } else { $null }

	if ($EventName -eq 'workflow_dispatch') {
		return [pscustomobject]@{ Full = $true; BaseSha = ''; HeadSha = ''; MergeBase = $false }
	}

	# A synchronize event compares only the newly pushed delta. The previous HEAD already has CI evidence.
	if ($EventName -eq 'pull_request' -and
		$action -eq 'synchronize' -and
		$eventBefore -match $validSha -and
		$eventAfter -match $validSha) {
		return [pscustomobject]@{ Full = $false; BaseSha = $eventBefore; HeadSha = $eventAfter; MergeBase = $false }
	}

	if ($EventName -eq 'pull_request' -and $null -ne $pullRequest) {
		$baseSha = [string]$pullRequest.base.sha
		$headSha = [string]$pullRequest.head.sha
		if ($baseSha -match $validSha -and $headSha -match $validSha) {
			return [pscustomobject]@{ Full = $false; BaseSha = $baseSha; HeadSha = $headSha; MergeBase = $true }
		}
	}

	if ($EventName -eq 'push' -and
		$eventBefore -match $validSha -and
		$eventBefore -notmatch '^0{40}$' -and
		$eventAfter -match $validSha) {
		return [pscustomobject]@{ Full = $false; BaseSha = $eventBefore; HeadSha = $eventAfter; MergeBase = $false }
	}

	return [pscustomobject]@{ Full = $true; BaseSha = ''; HeadSha = ''; MergeBase = $false }
}

Export-ModuleMember -Function Get-CiChangePlan, Get-CiEventComparison
