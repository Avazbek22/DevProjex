[CmdletBinding()]
param(
    [string] $Version,
    [switch] $DryRun = $true
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "Apps/TerminalHost/DevProjex.TerminalHost.csproj"
$manifestPath = Join-Path $repoRoot "Packaging/Headless/payload-manifest.json"
$headlessRoot = Join-Path $repoRoot "artifacts/headless"
$nugetRoot = Join-Path $headlessRoot "nuget"
$npmRoot = Join-Path $headlessRoot "npm"
$publishRoot = Join-Path $headlessRoot "publish"
$stagingRoot = Join-Path $headlessRoot "staging"
$releasePublishRoot = Join-Path $headlessRoot "release"

. (Join-Path $PSScriptRoot "release-archive-helpers.ps1")
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Normalize-PackageVersion([string] $Value) {
    if ($Value -match '^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)(?<suffix>[-+].+)?$') {
        $suffix = if ($Matches.ContainsKey("suffix")) { $Matches["suffix"] } else { "" }
        return "$($Matches.major).$($Matches.minor).0$suffix"
    }
    if ($Value -match '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)([-+].+)?$') {
        return $Value
    }
    throw "Version '$Value' must be a two- or three-part semantic version."
}

function Invoke-Checked([string] $FilePath, [string[]] $Arguments, [string] $WorkingDirectory) {
    Push-Location $WorkingDirectory
    try {
        & $FilePath @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "$FilePath failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

function Get-HeadlessArchiveName([object] $Rid) {
    $extension = if ($Rid.os -ceq "win32") { "zip" } else { "tar.gz" }
    return "$($manifest.release.headless.archivePrefix).v$Version.$($Rid.rid).$extension"
}

function New-HeadlessArchive([object] $Rid, [string] $BinaryPath) {
    $archivePath = Join-Path $headlessReleaseRoot (Get-HeadlessArchiveName $Rid)
    if ($Rid.os -ceq "win32") {
        $stream = [System.IO.File]::Create($archivePath)
        try {
            $archive = [System.IO.Compression.ZipArchive]::new(
                $stream,
                [System.IO.Compression.ZipArchiveMode]::Create,
                $false)
            try {
                $entry = $archive.CreateEntry([string]$Rid.binary, [System.IO.Compression.CompressionLevel]::Optimal)
                $entryStream = $entry.Open()
                try {
                    $source = [System.IO.File]::OpenRead($BinaryPath)
                    try { $source.CopyTo($entryStream) } finally { $source.Dispose() }
                }
                finally { $entryStream.Dispose() }
            }
            finally { $archive.Dispose() }
        }
        finally { $stream.Dispose() }
        return
    }

    New-UstarGzipArchive -archivePath $archivePath -entries @(
        [pscustomobject]@{
            Name = [string]$Rid.binary
            Mode = 493
            IsDirectory = $false
            SourcePath = $BinaryPath
            Bytes = $null
        })
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml] $buildProperties = Get-Content -LiteralPath (Join-Path $repoRoot "Directory.Build.props") -Raw
    $Version = [string]($buildProperties.Project.PropertyGroup.DevProjexVersion | Select-Object -First 1)
}
$packageVersion = Normalize-PackageVersion $Version
$headlessReleaseRoot = Join-Path $releasePublishRoot "headless/v$Version"
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

$resolvedHeadlessRoot = [System.IO.Path]::GetFullPath($headlessRoot)
$resolvedArtifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts"))
if (-not $resolvedHeadlessRoot.StartsWith($resolvedArtifactsRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to replace an output outside the repository artifacts directory: $resolvedHeadlessRoot"
}
if (Test-Path -LiteralPath $resolvedHeadlessRoot) {
    Remove-Item -LiteralPath $resolvedHeadlessRoot -Recurse -Force
}
foreach ($directory in @($nugetRoot, $npmRoot, $publishRoot, $stagingRoot, $headlessReleaseRoot)) {
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
}

$mode = if ($DryRun) { "dry-run" } else { "release-build" }
Write-Host "Building DevProjex headless packages ($mode)"
Write-Host "  Display version: $Version"
Write-Host "  Package version: $packageVersion"
Write-Host "  Output: $headlessRoot"

Invoke-Checked "dotnet" @(
    "pack", $projectPath,
    "-c", "Release",
    "-o", $nugetRoot,
    "/p:DevProjexVersion=$Version",
    "/p:DebugType=None",
    "/p:DebugSymbols=false"
) $repoRoot

foreach ($rid in $manifest.rids) {
    $ridPublishRoot = Join-Path $publishRoot $rid.rid
    $ridBuildRoot = Join-Path $headlessRoot "publish-build/$($rid.rid)"
    Invoke-Checked "dotnet" @(
        "publish", $projectPath,
        "-c", "Release",
        "-r", $rid.rid,
        "--self-contained", "true",
        "-m:1",
        "/p:BuildInParallel=false",
        "/p:DevProjexVersion=$Version",
        "/p:DevProjexHeadlessBuildRoot=$ridBuildRoot",
        "/p:PublishSingleFile=true",
        "/p:IncludeNativeLibrariesForSelfExtract=true",
        "/p:EnableCompressionInSingleFile=false",
        "/p:PublishReadyToRun=true",
        "/p:PublishTrimmed=false",
        "/p:DebugType=None",
        "/p:DebugSymbols=false",
        "/p:DevProjexGenerateReleasePayloadReceipt=true",
        "/p:DevProjexPayloadReceiptDirectory=$headlessReleaseRoot",
        "-o", $ridPublishRoot
    ) $repoRoot

    $publishedFiles = @(Get-ChildItem -LiteralPath $ridPublishRoot -File -Recurse)
    if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].Name -cne $rid.binary) {
        throw "Publish for '$($rid.rid)' must contain exactly '$($rid.binary)'; found: $($publishedFiles.FullName -join ', ')"
    }

    $platformStage = Join-Path $stagingRoot "platform-$($rid.npmPlatform)"
    $platformBin = Join-Path $platformStage "bin"
    [System.IO.Directory]::CreateDirectory($platformBin) | Out-Null
    $platformTemplate = Get-Content -LiteralPath (Join-Path $repoRoot "Packaging/Npm/platform/package.json.template") -Raw
    $platformJsonText = $platformTemplate.Replace("__PLATFORM__", [string]$rid.npmPlatform)
    $platformJsonText = $platformJsonText.Replace("__VERSION__", $packageVersion)
    $platformJsonText = $platformJsonText.Replace("__OS__", [string]$rid.os)
    $platformJsonText = $platformJsonText.Replace("__CPU__", [string]$rid.cpu)
    $platformJsonText = $platformJsonText.Replace("__BINARY__", [string]$rid.binary)
    $platformJson = $platformJsonText | ConvertFrom-Json
    if ($rid.os -ceq "linux") {
        $platformJson | Add-Member -NotePropertyName "libc" -NotePropertyValue @("glibc")
    }
    [System.IO.File]::WriteAllText(
        (Join-Path $platformStage "package.json"),
        (($platformJson | ConvertTo-Json -Depth 10) + [Environment]::NewLine),
        [System.Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath $publishedFiles[0].FullName -Destination (Join-Path $platformBin $rid.binary)
    Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination (Join-Path $platformStage "LICENSE")
    if (-not $IsWindows) {
        Invoke-Checked "chmod" @("+x", (Join-Path $platformBin $rid.binary)) $repoRoot
    }
    Invoke-Checked "npm" @("pack", ".", "--pack-destination", $npmRoot) $platformStage
    if ($rid.os -cne "win32") {
        & (Join-Path $PSScriptRoot "Set-NpmTarExecutable.ps1") `
            -PackagePath (Join-Path $npmRoot "devprojex-cli-$($rid.npmPlatform)-$packageVersion.tgz") `
            -EntryName "package/bin/$($rid.binary)"
    }
    New-HeadlessArchive -Rid $rid -BinaryPath $publishedFiles[0].FullName
}

$mainStage = Join-Path $stagingRoot "devprojex"
[System.IO.Directory]::CreateDirectory((Join-Path $mainStage "bin")) | Out-Null
$mainTemplate = Get-Content -LiteralPath (Join-Path $repoRoot "Packaging/Npm/devprojex/package.json.template") -Raw
[System.IO.File]::WriteAllText(
    (Join-Path $mainStage "package.json"),
    $mainTemplate.Replace("__VERSION__", $packageVersion),
    [System.Text.UTF8Encoding]::new($false))
Copy-Item -LiteralPath (Join-Path $repoRoot "Packaging/Npm/devprojex/bin/devprojex.js") -Destination (Join-Path $mainStage "bin/devprojex.js")
Copy-Item -LiteralPath (Join-Path $repoRoot "LICENSE") -Destination (Join-Path $mainStage "LICENSE")
Invoke-Checked "npm" @("pack", ".", "--pack-destination", $npmRoot) $mainStage

$releaseFiles = @(Get-ChildItem -LiteralPath $headlessReleaseRoot -File | Sort-Object Name)
$checksumLines = @($releaseFiles | ForEach-Object {
    "$(Get-FileSha256Hex -path $_.FullName) *$($_.Name)"
})
Write-ReleaseChecksumManifest `
    -path (Join-Path $headlessReleaseRoot ([string]$manifest.release.headless.checksumFile)) `
    -lines $checksumLines

& (Join-Path $PSScriptRoot "Test-HeadlessPackages.ps1") `
    -ArtifactsRoot $headlessRoot `
    -Version $packageVersion `
    -ReleasePublishRoot $releasePublishRoot `
    -ReleaseVersion $Version

& (Join-Path $PSScriptRoot "Test-ReleaseArtifacts.ps1") `
    -PublishRoot $releasePublishRoot `
    -Version $Version `
    -Channels headless

$packageFiles = @(
    Get-ChildItem -LiteralPath $nugetRoot -Filter "*.nupkg" -File
    Get-ChildItem -LiteralPath $npmRoot -Filter "*.tgz" -File
    Get-ChildItem -LiteralPath $headlessReleaseRoot -File
) | Sort-Object Name
$sizeReport = [System.Collections.Generic.List[string]]::new()
$sizeReport.Add("| Package | Bytes | MiB |")
$sizeReport.Add("|---|---:|---:|")
foreach ($package in $packageFiles) {
    $mib = $package.Length / 1MB
    $line = "| $($package.Name) | $($package.Length) | $($mib.ToString('0.00', [System.Globalization.CultureInfo]::InvariantCulture)) |"
    $sizeReport.Add($line)
}
$sizeReportPath = Join-Path $headlessRoot "package-sizes.md"
[System.IO.File]::WriteAllLines($sizeReportPath, $sizeReport, [System.Text.UTF8Encoding]::new($false))
Write-Host ""
Write-Host "Package sizes"
$sizeReport | ForEach-Object { Write-Host $_ }
