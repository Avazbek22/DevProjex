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

function Write-TestImageArchive {
    param([string] $Path, [string] $Tag, [string] $Architecture, [string] $Label = '', [switch] $BlobLayout)

    $config = [System.Text.Encoding]::UTF8.GetBytes(
        ('{"architecture":"' + $Architecture + '","os":"linux","config":{"Labels":{"test":"' + $Label +
        '"}},"rootfs":{"type":"layers","diff_ids":["sha256:' + ('a' * 64) + '"]}}'))
    $digest = [System.Convert]::ToHexStringLower([System.Security.Cryptography.SHA256]::HashData($config))
    $configName = if ($BlobLayout) { "blobs/sha256/$digest" } else { "$digest.json" }
    $manifest = [System.Text.Encoding]::UTF8.GetBytes(
        ('[{"Config":"' + $configName + '","RepoTags":["' + $Tag + '"],"Layers":[]}]'))
    $file = [System.IO.File]::Create($Path)
    $stream = $file
    $writer = $null
    try {
        if ($Path.EndsWith('.gz', [System.StringComparison]::Ordinal)) {
            $stream = [System.IO.Compression.GZipStream]::new($file, [System.IO.Compression.CompressionMode]::Compress, $true)
        }
        $writer = [System.Formats.Tar.TarWriter]::new($stream, $true)
        foreach ($item in @(@($configName, $config), @('manifest.json', $manifest))) {
            $entry = [System.Formats.Tar.PaxTarEntry]::new([System.Formats.Tar.TarEntryType]::RegularFile, $item[0])
            $entry.DataStream = [System.IO.MemoryStream]::new([byte[]]$item[1], $false)
            try { $writer.WriteEntry($entry) }
            finally { $entry.DataStream.Dispose() }
        }
    }
    finally {
        if ($null -ne $writer) { $writer.Dispose() }
        if ($stream -ne $file) { $stream.Dispose() }
        $file.Dispose()
    }
    return "sha256:$digest"
}

function Assert-Rejected {
    param([scriptblock] $Action, [string] $ExpectedMessage)

    try { & $Action }
    catch {
        if (-not $_.Exception.Message.Contains($ExpectedMessage, [System.StringComparison]::Ordinal)) { throw }
        return
    }
    throw "Expected rejection containing '$ExpectedMessage'."
}

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('devprojex-container-receipt-' + [guid]::NewGuid().ToString('N'))
try {
    [System.IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    $images = foreach ($platform in @('linux/amd64', 'linux/arm64')) {
        $suffix = $platform.Split('/')[1]
        $archive = "devprojex-$suffix.tar.gz"
        $configDigest = Write-TestImageArchive (Join-Path $temporaryRoot $archive) "devprojex-ci:$suffix" $suffix -BlobLayout
        [ordered]@{
            platform = $platform
            localTag = "devprojex-ci:$suffix"
            archive = $archive
            configDigest = $configDigest
            sha256 = (Get-FileHash (Join-Path $temporaryRoot $archive) -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    $receipt = [ordered]@{
        schemaVersion = 2
        sourceSha = '0123456789abcdef0123456789abcdef01234567'
        version = '5.2.0'
        images = @($images)
    }
    $receiptPath = Join-Path $temporaryRoot 'container-images.json'
    [System.IO.File]::WriteAllText(
        $receiptPath,
        ($receipt | ConvertTo-Json -Depth 5),
        [System.Text.UTF8Encoding]::new($false))
    & (Join-Path (Split-Path -Parent $PSScriptRoot) 'Test-ContainerImageArtifacts.ps1') `
        -ArtifactsRoot $temporaryRoot -Version '5.2.0' -SourceSha $receipt.sourceSha

    $mockSavedImage = Join-Path $temporaryRoot 'loaded.tar'
    $dockerState = @{
        SavedImage = $mockSavedImage
        ExitCode = 0
        SavedTags = [System.Collections.Generic.List[string]]::new()
    }
    Set-Item Function:docker -Value {
        if ($args.Count -ne 5 -or $args[0] -cne 'image' -or $args[1] -cne 'save' -or $args[2] -cne '--output') {
            throw "Unexpected Docker invocation: $args"
        }
        $dockerState.SavedTags.Add([string]$args[4])
        if ($dockerState.ExitCode -eq 0) {
            Copy-Item -LiteralPath $dockerState.SavedImage -Destination $args[3] -Force
        }
        $global:LASTEXITCODE = $dockerState.ExitCode
    }.GetNewClosure()
    foreach ($image in $images) {
        $suffix = $image.platform.Split('/')[1]
        $loadedDigest = Write-TestImageArchive $mockSavedImage $image.localTag $suffix
        if ($loadedDigest -cne $image.configDigest) { throw 'Fixture changed the config across archive layouts.' }
        & (Join-Path (Split-Path -Parent $PSScriptRoot) 'Test-ContainerImageArtifacts.ps1') `
            -ArtifactsRoot $temporaryRoot -Version '5.2.0' -SourceSha $receipt.sourceSha `
            -VerifyLoadedImages -LoadedPlatform $image.platform
    }
    if (($dockerState.SavedTags -join ',') -cne 'devprojex-ci:amd64,devprojex-ci:arm64') {
        throw 'LoadedPlatform did not restrict validation to the requested image.'
    }
    Write-Host 'Both loaded platforms passed with identical config bytes in different archive layouts.'

    $changedDigest = Write-TestImageArchive $mockSavedImage 'devprojex-ci:arm64' 'arm64' -Label 'substituted'
    if ($changedDigest -ceq $images[1].configDigest) { throw 'The substituted config fixture did not change identity.' }
    Assert-Rejected {
        & (Join-Path (Split-Path -Parent $PSScriptRoot) 'Test-ContainerImageArtifacts.ps1') `
            -ArtifactsRoot $temporaryRoot -Version '5.2.0' -SourceSha $receipt.sourceSha `
            -VerifyLoadedImages -LoadedPlatform linux/arm64
    } 'tested config digest'
    Write-Host 'Rejected a substituted loaded image with unchanged rootfs and different config.'

    $dockerState.ExitCode = 1
    Assert-Rejected { Assert-ContainerLoadedImage 'devprojex-ci:arm64' $images[1].configDigest } 'Could not save loaded'
    $dockerState.ExitCode = 0

    $originalDigest = $images[1].configDigest
    $images[1].configDigest = 'sha256:' + ('0' * 64)
    [System.IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Depth 5))
    Assert-Rejected {
        & (Join-Path (Split-Path -Parent $PSScriptRoot) 'Test-ContainerImageArtifacts.ps1') `
            -ArtifactsRoot $temporaryRoot -Version '5.2.0' -SourceSha $receipt.sourceSha
    } 'config digest receipt'
    $images[1].configDigest = $originalDigest
    [System.IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json -Depth 5))

    Assert-Rejected { Get-ContainerArchiveConfigDigest $mockSavedImage 'foreign:tag' } 'exactly the requested image'
    Assert-Rejected { Read-ContainerArchiveEntry $mockSavedImage 'missing.json' 1024 } 'is missing'
    Assert-Rejected { Read-ContainerArchiveEntry $mockSavedImage 'manifest.json' 1 } 'is duplicated or invalid'

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
