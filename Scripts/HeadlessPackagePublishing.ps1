Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Formats.Tar

$script:NuGetPayloadReceiptPath = 'devprojex/payload-receipt.json'

function Get-ArchiveEntryBytes([System.IO.Compression.ZipArchiveEntry] $Entry) {
    $inputStream = $Entry.Open()
    try {
        $output = [System.IO.MemoryStream]::new()
        try {
            $inputStream.CopyTo($output)
            return $output.ToArray()
        }
        finally { $output.Dispose() }
    }
    finally { $inputStream.Dispose() }
}

function Test-NuGetSemanticEntry([string] $Path) {
    if ($Path.EndsWith('/', [System.StringComparison]::Ordinal)) { return $false }
    if ($Path -ceq $script:NuGetPayloadReceiptPath) { return $false }
    if ($Path -ceq '.signature.p7s' -or $Path -ceq '[Content_Types].xml') { return $false }
    if ($Path.StartsWith('_rels/', [System.StringComparison]::Ordinal) -or
        $Path.StartsWith('package/services/', [System.StringComparison]::Ordinal)) {
        return $false
    }
    return $true
}

function Get-ZipEntrySha256([System.IO.Compression.ZipArchiveEntry] $Entry) {
    $stream = $Entry.Open()
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try { return [Convert]::ToHexString($sha256.ComputeHash($stream)).ToLowerInvariant() }
    finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
}

function Get-NuGetSemanticFiles([System.IO.Compression.ZipArchive] $Archive) {
    return @($Archive.Entries |
        Where-Object { Test-NuGetSemanticEntry $_.FullName } |
        Sort-Object FullName |
        ForEach-Object {
            [ordered]@{
                path = $_.FullName
                size = [long]$_.Length
                sha256 = Get-ZipEntrySha256 $_
            }
        })
}

function Get-NuGetPackageIdentity([System.IO.Compression.ZipArchive] $Archive) {
    $nuspecs = @($Archive.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [System.StringComparison]::Ordinal) })
    if ($nuspecs.Count -ne 1) { throw "NuGet package must contain exactly one nuspec; found $($nuspecs.Count)." }
    [xml]$nuspec = [System.Text.Encoding]::UTF8.GetString((Get-ArchiveEntryBytes $nuspecs[0])).TrimStart([char]0xFEFF)
    $metadata = $nuspec.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']")
    if ($null -eq $metadata) { throw 'NuGet package nuspec has no metadata element.' }
    $id = [string]$metadata.SelectSingleNode("*[local-name()='id']").InnerText
    $version = [string]$metadata.SelectSingleNode("*[local-name()='version']").InnerText
    if ([string]::IsNullOrWhiteSpace($id) -or [string]::IsNullOrWhiteSpace($version)) {
        throw 'NuGet package nuspec has no package identity.'
    }
    return [pscustomobject]@{ Id = $id; Version = $version }
}

