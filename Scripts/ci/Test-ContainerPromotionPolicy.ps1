[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path (Split-Path -Parent $PSScriptRoot) 'ContainerPublishing.ps1')

if (-not (Test-ContainerLatestPromotion '5.2.0' @())) {
    throw 'The first published container version must become latest.'
}
if (-not (Test-ContainerLatestPromotion '5.2.0' @('latest', '5.1.9'))) {
    throw 'A newer container version must become latest.'
}
if (-not (Test-ContainerLatestPromotion '5.2.0' @('5.2.0', 'latest'))) {
    throw 'A resumed publication of the current maximum must keep latest.'
}
if (Test-ContainerLatestPromotion '5.1.9' @('5.2.0', 'latest')) {
    throw 'An older container version must not move latest.'
}
if (Test-ContainerLatestPromotion '5.3.0-preview.1' @('5.2.0', 'latest')) {
    throw 'A prerelease container version must not move the stable latest tag.'
}
if (-not (Test-ContainerLatestPromotion '5.2.0' @('not-a-version', '5.1.0'))) {
    throw 'Non-version tags must not affect latest selection.'
}

Write-Host 'Container latest promotion policy passed.'

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('devprojex-container-receipt-' + [guid]::NewGuid().ToString('N'))
try {
    [System.IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    $images = foreach ($platform in @('linux/amd64', 'linux/arm64')) {
        $suffix = $platform.Split('/')[1]
        $archive = "devprojex-$suffix.tar.gz"
        [System.IO.File]::WriteAllText((Join-Path $temporaryRoot $archive), "tested-$platform")
        [ordered]@{
            platform = $platform
            localTag = "devprojex-ci:$suffix"
            archive = $archive
            imageId = "sha256:$suffix"
            sha256 = (Get-FileHash (Join-Path $temporaryRoot $archive) -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    $receipt = [ordered]@{
        schemaVersion = 1
        sourceSha = '0123456789abcdef0123456789abcdef01234567'
        version = '5.2.0'
        images = @($images)
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $temporaryRoot 'container-images.json'),
        ($receipt | ConvertTo-Json -Depth 5),
        [System.Text.UTF8Encoding]::new($false))
    & (Join-Path (Split-Path -Parent $PSScriptRoot) 'Test-ContainerImageArtifacts.ps1') `
        -ArtifactsRoot $temporaryRoot -Version '5.2.0' -SourceSha $receipt.sourceSha

    [System.IO.File]::AppendAllText((Join-Path $temporaryRoot 'devprojex-amd64.tar.gz'), 'mutation')
    $rejected = $false
    try {
        & (Join-Path (Split-Path -Parent $PSScriptRoot) 'Test-ContainerImageArtifacts.ps1') `
            -ArtifactsRoot $temporaryRoot -Version '5.2.0' -SourceSha $receipt.sourceSha
    }
    catch {
        if (-not $_.Exception.Message.Contains('SHA-256 receipt', [System.StringComparison]::Ordinal)) { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'A mutated tested-image archive was not rejected.' }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot -PathType Container) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

Write-Host 'Container image artifact receipt mutation gate passed.'
