[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)] [string] $ResolvedVersion,
	[Parameter(Mandatory = $true)] [string] $EventName,
	[string] $ReleaseTag = "",
	[string] $PropsPath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ([string]::IsNullOrWhiteSpace($PropsPath)) {
	$PropsPath = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) 'Directory.Build.props'
}
$resolvedPropsPath = [System.IO.Path]::GetFullPath($PropsPath)
if (-not (Test-Path -LiteralPath $resolvedPropsPath -PathType Leaf)) {
	throw "Directory.Build.props was not found: $resolvedPropsPath"
}

[xml]$props = Get-Content -LiteralPath $resolvedPropsPath -Raw
$versionNodes = @($props.SelectNodes("//*[local-name()='DevProjexVersion']"))
if ($versionNodes.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$versionNodes[0].InnerText)) {
	throw "Directory.Build.props must contain exactly one non-empty DevProjexVersion."
}
$repositoryVersion = ([string]$versionNodes[0].InnerText).Trim()

if ($EventName -ceq 'release') {
	$tagVersion = if ($ReleaseTag.StartsWith('v', [System.StringComparison]::Ordinal)) {
		$ReleaseTag.Substring(1)
	} else {
		$ReleaseTag
	}
	if ($tagVersion -cne $repositoryVersion) {
		[Console]::Error.WriteLine(
			"Release tag version '$tagVersion' does not match DevProjexVersion '$repositoryVersion' in Directory.Build.props.")
		exit 1
	}
}
elseif ($EventName -ceq 'workflow_dispatch') {
	$summaryPath = [Environment]::GetEnvironmentVariable('GITHUB_STEP_SUMMARY')
	if (-not [string]::IsNullOrWhiteSpace($summaryPath)) {
		@(
			'### Release version selection'
			"- Explicit workflow_dispatch override: ``$ResolvedVersion``."
			"- Repository DevProjexVersion: ``$repositoryVersion``."
		) | Add-Content -LiteralPath $summaryPath -Encoding utf8
	}
}

Write-Host "Release version metadata: event=$EventName; resolved=$ResolvedVersion; repository=$repositoryVersion"
