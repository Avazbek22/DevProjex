#Requires -Version 7.0
[CmdletBinding()]
param(
	[string] $RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-GateScript {
	param([string] $Workflow, [string] $Step)
	$lines = [IO.File]::ReadAllLines((Join-Path $RepositoryRoot ".github/workflows/$Workflow"))
	$stepIndex = [Array]::FindIndex($lines, [Predicate[string]] { param($line) $line.Trim() -eq "- name: $Step" })
	if ($stepIndex -lt 0) { throw "Missing step '$Step' in $Workflow." }
	$runIndex = $stepIndex + 1
	while ($runIndex -lt $lines.Length -and $lines[$runIndex].Trim() -ne 'run: |') { $runIndex++ }
	if ($runIndex -eq $lines.Length) { throw "Missing script for '$Step'." }
	$indent = $lines[$runIndex].Length - $lines[$runIndex].TrimStart().Length + 2
	$body = [Collections.Generic.List[string]]::new()
	for ($index = $runIndex + 1; $index -lt $lines.Length; $index++) {
		$line = $lines[$index]
		if ([string]::IsNullOrWhiteSpace($line)) { $body.Add(''); continue }
		if ($line.Length - $line.TrimStart().Length -lt $indent) { break }
		$body.Add($line.Substring($indent))
	}
	if ($body.Count -eq 0) { throw "Empty script for '$Step'." }
	return ($body -join [Environment]::NewLine)
}

$gates = @(
	@{ Workflow = 'dotnet.yml'; Step = 'Verify selected jobs'; Result = 'TEST_RESULT'; Key = 'tests'; Prefix = 'Selected CI jobs failed:' },
	@{ Workflow = 'release-validate.yml'; Step = 'Verify selected release jobs'; Result = 'PUBLISH_RESULT'; Key = 'publish'; Prefix = 'Selected release jobs failed:' },
	@{ Workflow = 'store-package-smoke.yml'; Step = 'Verify selected Store jobs'; Result = 'STORE_RESULT'; Key = 'store'; Prefix = 'Selected Store jobs failed:' }
)
$cases = @(
	@{ Result = 'success'; Full = 'false'; Accept = $true },
	@{ Result = 'success'; Full = 'true'; Accept = $true },
	@{ Result = 'skipped'; Full = 'false'; Accept = $true },
	@{ Result = 'skipped'; Full = 'true'; Accept = $false },
	@{ Result = 'failure'; Full = 'false'; Accept = $false },
	@{ Result = 'cancelled'; Full = 'false'; Accept = $false },
	@{ Result = 'timed_out'; Full = 'false'; Accept = $false }
)
$checked = 0
foreach ($gate in $gates) {
	$source = Get-GateScript -Workflow $gate.Workflow -Step $gate.Step
	$script = [ScriptBlock]::Create($source)
	$variables = @([regex]::Matches($source, '\$env:([A-Z_]+_RESULT)') |
		ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique) + @('FORCE_FULL')
	$saved = @{}
	foreach ($variable in $variables) { $saved[$variable] = [Environment]::GetEnvironmentVariable($variable, 'Process') }
	try {
		foreach ($case in $cases) {
			foreach ($variable in $variables) { [Environment]::SetEnvironmentVariable($variable, 'success', 'Process') }
			[Environment]::SetEnvironmentVariable('FORCE_FULL', $case.Full, 'Process')
			[Environment]::SetEnvironmentVariable($gate.Result, $case.Result, 'Process')
			$message = $null
			try { & $script } catch { $message = $_.Exception.Message }
			if ($case.Accept) {
				if ($null -ne $message) { throw "$($gate.Workflow) rejected $($case.Result): $message" }
			}
			else {
				$expected = "$($gate.Prefix) $($gate.Key)=$($case.Result)"
				if ($message -ne $expected) { throw "$($gate.Workflow): expected '$expected', received '$message'." }
			}
			$checked++
		}
	}
	finally {
		foreach ($variable in $variables) { [Environment]::SetEnvironmentVariable($variable, $saved[$variable], 'Process') }
	}
}
Write-Host "Checked $checked workflow result gate contracts."
