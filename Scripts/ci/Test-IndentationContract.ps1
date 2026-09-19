#Requires -Version 7.0
<#
.SYNOPSIS
	Checks that Test-Indentation.ps1 still fails what it should and passes what it should.

.DESCRIPTION
	The gate is a comparison between two commits, so the cases below are real repositories with real
	history rather than stubs. Each one builds a base commit, a head commit, and runs the gate
	between them exactly as the workflow does, step summary included.

	One case exists because it was missing. The gate shipped having only ever been exercised on
	short changes, where no touched file had differed from the rules beforehand. The first change
	large enough to include such a file crashed it — the reporting list was non-empty for the first
	time, and the code that formatted it used a method that does not bind the loop variable. So
	`a long list of files that already differed` is here, and it fails against the code as it was.
#>
[CmdletBinding()]
param(
	[string] $GatePath = (Join-Path $PSScriptRoot 'Test-Indentation.ps1'),

	[string] $EditorConfigPath = (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) '.editorconfig')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$failures = [Collections.Generic.List[string]]::new()
$root = Join-Path ([IO.Path]::GetTempPath()) ("devprojex-indent-" + [Guid]::NewGuid().ToString('N'))

# Indented with spaces, which the rules call tabs, so the formatter reports it every time.
$differing = @(
	'namespace Fixture;',
	'',
	'internal static class Sample',
	'{',
	'    internal static int Value()',
	'    {',
	'        return 1;',
	'    }',
	'}') -join "`n"

$conforming = @(
	'namespace Fixture;',
	'',
	'internal static class Sample',
	'{',
	"`tinternal static int Value() => 1;",
	'}') -join "`n"

function New-Repository {
	param([string] $Path, [switch] $WithRules)

	New-Item -ItemType Directory -Path $Path -Force | Out-Null
	Push-Location $Path
	try {
		& git init --quiet --initial-branch=main 2>&1 | Out-Null
		& git config user.email 'contract@example.invalid' | Out-Null
		& git config user.name 'Contract' | Out-Null
		& git config commit.gpgsign false | Out-Null
		if ($WithRules) {
			Copy-Item -LiteralPath $EditorConfigPath -Destination (Join-Path $Path '.editorconfig')
		}
	}
	finally {
		Pop-Location
	}
}

function Add-Commit {
	param([string] $Path, [string] $Message)

	Push-Location $Path
	try {
		& git add -A 2>&1 | Out-Null
		& git commit --quiet -m $Message 2>&1 | Out-Null
		return (& git rev-parse HEAD).Trim()
	}
	finally {
		Pop-Location
	}
}

function Write-Source {
	param([string] $Path, [string] $Relative, [string] $Content)

	$file = Join-Path $Path $Relative
	New-Item -ItemType Directory -Path (Split-Path -Parent $file) -Force | Out-Null
	Set-Content -LiteralPath $file -Value $Content -Encoding UTF8
}

