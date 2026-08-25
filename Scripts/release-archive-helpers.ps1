Set-StrictMode -Version Latest

$script:UstarBlockSize = 512
$script:UstarFixedModificationTime = 0

function Get-FileSha256Hex([string]$path) {
    $stream = [System.IO.File]::OpenRead($path)
    try {
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $hash = $sha256.ComputeHash($stream)
        }
        finally {
            $sha256.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    return ([System.BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
}

function Write-ReleaseChecksumManifest(
    [string]$path,
    [string[]]$lines
) {
    foreach ($line in $lines) {
        if ($line.Contains("`r") -or $line.Contains("`n")) {
            throw "Release checksum entries must be single-line values."
        }
    }

    $content = if ($lines.Count -eq 0) {
        ''
    }
    else {
        ($lines -join "`n") + "`n"
    }
    $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($path, $content, $utf8WithoutBom)
}

function Set-UstarAsciiField(
    [byte[]]$header,
    [int]$offset,
    [int]$length,
    [string]$value
) {
    $bytes = [System.Text.Encoding]::ASCII.GetBytes($value)
    if ($bytes.Length -gt $length) {
        throw "USTAR field value is too long: '$value'."
    }

    [System.Array]::Copy($bytes, 0, $header, $offset, $bytes.Length)
}

function ConvertTo-UstarOctal([long]$value, [int]$length) {
    if ($value -lt 0) {
        throw "USTAR numeric values cannot be negative: $value."
    }

    $digits = [System.Convert]::ToString($value, 8)
    if ($digits.Length -gt ($length - 1)) {
        throw "USTAR numeric value '$value' does not fit in a $length-byte field."
    }

    return $digits.PadLeft($length - 1, '0') + [char]0
}

function New-UstarHeader(
    [string]$name,
    [int]$mode,
    [long]$size,
    [char]$typeFlag
) {
    $header = New-Object byte[] $script:UstarBlockSize
    Set-UstarAsciiField -header $header -offset 0 -length 100 -value $name
    Set-UstarAsciiField -header $header -offset 100 -length 8 -value (ConvertTo-UstarOctal -value $mode -length 8)
    Set-UstarAsciiField -header $header -offset 108 -length 8 -value (ConvertTo-UstarOctal -value 0 -length 8)
    Set-UstarAsciiField -header $header -offset 116 -length 8 -value (ConvertTo-UstarOctal -value 0 -length 8)
    Set-UstarAsciiField -header $header -offset 124 -length 12 -value (ConvertTo-UstarOctal -value $size -length 12)
    Set-UstarAsciiField -header $header -offset 136 -length 12 -value (ConvertTo-UstarOctal -value $script:UstarFixedModificationTime -length 12)
    for ($index = 148; $index -lt 156; $index++) {
        $header[$index] = 32
    }
    Set-UstarAsciiField -header $header -offset 156 -length 1 -value ([string]$typeFlag)
    Set-UstarAsciiField -header $header -offset 257 -length 6 -value ("ustar" + [char]0)
    Set-UstarAsciiField -header $header -offset 263 -length 2 -value "00"

    [long]$checksum = 0
    foreach ($value in $header) {
        $checksum += $value
    }

    $checksumDigits = [System.Convert]::ToString($checksum, 8)
    if ($checksumDigits.Length -gt 6) {
        throw "USTAR checksum '$checksum' is too large."
    }
    Set-UstarAsciiField -header $header -offset 148 -length 8 -value ($checksumDigits.PadLeft(6, '0') + [char]0 + ' ')
    return $header
}

function Copy-StreamBytes(
    [System.IO.Stream]$source,
    [System.IO.Stream]$destination,
    [long]$count
) {
    $buffer = New-Object byte[] 81920
    [long]$remaining = $count
    while ($remaining -gt 0) {
        $requested = [int][System.Math]::Min([long]$buffer.Length, $remaining)
        $read = $source.Read($buffer, 0, $requested)
        if ($read -le 0) {
            throw "Unexpected end of stream with $remaining byte(s) remaining."
        }
        $destination.Write($buffer, 0, $read)
        $remaining -= $read
    }
}

function Skip-StreamBytes([System.IO.Stream]$stream, [long]$count) {
    $buffer = New-Object byte[] 81920
    [long]$remaining = $count
    while ($remaining -gt 0) {
        $requested = [int][System.Math]::Min([long]$buffer.Length, $remaining)
        $read = $stream.Read($buffer, 0, $requested)
        if ($read -le 0) {
            throw "Unexpected end of archive with $remaining byte(s) remaining."
        }
        $remaining -= $read
    }
}

function Read-StreamBlock([System.IO.Stream]$stream, [int]$length) {
    $buffer = New-Object byte[] $length
    $offset = 0
    while ($offset -lt $length) {
        $read = $stream.Read($buffer, $offset, $length - $offset)
        if ($read -le 0) {
            throw "Unexpected end of archive while reading a $length-byte block."
        }
        $offset += $read
    }
    return $buffer
}

function New-UstarGzipArchive(
    [string]$archivePath,
    [object[]]$entries
) {
    if ($null -eq $entries -or $entries.Count -eq 0) {
        throw "At least one USTAR entry is required."
    }

    $parent = Split-Path -Path $archivePath -Parent
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    $fileStream = [System.IO.File]::Create($archivePath)
    try {
        $gzipStream = New-Object System.IO.Compression.GZipStream(
            $fileStream,
            [System.IO.Compression.CompressionMode]::Compress,
            $true)
        try {
            foreach ($entry in $entries) {
                $name = [string]$entry.Name
                $mode = [int]$entry.Mode
                $isDirectory = [bool]$entry.IsDirectory
                if ([string]::IsNullOrWhiteSpace($name) -or $name.Contains('\')) {
                    throw "USTAR entry names must be non-empty forward-slash paths: '$name'."
                }
                if ([System.Text.Encoding]::ASCII.GetByteCount($name) -gt 100) {
                    throw "USTAR entry name exceeds the portable 100-byte field: '$name'."
                }
                if ($isDirectory -and -not $name.EndsWith('/', [System.StringComparison]::Ordinal)) {
                    throw "USTAR directory entries must end with '/': '$name'."
                }

                $sourcePath = [string]$entry.SourcePath
                $entryBytes = $entry.Bytes
                [long]$size = 0
                if (-not $isDirectory) {
                    if ($null -ne $entryBytes) {
                        $size = [long]$entryBytes.Length
                    }
                    elseif (-not [string]::IsNullOrWhiteSpace($sourcePath) -and (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
                        $size = (Get-Item -LiteralPath $sourcePath).Length
                    }
                    else {
                        throw "USTAR file entry '$name' has no readable source."
                    }
                }

                $typeFlag = if ($isDirectory) { [char]'5' } else { [char]'0' }
                $header = New-UstarHeader -name $name -mode $mode -size $size -typeFlag $typeFlag
                $gzipStream.Write($header, 0, $header.Length)

                if (-not $isDirectory -and $size -gt 0) {
                    if ($null -ne $entryBytes) {
                        $gzipStream.Write($entryBytes, 0, $entryBytes.Length)
                    }
                    else {
                        $sourceStream = [System.IO.File]::OpenRead($sourcePath)
                        try {
                            Copy-StreamBytes -source $sourceStream -destination $gzipStream -count $size
                        }
                        finally {
                            $sourceStream.Dispose()
                        }
                    }

                    $paddingLength = [int](($script:UstarBlockSize - ($size % $script:UstarBlockSize)) % $script:UstarBlockSize)
                    if ($paddingLength -gt 0) {
                        $padding = New-Object byte[] $paddingLength
                        $gzipStream.Write($padding, 0, $padding.Length)
                    }
                }
            }

            $endBlocks = New-Object byte[] ($script:UstarBlockSize * 2)
            $gzipStream.Write($endBlocks, 0, $endBlocks.Length)
        }
        finally {
            $gzipStream.Dispose()
        }
    }
    finally {
        $fileStream.Dispose()
    }
}

function ConvertFrom-UstarOctal([byte[]]$header, [int]$offset, [int]$length) {
    $value = [System.Text.Encoding]::ASCII.GetString($header, $offset, $length).Trim([char]0, ' ')
    if ([string]::IsNullOrWhiteSpace($value)) {
        return [long]0
    }
    return [System.Convert]::ToInt64($value, 8)
}

function Read-UstarGzipArchive(
    [string]$archivePath,
    [string[]]$captureEntryNames = @()
) {
    $capturedNames = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
    foreach ($captureName in $captureEntryNames) {
        [void]$capturedNames.Add($captureName)
    }

    $entries = New-Object 'System.Collections.Generic.List[object]'
    $fileStream = [System.IO.File]::OpenRead($archivePath)
    try {
        $gzipStream = New-Object System.IO.Compression.GZipStream(
            $fileStream,
            [System.IO.Compression.CompressionMode]::Decompress,
            $true)
        try {
            while ($true) {
                $header = Read-StreamBlock -stream $gzipStream -length $script:UstarBlockSize
                $nonZeroHeaderByte = $header | Where-Object { $_ -ne 0 } | Select-Object -First 1
                if ($null -eq $nonZeroHeaderByte) {
                    $secondEndBlock = Read-StreamBlock -stream $gzipStream -length $script:UstarBlockSize
                    if ($null -ne ($secondEndBlock | Where-Object { $_ -ne 0 } | Select-Object -First 1)) {
                        throw "USTAR archive does not end with two zero blocks: $archivePath"
                    }
                    break
                }

                $storedChecksum = ConvertFrom-UstarOctal -header $header -offset 148 -length 8
                $checksumHeader = New-Object byte[] $script:UstarBlockSize
                [System.Array]::Copy($header, $checksumHeader, $header.Length)
                for ($index = 148; $index -lt 156; $index++) {
                    $checksumHeader[$index] = 32
                }
                [long]$calculatedChecksum = 0
                foreach ($value in $checksumHeader) {
                    $calculatedChecksum += $value
                }
                if ($storedChecksum -ne $calculatedChecksum) {
                    throw "USTAR checksum mismatch in '$archivePath'."
                }

                $name = [System.Text.Encoding]::ASCII.GetString($header, 0, 100).TrimEnd([char]0)
                $mode = [int](ConvertFrom-UstarOctal -header $header -offset 100 -length 8)
                $size = ConvertFrom-UstarOctal -header $header -offset 124 -length 12
                $typeFlag = [char]$header[156]
                $capturedBytes = $null
                if ($size -gt 0 -and $capturedNames.Contains($name)) {
                    if ($size -gt [int]::MaxValue) {
                        throw "Captured USTAR entry is too large: '$name'."
                    }
                    $capturedBytes = Read-StreamBlock -stream $gzipStream -length ([int]$size)
                }
                elseif ($size -gt 0) {
                    Skip-StreamBytes -stream $gzipStream -count $size
                }

                $paddingLength = [long](($script:UstarBlockSize - ($size % $script:UstarBlockSize)) % $script:UstarBlockSize)
                if ($paddingLength -gt 0) {
                    Skip-StreamBytes -stream $gzipStream -count $paddingLength
                }

                $entries.Add([pscustomobject]@{
                    Name = $name
                    Mode = $mode
                    Size = $size
                    IsDirectory = ($typeFlag -eq [char]'5')
                    Bytes = $capturedBytes
                })
            }
        }
        finally {
            $gzipStream.Dispose()
        }
    }
    finally {
        $fileStream.Dispose()
    }

    return $entries.ToArray()
}

function Write-BigEndianUInt32([System.IO.Stream]$stream, [uint32]$value) {
    $bytes = [byte[]]@(
        (($value -shr 24) -band 0xff),
        (($value -shr 16) -band 0xff),
        (($value -shr 8) -band 0xff),
        ($value -band 0xff)
    )
    $stream.Write($bytes, 0, $bytes.Length)
}

function Read-BigEndianUInt32(
    [byte[]]$bytes,
    [int]$offset
) {
    if ($null -eq $bytes -or $offset -lt 0 -or $bytes.Length -lt ($offset + 4)) {
        throw "Cannot read a big-endian UInt32 at offset $offset."
    }

    return [uint32](
        ([uint32]$bytes[$offset] * 16777216) +
        ([uint32]$bytes[$offset + 1] * 65536) +
        ([uint32]$bytes[$offset + 2] * 256) +
        [uint32]$bytes[$offset + 3])
}

function Assert-PngDimensions(
    [byte[]]$payload,
    [string]$sourcePath,
    [string]$slotType,
    [int]$expectedSize
) {
    $pngSignature = [byte[]]@(137, 80, 78, 71, 13, 10, 26, 10)
    if ($null -eq $payload -or $payload.Length -lt 24) {
        throw "macOS icon source for $slotType is not a complete PNG: $sourcePath"
    }
    for ($index = 0; $index -lt $pngSignature.Length; $index++) {
        if ($payload[$index] -ne $pngSignature[$index]) {
            throw "macOS icon source for $slotType is not a PNG: $sourcePath"
        }
    }

    $ihdrLength = Read-BigEndianUInt32 -bytes $payload -offset 8
    $ihdrType = [System.Text.Encoding]::ASCII.GetString($payload, 12, 4)
    if ($ihdrLength -ne 13 -or $ihdrType -ne 'IHDR') {
        throw "macOS icon source for $slotType has an invalid PNG IHDR: $sourcePath"
    }

    $width = Read-BigEndianUInt32 -bytes $payload -offset 16
    $height = Read-BigEndianUInt32 -bytes $payload -offset 20
    if ($width -ne $expectedSize -or $height -ne $expectedSize) {
        throw "macOS icon source for $slotType must be ${expectedSize}x${expectedSize}px; found ${width}x${height}px: $sourcePath"
    }
}

function New-DeterministicIcns([string]$iconSetPath, [string]$outputPath) {
    $slots = @(
        [pscustomobject]@{ Type = 'ic07'; File = '128.png'; Size = 128 },
        [pscustomobject]@{ Type = 'ic08'; File = '256.png'; Size = 256 },
        [pscustomobject]@{ Type = 'ic09'; File = '512.png'; Size = 512 },
        [pscustomobject]@{ Type = 'ic10'; File = '1024.png'; Size = 1024 },
        [pscustomobject]@{ Type = 'ic11'; File = '32.png'; Size = 32 },
        [pscustomobject]@{ Type = 'ic12'; File = '64.png'; Size = 64 },
        [pscustomobject]@{ Type = 'ic13'; File = '256.png'; Size = 256 },
        [pscustomobject]@{ Type = 'ic14'; File = '512.png'; Size = 512 }
    )

    $slotPayloads = New-Object 'System.Collections.Generic.List[object]'
    [uint64]$totalLength = 8
    foreach ($slot in $slots) {
        $sourcePath = Join-Path $iconSetPath $slot.File
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
            throw "Required macOS icon source was not found: $sourcePath"
        }
        $payload = [System.IO.File]::ReadAllBytes($sourcePath)
        if ($payload.Length -eq 0) {
            throw "Required macOS icon source is empty: $sourcePath"
        }
        Assert-PngDimensions `
            -payload $payload `
            -sourcePath $sourcePath `
            -slotType ([string]$slot.Type) `
            -expectedSize ([int]$slot.Size)
        $slotPayloads.Add([pscustomobject]@{ Type = $slot.Type; Bytes = $payload })
        $totalLength += 8 + $payload.Length
    }
    if ($totalLength -gt [uint32]::MaxValue) {
        throw "Generated ICNS exceeds the 32-bit container limit."
    }

    $parent = Split-Path -Path $outputPath -Parent
    if (-not [string]::IsNullOrWhiteSpace($parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $stream = [System.IO.File]::Create($outputPath)
    try {
        $signature = [System.Text.Encoding]::ASCII.GetBytes('icns')
        $stream.Write($signature, 0, $signature.Length)
        Write-BigEndianUInt32 -stream $stream -value ([uint32]$totalLength)
        foreach ($slotPayload in $slotPayloads) {
            $typeBytes = [System.Text.Encoding]::ASCII.GetBytes([string]$slotPayload.Type)
            $stream.Write($typeBytes, 0, $typeBytes.Length)
            Write-BigEndianUInt32 -stream $stream -value ([uint32](8 + $slotPayload.Bytes.Length))
            $stream.Write($slotPayload.Bytes, 0, $slotPayload.Bytes.Length)
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Get-PlistStringValue([xml]$document, [string]$key) {
    $keyNode = $document.SelectSingleNode("/plist/dict/key[text()='$key']")
    if ($null -eq $keyNode) {
        return $null
    }

    $valueNode = $keyNode.NextSibling
    while ($null -ne $valueNode -and $valueNode.NodeType -ne [System.Xml.XmlNodeType]::Element) {
        $valueNode = $valueNode.NextSibling
    }
    if ($null -eq $valueNode -or $valueNode.LocalName -ne 'string') {
        return $null
    }
    return [string]$valueNode.InnerText
}

function Get-MacInfoPlistContent([string]$templatePath, [string]$version) {
    $template = [System.IO.File]::ReadAllText($templatePath)
    $placeholder = 'YOUR_RELEASE_VERSION'
    $placeholderCount = [System.Text.RegularExpressions.Regex]::Matches(
        $template,
        [System.Text.RegularExpressions.Regex]::Escape($placeholder)).Count
    if ($placeholderCount -ne 2) {
        throw "macOS Info.plist template must contain exactly two $placeholder placeholders. Found: $placeholderCount."
    }
    if ($version -notmatch '^\d+(\.\d+){0,3}$') {
        throw "Invalid macOS bundle version '$version'."
    }

    $content = $template.Replace($placeholder, $version)
    [xml]$document = $content
    $requiredValues = @{
        CFBundleName = 'DevProjex'
        CFBundleDisplayName = 'DevProjex'
        CFBundleIdentifier = 'com.devprojex.app'
        CFBundleVersion = $version
        CFBundleShortVersionString = $version
        CFBundleExecutable = 'DevProjex'
        CFBundleIconFile = 'app.icns'
        CFBundlePackageType = 'APPL'
        LSMinimumSystemVersion = '14.0'
    }
    foreach ($requiredValue in $requiredValues.GetEnumerator()) {
        $actualValue = Get-PlistStringValue -document $document -key ([string]$requiredValue.Key)
        if ($actualValue -ne [string]$requiredValue.Value) {
            throw "macOS Info.plist value '$($requiredValue.Key)' must be '$($requiredValue.Value)'. Found: '$actualValue'."
        }
    }
    if ($content.Contains($placeholder)) {
        throw "macOS Info.plist still contains a release-version placeholder."
    }
    return $content
}
