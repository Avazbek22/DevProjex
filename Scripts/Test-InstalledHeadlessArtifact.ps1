[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Executable,

    [Parameter(Mandatory = $true)]
    [string]$WorkingRoot,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$resolvedExecutable = (Resolve-Path -LiteralPath $Executable).Path
$resolvedWorkingRoot = [System.IO.Path]::GetFullPath($WorkingRoot)
[System.IO.Directory]::CreateDirectory($resolvedWorkingRoot) | Out-Null
$project = Join-Path $resolvedWorkingRoot 'project'
$source = Join-Path $project 'src'
$dataRoot = Join-Path $resolvedWorkingRoot 'data'
[System.IO.Directory]::CreateDirectory($source) | Out-Null
[System.IO.Directory]::CreateDirectory($dataRoot) | Out-Null
[System.IO.File]::WriteAllText(
    (Join-Path $project 'Artifact.csproj'),
    '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>')
[System.IO.File]::WriteAllText(
    (Join-Path $source 'Target.cs'),
    "namespace Artifact;`npublic sealed class Target { }`n")
[System.IO.File]::WriteAllText(
	(Join-Path $source 'App.cs'),
	"namespace Artifact;`npublic sealed class App { private readonly Target _target = new(); public string Find() => `"artifactNeedle`"; }`n")

function Invoke-InstalledCommand {
    param([string[]]$Arguments)

    $lines = @(& $resolvedExecutable @Arguments 2>&1)
    $exitCode = $LASTEXITCODE
    $text = $lines -join [Environment]::NewLine
    if ($exitCode -ne 0) {
		throw "Installed command failed with exit code ${exitCode}: $resolvedExecutable $($Arguments -join ' ')`n$text"
    }
    return $text
}

$previousDataRoot = [Environment]::GetEnvironmentVariable('DEVPROJEX_INTERNAL_DATA_ROOT', 'Process')
try {
    [Environment]::SetEnvironmentVariable('DEVPROJEX_INTERNAL_DATA_ROOT', $dataRoot, 'Process')

    $version = (Invoke-InstalledCommand @('--version')).Trim()
    if ($version -cne $ExpectedVersion) {
        throw "Expected version '$ExpectedVersion', found '$version'."
    }
    Write-Output "version: $version"

    $tree = Invoke-InstalledCommand @(
        'tree', $project, '--git-mode', 'none', '--exclude', 'none',
        '--format', 'text', '--plain', '--progress', 'never', '-o', '-')
    if (-not $tree.Contains('App.cs', [System.StringComparison]::Ordinal)) {
        throw 'tree did not include App.cs.'
    }
    Write-Output 'tree: ok'

    $search = Invoke-InstalledCommand @(
        'search', 'artifactNeedle', $project, '--git-mode', 'none', '--exclude', 'none',
        '--format', 'json', '--plain', '--progress', 'never', '-o', '-') | ConvertFrom-Json
    if ($search.kind -cne 'devprojex-search-results' -or $search.matches.Count -ne 1) {
        throw 'search did not return the expected result.'
    }
    Write-Output 'search: ok'

	$related = Invoke-InstalledCommand @(
		'related', 'src/App.cs', '--project', $project, '--git-mode', 'none', '--exclude', 'none',
		'--format', 'json', '--plain', '--progress', 'never') | ConvertFrom-Json
    if ($related.kind -cne 'devprojex-related-files') {
        throw 'related did not return its versioned JSON document.'
    }
    Write-Output 'related: ok'

    $profile = Invoke-InstalledCommand @(
        'profile', 'show', $project, '--profile', 'standard', '--format', 'json') | ConvertFrom-Json
    if ($profile.kind -cne 'devprojex-profile') {
        throw 'profile show did not return its versioned JSON document.'
    }
    Write-Output 'profile show: ok'

	$connection = Invoke-InstalledCommand @(
		'mcp', 'connect', $project, '--client', 'json', '--print') | ConvertFrom-Json
	if ([string]::IsNullOrWhiteSpace($connection.mcpServers.devprojex.command)) {
        throw 'mcp connect --print did not return an executable command.'
    }
    Write-Output 'mcp connect --print: ok'
}
finally {
    [Environment]::SetEnvironmentVariable('DEVPROJEX_INTERNAL_DATA_ROOT', $previousDataRoot, 'Process')
}
