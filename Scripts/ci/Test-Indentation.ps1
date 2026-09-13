#Requires -Version 7.0
<#
.SYNOPSIS
	Fails when a change makes a C# file's formatting worse than it already was.

.DESCRIPTION
	The repository has never had a formatting gate, and it cannot adopt one wholesale: most of the
	tree does not match any single set of rules, so a check that demanded conformity everywhere
	would either fail on the first run or force a reformat of half the files. Reformatting rewrites
	authorship across the tree and collides with every open branch, so that decision is taken
	separately from this one.

	What this checks instead is a difference. Each C# file a change touches is formatted twice, as
	it was at the base commit and as it is now, and the two complaint counts are compared. A file
	that was clean has to stay clean. A file that was not is reported and left alone, because the
	change did not put it in that state and fixing it belongs to its own commit.

	Formatting runs in folder mode, which reads the files and the .editorconfig without loading a
	project, so the whole repository takes about as long as a restore and a handful of changed
	files take seconds.
#>
[CmdletBinding()]
param(
	[Parameter(Mandatory)]
	[string] $BaseSha,

	[Parameter(Mandatory)]
	[string] $HeadSha,

	[string] $RepositoryRoot = (Get-Location).Path,

	[string] $StepSummaryPath = $env:GITHUB_STEP_SUMMARY,

	# The rules to apply. Separate from the repository root only so the gate can be exercised
	# against a candidate set of rules before one is committed.
	[string] $EditorConfigPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ChangedCSharpFile {
	param([string] $Base, [string] $Head)

	# Rename detection matters: a renamed file keeps the state it already had, and comparing it
	# against nothing would demand a reformat as the price of moving it.
	$status = & git -c core.quotepath=false diff --name-status -M --diff-filter=ACMR "$Base...$Head" -- '*.cs'
	if ($LASTEXITCODE -ne 0) {
		throw "Unable to list the changed files between '$Base' and '$Head'."
	}
	foreach ($line in $status) {
		if ([string]::IsNullOrWhiteSpace($line)) { continue }
		$fields = $line -split "`t"
		$code = $fields[0]
		if ($code.StartsWith('R')) {
			[pscustomobject]@{ HeadPath = $fields[2]; BasePath = $fields[1] }
		}
		elseif ($code -eq 'A') {
			[pscustomobject]@{ HeadPath = $fields[1]; BasePath = $null }
		}
		else {
			[pscustomobject]@{ HeadPath = $fields[1]; BasePath = $fields[1] }
		}
	}
}

function Write-RevisionTree {
	param([string] $Revision, [string[]] $Path, [string] $Destination, [string] $EditorConfig)

	New-Item -ItemType Directory -Path $Destination -Force | Out-Null
	Copy-Item -LiteralPath $EditorConfig -Destination (Join-Path $Destination '.editorconfig')
	$written = 0
	foreach ($relative in $Path) {
		if ([string]::IsNullOrWhiteSpace($relative)) { continue }
		$target = Join-Path $Destination $relative
		New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
		$content = & git show "${Revision}:${relative}" 2>$null
		if ($LASTEXITCODE -ne 0) { continue }
		# Both sides are written with this platform's line ending, which is also the ending the
		# formatter assumes when .editorconfig does not pin one. Pinning one there would make every
		# checkout on the other platform look wrong, and writing the other one here would make every
		# line a complaint on both sides, which would quietly exempt the file from the comparison.
		[IO.File]::WriteAllText($target, ($content -join [Environment]::NewLine) + [Environment]::NewLine)
		$written++
	}
	return $written
}

function Measure-Formatting {
	param([string] $Directory, [string] $ReportPath)

	$complaints = @{}
	if (-not (Get-ChildItem -LiteralPath $Directory -Recurse -File -Filter '*.cs' | Select-Object -First 1)) {
		return $complaints
	}
	# --verify-no-changes exits non-zero when anything would be reformatted, which is the normal
	# outcome here rather than an error; the report is what carries the answer.
	& dotnet format whitespace --folder $Directory --verify-no-changes --report $ReportPath --verbosity quiet *> $null
	if (-not (Test-Path -LiteralPath $ReportPath)) {
		throw "The formatter produced no report for '$Directory'."
	}
	$report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
	foreach ($entry in @($report)) {
		$relative = [IO.Path]::GetRelativePath($Directory, $entry.FilePath) -replace '\\', '/'
		$complaints[$relative] = @($entry.FileChanges).Count
	}
	return $complaints
}

$editorConfig = if ($EditorConfigPath) { $EditorConfigPath } else { Join-Path $RepositoryRoot '.editorconfig' }
if (-not (Test-Path -LiteralPath $editorConfig)) {
	throw "No .editorconfig at '$RepositoryRoot'; the gate has no rules to apply."
}

Push-Location $RepositoryRoot
try {
	# The whole check is the question "did a file that matched these rules stop matching them", and
	# that question has no answer when the base never had the rules. A release pull request is the
	# case in point: its base is master, hundreds of commits behind and from before the rules
	# existed, so every file that happens to match them there and was edited since would be reported
	# as a regression by a gate that never judged any of those edits. This resolves itself — once a
	# release carries the rules into master, the comparison becomes meaningful there too.
	& git cat-file -e "${BaseSha}:.editorconfig" 2>$null
	if ($LASTEXITCODE -ne 0) {
		Write-Host ("The base commit $BaseSha carries no .editorconfig, so there are no rules a " +
		            'file could have stopped matching. Nothing to compare.')
		exit 0
	}

	$changed = @(Get-ChangedCSharpFile -Base $BaseSha -Head $HeadSha)
	if ($changed.Count -eq 0) {
		Write-Host 'No C# files changed; nothing to check.'
		exit 0
	}

	$workspace = Join-Path ([IO.Path]::GetTempPath()) ("devprojex-format-" + [Guid]::NewGuid().ToString('N'))
	$baseDirectory = Join-Path $workspace 'base'
	$headDirectory = Join-Path $workspace 'head'
	try {
		Write-RevisionTree -Revision $BaseSha -Path @($changed.BasePath) -Destination $baseDirectory -EditorConfig $editorConfig | Out-Null
		Write-RevisionTree -Revision $HeadSha -Path @($changed.HeadPath) -Destination $headDirectory -EditorConfig $editorConfig | Out-Null

		$before = Measure-Formatting -Directory $baseDirectory -ReportPath (Join-Path $workspace 'base.json')
		$after = Measure-Formatting -Directory $headDirectory -ReportPath (Join-Path $workspace 'head.json')

		$failed = [Collections.Generic.List[string]]::new()
		$carried = [Collections.Generic.List[string]]::new()
		foreach ($file in $changed) {
			$head = $file.HeadPath
			$headCount = if ($after.ContainsKey($head)) { $after[$head] } else { 0 }
			if ($headCount -eq 0) { continue }
			$baseCount = 0
			if ($file.BasePath -and $before.ContainsKey($file.BasePath)) { $baseCount = $before[$file.BasePath] }
			if ($baseCount -eq 0) {
				$failed.Add("${head}: $headCount formatting difference(s); this file was clean at $BaseSha.")
			}
			else {
				$carried.Add("${head}: $headCount difference(s), $baseCount already present at $BaseSha.")
			}
		}

		if ($carried.Count -gt 0) {
			Write-Host 'Files that already differed from the style rules and were left alone:'
			$carried | ForEach-Object { Write-Host "  $_" }
		}
		if ($StepSummaryPath) {
			# Written with an ordinary loop rather than the .ForEach() method. That method does not
			# populate $_ under Set-StrictMode -Version Latest, so it throws the moment a list is
			# long enough for its body to run at all — which is to say, the moment a change touches
			# a file that already differed from the rules.
			$summary = [Collections.Generic.List[string]]::new()
			$summary.Add('### Indentation')
			foreach ($line in $failed) {
				$summary.Add("- FAIL $line")
			}
			foreach ($line in $carried) {
				$summary.Add("- carried $line")
			}
			Add-Content -LiteralPath $StepSummaryPath -Value ($summary -join "`n")
		}
		if ($failed.Count -gt 0) {
			Write-Host ''
			Write-Host 'These files were formatted correctly before this change and are not now:'
			$failed | ForEach-Object { Write-Host "  $_" }
			Write-Host ''
			Write-Host "Run: dotnet format whitespace --folder . --include $($failed[0].Split(':')[0])"
			exit 1
		}
		Write-Host "Checked $($changed.Count) changed C# file(s); no file lost its formatting."
		exit 0
	}
	finally {
		Remove-Item -LiteralPath $workspace -Recurse -Force -ErrorAction SilentlyContinue
	}
}
finally {
	Pop-Location
}
