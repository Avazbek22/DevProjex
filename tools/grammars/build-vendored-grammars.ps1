[CmdletBinding()]
param(
    [string]$ZigPath,
    [string[]]$GrammarName = @(),
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$vendoredRoot = Join-Path $repositoryRoot 'Infrastructure\Grammars\vendored'
$manifestPath = Join-Path $vendoredRoot 'vendored-grammars.lock.json'
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$grammars = @($manifest.grammars)

if ($GrammarName.Count -gt 0)
{
    $requestedNames = [Collections.Generic.HashSet[string]]::new(
        $GrammarName,
        [StringComparer]::Ordinal)
    $grammars = @($grammars | Where-Object { $requestedNames.Contains($_.name) })
    $missingNames = @($requestedNames | Where-Object { $_ -notin $grammars.name })
    if ($missingNames.Count -gt 0)
    {
        throw "The vendored grammar manifest does not contain: $($missingNames -join ', ')."
    }
}

if ($grammars.Count -eq 0)
{
    throw 'The vendored grammar manifest contains no selected grammars.'
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

function Assert-BinaryShape($Grammar, $Binary)
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
    if (-not $ascii.Contains($Grammar.export, [StringComparison]::Ordinal))
    {
        throw "Export name '$($Grammar.export)' is absent from '$path'."
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

function Apply-SourcePatches($Grammar, [string]$SourceDirectory)
{
    if ($null -eq $Grammar.PSObject.Properties['sourcePatches'])
    {
        return
    }

    foreach ($patch in $Grammar.sourcePatches)
    {
        $path = Join-Path $SourceDirectory $patch.path
        if (-not (Test-Path -LiteralPath $path -PathType Leaf))
        {
            throw "Pinned source patch target is missing: $path"
        }

        $content = [IO.File]::ReadAllText($path)
        $first = $content.IndexOf($patch.oldText, [StringComparison]::Ordinal)
        $second = if ($first -lt 0)
        {
            -1
        }
        else
        {
            $content.IndexOf(
                $patch.oldText,
                $first + $patch.oldText.Length,
                [StringComparison]::Ordinal)
        }
        if ($first -lt 0 -or $second -ge 0)
        {
            throw "Source patch '$($patch.description)' must match exactly once in '$path'."
        }

        $updated = $content.Replace(
            $patch.oldText,
            $patch.newText,
            [StringComparison]::Ordinal)
        [IO.File]::WriteAllText($path, $updated, [Text.UTF8Encoding]::new($false))
    }
}

if ($VerifyOnly)
{
    foreach ($grammar in $grammars)
    {
        foreach ($binary in $grammar.binaries)
        {
            Assert-BinaryShape $grammar $binary
        }
    }

    $binaryCount = ($grammars | ForEach-Object { $_.binaries.Count } | Measure-Object -Sum).Sum
    Write-Host "Verified $binaryCount binaries for $($grammars.Count) vendored grammars."
    return
}

$toolchainKeys = @($grammars | ForEach-Object {
    "$($_.toolchain.name)|$($_.toolchain.version)|$($_.toolchain.archiveSha256)"
} | Select-Object -Unique)
if ($toolchainKeys.Count -ne 1)
{
    throw 'Selected grammars require different toolchains; build them in separate invocations.'
}

$toolchain = $grammars[0].toolchain
$workRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'devprojex-vendored-grammars-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($workRoot) | Out-Null

try
{
    if ([string]::IsNullOrWhiteSpace($ZigPath))
    {
        $zigArchive = Join-Path $workRoot 'zig.zip'
        Invoke-WebRequest -UseBasicParsing -Uri $toolchain.archiveUrl -OutFile $zigArchive
        Assert-FileHash $zigArchive $toolchain.archiveSha256
        $zigRoot = Join-Path $workRoot 'zig'
        Expand-Archive -LiteralPath $zigArchive -DestinationPath $zigRoot
        $ZigPath = (Get-ChildItem -LiteralPath $zigRoot -Filter zig.exe -File -Recurse |
            Select-Object -First 1).FullName
    }

    if (-not (Test-Path -LiteralPath $ZigPath -PathType Leaf))
    {
        throw "Zig executable is missing: $ZigPath"
    }

    $actualZigVersion = (& $ZigPath version).Trim()
    if ($LASTEXITCODE -ne 0 -or $actualZigVersion -ne $toolchain.version)
    {
        throw "Zig $($toolchain.version) is required; '$ZigPath' reports '$actualZigVersion'."
    }

    foreach ($grammar in $grammars)
    {
        $grammarWorkRoot = Join-Path $workRoot $grammar.name
        [IO.Directory]::CreateDirectory($grammarWorkRoot) | Out-Null
        $sourceArchive = Join-Path $grammarWorkRoot 'source.zip'
        Invoke-WebRequest -UseBasicParsing -Uri $grammar.source.archiveUrl -OutFile $sourceArchive
        Assert-FileHash $sourceArchive $grammar.source.archiveSha256
        $sourceRoot = Join-Path $grammarWorkRoot 'source'
        Expand-Archive -LiteralPath $sourceArchive -DestinationPath $sourceRoot
        $sourceDirectories = @(Get-ChildItem -LiteralPath $sourceRoot -Directory)
        if ($sourceDirectories.Count -ne 1)
        {
            throw "The $($grammar.name) archive must contain exactly one source directory."
        }

        $sourceDirectory = $sourceDirectories[0].FullName
        Apply-SourcePatches $grammar $sourceDirectory
        $sourceFiles = @($grammar.build.sourceFiles | ForEach-Object {
            $path = Join-Path $sourceDirectory $_
            if (-not (Test-Path -LiteralPath $path -PathType Leaf))
            {
                throw "Pinned $($grammar.name) source is missing: $path"
            }
            $path
        })

        foreach ($binary in $grammar.binaries)
        {
            $buildDirectory = Join-Path $grammarWorkRoot ("build-" + $binary.rid)
            [IO.Directory]::CreateDirectory($buildDirectory) | Out-Null
            $builtPath = Join-Path $buildDirectory ([IO.Path]::GetFileName($binary.path))
            $arguments = @(
                $grammar.build.compiler,
                '-target', $binary.target
            ) + @($grammar.toolchain.compilerFlags)
            if ($null -ne $binary.PSObject.Properties['linkerFlags'])
            {
                $arguments += @($binary.linkerFlags)
            }
            $arguments += @(
                '-I', (Join-Path $sourceDirectory 'src'),
                '-o', $builtPath
            ) + $sourceFiles

            & $ZigPath @arguments
            if ($LASTEXITCODE -ne 0)
            {
                throw "Zig failed to build $($grammar.name) for $($binary.rid)."
            }

            if ($binary.format -eq 'macho')
            {
                Clear-MachOUuid $builtPath
            }

            Assert-FileHash $builtPath $binary.sha256
            $destination = Join-Path $vendoredRoot $binary.path
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
            Copy-Item -LiteralPath $builtPath -Destination $destination -Force
            Assert-BinaryShape $grammar $binary
            Write-Host "Built $($grammar.name) $($binary.rid): $destination"
        }
    }
}
finally
{
    if (Test-Path -LiteralPath $workRoot)
    {
        Remove-Item -LiteralPath $workRoot -Recurse -Force
    }
}

$builtCount = ($grammars | ForEach-Object { $_.binaries.Count } | Measure-Object -Sum).Sum
Write-Host "Built and verified $builtCount binaries for $($grammars.Count) vendored grammars."
