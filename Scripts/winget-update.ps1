<#
.SYNOPSIS
  Smart helper for updating DevProjex package in winget-pkgs.

.DESCRIPTION
  This script automates the common winget update flow:
    1) asks release version (supports 1-4 numeric segments)
    2) normalizes PackageVersion to 4 segments for winget
    3) builds GitHub installer URL for the selected architecture
    4) runs wingetcreate update into a temp folder
    5) updates License field in locale manifests
    6) validates manifest locally
    7) optionally tests local install from manifest
    8) optionally submits PR
    9) if possible, updates PR checklist and posts a reviewer-friendly comment

  The script is designed to be safe:
    - clear validation errors
    - non-destructive temp workspace
    - "best effort" PR post-processing (does not fail submission if GitHub CLI is unavailable)
#>
[CmdletBinding()]
param(
    [string]$Version = "",
    [string]$PackageIdentifier = "OlimoffDev.DevProjex",
    [string]$Repository = "Avazbek22/DevProjex",
    [ValidateSet("x64", "arm64")]
    [string]$Architecture = "x64",
    [string]$LicenseValue = "GPL-3.0-only"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Write-Step([string]$message) {
    Write-Host ""
    Write-Host "=== $message ===" -ForegroundColor Cyan
}

function Ensure-Command([string]$name) {
    if ($null -eq (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "Required command not found: $name"
    }
}

function Read-Optional([string]$prompt, [string]$defaultValue = "") {
    $suffix = if ([string]::IsNullOrWhiteSpace($defaultValue)) { "" } else { " [$defaultValue]" }
    $inputValue = Read-Host ($prompt + $suffix)
    if ([string]::IsNullOrWhiteSpace($inputValue)) {
        return $defaultValue
    }

    return $inputValue.Trim()
}

function Read-Required([string]$prompt, [string]$defaultValue = "") {
    while ($true) {
        $value = Read-Optional -prompt $prompt -defaultValue $defaultValue
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return $value
        }

        Write-Host "Value is required." -ForegroundColor Yellow
    }
}

function Invoke-ExternalCommand(
    [string]$filePath,
    [string[]]$arguments,
    [string]$failureMessage
) {
    & $filePath @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$failureMessage (exit code: $LASTEXITCODE)"
    }
}

function Parse-VersionInfo([string]$rawVersion) {
    if ([string]::IsNullOrWhiteSpace($rawVersion)) {
        throw "Version is empty."
    }

    $trimmed = $rawVersion.Trim()
    if ($trimmed -notmatch '^\d+(\.\d+){0,3}$') {
        throw "Invalid version format '$trimmed'. Use 1-4 numeric segments (example: 4.6, 4.6.1, 4.6.1.0)."
    }

    $parts = @($trimmed.Split('.'))
    $storeParts = New-Object System.Collections.Generic.List[string]
    foreach ($part in $parts) {
        $storeParts.Add($part)
    }

    while ($storeParts.Count -lt 4) {
        $storeParts.Add("0")
    }

    return @{
        DisplayVersion = ($parts -join '.')
        PackageVersion = ($storeParts -join '.')
    }
}

function Test-RemoteFileAvailable([string]$url) {
    try {
        Invoke-WebRequest -Uri $url -Method Head -UseBasicParsing | Out-Null
        return $true
    }
    catch {
        return $false
    }
}

function Resolve-ManifestRoot([string]$outDirectory, [string]$packageIdentifier, [string]$packageVersion) {
    $segments = $packageIdentifier.Split('.')
    if ($segments.Length -lt 2) {
        throw "Unexpected PackageIdentifier format: $packageIdentifier"
    }

    $publisher = $segments[0]
    $packageName = $segments[1]
    $expected = Join-Path $outDirectory ("manifests\" + $publisher.Substring(0, 1).ToLowerInvariant() + "\" + $publisher + "\" + $packageName + "\" + $packageVersion)
    if (Test-Path $expected) {
        return $expected
    }

    $fallback = Get-ChildItem -Path $outDirectory -Recurse -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq $packageVersion } |
        Select-Object -First 1

    if ($null -eq $fallback) {
        throw "Manifest directory was not found under: $outDirectory"
    }

    return $fallback.FullName
}