function Test-Case {
	param([string] $Name, [string] $Path, [string] $Base, [string] $Head, [switch] $ShouldPass)

	$summaryFile = Join-Path $Path 'step-summary.md'
	Set-Content -LiteralPath $summaryFile -Value '' -NoNewline
	$accepted = $true
	$output = ''
	try {
		$output = & $GatePath -BaseSha $Base -HeadSha $Head -RepositoryRoot $Path `
			-StepSummaryPath $summaryFile -EditorConfigPath $EditorConfigPath 2>&1 | Out-String
		if ($LASTEXITCODE -ne 0) {
			$accepted = $false
		}
	}
	catch {
		$accepted = $false
		$output = $_.Exception.Message
	}

	# A crash is not a verdict. The gate has to reach a decision and say so, which is what
	# distinguishes "this change is fine" from "the gate fell over before deciding".
	if ($output -match 'cannot be retrieved because it has not been set' -or $output -match 'Exception calling') {
		$failures.Add("$Name crashed instead of deciding: $($output.Trim())")
		return
	}
	if ($ShouldPass -and -not $accepted) {
		$failures.Add("$Name should have passed but failed: $($output.Trim())")
	}
	elseif (-not $ShouldPass -and $accepted) {
		$failures.Add("$Name should have failed but passed.")
	}
}

try {
	# --- the case that was missing: a change touching many files that already differed ----------
	$large = Join-Path $root 'large'
	New-Repository -Path $large -WithRules
	foreach ($index in 1..40) {
		Write-Source -Path $large -Relative "src/Differing$index.cs" -Content $differing
	}
	Write-Source -Path $large -Relative 'src/Clean.cs' -Content $conforming
	$base = Add-Commit -Path $large -Message 'base'
	foreach ($index in 1..40) {
		Write-Source -Path $large -Relative "src/Differing$index.cs" -Content ($differing + "`n// touched")
	}
	$head = Add-Commit -Path $large -Message 'touch every differing file'
	Test-Case -Name 'a long list of files that already differed' -Path $large -Base $base -Head $head -ShouldPass

	# --- a base from before the rules existed ---------------------------------------------------
	$unruled = Join-Path $root 'unruled'
	New-Repository -Path $unruled
	Write-Source -Path $unruled -Relative 'src/Clean.cs' -Content $conforming
	Write-Source -Path $unruled -Relative 'src/Differing.cs' -Content $differing
	$base = Add-Commit -Path $unruled -Message 'base without rules'
	Copy-Item -LiteralPath $EditorConfigPath -Destination (Join-Path $unruled '.editorconfig')
	Write-Source -Path $unruled -Relative 'src/Clean.cs' -Content $differing
	$head = Add-Commit -Path $unruled -Message 'adopt the rules'
	Test-Case -Name 'a base that predates the rules' -Path $unruled -Base $base -Head $head -ShouldPass

	# --- the rule itself, both directions --------------------------------------------------------
	$broken = Join-Path $root 'broken'
	New-Repository -Path $broken -WithRules
	Write-Source -Path $broken -Relative 'src/Clean.cs' -Content $conforming
	Write-Source -Path $broken -Relative 'src/Differing.cs' -Content $differing
	$base = Add-Commit -Path $broken -Message 'base'
	Write-Source -Path $broken -Relative 'src/Clean.cs' -Content ($conforming -replace "`tinternal", "`t`t`tinternal")
	$head = Add-Commit -Path $broken -Message 'break a file that was clean'
	Test-Case -Name 'a clean file given broken indentation' -Path $broken -Base $base -Head $head

	$carriedOnly = Join-Path $root 'carried'
	New-Repository -Path $carriedOnly -WithRules
	Write-Source -Path $carriedOnly -Relative 'src/Differing.cs' -Content $differing
	$base = Add-Commit -Path $carriedOnly -Message 'base'
	Write-Source -Path $carriedOnly -Relative 'src/Differing.cs' -Content ($differing + "`n// touched")
	$head = Add-Commit -Path $carriedOnly -Message 'touch it'
	Test-Case -Name 'a file that already differed is reported, not failed' -Path $carriedOnly -Base $base -Head $head -ShouldPass

	$newClean = Join-Path $root 'newfile'
	New-Repository -Path $newClean -WithRules
	Write-Source -Path $newClean -Relative 'src/Existing.cs' -Content $conforming
	$base = Add-Commit -Path $newClean -Message 'base'
	Write-Source -Path $newClean -Relative 'src/Added.cs' -Content $differing
	$head = Add-Commit -Path $newClean -Message 'add a file that does not match'
	Test-Case -Name 'a new file that does not match the rules' -Path $newClean -Base $base -Head $head

	$untouched = Join-Path $root 'untouched'
	New-Repository -Path $untouched -WithRules
	Write-Source -Path $untouched -Relative 'src/Clean.cs' -Content $conforming
	$base = Add-Commit -Path $untouched -Message 'base'
	Set-Content -LiteralPath (Join-Path $untouched 'README.md') -Value 'text' -Encoding UTF8
	$head = Add-Commit -Path $untouched -Message 'documentation only'
	Test-Case -Name 'a change touching no C# file' -Path $untouched -Base $base -Head $head -ShouldPass
}
finally {
	Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
	Write-Host 'The indentation gate no longer behaves as its own documentation says:'
	foreach ($failure in $failures) {
		Write-Host "  $failure"
	}
	throw "Test-Indentation.ps1 failed $($failures.Count) of its own contract cases."
}

Write-Host 'Indentation gate contract cases passed.'
