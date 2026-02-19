<#
.SYNOPSIS
  Interactive release builder that runs in an isolated temporary workspace.

.DESCRIPTION
  This script keeps your local Rider workspace stable:
    1) asks for a version in 0.0.0.0 format (unless provided)
    2) copies repo to a temporary isolated workspace
    3) builds GitHub artifacts in Release mode
    4) builds Microsoft Store package in ReleaseStore mode
    5) copies final artifacts back to local publish folders only

  The script never writes build artifacts into your working source tree
  except:
    - publish\github\v<version>\
    - publish\store\v<version>\

  Output locations:
    - GitHub: publish\github\v<version>\
    - Store : publish\store\v<version>\
#>
[CmdletBinding()]
param(
    [string]$Version = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$script:IsolatedWorkspaceRoot = $null
$script:IsolatedRepoRoot = $null
$script:OriginalNuGetPackages = [Environment]::GetEnvironmentVariable("NUGET_PACKAGES", "Process")
$script:OriginalNuGetHttpCachePath = [Environment]::GetEnvironmentVariable("NUGET_HTTP_CACHE_PATH", "Process")
$script:OriginalNuGetPluginsCachePath = [Environment]::GetEnvironmentVariable("NUGET_PLUGINS_CACHE_PATH", "Process")

function Write-Step([string]$message) {
    Write-Host ""
    Write-Host "=== $message ===" -ForegroundColor Cyan
}

function Invoke-ExternalCommand(
    [string]$filePath,
    [string[]]$arguments,
    [string]$failureMessage,
    [string]$workingDirectory = ""
) {
    if ([string]::IsNullOrWhiteSpace($workingDirectory)) {
        & $filePath @arguments
    }
    else {
        Push-Location $workingDirectory
        try {
            & $filePath @arguments
        }
        finally {
            Pop-Location
        }
    }

    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) {
        throw "$failureMessage (exit code: $exitCode)"
    }
}

