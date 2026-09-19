[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $ArtifactsRoot,
    [Parameter(Mandatory = $true)] [string] $ImageName,
    [Parameter(Mandatory = $true)] [string] $Version,
    [Parameter(Mandatory = $true)] [string] $SourceSha,
    [Parameter(Mandatory = $true)] [string] $RegistryOwner,
    [Parameter(Mandatory = $true)] [string] $GitHubOutputPath
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'ContainerPublishing.ps1')

$root = [System.IO.Path]::GetFullPath($ArtifactsRoot)
& (Join-Path $PSScriptRoot 'Test-ContainerImageArtifacts.ps1') `
    -ArtifactsRoot $root -Version $Version -SourceSha $SourceSha

$receipt = Get-Content -LiteralPath (Join-Path $root 'container-images.json') -Raw | ConvertFrom-Json
$platformDigests = [ordered]@{}
foreach ($image in @($receipt.images | Sort-Object platform)) {
    $archivePath = Join-Path $root ([string]$image.archive)
    & docker load -i $archivePath
    if ($LASTEXITCODE -ne 0) { throw "Could not load tested image archive '$($image.archive)'." }
    $actualId = (& docker image inspect --format '{{.Id}}' ([string]$image.localTag)).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualId -cne [string]$image.imageId) {
        throw "Loaded image '$($image.localTag)' differs from the tested image ID."
    }
    $suffix = if ([string]$image.platform -ceq 'linux/amd64') { 'amd64' } else { 'arm64' }
    $publishedTag = "$ImageName`:$Version-$suffix"
    & docker tag ([string]$image.localTag) $publishedTag
    if ($LASTEXITCODE -ne 0) { throw "Could not tag tested image '$($image.localTag)'." }
    $pushOutput = @(& docker push $publishedTag 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "Could not push tested image '$publishedTag': $($pushOutput -join [Environment]::NewLine)" }
    $digestLine = @($pushOutput | Where-Object { $_ -match 'digest:\s*(sha256:[0-9a-f]{64})' } | Select-Object -Last 1)
    if ($digestLine.Count -ne 1 -or $digestLine[0] -notmatch 'digest:\s*(sha256:[0-9a-f]{64})') {
        throw "Registry did not report a digest for '$publishedTag'."
    }
    $platformDigests[$suffix] = $Matches[1]
    Write-Host "Pushed tested $($image.platform) image as $publishedTag@$($Matches[1])."
}

& docker buildx imagetools create `
    --tag "$ImageName`:$Version" `
    "$ImageName@$($platformDigests.amd64)" `
    "$ImageName@$($platformDigests.arm64)"
if ($LASTEXITCODE -ne 0) { throw 'Could not publish the tested multi-architecture manifest.' }
$inspect = @(& docker buildx imagetools inspect "$ImageName`:$Version" 2>&1)
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the published multi-architecture manifest.' }
$digestLine = @($inspect | Where-Object { $_ -match '^Digest:\s*(sha256:[0-9a-f]{64})\s*$' } | Select-Object -First 1)
if ($digestLine.Count -ne 1 -or $digestLine[0] -notmatch '^Digest:\s*(sha256:[0-9a-f]{64})\s*$') {
    throw 'Published multi-architecture manifest did not report one digest.'
}
$manifestDigest = $Matches[1]

$publishedVersionTags = @()
$tagOutput = @()
$foundPackage = $false
foreach ($ownerKind in @('orgs', 'users')) {
    $packagePath = "/$ownerKind/$RegistryOwner/packages/container/devprojex/versions?per_page=100"
    $candidateOutput = @(& gh api --paginate --method GET $packagePath --jq '.[].metadata.container.tags[]' 2>&1)
    if ($LASTEXITCODE -eq 0) {
        $tagOutput = $candidateOutput
        $foundPackage = $true
        break
    }
    if (-not (($candidateOutput -join "`n").Contains('HTTP 404', [System.StringComparison]::OrdinalIgnoreCase))) {
        throw "Could not enumerate existing GHCR tags: $($candidateOutput -join [Environment]::NewLine)"
    }
}
if ($foundPackage) {
    $publishedVersionTags = @($tagOutput)
}

$maximum = Get-MaximumContainerVersion $publishedVersionTags
$promoteLatest = Test-ContainerLatestPromotion $Version $publishedVersionTags
if ($promoteLatest) {
    & docker buildx imagetools create --tag "$ImageName`:latest" "$ImageName@$manifestDigest"
    if ($LASTEXITCODE -ne 0) { throw 'Could not promote the tested manifest to latest.' }
    Write-Host "Promoted latest to $Version because no greater published version exists."
}
else {
    Write-Host "Kept latest unchanged because published version $maximum is greater than $Version."
}

"manifest_digest=$manifestDigest" >> $GitHubOutputPath
"latest_promoted=$($promoteLatest.ToString().ToLowerInvariant())" >> $GitHubOutputPath
