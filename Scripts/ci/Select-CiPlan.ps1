[CmdletBinding(DefaultParameterSetName = 'ChangedPaths')]
param(
	[Parameter(ParameterSetName = 'ChangedPaths')]
	[string[]] $ChangedPath = @(),

	[Parameter(Mandatory, ParameterSetName = 'GitDiff')]
	[ValidatePattern('^[0-9a-fA-F]{40}$')]
	[string] $BaseSha,

	[Parameter(Mandatory, ParameterSetName = 'GitDiff')]
	[ValidatePattern('^[0-9a-fA-F]{40}$')]
	[string] $HeadSha,

	[Parameter(ParameterSetName = 'GitDiff')]
	[switch] $MergeBase,

	[Parameter(Mandatory, ParameterSetName = 'GitHubEvent')]
	[string] $GitHubEventPath,

	[Parameter(Mandatory, ParameterSetName = 'GitHubEvent')]
	[string] $GitHubEventName,

	[Parameter(Mandatory, ParameterSetName = 'Full')]
	[switch] $Full,

	[string] $PreviousGateName,

	[string] $GitHubRepository = $env:GITHUB_REPOSITORY,

	[string] $GitHubToken = $env:GITHUB_TOKEN,

	[string] $GitHubOutputPath,

	[switch] $AsJson
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'CiChangePlanner.psm1') -Force

$utf8Encoding = [Text.UTF8Encoding]::new($false)
$outputSummaryMaximumBytes = 16KB
$stepSummaryMaximumBytes = 512KB

function ConvertTo-CappedReasonText {
	param(
		[Parameter(Mandatory)][AllowEmptyCollection()][string[]] $Reason,
		[Parameter(Mandatory)][ValidateRange(1, [int]::MaxValue)][int] $MaximumUtf8Bytes,
		[string] $Prefix = '',
		[string] $ReasonPrefix = '',
		[string] $Separator = '; ',
		[string] $Suffix = ''
	)

	$safeReasons = @($Reason | ForEach-Object { $_ -replace '[\r\n]+', ' ' })
	$formattedReasons = @($safeReasons | ForEach-Object { "$ReasonPrefix$_" })
	$fullText = "$Prefix$($formattedReasons -join $Separator)$Suffix"
	if ($utf8Encoding.GetByteCount($fullText) -le $MaximumUtf8Bytes) {
		return $fullText
	}

	$fixedByteCount = $utf8Encoding.GetByteCount($Prefix) + $utf8Encoding.GetByteCount($Suffix)
	$separatorByteCount = $utf8Encoding.GetByteCount($Separator)
	$includedReasonsByteCount = 0
	$includedReasonCount = 0

	for ($candidateCount = 0; $candidateCount -lt $safeReasons.Count; $candidateCount++) {
		$omittedReasonCount = $safeReasons.Count - $candidateCount
		$marker = "$ReasonPrefix... (+$omittedReasonCount more reasons)"
		$candidateByteCount = $fixedByteCount +
			$includedReasonsByteCount +
			($separatorByteCount * $candidateCount) +
			$utf8Encoding.GetByteCount($marker)
		if ($candidateByteCount -le $MaximumUtf8Bytes) {
			$includedReasonCount = $candidateCount
		}

		$includedReasonsByteCount += $utf8Encoding.GetByteCount($formattedReasons[$candidateCount])
	}

	$omittedReasonCount = $safeReasons.Count - $includedReasonCount
	$items = [System.Collections.Generic.List[string]]::new($includedReasonCount + 1)
	for ($index = 0; $index -lt $includedReasonCount; $index++) {
		$items.Add($formattedReasons[$index])
	}
	$items.Add("$ReasonPrefix... (+$omittedReasonCount more reasons)")

	$cappedText = "$Prefix$($items -join $Separator)$Suffix"
	if ($utf8Encoding.GetByteCount($cappedText) -gt $MaximumUtf8Bytes) {
		throw "Unable to fit the CI reason omission marker within $MaximumUtf8Bytes UTF-8 bytes."
	}

	return $cappedText
}

$effectiveBaseSha = $BaseSha
$effectiveHeadSha = $HeadSha
$useMergeBase = [bool]$MergeBase
$previousGateVerified = $false

