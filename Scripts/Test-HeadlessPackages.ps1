[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ArtifactsRoot,

    [Parameter(Mandatory = $true)]
    [string] $Version,

    [string] $ReleasePublishRoot = "",

    [string] $ReleaseVersion = ""
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repoRoot "Packaging/Headless/payload-manifest.json"
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$artifactsPath = [System.IO.Path]::GetFullPath($ArtifactsRoot)
$nugetPath = Join-Path $artifactsPath "nuget"
$npmPath = Join-Path $artifactsPath "npm"
$publishPath = Join-Path $artifactsPath "publish"
$hasReleaseArchives = -not [string]::IsNullOrWhiteSpace($ReleasePublishRoot)
if ([string]::IsNullOrWhiteSpace($ReleaseVersion)) { $ReleaseVersion = $Version }

. (Join-Path $PSScriptRoot 'release-archive-helpers.ps1')

if ($manifest.schemaVersion -ne 1) {
    throw "Unsupported headless payload manifest schema: $($manifest.schemaVersion)."
}
if (@($manifest.rids).Count -ne 6) {
    throw "Headless payload manifest must define exactly six RIDs."
}
if (@($manifest.grammars).Count -lt 20) {
    throw "Headless payload manifest must define at least 20 grammars."
}
foreach ($sentinel in @("tree-sitter-c-sharp", "tree-sitter-kotlin")) {
    if ($sentinel -notin @($manifest.grammars)) {
        throw "Headless payload manifest is missing sentinel grammar '$sentinel'."
    }
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.Formats.Tar

function Assert-Artifact([bool] $Condition, [string] $ArtifactName, [string] $Missing) {
    if (-not $Condition) {
        throw "Artifact '$ArtifactName' is incomplete: missing or invalid $Missing."
    }
}

function Get-ZipEntryBytes([System.IO.Compression.ZipArchiveEntry] $Entry) {
    $stream = $Entry.Open()
    try {
        $memory = [System.IO.MemoryStream]::new()
        try {
            $stream.CopyTo($memory)
            return $memory.ToArray()
        }
        finally {
            $memory.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-GrammarPayload(
    [byte[]] $Bytes,
    [object] $Rid,
    [string] $ArtifactName
) {
    # Managed metadata stores embedded-resource names as UTF-8. The same bytes stay
    # uncompressed inside the default .NET single-file bundle, so this verifies the
    # actual resource table in both package shapes without executing a foreign RID.
    $payloadText = [System.Text.Encoding]::Latin1.GetString($Bytes)
    foreach ($grammar in @($manifest.grammars)) {
        $fileName = "$($Rid.grammarPrefix)$grammar$($Rid.grammarExtension)"
        Assert-Artifact `
            -Condition $payloadText.Contains($fileName, [System.StringComparison]::Ordinal) `
            -ArtifactName $ArtifactName `
            -Missing "grammar '$fileName'"
    }
}

function Open-Zip([string] $Path) {
    return [System.IO.Compression.ZipFile]::OpenRead($Path)
}

function Get-BytesSha256([byte[]] $Bytes) {
    return [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
}

$expectedNugetNames = @("devprojex.$Version.nupkg") + @(
    $manifest.rids | ForEach-Object { "devprojex.$($_.rid).$Version.nupkg" }
)
$actualNugetNames = @(
    Get-ChildItem -LiteralPath $nugetPath -Filter "*.nupkg" -File |
        Where-Object { $_.Name -notlike "*.snupkg" } |
        ForEach-Object Name |
        Sort-Object
)
Assert-Artifact `
    -Condition (@(Compare-Object ($expectedNugetNames | Sort-Object) $actualNugetNames).Count -eq 0) `
    -ArtifactName $nugetPath `
    -Missing "exact NuGet set: $($expectedNugetNames -join ', ')"
Assert-Artifact `
    -Condition (-not ($actualNugetNames | Where-Object { $_ -match '\.any\.' })) `
    -ArtifactName $nugetPath `
    -Missing "the no-any-package contract"

$pointerName = "devprojex.$Version.nupkg"
$pointerPackage = Join-Path $nugetPath $pointerName
$pointerZip = Open-Zip $pointerPackage
try {
    $nuspecEntry = @($pointerZip.Entries | Where-Object { $_.FullName -like "*.nuspec" })
    Assert-Artifact ($nuspecEntry.Count -eq 1) $pointerName "one nuspec"
    # .NET 10.0.400 made RID-pointer settings TFM-agnostic and moved this entry
    # from tools/net10.0/any to tools/any/any. Both layouts are produced by
    # supported .NET 10 SDKs; the parsed RID links below are the stable contract.
    $settingsEntry = @($pointerZip.Entries | Where-Object {
        $_.FullName -cmatch '^tools/(?:net10\.0|any)/any/DotnetToolSettings\.xml$'
    })
    Assert-Artifact ($settingsEntry.Count -eq 1) $pointerName "RID pointer settings"
    $settingsText = [System.Text.Encoding]::UTF8.GetString((Get-ZipEntryBytes $settingsEntry[0])).TrimStart([char]0xFEFF)
    [xml] $settings = $settingsText
    $dependencies = @($settings.SelectNodes("//*[local-name()='RuntimeIdentifierPackage']"))
    foreach ($rid in $manifest.rids) {
        $dependencyId = "devprojex.$($rid.rid)"
        $matches = @($dependencies | Where-Object {
            $_.RuntimeIdentifier -ceq $rid.rid -and $_.Id -ceq $dependencyId
        })
        Assert-Artifact ($matches.Count -eq 1) $pointerName "RID link '$($rid.rid)' -> '$dependencyId@$Version'"
    }
    Assert-Artifact ($dependencies.Count -eq 6) $pointerName "exactly six same-version RID links"
}
finally {
    $pointerZip.Dispose()
}

foreach ($rid in $manifest.rids) {
    $packageName = "devprojex.$($rid.rid).$Version.nupkg"
    $packagePath = Join-Path $nugetPath $packageName
    $archive = Open-Zip $packagePath
    try {
        $binaryEntries = @($archive.Entries | Where-Object {
            [System.IO.Path]::GetFileName($_.FullName) -ceq $rid.binary
        })
        Assert-Artifact ($binaryEntries.Count -eq 1) $packageName "executable '$($rid.binary)'"
        $infrastructureEntries = @($archive.Entries | Where-Object {
            [System.IO.Path]::GetFileName($_.FullName) -ceq "Infrastructure.dll"
        })
        Assert-Artifact ($infrastructureEntries.Count -eq 1) $packageName "Infrastructure.dll grammar carrier"
        Assert-GrammarPayload (Get-ZipEntryBytes $infrastructureEntries[0]) $rid $packageName
    }
    finally {
        $archive.Dispose()
    }
}

function Invoke-Tar([string[]] $Arguments, [string] $ArtifactName) {
    & tar @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Artifact '$ArtifactName' could not be read as a tgz archive."
    }
}

function Test-TarEntryExecutable([string] $Path, [string] $EntryName) {
    $file = [System.IO.File]::OpenRead($Path)
    try {
        $gzip = [System.IO.Compression.GZipStream]::new(
            $file,
            [System.IO.Compression.CompressionMode]::Decompress,
            $false)
        try {
            $reader = [System.Formats.Tar.TarReader]::new($gzip, $false)
            try {
                while ($null -ne ($entry = $reader.GetNextEntry())) {
                    if ($entry.Name -cne $EntryName) {
                        continue
                    }
                    return ($entry.Mode -band [System.IO.UnixFileMode]::UserExecute) -ne 0
                }
                return $false
            }
            finally {
                $reader.Dispose()
            }
        }
        finally {
            $gzip.Dispose()
        }
    }
    finally {
        $file.Dispose()
    }
}

$expectedNpmNames = @("devprojex-$Version.tgz") + @(
    $manifest.rids | ForEach-Object { "devprojex-cli-$($_.npmPlatform)-$Version.tgz" }
)
$actualNpmNames = @(
    Get-ChildItem -LiteralPath $npmPath -Filter "*.tgz" -File |
        ForEach-Object Name |
        Sort-Object
)
Assert-Artifact `
    -Condition (@(Compare-Object ($expectedNpmNames | Sort-Object) $actualNpmNames).Count -eq 0) `
    -ArtifactName $npmPath `
    -Missing "exact npm set: $($expectedNpmNames -join ', ')"

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("devprojex-headless-gate-" + [guid]::NewGuid().ToString("N"))
[System.IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
try {
    foreach ($rid in $manifest.rids) {
        $packageName = "devprojex-cli-$($rid.npmPlatform)-$Version.tgz"
        $packagePath = Join-Path $npmPath $packageName
        $extractPath = Join-Path $temporaryRoot $rid.npmPlatform
        [System.IO.Directory]::CreateDirectory($extractPath) | Out-Null
        Invoke-Tar @("-xzf", $packagePath, "-C", $extractPath) $packageName
        $packageRoot = Join-Path $extractPath "package"
        $packageJsonPath = Join-Path $packageRoot "package.json"
        $binaryPath = Join-Path (Join-Path $packageRoot "bin") $rid.binary
        Assert-Artifact (Test-Path -LiteralPath $packageJsonPath -PathType Leaf) $packageName "package.json"
        Assert-Artifact (Test-Path -LiteralPath $binaryPath -PathType Leaf) $packageName "binary '$($rid.binary)'"
        Assert-Artifact (Test-Path -LiteralPath (Join-Path $packageRoot "LICENSE") -PathType Leaf) $packageName "LICENSE"
        if ($rid.os -cne "win32") {
            Assert-Artifact `
                (Test-TarEntryExecutable $packagePath "package/bin/$($rid.binary)") `
                $packageName `
                "executable mode for 'package/bin/$($rid.binary)'"
        }

        $packageJson = Get-Content -LiteralPath $packageJsonPath -Raw | ConvertFrom-Json
        Assert-Artifact ($packageJson.name -ceq "@devprojex/cli-$($rid.npmPlatform)") $packageName "package name"
        Assert-Artifact ($packageJson.version -ceq $Version) $packageName "version '$Version'"
        Assert-Artifact (@($packageJson.os).Count -eq 1 -and $packageJson.os[0] -ceq $rid.os) $packageName "os '$($rid.os)'"
        Assert-Artifact (@($packageJson.cpu).Count -eq 1 -and $packageJson.cpu[0] -ceq $rid.cpu) $packageName "cpu '$($rid.cpu)'"
        if ($rid.os -ceq "linux") {
            Assert-Artifact (@($packageJson.libc).Count -eq 1 -and $packageJson.libc[0] -ceq "glibc") $packageName "libc 'glibc'"
        }
        else {
            Assert-Artifact (-not ($packageJson.PSObject.Properties.Name -contains "libc")) $packageName "absence of a non-Linux libc restriction"
        }
        Assert-GrammarPayload ([System.IO.File]::ReadAllBytes($binaryPath)) $rid $packageName

        if ($hasReleaseArchives) {
            $releaseDirectory = Join-Path ([System.IO.Path]::GetFullPath($ReleasePublishRoot)) "headless/v$ReleaseVersion"
            $extension = if ($rid.os -ceq 'win32') { 'zip' } else { 'tar.gz' }
            $releaseName = "$($manifest.release.headless.archivePrefix).v$ReleaseVersion.$($rid.rid).$extension"
            $releasePath = Join-Path $releaseDirectory $releaseName
            Assert-Artifact (Test-Path -LiteralPath $releasePath -PathType Leaf) $releaseName "release archive"

            if ($rid.os -ceq 'win32') {
                $releaseZip = Open-Zip $releasePath
                try {
                    $releaseEntries = @($releaseZip.Entries | Where-Object { -not $_.FullName.EndsWith('/') })
                    Assert-Artifact ($releaseEntries.Count -eq 1 -and $releaseEntries[0].FullName -ceq [string]$rid.binary) `
                        $releaseName "exact archive layout '$($rid.binary)'"
                    $releaseBytes = Get-ZipEntryBytes $releaseEntries[0]
                }
                finally { $releaseZip.Dispose() }
            }
            else {
                $releaseEntries = @(Read-UstarGzipArchive -archivePath $releasePath -captureEntryNames @([string]$rid.binary))
                Assert-Artifact ($releaseEntries.Count -eq 1 -and $releaseEntries[0].Name -ceq [string]$rid.binary) `
                    $releaseName "exact archive layout '$($rid.binary)'"
                Assert-Artifact (($releaseEntries[0].Mode -band 73) -eq 73) $releaseName "executable mode for '$($rid.binary)'"
                $releaseBytes = [byte[]]$releaseEntries[0].Bytes
            }

            $npmHash = Get-FileSha256Hex -path $binaryPath
            $publishBinary = Join-Path (Join-Path $publishPath ([string]$rid.rid)) ([string]$rid.binary)
            Assert-Artifact (Test-Path -LiteralPath $publishBinary -PathType Leaf) $releaseName "canonical publish binary"
            $publishHash = Get-FileSha256Hex -path $publishBinary
            $releaseHash = Get-BytesSha256 $releaseBytes
            Assert-Artifact ($releaseHash -ceq $npmHash -and $releaseHash -ceq $publishHash) `
                $releaseName "byte-identical release, npm, and canonical publish binaries"
        }
    }

    $mainName = "devprojex-$Version.tgz"
    $mainExtractPath = Join-Path $temporaryRoot "main"
    [System.IO.Directory]::CreateDirectory($mainExtractPath) | Out-Null
    Invoke-Tar @("-xzf", (Join-Path $npmPath $mainName), "-C", $mainExtractPath) $mainName
    $mainRoot = Join-Path $mainExtractPath "package"
    $mainJsonPath = Join-Path $mainRoot "package.json"
    Assert-Artifact (Test-Path -LiteralPath $mainJsonPath -PathType Leaf) $mainName "package.json"
    Assert-Artifact (Test-Path -LiteralPath (Join-Path $mainRoot "bin/devprojex.js") -PathType Leaf) $mainName "bin/devprojex.js"
    Assert-Artifact (Test-Path -LiteralPath (Join-Path $mainRoot "LICENSE") -PathType Leaf) $mainName "LICENSE"
    $mainJson = Get-Content -LiteralPath $mainJsonPath -Raw | ConvertFrom-Json
    Assert-Artifact ($mainJson.name -ceq "devprojex") $mainName "package name 'devprojex'"
    Assert-Artifact ($mainJson.version -ceq $Version) $mainName "version '$Version'"
    Assert-Artifact ($mainJson.engines.node -ceq ">=20") $mainName "Node engine >=20"
    Assert-Artifact (-not ($mainJson.PSObject.Properties.Name -contains "dependencies")) $mainName "absence of dependencies"
    Assert-Artifact (-not ($mainJson.PSObject.Properties.Name -contains "scripts")) $mainName "absence of lifecycle scripts"
    $optional = $mainJson.optionalDependencies
    Assert-Artifact (@($optional.PSObject.Properties).Count -eq 6) $mainName "six optional platform dependencies"
    foreach ($rid in $manifest.rids) {
        $dependencyName = "@devprojex/cli-$($rid.npmPlatform)"
        $property = $optional.PSObject.Properties[$dependencyName]
        Assert-Artifact ($null -ne $property -and $property.Value -ceq $Version) $mainName "optional dependency '$dependencyName@$Version'"
    }
}
finally {
    $resolvedTemporaryRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
    $resolvedSystemTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($resolvedTemporaryRoot.StartsWith($resolvedSystemTemp, [System.StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$releaseSummary = if ($hasReleaseArchives) { ' + 6 release archives' } else { '' }
Write-Host "Headless package completeness gate passed for $Version (7 NuGet + 7 npm artifacts$releaseSummary)."
