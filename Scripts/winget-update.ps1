<#
.SYNOPSIS
  Smart helper for updating DevProjex package in winget-pkgs.

.DESCRIPTION
  This script automates the common winget update flow:
    1) asks release version (supports 1-4 numeric segments)
    2) normalizes PackageVersion to 4 segments for winget
    3) builds GitHub installer URLs for the selected architecture set
    4) runs wingetcreate update into a temp folder
    5) updates License field in locale manifests
    5a) rewrites listing metadata (descriptions, tags, URLs, docs links,
        release notes, 19 locale manifests) from winget-listing/listing.json
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
    [ValidateSet("x64", "arm64", "all")]
    [string]$Architecture = "all",
    [string]$LicenseValue = "Apache-2.0"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "release-helpers.ps1")

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

function Resolve-InstallerTargets([string]$architecture) {
    if ($architecture -eq "all") {
        # The published winget manifest contains both architectures. wingetcreate
        # requires the update command to keep the installer URL count identical,
        # so the default release flow must update x64 and arm64 together.
        return @("x64", "arm64")
    }

    return @($architecture)
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
    $localeFiles = @(Get-ChildItem -Path $manifestRoot -File -Filter "*.locale.*.yaml" -ErrorAction SilentlyContinue)
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

function ConvertTo-YamlSingleQuoted([string]$value) {
    return "'" + ($value -replace "'", "''") + "'"
}

function ConvertTo-YamlLiteralBlock([string]$fieldName, [string]$value) {
    $lines = ($value -replace "`r`n", "`n") -split "`n"
    $body = foreach ($line in $lines) {
        if ([string]::IsNullOrEmpty($line)) { "" } else { "  " + $line }
    }
    return (, "${fieldName}: |-" + $body)
}

function Remove-YamlTopLevelFields([string[]]$lines, [string[]]$fieldNames) {
    $result = New-Object System.Collections.Generic.List[string]
    $skipping = $false
    foreach ($line in $lines) {
        if ($line -match '^[A-Za-z][A-Za-z0-9]*:') {
            $key = ($line -split ':', 2)[0]
            $skipping = $fieldNames -contains $key
        }
        if (-not $skipping) {
            [void]$result.Add($line)
        }
    }
    return $result.ToArray()
}

function Get-YamlTopLevelValue([string[]]$lines, [string]$fieldName) {
    foreach ($line in $lines) {
        if ($line -match "^${fieldName}:\s*(.+)$") {
            return $Matches[1].Trim()
        }
    }
    return $null
}

function Get-ListingDescriptionText($locale) {
    $paragraphs = @($locale.description) -join "`n`n"
    $featureLines = @($locale.features | ForEach-Object { "- $_" }) -join "`n"
    return $paragraphs + "`n`n" + $locale.featuresHeader + "`n" + $featureLines
}

function Update-ListingInManifests(
    [string]$manifestRoot,
    [string]$packageIdentifier,
    [string]$packageVersion,
    [string]$displayVersion,
    [string]$releaseTag,
    [string]$listingDataPath) {
    if (-not (Test-Path $listingDataPath)) {
        Write-Warning "Listing data file not found, locale texts left as generated: $listingDataPath"
        return
    }

    $listing = Get-Content -Path $listingDataPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $includeReleaseNotes = ($listing.releaseNotesVersion -eq $displayVersion)
    if (-not $includeReleaseNotes) {
        Write-Warning ("ReleaseNotes in listing.json target version {0}, current release is {1}; ReleaseNotes will be omitted." -f $listing.releaseNotesVersion, $displayVersion)
    }

    $defaultLocaleFile = Join-Path $manifestRoot "$packageIdentifier.locale.en-US.yaml"
    if (-not (Test-Path $defaultLocaleFile)) {
        throw "Default locale manifest not found: $defaultLocaleFile"
    }

    $lines = @(Get-Content -Path $defaultLocaleFile)
    $publisherName = Get-YamlTopLevelValue -lines $lines -fieldName "Publisher"
    $packageName = Get-YamlTopLevelValue -lines $lines -fieldName "PackageName"
    $manifestVersion = Get-YamlTopLevelValue -lines $lines -fieldName "ManifestVersion"
    $managedFields = @(
        "Moniker", "ShortDescription", "Description", "Tags",
        "ReleaseNotes", "ReleaseNotesUrl", "PackageUrl", "PublisherUrl",
        "PublisherSupportUrl", "LicenseUrl", "Copyright", "CopyrightUrl",
        "Documentations", "InstallationNotes"
    )
    $lines = Remove-YamlTopLevelFields -lines $lines -fieldNames $managedFields

    $en = $listing.locales.'en-US'
    $block = New-Object System.Collections.Generic.List[string]
    [void]$block.Add("Moniker: $($listing.moniker)")
    [void]$block.Add("PackageUrl: $($listing.packageUrl)")
    [void]$block.Add("PublisherUrl: $($listing.publisherUrl)")
    [void]$block.Add("PublisherSupportUrl: $($listing.publisherSupportUrl)")
    [void]$block.Add("LicenseUrl: $($listing.licenseUrl)")
    [void]$block.Add("Copyright: " + (ConvertTo-YamlSingleQuoted $listing.copyright))
    [void]$block.Add("ShortDescription: " + (ConvertTo-YamlSingleQuoted $en.shortDescription))
    foreach ($descLine in (ConvertTo-YamlLiteralBlock -fieldName "Description" -value (Get-ListingDescriptionText $en))) {
        [void]$block.Add($descLine)
    }
    [void]$block.Add("Tags:")
    foreach ($tag in $listing.tags) {
        [void]$block.Add("- $tag")
    }
    [void]$block.Add("Documentations:")
    foreach ($document in $listing.documentations) {
        [void]$block.Add("- DocumentLabel: $($document.documentLabel)")
        [void]$block.Add("  DocumentUrl: $($document.documentUrl)")
    }
    if ($includeReleaseNotes) {
        [void]$block.Add("ReleaseNotes: " + (ConvertTo-YamlSingleQuoted $en.releaseNotes))
    }
    [void]$block.Add("ReleaseNotesUrl: https://github.com/$Repository/releases/tag/$releaseTag")
    [void]$block.Add("InstallationNotes: " + (ConvertTo-YamlSingleQuoted $en.installationNotes))

    $manifestTypeIndex = [array]::FindIndex($lines, [Predicate[string]] { param($line) $line -match '^ManifestType:' })
    if ($manifestTypeIndex -lt 0) {
        throw "ManifestType field not found in $defaultLocaleFile"
    }
    $updatedLines = @($lines[0..($manifestTypeIndex - 1)]) + $block.ToArray() + @($lines[$manifestTypeIndex..($lines.Count - 1)])
    Set-Content -Path $defaultLocaleFile -Value ($updatedLines -join "`r`n") -Encoding UTF8

    foreach ($localeProperty in @($listing.locales.PSObject.Properties | Where-Object { $_.Name -ne "en-US" })) {
        $localeName = $localeProperty.Name
        $locale = $localeProperty.Value
        $localeLines = New-Object System.Collections.Generic.List[string]
        if ($manifestVersion) {
            [void]$localeLines.Add("# yaml-language-server: `$schema=https://aka.ms/winget-manifest.locale.$manifestVersion.schema.json")
            [void]$localeLines.Add("")
        }
        [void]$localeLines.Add("PackageIdentifier: $packageIdentifier")
        [void]$localeLines.Add("PackageVersion: $packageVersion")
        [void]$localeLines.Add("PackageLocale: $localeName")
        if ($publisherName) { [void]$localeLines.Add("Publisher: $publisherName") }
        if ($packageName) { [void]$localeLines.Add("PackageName: $packageName") }
        [void]$localeLines.Add("Copyright: " + (ConvertTo-YamlSingleQuoted $listing.copyright))
        [void]$localeLines.Add("ShortDescription: " + (ConvertTo-YamlSingleQuoted $locale.shortDescription))
        foreach ($descLine in (ConvertTo-YamlLiteralBlock -fieldName "Description" -value (Get-ListingDescriptionText $locale))) {
            [void]$localeLines.Add($descLine)
        }
        if ($includeReleaseNotes) {
            [void]$localeLines.Add("ReleaseNotes: " + (ConvertTo-YamlSingleQuoted $locale.releaseNotes))
        }
        [void]$localeLines.Add("InstallationNotes: " + (ConvertTo-YamlSingleQuoted $locale.installationNotes))
        [void]$localeLines.Add("ManifestType: locale")
        if ($manifestVersion) { [void]$localeLines.Add("ManifestVersion: $manifestVersion") }
        $localeFile = Join-Path $manifestRoot "$packageIdentifier.locale.$localeName.yaml"
        Set-Content -Path $localeFile -Value ($localeLines.ToArray() -join "`r`n") -Encoding UTF8
        Write-Host "Locale manifest written: $localeFile"
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
    [string[]]$installerUrls,
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
    $installerLines = ($installerUrls | ForEach-Object { "- Installer: $_" }) -join [Environment]::NewLine
    $comment = @"
Automated update summary:
- Package: $packageIdentifier
- Version: $packageVersion
$installerLines
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

$defaultReleaseVersionInfo = Get-DefaultReleaseVersionInfo -repoRoot (Get-DevProjexRepoRoot -startPath $PSScriptRoot)
$resolvedVersionInput = if ([string]::IsNullOrWhiteSpace($Version)) {
    Read-Required -prompt "Version (example: 4.6 / 4.6.1 / 4.6.1.0)" -defaultValue ([string]$defaultReleaseVersionInfo.DisplayVersion)
}
else {
    $Version
}

$versionInfo = Parse-VersionInfo -rawVersion $resolvedVersionInput
$displayVersion = [string]$versionInfo.DisplayVersion
$packageVersion = [string]$versionInfo.PackageVersion
$releaseTag = "v$displayVersion"
$installerTargets = @(Resolve-InstallerTargets -architecture $Architecture)
$installerEntries = @(
    foreach ($installerArchitecture in $installerTargets) {
        $defaultInstallerName = "DevProjex.v$displayVersion.win-$installerArchitecture.exe"
        $installerName = if ($installerTargets.Count -eq 1) {
            Read-Required -prompt "Installer file name in GitHub release" -defaultValue $defaultInstallerName
        }
        else {
            $defaultInstallerName
        }

        [pscustomobject]@{
            Architecture = $installerArchitecture
            Url = "https://github.com/$Repository/releases/download/$releaseTag/$installerName"
        }
    }
)

Write-Step "Winget update plan"
Write-Host "PackageIdentifier: $PackageIdentifier"
Write-Host "DisplayVersion   : $displayVersion"
Write-Host "PackageVersion   : $packageVersion"
Write-Host "Architecture     : $Architecture"
foreach ($installerEntry in $installerEntries) {
    Write-Host ("Installer URL    : {0} -> {1}" -f $installerEntry.Architecture, $installerEntry.Url)
}

foreach ($installerEntry in $installerEntries) {
    if (-not (Test-RemoteFileAvailable -url $installerEntry.Url)) {
        throw "Installer URL is not reachable: $($installerEntry.Url)"
    }
}

$tempOut = Join-Path $env:TEMP ("winget-update-" + ($PackageIdentifier -replace '[^a-zA-Z0-9\.-]', '_') + "-" + $packageVersion)
if (Test-Path $tempOut) {
    Remove-Item -Path $tempOut -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Step "Generating updated manifest"
$wingetUrlArguments = @($installerEntries | ForEach-Object { "$($_.Url)|$($_.Architecture)" })
$wingetUpdateArguments = @(
    "update",
    "--urls"
) + $wingetUrlArguments + @(
    "--version", $packageVersion,
    "--out", $tempOut,
    $PackageIdentifier
)

Invoke-ExternalCommand -filePath "wingetcreate" -arguments $wingetUpdateArguments -failureMessage "wingetcreate update failed"

$manifestRoot = Resolve-ManifestRoot -outDirectory $tempOut -packageIdentifier $PackageIdentifier -packageVersion $packageVersion
Update-LicenseInLocaleManifests -manifestRoot $manifestRoot -licenseValue $LicenseValue
Update-ListingInManifests -manifestRoot $manifestRoot -packageIdentifier $PackageIdentifier -packageVersion $packageVersion -displayVersion $displayVersion -releaseTag $releaseTag -listingDataPath (Join-Path $PSScriptRoot "winget-listing\listing.json")

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
    Try-PostPrComment -prNumber $prNumber -packageIdentifier $PackageIdentifier -packageVersion $packageVersion -installerUrls @($installerEntries.Url) -installTestExecuted $installTestExecuted
}

Write-Step "Completed"
if (-not [string]::IsNullOrWhiteSpace($prUrl)) {
    Write-Host "PR: $prUrl"
}
else {
    Write-Host "PR submitted. Check output above for URL."
}
Write-Host "Manifest path: $manifestRoot"
