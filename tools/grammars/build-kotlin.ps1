[CmdletBinding()]
param(
    [string]$ZigPath,
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$vendoredRoot = Join-Path $repositoryRoot 'Infrastructure\Grammars\vendored'
$manifestPath = Join-Path $vendoredRoot 'vendored-grammars.lock.json'
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$grammar = $manifest.grammars | Where-Object name -eq 'tree-sitter-kotlin'
if ($null -eq $grammar)
{
    throw 'The vendored grammar manifest does not contain tree-sitter-kotlin.'
}

function Get-LowerSha256([string]$Path)
{
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-FileHash([string]$Path, [string]$ExpectedHash)
{
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf))
    {
        throw "Vendored grammar file is missing: $Path"
    }

    $actualHash = Get-LowerSha256 $Path
    if ($actualHash -ne $ExpectedHash)
    {
        throw "SHA-256 mismatch for '$Path'. Expected $ExpectedHash, got $actualHash."
    }
}

function Assert-BinaryShape($Binary)
{
    $path = Join-Path $vendoredRoot $Binary.path
    Assert-FileHash $path $Binary.sha256

    $bytes = [IO.File]::ReadAllBytes($path)
    if ($bytes.Length -ne [long]$Binary.size)
    {
        throw "Size mismatch for '$path'. Expected $($Binary.size), got $($bytes.Length)."
    }

    $validMagic = switch ($Binary.format)
    {
        'pe'    { $bytes.Length -ge 2 -and $bytes[0] -eq 0x4D -and $bytes[1] -eq 0x5A }
        'elf'   { $bytes.Length -ge 4 -and $bytes[0] -eq 0x7F -and $bytes[1] -eq 0x45 -and $bytes[2] -eq 0x4C -and $bytes[3] -eq 0x46 }
        'macho' { $bytes.Length -ge 4 -and $bytes[0] -eq 0xCF -and $bytes[1] -eq 0xFA -and $bytes[2] -eq 0xED -and $bytes[3] -eq 0xFE }
        default { throw "Unknown binary format '$($Binary.format)' in the vendored grammar manifest." }
    }
    if (-not $validMagic)
    {
        throw "Invalid $($Binary.format) magic bytes in '$path'."
    }

    $ascii = [Text.Encoding]::ASCII.GetString($bytes)
    if (-not $ascii.Contains($grammar.export, [StringComparison]::Ordinal))
    {
        throw "Export name '$($grammar.export)' is absent from '$path'."
    }
}

function Clear-MachOUuid([string]$Path)
{
    $bytes = [IO.File]::ReadAllBytes($Path)
    $commandCount = [BitConverter]::ToUInt32($bytes, 16)
    $commandOffset = 32
    for ($index = 0; $index -lt $commandCount; $index++)
    {
        if ($commandOffset + 8 -gt $bytes.Length)
        {
            throw "Malformed Mach-O load commands in '$Path'."
        }

        $command = [BitConverter]::ToUInt32($bytes, $commandOffset)
        $commandSize = [BitConverter]::ToUInt32($bytes, $commandOffset + 4)
        if ($commandSize -lt 8 -or $commandOffset + $commandSize -gt $bytes.Length)
        {
            throw "Malformed Mach-O load command in '$Path'."
        }

        if ($command -eq 0x1B)
        {
            # Debug symbols are stripped, so a random LC_UUID only breaks reproducible hashes.
            [Array]::Clear($bytes, $commandOffset + 8, 16)
        }
        $commandOffset += $commandSize
    }

    [IO.File]::WriteAllBytes($Path, $bytes)
}

if ($VerifyOnly)
{
    foreach ($binary in $grammar.binaries)
    {
        Assert-BinaryShape $binary
    }

    Write-Host "Verified $($grammar.binaries.Count) vendored Kotlin grammar binaries."
    return
}

$workRoot = Join-Path ([IO.Path]::GetTempPath()) ("devprojex-kotlin-grammar-" + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($workRoot) | Out-Null

try
{
    if ([string]::IsNullOrWhiteSpace($ZigPath))
    {
        $zigArchive = Join-Path $workRoot 'zig.zip'
        Invoke-WebRequest -UseBasicParsing -Uri $grammar.toolchain.archiveUrl -OutFile $zigArchive
        Assert-FileHash $zigArchive $grammar.toolchain.archiveSha256
        $zigRoot = Join-Path $workRoot 'zig'
        Expand-Archive -LiteralPath $zigArchive -DestinationPath $zigRoot
        $ZigPath = (Get-ChildItem -LiteralPath $zigRoot -Filter zig.exe -File -Recurse | Select-Object -First 1).FullName
    }

    if (-not (Test-Path -LiteralPath $ZigPath -PathType Leaf))
    {
        throw "Zig executable is missing: $ZigPath"
    }

    $actualZigVersion = (& $ZigPath version).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualZigVersion -ne $grammar.toolchain.version)
    {
        throw "Zig $($grammar.toolchain.version) is required; '$ZigPath' reports '$actualZigVersion'."
    }

    $sourceArchive = Join-Path $workRoot 'tree-sitter-kotlin.zip'
    Invoke-WebRequest -UseBasicParsing -Uri $grammar.source.archiveUrl -OutFile $sourceArchive
    Assert-FileHash $sourceArchive $grammar.source.archiveSha256
    $sourceRoot = Join-Path $workRoot 'source'
    Expand-Archive -LiteralPath $sourceArchive -DestinationPath $sourceRoot
    $sourceDirectory = Get-ChildItem -LiteralPath $sourceRoot -Directory | Select-Object -First 1
    if ($null -eq $sourceDirectory)
    {
        throw 'The Kotlin grammar archive contains no source directory.'
    }

    $parserSource = Join-Path $sourceDirectory.FullName 'src\parser.c'
    $scannerSource = Join-Path $sourceDirectory.FullName 'src\scanner.c'
    foreach ($sourcePath in @($parserSource, $scannerSource))
    {
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf))
        {
            throw "Pinned Kotlin grammar source is missing: $sourcePath"
        }
    }

    foreach ($binary in $grammar.binaries)
    {
        $buildDirectory = Join-Path $workRoot ("build-" + $binary.rid)
        [IO.Directory]::CreateDirectory($buildDirectory) | Out-Null
        $builtPath = Join-Path $buildDirectory ([IO.Path]::GetFileName($binary.path))
        $arguments = @(
            'cc',
            '-target', $binary.target
        ) + @($grammar.toolchain.compilerFlags)
        if ($null -ne $binary.PSObject.Properties['linkerFlags'])
        {
            $arguments += @($binary.linkerFlags)
        }
        $arguments += @(
            '-I', (Join-Path $sourceDirectory.FullName 'src'),
            '-o', $builtPath,
            $parserSource,
            $scannerSource
        )

        & $ZigPath @arguments
        if ($LASTEXITCODE -ne 0)
        {
            throw "Zig failed to build tree-sitter-kotlin for $($binary.rid)."
        }

        if ($binary.format -eq 'macho')
        {
            Clear-MachOUuid $builtPath
        }

        Assert-FileHash $builtPath $binary.sha256
        $destination = Join-Path $vendoredRoot $binary.path
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        Copy-Item -LiteralPath $builtPath -Destination $destination -Force
        Assert-BinaryShape $binary
        Write-Host "Built $($binary.rid): $destination"
    }
}
finally
{
    if (Test-Path -LiteralPath $workRoot)
    {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}

Write-Host "Built and verified $($grammar.binaries.Count) vendored Kotlin grammar binaries."
