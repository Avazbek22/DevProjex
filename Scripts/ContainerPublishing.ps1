function Get-MaximumContainerVersion([string[]] $Tags) {
    $maximum = $null
    foreach ($tag in $Tags) {
        if ($tag -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)(?:\.(0|[1-9]\d*))?$') {
            continue
        }
        $candidate = [System.Management.Automation.SemanticVersion]::Parse($tag)
        if ($null -eq $maximum -or $candidate -gt $maximum) { $maximum = $candidate }
    }
    return $maximum
}

function Test-ContainerLatestPromotion([string] $Version, [string[]] $PublishedTags) {
    if ($Version.Contains('-', [System.StringComparison]::Ordinal)) { return $false }
    $current = [System.Management.Automation.SemanticVersion]::Parse($Version)
    $maximum = Get-MaximumContainerVersion $PublishedTags
    return $null -eq $maximum -or $current -ge $maximum
}

function Read-ContainerArchiveEntry {
    param([string] $ArchivePath, [string] $EntryName, [int] $MaximumLength)

    $file = [System.IO.File]::OpenRead($ArchivePath)
    $stream = $file
    $reader = $null
    try {
        if ($ArchivePath.EndsWith('.gz', [System.StringComparison]::OrdinalIgnoreCase)) {
            $stream = [System.IO.Compression.GZipStream]::new($file, [System.IO.Compression.CompressionMode]::Decompress)
        }
        $reader = [System.Formats.Tar.TarReader]::new($stream, $true)
        $content = $null
        while ($null -ne ($entry = $reader.GetNextEntry())) {
            $name = $entry.Name -replace '^\./', ''
            if ($name -cne $EntryName) { continue }
            if ($null -ne $content -or $entry.EntryType -notin @(
                [System.Formats.Tar.TarEntryType]::RegularFile,
                [System.Formats.Tar.TarEntryType]::V7RegularFile) -or
                $entry.Length -le 0 -or $entry.Length -gt $MaximumLength) {
                throw "Container archive entry '$EntryName' is duplicated or invalid."
            }
            $content = [byte[]]::new([int]$entry.Length)
            $entry.DataStream.ReadExactly($content, 0, $content.Length)
        }
        if ($null -eq $content) { throw "Container archive entry '$EntryName' is missing." }
        return ,$content
    }
    finally {
        if ($null -ne $reader) { $reader.Dispose() }
        if ($stream -ne $file) { $stream.Dispose() }
        $file.Dispose()
    }
}

function Get-ContainerArchiveConfigDigest {
    param([string] $ArchivePath, [string] $ImageTag)

    $manifestBytes = Read-ContainerArchiveEntry $ArchivePath 'manifest.json' 1048576
    $manifest = @([System.Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json)
    if ($manifest.Count -ne 1 -or @($manifest[0].RepoTags) -cnotcontains $ImageTag) {
        throw "Container archive must contain exactly the requested image '$ImageTag'."
    }
    $configPath = [string]$manifest[0].Config
    if ([string]::IsNullOrWhiteSpace($configPath)) { throw 'Container archive config path is missing.' }
    $config = Read-ContainerArchiveEntry $ArchivePath $configPath 16777216
    return 'sha256:' + [System.Convert]::ToHexStringLower([System.Security.Cryptography.SHA256]::HashData($config))
}

function Assert-ContainerLoadedImage {
    param([string] $ImageTag, [string] $ConfigDigest)

    $savedImage = [System.IO.Path]::GetTempFileName()
    try {
        # Docker's image ID can identify a config, manifest, or index depending on its image store.
        & docker image save --output $savedImage $ImageTag
        if ($LASTEXITCODE -ne 0) { throw "Could not save loaded container image '$ImageTag'." }
        $actualDigest = Get-ContainerArchiveConfigDigest $savedImage $ImageTag
        if ($actualDigest -cne $ConfigDigest) {
            throw "Loaded container image '$ImageTag' does not match its tested config digest."
        }
    }
    finally {
        Remove-Item -LiteralPath $savedImage -Force
    }
}
