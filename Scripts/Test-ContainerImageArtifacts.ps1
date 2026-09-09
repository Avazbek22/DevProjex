[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $ArtifactsRoot,
    [Parameter(Mandatory = $true)] [string] $Version,
    [Parameter(Mandatory = $true)] [string] $SourceSha,
    [switch] $VerifyLoadedImages,
    [ValidateSet('linux/amd64', 'linux/arm64')] [string] $LoadedPlatform = ''
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

$root = [System.IO.Path]::GetFullPath($ArtifactsRoot)
$receiptPath = Join-Path $root 'container-images.json'
if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) { throw 'Container image artifact receipt is missing.' }
$receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
if ($receipt.schemaVersion -ne 1 -or [string]$receipt.version -cne $Version -or
    [string]$receipt.sourceSha -cne $SourceSha) {
    throw 'Container image artifact receipt identity does not match the requested source and version.'
}
$images = @($receipt.images)
$platformDifference = @(Compare-Object @('linux/amd64', 'linux/arm64') @($images.platform | Sort-Object))
if ($images.Count -ne 2 -or $platformDifference.Count -ne 0) {
    throw 'Container image artifact receipt must name exactly linux/amd64 and linux/arm64.'
}
foreach ($image in $images) {
    $archivePath = Join-Path $root ([string]$image.archive)
    if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
        throw "Container image archive '$($image.archive)' is missing."
    }
    $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actualHash -cne [string]$image.sha256) {
        throw "Container image archive '$($image.archive)' does not match its SHA-256 receipt."
    }
    if ($VerifyLoadedImages -and ([string]::IsNullOrEmpty($LoadedPlatform) -or
        [string]$image.platform -ceq $LoadedPlatform)) {
        $actualId = (& docker image inspect --format '{{.Id}}' ([string]$image.localTag)).Trim()
        if ($LASTEXITCODE -ne 0 -or $actualId -cne [string]$image.imageId) {
            throw "Loaded container image '$($image.localTag)' does not match its tested image ID."
        }
    }
}
Write-Host "Container promotion artifact receipt passed for source $SourceSha and version $Version."
