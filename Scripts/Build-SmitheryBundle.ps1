<#
.SYNOPSIS
    Builds the Smithery bundle and checks it before it is published.

.DESCRIPTION
    The bundle holds Packaging/Smithery/manifest.json, server/index.js, and the application icon. It
    carries no DevProjex binary: the manifest starts the npm package with npx, which selects the
    build for the user's platform. The script refuses a manifest whose version differs from
    DevProjexVersion or that npm does not serve yet, validates it with the MCPB tooling, and checks
    that the packed archive holds exactly those files and stays under the size Smithery accepts.

.PARAMETER AllowUnpublishedVersion
    Builds the bundle even though npm does not serve its version yet, to check it before a release.
    Such a bundle must not be published.
#>
[CmdletBinding()]
param(
    [string] $OutputPath,

    [switch] $AllowUnpublishedVersion
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

$mcpbPackage = "@anthropic-ai/mcpb@2.1.2"
# The Smithery CLI rejects a larger bundle before uploading it.
$maximumBundleBytes = 25MB
$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceRoot = Join-Path $repoRoot "Packaging/Smithery"
# Bundle path -> source path. Claude Desktop shows only an icon that is inside the bundle.
$bundleFiles = [ordered]@{
    "manifest.json" = Join-Path $sourceRoot "manifest.json"
    "server/index.js" = Join-Path $sourceRoot "server/index.js"
    "icon.png" = Join-Path $repoRoot "Assets/AppIcon/Linux/png/512.png"
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot "artifacts/smithery/devprojex.mcpb"
}
$outputFile = [System.IO.Path]::GetFullPath($OutputPath)
$stagingRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "devprojex-smithery-" + [guid]::NewGuid().ToString("N"))

function Invoke-Mcpb([string[]] $Arguments, [string] $Failure) {
    & npx --yes $mcpbPackage @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Failure (mcpb exit code $LASTEXITCODE)."
    }
}

function Get-ProductVersion {
    [xml] $properties = Get-Content -LiteralPath (Join-Path $repoRoot "Directory.Build.props") -Raw
    $node = $properties.SelectSingleNode("//*[local-name()='DevProjexVersion']")
    if ($null -eq $node) {
        throw "Directory.Build.props does not define DevProjexVersion."
    }
    $version = $node.InnerText.Trim()
    if ($version -match '^\d+\.\d+$') {
        return "$version.0"
    }
    return $version
}

try {
    $manifest = Get-Content -LiteralPath (Join-Path $sourceRoot "manifest.json") -Raw | ConvertFrom-Json
    $productVersion = Get-ProductVersion
    if ($manifest.version -cne $productVersion) {
        throw "Packaging/Smithery/manifest.json declares version '$($manifest.version)', but DevProjexVersion is '$productVersion'."
    }
    # The listing names this version while npx starts whatever npm serves, so it must not name a
    # version that npm does not have yet.
    if (-not $AllowUnpublishedVersion) {
        $published = @(& npm view "devprojex@$productVersion" version 2>$null)
        $publishedVersion = if ($published.Count -gt 0) { "$($published[-1])".Trim() } else { "" }
        if ($LASTEXITCODE -ne 0 -or $publishedVersion -cne $productVersion) {
            throw "npm does not serve devprojex $productVersion yet. Publish the npm release first, or pass -AllowUnpublishedVersion to check the bundle without publishing it."
        }
    }

    foreach ($file in $bundleFiles.GetEnumerator()) {
        $destination = Join-Path $stagingRoot $file.Key
        [System.IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
        Copy-Item -LiteralPath $file.Value -Destination $destination
    }

    Invoke-Mcpb @("validate", (Join-Path $stagingRoot "manifest.json")) "The bundle manifest is not valid"
    [System.IO.Directory]::CreateDirectory((Split-Path -Parent $outputFile)) | Out-Null
    if (Test-Path -LiteralPath $outputFile) {
        Remove-Item -LiteralPath $outputFile -Force
    }
    Invoke-Mcpb @("pack", $stagingRoot, $outputFile) "The bundle could not be packed"

    $size = (Get-Item -LiteralPath $outputFile).Length
    if ($size -gt $maximumBundleBytes) {
        throw "The bundle is $size bytes; Smithery accepts at most $maximumBundleBytes."
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($outputFile)
    try {
        $entries = @($archive.Entries |
            Where-Object { -not $_.FullName.EndsWith("/") } |
            ForEach-Object { $_.FullName.Replace("\", "/") } |
            Sort-Object)
    }
    finally {
        $archive.Dispose()
    }
    $expected = @($bundleFiles.Keys | Sort-Object)
    if (($entries -join "|") -cne ($expected -join "|")) {
        throw "The bundle holds '$($entries -join ", ")' instead of '$($expected -join ", ")'."
    }

    $hash = (Get-FileHash -LiteralPath $outputFile -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "Smithery bundle: $outputFile"
    Write-Host "Version: $productVersion; size: $size bytes; SHA-256: $hash"
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}