function Ensure-DotnetAvailable() {
    if ($null -eq (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "dotnet CLI is not available in PATH."
    }
}

function Get-VersionInteractive([string]$currentValue) {
    $value = $currentValue
    while ($true) {
        if ([string]::IsNullOrWhiteSpace($value)) {
            $value = Read-Host "Enter release version (format 0.0.0.0)"
        }

        if ($value -match '^\d+\.\d+\.\d+\.\d+$') {
            return $value
        }

        Write-Host "Invalid version format. Use exactly: 0.0.0.0" -ForegroundColor Yellow
        $value = ""
    }
}

function Restore-NuGetEnvironment() {
    if ($null -eq $script:OriginalNuGetPackages) {
        Remove-Item Env:NUGET_PACKAGES -ErrorAction SilentlyContinue
    }
    else {
        $env:NUGET_PACKAGES = $script:OriginalNuGetPackages
    }

    if ($null -eq $script:OriginalNuGetHttpCachePath) {
        Remove-Item Env:NUGET_HTTP_CACHE_PATH -ErrorAction SilentlyContinue
    }
    else {
        $env:NUGET_HTTP_CACHE_PATH = $script:OriginalNuGetHttpCachePath
    }

    if ($null -eq $script:OriginalNuGetPluginsCachePath) {
        Remove-Item Env:NUGET_PLUGINS_CACHE_PATH -ErrorAction SilentlyContinue
    }
    else {
        $env:NUGET_PLUGINS_CACHE_PATH = $script:OriginalNuGetPluginsCachePath
    }
}

function Configure-IsolatedNuGetCache() {
    $isolatedNuGetRoot = Join-Path $script:IsolatedWorkspaceRoot ("nuget\" + [Guid]::NewGuid().ToString("N"))
    $isolatedPackages = Join-Path $isolatedNuGetRoot "packages"
    $isolatedHttp = Join-Path $isolatedNuGetRoot "http"
    $isolatedPlugins = Join-Path $isolatedNuGetRoot "plugins"

    New-Item -ItemType Directory -Path $isolatedPackages -Force | Out-Null
    New-Item -ItemType Directory -Path $isolatedHttp -Force | Out-Null
    New-Item -ItemType Directory -Path $isolatedPlugins -Force | Out-Null

    # Always use isolated per-run caches to keep local developer environment untouched.
    $env:NUGET_PACKAGES = $isolatedPackages
    $env:NUGET_HTTP_CACHE_PATH = $isolatedHttp
    $env:NUGET_PLUGINS_CACHE_PATH = $isolatedPlugins
}

function Create-IsolatedWorkspace([string]$sourceRoot) {
    Write-Step "Preparing isolated workspace"

    $script:IsolatedWorkspaceRoot = Join-Path $env:TEMP ("devprojex-release-work\" + [Guid]::NewGuid().ToString("N"))
    $script:IsolatedRepoRoot = Join-Path $script:IsolatedWorkspaceRoot "repo"
    New-Item -ItemType Directory -Path $script:IsolatedRepoRoot -Force | Out-Null

    $robocopyArgs = @(
        $sourceRoot,
        $script:IsolatedRepoRoot,
        "/MIR",
        "/R:1",
        "/W:1",
        "/NFL",
        "/NDL",
        "/NJH",
        "/NJS",
        "/NP",
        "/XD",
        (Join-Path $sourceRoot ".git"),
        (Join-Path $sourceRoot ".idea"),
        (Join-Path $sourceRoot ".vs"),
        (Join-Path $sourceRoot ".codex"),
        (Join-Path $sourceRoot ".claude"),
        (Join-Path $sourceRoot "publish"),
        (Join-Path $sourceRoot ".release-cache")
    )

    & robocopy @robocopyArgs | Out-Null
    $robocopyExitCode = $LASTEXITCODE
    if ($robocopyExitCode -gt 7) {
        throw "Failed to copy source into isolated workspace (robocopy exit code: $robocopyExitCode)"
    }

    # Remove stale bin/obj copied from source tree to guarantee deterministic build in workspace.
    $projectDirectories = @(
        Get-ChildItem -Path $script:IsolatedRepoRoot -Recurse -File -Include *.csproj,*.wapproj |
        ForEach-Object { $_.Directory.FullName }
    ) | Sort-Object -Unique

    foreach ($projectDir in $projectDirectories) {
        foreach ($artifactDirectoryName in @("bin", "obj")) {
            $artifactPath = Join-Path $projectDir $artifactDirectoryName
            if (Test-Path $artifactPath) {
                Remove-Item -Path $artifactPath -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }
}

function Invoke-IsolatedScript([string]$relativeScriptPath, [hashtable]$arguments) {
    $scriptPath = Join-Path $script:IsolatedRepoRoot $relativeScriptPath
    if (-not (Test-Path $scriptPath)) {
        throw "Isolated script not found: $scriptPath"
    }

    $psExe = Join-Path $PSHOME "powershell.exe"
    if (-not (Test-Path $psExe)) {
        $psExe = "powershell"
    }

    $argList = New-Object System.Collections.Generic.List[string]
    $argList.Add("-NoProfile")
    $argList.Add("-ExecutionPolicy")
    $argList.Add("Bypass")
    $argList.Add("-File")
    $argList.Add($scriptPath)

    foreach ($key in $arguments.Keys) {
        $argList.Add("-$key")
        $argList.Add([string]$arguments[$key])
    }

    Invoke-ExternalCommand -filePath $psExe -arguments $argList -failureMessage "Script failed: $relativeScriptPath" -workingDirectory $script:IsolatedRepoRoot
}

function Get-LatestStoreArtifactPath() {
    $candidateRoots = @(
        (Join-Path $script:IsolatedRepoRoot "publish\store"),
        (Join-Path $script:IsolatedRepoRoot "Packaging\Windows\DevProjex.Store\publish\store")
    )

    $artifacts = @()
    foreach ($candidateRoot in $candidateRoots) {
        if (-not (Test-Path $candidateRoot)) {
            continue
        }

        $artifacts += Get-ChildItem -Path $candidateRoot -Recurse -File -Include *.msixupload -ErrorAction SilentlyContinue
    }

    return ($artifacts | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
}

function Publish-ArtifactsToSource([string]$sourceRoot, [string]$version) {
    Write-Step "Publishing artifacts to source publish folder"

    $isolatedGitHubDir = Join-Path $script:IsolatedRepoRoot "publish\github\v$version"
    if (-not (Test-Path $isolatedGitHubDir)) {
        throw "Isolated GitHub artifacts folder not found: $isolatedGitHubDir"
    }

    $sourceGitHubDir = Join-Path $sourceRoot "publish\github\v$version"
    if (Test-Path $sourceGitHubDir) {
        Remove-Item -Path $sourceGitHubDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Path $sourceGitHubDir -Force | Out-Null
    Copy-Item -Path (Join-Path $isolatedGitHubDir "*") -Destination $sourceGitHubDir -Recurse -Force

    $latestStoreArtifact = Get-LatestStoreArtifactPath
    if ($null -eq $latestStoreArtifact) {
        throw "MS Store artifact (.msixupload) not found in isolated workspace."
    }

    $sourceStoreDir = Join-Path $sourceRoot "publish\store\v$version"
    if (Test-Path $sourceStoreDir) {
        Remove-Item -Path $sourceStoreDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Path $sourceStoreDir -Force | Out-Null

    Copy-Item -Path $latestStoreArtifact.FullName -Destination (Join-Path $sourceStoreDir $latestStoreArtifact.Name) -Force

    $isolatedBuildLogPath = Join-Path $script:IsolatedRepoRoot "publish\store\msix-build.log"
    if (Test-Path $isolatedBuildLogPath) {
        Copy-Item -Path $isolatedBuildLogPath -Destination (Join-Path $sourceStoreDir "msix-build.log") -Force
    }

    return @{
        GitHub = $sourceGitHubDir
        Store = $sourceStoreDir
    }
}

function Cleanup-IsolatedWorkspace() {
    if (-not [string]::IsNullOrWhiteSpace($script:IsolatedWorkspaceRoot) -and (Test-Path $script:IsolatedWorkspaceRoot)) {
        Remove-Item -Path $script:IsolatedWorkspaceRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

Ensure-DotnetAvailable
$resolvedVersion = Get-VersionInteractive -currentValue $Version
$sourceRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$finalPaths = $null

Write-Step "Release plan"
Write-Host "Version: $resolvedVersion"
Write-Host "Build mode  : isolated workspace (local source tree untouched)"
Write-Host "GitHub build: Release (single-file, self-contained)"
Write-Host "Store build : ReleaseStore (.msixupload, x64|arm64)"
Write-Host "Store listing CSV: not modified"

try {
    Create-IsolatedWorkspace -sourceRoot $sourceRoot
    Configure-IsolatedNuGetCache

    Write-Step "Building GitHub artifacts in isolated workspace"
    Invoke-IsolatedScript -relativeScriptPath "Scripts\publish-github.ps1" -arguments @{
        Version = $resolvedVersion
        Configuration = "Release"
        OutputRoot = "publish\github"
    }

    Write-Step "Building Microsoft Store package in isolated workspace"
    Invoke-IsolatedScript -relativeScriptPath "Scripts\build-msix.ps1" -arguments @{
        Configuration = "ReleaseStore"
        Platform = "x64"
        BundlePlatforms = "x64|arm64"
        PackageVersion = $resolvedVersion
        CleanPackagingArtifacts = 1
    }

    $finalPaths = Publish-ArtifactsToSource -sourceRoot $sourceRoot -version $resolvedVersion

    Write-Step "Done"
    Write-Host "GitHub artifacts: $($finalPaths.GitHub)"
    Write-Host "MS Store artifacts: $($finalPaths.Store)"
}
finally {
    Restore-NuGetEnvironment
    Cleanup-IsolatedWorkspace
}
