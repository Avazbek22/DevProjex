[CmdletBinding()]
param(
    [string]$PublishedExe,
    [string]$PartnerCenterCsv,
    [string]$CaptureHost,
    [string]$ProjectPath,
    [string]$OutputRoot,
    [switch]$SkipPublish,
    [switch]$SkipTuiBuild,
    [switch]$KeepSessionData,
    [switch]$PlanOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$resolvedOutputRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $repositoryRoot "Packaging\Windows\StoreListing\ImportFolder"
} else {
    [System.IO.Path]::GetFullPath($OutputRoot)
}

$guiParameters = @{}
foreach ($entry in @{
        PublishedExe = $PublishedExe
        PartnerCenterCsv = $PartnerCenterCsv
        ProjectPath = $ProjectPath
        OutputRoot = $resolvedOutputRoot
    }.GetEnumerator()) {
    if (-not [string]::IsNullOrWhiteSpace([string]$entry.Value)) {
        $guiParameters[$entry.Key] = $entry.Value
    }
}
if ($SkipPublish) { $guiParameters.SkipPublish = $true }
if ($KeepSessionData) { $guiParameters.KeepSessionData = $true }
if ($PlanOnly) { $guiParameters.PlanOnly = $true }

& (Join-Path $PSScriptRoot "generate-store-screenshots.ps1") @guiParameters
if (-not $?) {
    throw "GUI Store screenshot generation failed."
}

$tuiParameters = @{
    OutputRoot = $resolvedOutputRoot
    ListingCsv = Join-Path $resolvedOutputRoot "listingData.csv"
}
foreach ($entry in @{
        CaptureHost = $CaptureHost
        ProjectPath = $ProjectPath
    }.GetEnumerator()) {
    if (-not [string]::IsNullOrWhiteSpace([string]$entry.Value)) {
        $tuiParameters[$entry.Key] = $entry.Value
    }
}
if ($SkipTuiBuild) { $tuiParameters.SkipBuild = $true }
if ($KeepSessionData) { $tuiParameters.KeepSessionData = $true }
if ($PlanOnly) { $tuiParameters.PlanOnly = $true }

& (Join-Path $PSScriptRoot "generate-store-tui-screenshots.ps1") @tuiParameters
if (-not $?) {
    throw "TUI Store screenshot generation failed."
}