function Update-LicenseInLocaleManifests([string]$manifestRoot, [string]$licenseValue) {
    $localeFiles = Get-ChildItem -Path $manifestRoot -File -Filter "*.locale.*.yaml" -ErrorAction SilentlyContinue
    if ($null -eq $localeFiles -or $localeFiles.Count -eq 0) {
        return
    }

    foreach ($localeFile in $localeFiles) {
        $content = Get-Content -Path $localeFile.FullName -Raw
        if ($content -match "(?m)^License:\s*.+$") {
            $updated = [regex]::Replace($content, "(?m)^License:\s*.+$", "License: $licenseValue")
            Set-Content -Path $localeFile.FullName -Value $updated -Encoding UTF8
        }
    }
}

function Try-ExtractPrUrl([string]$text) {
    if ([string]::IsNullOrWhiteSpace($text)) {
        return $null
    }

    $match = [regex]::Match($text, 'https://github\.com/microsoft/winget-pkgs/pull/\d+')
    if (-not $match.Success) {
        return $null
    }

    return $match.Value
}

function Try-GetPrNumberFromUrl([string]$prUrl) {
    if ([string]::IsNullOrWhiteSpace($prUrl)) {
        return $null
    }

    $match = [regex]::Match($prUrl, '/pull/(\d+)$')
    if (-not $match.Success) {
        return $null
    }

    return $match.Groups[1].Value
}

function Try-UpdatePrChecklist(
    [string]$prNumber,
    [bool]$installTestExecuted
) {
    if ([string]::IsNullOrWhiteSpace($prNumber)) {
        return
    }

    if ($null -eq (Get-Command gh -ErrorAction SilentlyContinue)) {
        Write-Warning "GitHub CLI (gh) not found. Skipping PR checklist update."
        return
    }

    try {
        $body = gh pr view $prNumber -R microsoft/winget-pkgs --json body -q .body
        if ([string]::IsNullOrWhiteSpace($body)) {
            Write-Warning "PR body is empty. Skipping checklist update."
            return
        }

        # Mark only items that script can assert.
        $updated = $body
        $updated = [regex]::Replace($updated, '- \[ \] This PR only modifies one \(1\) manifest', '- [x] This PR only modifies one (1) manifest')
        $updated = [regex]::Replace($updated, '- \[ \] Have you validated your manifest locally with winget validate --manifest <path>\?', '- [x] Have you validated your manifest locally with winget validate --manifest <path>?')
        $updated = [regex]::Replace($updated, '- \[ \] Does your manifest conform to the 1\.10 schema\?', '- [x] Does your manifest conform to the 1.10 schema?')

        if ($installTestExecuted) {
            $updated = [regex]::Replace($updated, '- \[ \] Have you tested your manifest locally with winget install --manifest <path>\?', '- [x] Have you tested your manifest locally with winget install --manifest <path>?')
        }

        if ($updated -ne $body) {
            gh pr edit $prNumber -R microsoft/winget-pkgs --body $updated | Out-Null
        }
    }
    catch {
        Write-Warning "Failed to update PR checklist automatically: $($_.Exception.Message)"
    }
}

function Try-PostPrComment(
    [string]$prNumber,
    [string]$packageIdentifier,
    [string]$packageVersion,
    [string]$installerUrl,
    [bool]$installTestExecuted
) {
    if ([string]::IsNullOrWhiteSpace($prNumber)) {
        return
    }

    if ($null -eq (Get-Command gh -ErrorAction SilentlyContinue)) {
        Write-Warning "GitHub CLI (gh) not found. Skipping PR comment."
        return
    }

    $testedLine = if ($installTestExecuted) { "- Local install test: PASS (`winget install --manifest`)"} else { "- Local install test: SKIPPED" }
    $comment = @"
Automated update summary:
- Package: $packageIdentifier
- Version: $packageVersion
- Installer: $installerUrl
- Local validation: PASS (`winget validate --manifest`)
$testedLine
- Manifest schema: 1.10
"@

    try {
        gh pr comment $prNumber -R microsoft/winget-pkgs --body $comment | Out-Null
    }
    catch {
        Write-Warning "Failed to post PR comment automatically: $($_.Exception.Message)"
    }
}

