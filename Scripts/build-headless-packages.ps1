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

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml] $buildProperties = Get-Content -LiteralPath (Join-Path $repoRoot "Directory.Build.props") -Raw
    $Version = [string]($buildProperties.Project.PropertyGroup.DevProjexVersion | Select-Object -First 1)
}
$packageVersion = Normalize-PackageVersion $Version
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

$resolvedHeadlessRoot = [System.IO.Path]::GetFullPath($headlessRoot)
$resolvedArtifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts"))
if (-not $resolvedHeadlessRoot.StartsWith($resolvedArtifactsRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to replace an output outside the repository artifacts directory: $resolvedHeadlessRoot"
}
if (Test-Path -LiteralPath $resolvedHeadlessRoot) {
    Remove-Item -LiteralPath $resolvedHeadlessRoot -Recurse -Force
}
foreach ($directory in @($nugetRoot, $npmRoot, $publishRoot, $stagingRoot)) {
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
        "/p:PublishReadyToRun=true",
        "/p:PublishTrimmed=false",
        "/p:DebugType=None",
        "/p:DebugSymbols=false",
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

& (Join-Path $PSScriptRoot "Test-HeadlessPackages.ps1") `
    -ArtifactsRoot $headlessRoot `
    -Version $packageVersion

$packageFiles = @(
    Get-ChildItem -LiteralPath $nugetRoot -Filter "*.nupkg" -File
    Get-ChildItem -LiteralPath $npmRoot -Filter "*.tgz" -File
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