function Test-PreviousGateSucceeded {
	param(
		[Parameter(Mandatory)][string] $Repository,
		[Parameter(Mandatory)][string] $CommitSha,
		[Parameter(Mandatory)][string] $GateName,
		[Parameter(Mandatory)][string] $Token
	)

	if ($Repository -notmatch '^[^/]+/[^/]+$' -or
		$CommitSha -notmatch '^[0-9a-fA-F]{40}$' -or
		[string]::IsNullOrWhiteSpace($Token)) {
		return $false
	}

	try {
		$headers = @{
			Accept = 'application/vnd.github+json'
			Authorization = "Bearer $Token"
			'X-GitHub-Api-Version' = '2022-11-28'
		}
		$uri = "https://api.github.com/repos/$Repository/commits/$CommitSha/check-runs?filter=latest&per_page=100"
		$response = Invoke-RestMethod -Method Get -Uri $uri -Headers $headers
		$successfulGate = @($response.check_runs | Where-Object {
			$_.name -eq $GateName -and
			$_.conclusion -eq 'success' -and
			$_.app.slug -eq 'github-actions'
		})
		return $successfulGate.Count -gt 0
	}
	catch {
		# API outages must spend CI time, never suppress validation.
		Write-Warning "Unable to verify the previous '$GateName' check; using the safe full plan."
		return $false
	}
}

if ($PSCmdlet.ParameterSetName -eq 'GitHubEvent') {
	$event = Get-Content -Raw -LiteralPath $GitHubEventPath | ConvertFrom-Json
	$comparison = Get-CiEventComparison -EventName $GitHubEventName -Event $event
	$Full = $comparison.Full
	$effectiveBaseSha = $comparison.BaseSha
	$effectiveHeadSha = $comparison.HeadSha
	$useMergeBase = $comparison.MergeBase

	# Incremental plans are valid only when the previous repository state already passed this gate.
	if (-not $Full -and -not $useMergeBase -and -not [string]::IsNullOrWhiteSpace($PreviousGateName)) {
		$previousGateVerified = Test-PreviousGateSucceeded `
			-Repository $GitHubRepository `
			-CommitSha $effectiveBaseSha `
			-GateName $PreviousGateName `
			-Token $GitHubToken
		if (-not $previousGateVerified) {
			$Full = $true
		}
	}
}

if (-not $Full -and -not [string]::IsNullOrWhiteSpace($effectiveBaseSha)) {
	$separator = if ($useMergeBase) { '...' } else { '..' }
	$range = "$effectiveBaseSha$separator$effectiveHeadSha"
	$ChangedPath = @(& git -c core.quotepath=false diff --name-only --no-renames $range --)
	if ($LASTEXITCODE -ne 0) {
		Write-Warning "Unable to calculate changed paths for '$range'; using the safe full plan."
		$Full = $true
		$ChangedPath = @()
	}
}

$plan = if ($Full) {
	Get-CiChangePlan -Full
}
else {
	Get-CiChangePlan -ChangedPath $ChangedPath
}

$safeSummary = ConvertTo-CappedReasonText `
	-Reason $plan.Reasons `
	-MaximumUtf8Bytes $outputSummaryMaximumBytes

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
	$markdownSummary = ConvertTo-CappedReasonText `
		-Reason $plan.Reasons `
		-MaximumUtf8Bytes $stepSummaryMaximumBytes `
		-Prefix "## Selected CI plan reasons`n" `
		-ReasonPrefix '- ' `
		-Separator "`n" `
		-Suffix "`n"
	Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $markdownSummary -Encoding utf8 -NoNewline
}

$output = [ordered]@{
	test_matrix = $plan.TestMatrix | ConvertTo-Json -Compress -Depth 5
	has_test_matrix = $plan.HasTestMatrix.ToString().ToLowerInvariant()
	run_terminal_command = $plan.TerminalCommand.ToString().ToLowerInvariant()
	run_ignore_scanner = $plan.IgnoreScanner.ToString().ToLowerInvariant()
	run_documentation = $plan.Documentation.ToString().ToLowerInvariant()
	run_release = $plan.Release.ToString().ToLowerInvariant()
	run_store = $plan.Store.ToString().ToLowerInvariant()
	full = $plan.Full.ToString().ToLowerInvariant()
	previous_gate_verified = $previousGateVerified.ToString().ToLowerInvariant()
	summary = $safeSummary
}

if (-not [string]::IsNullOrWhiteSpace($GitHubOutputPath)) {
	foreach ($entry in $output.GetEnumerator()) {
		"$($entry.Key)=$($entry.Value)" | Add-Content -LiteralPath $GitHubOutputPath -Encoding utf8
	}
}

if ($AsJson -or [string]::IsNullOrWhiteSpace($GitHubOutputPath)) {
	$output | ConvertTo-Json -Depth 6
}