Ensure-Command -name "winget"
Ensure-Command -name "wingetcreate"

$resolvedVersionInput = if ([string]::IsNullOrWhiteSpace($Version)) {
    Read-Required -prompt "Version (example: 4.6 / 4.6.1 / 4.6.1.0)" -defaultValue "4.6"
}
else {
    $Version
}

$versionInfo = Parse-VersionInfo -rawVersion $resolvedVersionInput
$displayVersion = [string]$versionInfo.DisplayVersion
$packageVersion = [string]$versionInfo.PackageVersion
$releaseTag = "v$displayVersion"
$defaultInstallerName = "DevProjex.v$displayVersion.win-$Architecture.exe"
$installerName = Read-Required -prompt "Installer file name in GitHub release" -defaultValue $defaultInstallerName
$installerUrl = "https://github.com/$Repository/releases/download/$releaseTag/$installerName"

Write-Step "Winget update plan"
Write-Host "PackageIdentifier: $PackageIdentifier"
Write-Host "DisplayVersion   : $displayVersion"
Write-Host "PackageVersion   : $packageVersion"
Write-Host "Architecture     : $Architecture"
Write-Host "Installer URL    : $installerUrl"

if (-not (Test-RemoteFileAvailable -url $installerUrl)) {
    throw "Installer URL is not reachable: $installerUrl"
}

$tempOut = Join-Path $env:TEMP ("winget-update-" + ($PackageIdentifier -replace '[^a-zA-Z0-9\.-]', '_') + "-" + $packageVersion)
if (Test-Path $tempOut) {
    Remove-Item -Path $tempOut -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Step "Generating updated manifest"
Invoke-ExternalCommand -filePath "wingetcreate" -arguments @(
    "update",
    "--urls", "$installerUrl|$Architecture",
    "--version", $packageVersion,
    "--out", $tempOut,
    $PackageIdentifier
) -failureMessage "wingetcreate update failed"

$manifestRoot = Resolve-ManifestRoot -outDirectory $tempOut -packageIdentifier $PackageIdentifier -packageVersion $packageVersion
Update-LicenseInLocaleManifests -manifestRoot $manifestRoot -licenseValue $LicenseValue

Write-Step "Validating manifest"
Invoke-ExternalCommand -filePath "winget" -arguments @(
    "validate",
    "--manifest", $manifestRoot
) -failureMessage "winget validate failed"

$runInstallTest = Read-Optional -prompt "Run local install test (winget install --manifest)? y/N" -defaultValue "N"
$installTestExecuted = $false
if ($runInstallTest -match '^(y|yes|д|да)$') {
    Write-Step "Local install test"
    Invoke-ExternalCommand -filePath "winget" -arguments @(
        "install",
        "--manifest", $manifestRoot
    ) -failureMessage "winget install --manifest failed"
    $installTestExecuted = $true
}

$submit = Read-Optional -prompt "Submit PR to winget-pkgs now? Y/n" -defaultValue "Y"
if ($submit -match '^(n|no|н|нет)$') {
    Write-Step "Done (local only)"
    Write-Host "Manifest path: $manifestRoot"
    exit 0
}

$prTitle = "Update $PackageIdentifier to $packageVersion"
Write-Step "Submitting PR"
$submitOutput = wingetcreate submit --prtitle $prTitle $manifestRoot 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    throw "wingetcreate submit failed.`n$submitOutput"
}

$prUrl = Try-ExtractPrUrl -text $submitOutput
$prNumber = Try-GetPrNumberFromUrl -prUrl $prUrl

if (-not [string]::IsNullOrWhiteSpace($prNumber)) {
    Try-UpdatePrChecklist -prNumber $prNumber -installTestExecuted $installTestExecuted
    Try-PostPrComment -prNumber $prNumber -packageIdentifier $PackageIdentifier -packageVersion $packageVersion -installerUrl $installerUrl -installTestExecuted $installTestExecuted
}

Write-Step "Completed"
if (-not [string]::IsNullOrWhiteSpace($prUrl)) {
    Write-Host "PR: $prUrl"
}
else {
    Write-Host "PR submitted. Check output above for URL."
}
Write-Host "Manifest path: $manifestRoot"
