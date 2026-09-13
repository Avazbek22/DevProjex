[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackagePath,

    [Parameter(Mandatory = $true)]
    [string] $EntryName
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.Formats.Tar

$resolvedPackage = [System.IO.Path]::GetFullPath($PackagePath)
$temporaryPackage = "$resolvedPackage.mode-$([guid]::NewGuid().ToString('N')).tmp"
$found = $false
$inputFile = $null
$inputGzip = $null
$reader = $null
$outputFile = $null
$outputGzip = $null
$writer = $null
$completed = $false
try {
    $inputFile = [System.IO.File]::OpenRead($resolvedPackage)
    $inputGzip = [System.IO.Compression.GZipStream]::new(
        $inputFile,
        [System.IO.Compression.CompressionMode]::Decompress,
        $false)
    $reader = [System.Formats.Tar.TarReader]::new($inputGzip, $false)
    $outputFile = [System.IO.File]::Create($temporaryPackage)
    $outputGzip = [System.IO.Compression.GZipStream]::new(
        $outputFile,
        [System.IO.Compression.CompressionLevel]::SmallestSize,
        $false)
    $writer = [System.Formats.Tar.TarWriter]::new($outputGzip, $false)

    while ($null -ne ($entry = $reader.GetNextEntry())) {
        if ($entry.Name -ceq $EntryName) {
            $entry.Mode =
                [System.IO.UnixFileMode]::UserRead -bor
                [System.IO.UnixFileMode]::UserWrite -bor
                [System.IO.UnixFileMode]::UserExecute -bor
                [System.IO.UnixFileMode]::GroupRead -bor
                [System.IO.UnixFileMode]::GroupExecute -bor
                [System.IO.UnixFileMode]::OtherRead -bor
                [System.IO.UnixFileMode]::OtherExecute
            $found = $true
        }
        $dataCopy = $null
        try {
            if ($null -ne $entry.DataStream -and -not $entry.DataStream.CanSeek) {
                $dataCopy = [System.IO.MemoryStream]::new()
                $entry.DataStream.CopyTo($dataCopy)
                $dataCopy.Position = 0
                $entry.DataStream = $dataCopy
            }
            $writer.WriteEntry($entry)
        }
        finally {
            if ($null -ne $dataCopy) { $dataCopy.Dispose() }
        }
    }
    $completed = $true
}
finally {
    if ($null -ne $writer) { $writer.Dispose() }
    if ($null -ne $outputGzip) { $outputGzip.Dispose() }
    if ($null -ne $outputFile) { $outputFile.Dispose() }
    if ($null -ne $reader) { $reader.Dispose() }
    if ($null -ne $inputGzip) { $inputGzip.Dispose() }
    if ($null -ne $inputFile) { $inputFile.Dispose() }
    if (-not $completed) {
        Remove-Item -LiteralPath $temporaryPackage -Force -ErrorAction SilentlyContinue
    }
}

if (-not $found) {
    Remove-Item -LiteralPath $temporaryPackage -Force -ErrorAction SilentlyContinue
    throw "Tarball '$resolvedPackage' does not contain '$EntryName'."
}

[System.IO.File]::Move($temporaryPackage, $resolvedPackage, $true)