function Add-NuGetPayloadReceipt([string] $PackagePath) {
    $fullPath = [System.IO.Path]::GetFullPath($PackagePath)
    $archive = [System.IO.Compression.ZipFile]::OpenRead($fullPath)
    try {
        $identity = Get-NuGetPackageIdentity $archive
        $files = Get-NuGetSemanticFiles $archive
    }
    finally { $archive.Dispose() }

    $receipt = [ordered]@{
        schemaVersion = 1
        packageId = $identity.Id
        packageVersion = $identity.Version
        files = $files
    }
    $json = ($receipt | ConvertTo-Json -Depth 6 -Compress) + "`n"
    $archive = [System.IO.Compression.ZipFile]::Open($fullPath, [System.IO.Compression.ZipArchiveMode]::Update)
    try {
        $existing = $archive.GetEntry($script:NuGetPayloadReceiptPath)
        if ($null -ne $existing) { $existing.Delete() }
        $entry = $archive.CreateEntry($script:NuGetPayloadReceiptPath, [System.IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [System.DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [System.TimeSpan]::Zero)
        $stream = $entry.Open()
        try {
            $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($json)
            $stream.Write($bytes, 0, $bytes.Length)
        }
        finally { $stream.Dispose() }
    }
    finally { $archive.Dispose() }
}

function Read-NuGetPayloadReceipt([string] $PackagePath) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead([System.IO.Path]::GetFullPath($PackagePath))
    try {
        $identity = Get-NuGetPackageIdentity $archive
        $entry = $archive.GetEntry($script:NuGetPayloadReceiptPath)
        if ($null -eq $entry) { throw "NuGet package '$PackagePath' has no payload receipt." }
        $receipt = [System.Text.Encoding]::UTF8.GetString((Get-ArchiveEntryBytes $entry)) | ConvertFrom-Json
        if ($receipt.schemaVersion -ne 1 -or
            [string]$receipt.packageId -cne $identity.Id -or
            [string]$receipt.packageVersion -cne $identity.Version) {
            throw "NuGet package '$PackagePath' has an invalid payload receipt identity."
        }
        $actual = Get-NuGetSemanticFiles $archive
        $expectedJson = @($receipt.files) | ConvertTo-Json -Depth 4 -Compress
        $actualJson = @($actual) | ConvertTo-Json -Depth 4 -Compress
        if ($expectedJson -cne $actualJson) {
            throw "NuGet package '$PackagePath' does not match its payload receipt."
        }
        return [pscustomobject]@{
            Id = $identity.Id
            Version = $identity.Version
            CanonicalReceipt = ($receipt | ConvertTo-Json -Depth 6 -Compress)
        }
    }
    finally { $archive.Dispose() }
}

function Test-NuGetPayloadEquivalent([string] $LocalPath, [string] $PublishedPath) {
    $local = Read-NuGetPayloadReceipt $LocalPath
    $published = Read-NuGetPayloadReceipt $PublishedPath
    return $local.Id -ceq $published.Id -and
        $local.Version -ceq $published.Version -and
        $local.CanonicalReceipt -ceq $published.CanonicalReceipt
}

function Test-InstalledNuGetPayloadReceipt([string] $PackagePath, [string] $InstalledReceiptPath) {
    $local = Read-NuGetPayloadReceipt $PackagePath
    $receipt = Get-Content -LiteralPath $InstalledReceiptPath -Raw | ConvertFrom-Json
    if ($receipt.schemaVersion -ne 1 -or
        [string]$receipt.packageId -cne $local.Id -or
        [string]$receipt.packageVersion -cne $local.Version) {
        return $false
    }
    return ($receipt | ConvertTo-Json -Depth 6 -Compress) -ceq $local.CanonicalReceipt
}

function Get-NuGetToolRuntimeVersion([string] $PackagePath, [string] $RuntimeIdentifier) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead([System.IO.Path]::GetFullPath($PackagePath))
    try {
        $entryPath = "tools/any/$RuntimeIdentifier/devprojex.runtimeconfig.json"
        $entry = $archive.GetEntry($entryPath)
        if ($null -eq $entry) {
            throw "NuGet package '$PackagePath' has no runtime configuration for '$RuntimeIdentifier'."
        }
        $configuration = [System.Text.Encoding]::UTF8.GetString((Get-ArchiveEntryBytes $entry)) | ConvertFrom-Json
        $framework = @($configuration.runtimeOptions.includedFrameworks |
            Where-Object { [string]$_.name -ceq 'Microsoft.NETCore.App' })
        if ($framework.Count -ne 1 -or [string]::IsNullOrWhiteSpace([string]$framework[0].version)) {
            throw "NuGet package '$PackagePath' has no included Microsoft.NETCore.App version."
        }
        return [string]$framework[0].version
    }
    finally { $archive.Dispose() }
}

function Get-NpmPackageIdentity([string] $PackagePath) {
    $file = [System.IO.File]::OpenRead([System.IO.Path]::GetFullPath($PackagePath))
    try {
        $gzip = [System.IO.Compression.GZipStream]::new($file, [System.IO.Compression.CompressionMode]::Decompress)
        try {
            $reader = [System.Formats.Tar.TarReader]::new($gzip, $false)
            try {
                while ($null -ne ($entry = $reader.GetNextEntry())) {
                    if ($entry.Name -cne 'package/package.json') { continue }
                    $memory = [System.IO.MemoryStream]::new()
                    try {
                        $entry.DataStream.CopyTo($memory)
                        $package = [System.Text.Encoding]::UTF8.GetString($memory.ToArray()) | ConvertFrom-Json
                        if ([string]::IsNullOrWhiteSpace([string]$package.name) -or
                            [string]::IsNullOrWhiteSpace([string]$package.version)) {
                            throw "npm package '$PackagePath' has no package identity."
                        }
                        return [pscustomobject]@{ Name = [string]$package.name; Version = [string]$package.version }
                    }
                    finally { $memory.Dispose() }
                }
            }
            finally { $reader.Dispose() }
        }
        finally { $gzip.Dispose() }
    }
    finally { $file.Dispose() }
    throw "npm package '$PackagePath' has no package/package.json entry."
}

function Get-NpmPackageIntegrity([string] $PackagePath) {
    $stream = [System.IO.File]::OpenRead([System.IO.Path]::GetFullPath($PackagePath))
    try {
        $hash = [System.Security.Cryptography.SHA512]::HashData($stream)
        return 'sha512-' + [Convert]::ToBase64String($hash)
    }
    finally { $stream.Dispose() }
}
