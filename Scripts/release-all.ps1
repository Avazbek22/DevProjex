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

function Build-GitHubArtifactsInWorkspace([string]$version, [string]$configuration) {
    $projectPath = Join-Path $script:IsolatedRepoRoot "Apps\Avalonia\DevProjex.Avalonia\DevProjex.Avalonia.csproj"
    if (-not (Test-Path $projectPath)) {
        throw "Avalonia project not found in isolated workspace: $projectPath"
    }

    $releaseDir = Join-Path $script:IsolatedRepoRoot "publish\github\v$version"
    $workDir = Join-Path $releaseDir "_work"

    $targets = @(
        @{ Rid = "win-x64"; Binary = "DevProjex.exe"; Name = "DevProjex.v$version.win-x64.exe" },
        @{ Rid = "win-arm64"; Binary = "DevProjex.exe"; Name = "DevProjex.v$version.win-arm64.exe" },
        @{ Rid = "linux-x64"; Binary = "DevProjex"; Name = "DevProjex.v$version.linux-x64.portable" },
        @{ Rid = "linux-arm64"; Binary = "DevProjex"; Name = "DevProjex.v$version.linux-arm64.portable" },
        @{ Rid = "osx-x64"; Binary = "DevProjex"; Name = "DevProjex.v$version.osx-x64" },
        @{ Rid = "osx-arm64"; Binary = "DevProjex"; Name = "DevProjex.v$version.osx-arm64" }
    )

    if (Test-Path $releaseDir) {
        Remove-Item -Path $releaseDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null
    New-Item -ItemType Directory -Path $workDir -Force | Out-Null

    Write-Host "Preparing GitHub release artifacts..."
    Write-Host "  Version: $version"
    Write-Host "  Configuration: $configuration"
    Write-Host "  Output: $releaseDir"

    Write-Host "Restoring project..."
    Invoke-ExternalCommand -filePath "dotnet" -arguments @("restore", $projectPath) -failureMessage "dotnet restore failed for GitHub artifacts" -workingDirectory $script:IsolatedRepoRoot

    foreach ($target in $targets) {
        $rid = [string]$target.Rid
        $ridOutDir = Join-Path $workDir $rid

        Write-Host "Publishing $rid..."
        $publishArgs = @(
            "publish", $projectPath,
            "-c", $configuration,
            "-r", $rid,
            "--self-contained", "true",
            "/p:PublishSingleFile=true",
            "/p:IncludeNativeLibrariesForSelfExtract=true",
            "/p:PublishTrimmed=false",
            "/p:DebugType=None",
            "/p:DebugSymbols=false",
            "-o", $ridOutDir
        )

        Invoke-ExternalCommand -filePath "dotnet" -arguments $publishArgs -failureMessage "dotnet publish failed for RID: $rid" -workingDirectory $script:IsolatedRepoRoot

        $sourcePath = Join-Path $ridOutDir ([string]$target.Binary)
        if (-not (Test-Path $sourcePath)) {
            throw "Single-file artifact not found: $sourcePath"
        }

        $destinationPath = Join-Path $releaseDir ([string]$target.Name)
        Copy-Item -Path $sourcePath -Destination $destinationPath -Force
    }

    $shaFile = Join-Path $releaseDir "SHA256SUMS.txt"
    $hashLines = @()
    Get-ChildItem -Path $releaseDir -File |
        Where-Object { $_.Name -ne "SHA256SUMS.txt" } |
        Sort-Object Name |
        ForEach-Object {
            $hash = (Get-FileHash -Algorithm SHA256 -Path $_.FullName).Hash.ToLowerInvariant()
            $hashLines += "$hash *$($_.Name)"
        }

    Set-Content -Path $shaFile -Value $hashLines -Encoding UTF8

    if (Test-Path $workDir) {
        Remove-Item -Path $workDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Host ""
    Write-Host "GitHub release artifacts are ready:"
    Get-ChildItem -Path $releaseDir -File |
        Sort-Object Name |
        ForEach-Object {
            Write-Host "  $($_.Name)"
        }
}

function Get-LatestVisualStudioInstancePath() {
    $vswherePath = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswherePath)) {
        return $null
    }

    try {
        $json = & $vswherePath -latest -format json -requires Microsoft.Component.MSBuild 2>$null
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($json)) {
            return $null
        }

        $instances = $json | ConvertFrom-Json
        if ($null -eq $instances) {
            return $null
        }

        if ($instances -is [array]) {
            if ($instances.Length -eq 0) {
                return $null
            }

            return [string]$instances[0].installationPath
        }

        return [string]$instances.installationPath
    }
    catch {
        return $null
    }
}

function Build-StoreArtifactsInWorkspace(
    [string]$configuration,
    [string]$platform,
    [string]$bundlePlatforms,
    [string]$packageVersion
) {
    $project = Join-Path $script:IsolatedRepoRoot "Packaging\Windows\DevProjex.Store\DevProjex.Store.wapproj"
    $manifestPath = Join-Path $script:IsolatedRepoRoot "Packaging\Windows\DevProjex.Store\Package.appxmanifest"
    $listingCsvPath = Join-Path $script:IsolatedRepoRoot "Packaging\Windows\StoreListing\listing.csv"

    Write-Host "Building MSIX bundle..."
    Write-Host "  Project: Packaging\Windows\DevProjex.Store\DevProjex.Store.wapproj"
    Write-Host "  Configuration: $configuration"
    Write-Host "  Platform: $platform"
    Write-Host "  Bundle platforms: $bundlePlatforms"
    if (-not [string]::IsNullOrWhiteSpace($packageVersion)) {
        Write-Host "  Package version override: $packageVersion"
    }

    Write-Host "Cleaning stale packaging artifacts..."
    $cleanupPaths = @(
        (Join-Path $script:IsolatedRepoRoot "publish\store"),
        (Join-Path $script:IsolatedRepoRoot "Packaging\Windows\DevProjex.Store\publish\store"),
        (Join-Path $script:IsolatedRepoRoot "Packaging\Windows\DevProjex.Store\BundleArtifacts"),
        (Join-Path $script:IsolatedRepoRoot "Packaging\Windows\DevProjex.Store\bin"),
        (Join-Path $script:IsolatedRepoRoot "Packaging\Windows\DevProjex.Store\obj")
    )
    foreach ($cleanupPath in $cleanupPaths) {
        if (Test-Path $cleanupPath) {
            Remove-Item -Path $cleanupPath -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    $vsInstancePath = Get-LatestVisualStudioInstancePath
    $desktopBridgeTargets = @(
        $(if (-not [string]::IsNullOrWhiteSpace($vsInstancePath)) { Join-Path $vsInstancePath "MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets" }),
        "C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets",
        "C:\Program Files\Microsoft Visual Studio\18\Professional\MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets",
        "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets",
        "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets",
        "C:\BuildTools\MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets",
        "C:\Program Files (x86)\MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets",
        "$env:ProgramFiles\Microsoft Visual Studio\18\Community\MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets",
        "$env:ProgramFiles\Microsoft Visual Studio\18\Professional\MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets",
        "$env:ProgramFiles(x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets",
        "$env:ProgramFiles(x86)\Microsoft Visual Studio\2022\Community\MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets",
        "$env:ProgramFiles(x86)\Microsoft Visual Studio\2022\Professional\MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets",
        "$env:ProgramFiles(x86)\Microsoft Visual Studio\2022\Enterprise\MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets",
        "$env:ProgramFiles(x86)\MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets",
        "$env:ProgramFiles(x86)\Windows Kits\10\DesignTime\CommonConfiguration\Neutral\Microsoft.DesktopBridge.targets"
    )

    $desktopBridgeAvailable = $desktopBridgeTargets | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($null -eq $desktopBridgeAvailable) {
        throw "Microsoft.DesktopBridge targets not found. Install Visual Studio Build Tools + Windows SDK."
    }

    $msbuildCandidates = @(
        $(if (-not [string]::IsNullOrWhiteSpace($vsInstancePath)) { Join-Path $vsInstancePath "MSBuild\Current\Bin\MSBuild.exe" }),
        "C:\Program Files\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe",
        "C:\BuildTools18\MSBuild\Current\Bin\MSBuild.exe",
        "C:\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
        "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles(x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles(x86)\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe",
        "$env:ProgramFiles\Microsoft Visual Studio\18\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    $msbuildExe = $msbuildCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($null -eq $msbuildExe) {
        throw "MSBuild.exe not found. Install Visual Studio Build Tools."
    }

    if (-not [string]::IsNullOrWhiteSpace($packageVersion)) {
        if ($packageVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') {
            throw "Invalid PackageVersion '$packageVersion'. Expected format: Major.Minor.Build.Revision"
        }

        [xml]$manifestForUpdate = Get-Content -Path $manifestPath
        $manifestForUpdate.Package.Identity.Version = $packageVersion
        $manifestForUpdate.Save($manifestPath)
        Write-Host "Updated Package.appxmanifest version to $packageVersion"
    }

    $avaloniaProjectPath = Join-Path $script:IsolatedRepoRoot "Apps\Avalonia\DevProjex.Avalonia\DevProjex.Avalonia.csproj"
    Write-Host "Restoring packages..."
    Invoke-ExternalCommand -filePath "dotnet" -arguments @("restore", $avaloniaProjectPath, "/p:Configuration=$configuration") -failureMessage "dotnet restore failed for store build" -workingDirectory $script:IsolatedRepoRoot

    $publishStoreDir = Join-Path $script:IsolatedRepoRoot "publish\store"
    New-Item -ItemType Directory -Force -Path $publishStoreDir | Out-Null
    $buildLogRelative = "publish\store\msix-build.log"

    $bundleMode = if ($bundlePlatforms -like "*|*") { "Always" } else { "Never" }
    $msbuildArgs = @(
        $project,
        "/p:Configuration=$configuration",
        "/p:Platform=$platform",
        "/p:AppxBundle=$bundleMode",
        "/p:AppxBundlePlatforms=$bundlePlatforms",
        "/p:UapAppxPackageBuildMode=StoreUpload",
        "/p:AppxPackageDir=publish\store\",
        "/flp:logfile=$buildLogRelative;verbosity=normal"
    )
    Invoke-ExternalCommand -filePath $msbuildExe -arguments $msbuildArgs -failureMessage "MSIX build failed" -workingDirectory $script:IsolatedRepoRoot

    $objRoot = Join-Path $script:IsolatedRepoRoot "Packaging\Windows\DevProjex.Store\obj"
    if (Test-Path $objRoot) {
        $bundleToken = $bundlePlatforms -replace '\|', '_'
        $bundlePattern = "*_${bundleToken}_bundle_${configuration}.msixbundle"
        $platformMsixPattern = "*_${platform}_${configuration}.msix"

        $bundleCandidate = Get-ChildItem -Path $objRoot -Recurse -File -Filter $bundlePattern -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if ($null -ne $bundleCandidate) {
            Copy-Item -Path $bundleCandidate.FullName -Destination (Join-Path $publishStoreDir $bundleCandidate.Name) -Force
        }

        $platformMsixCandidate = Get-ChildItem -Path $objRoot -Recurse -File -Filter $platformMsixPattern -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if ($null -ne $platformMsixCandidate) {
            Copy-Item -Path $platformMsixCandidate.FullName -Destination (Join-Path $publishStoreDir $platformMsixCandidate.Name) -Force
        }
    }

    [xml]$manifest = Get-Content -Path $manifestPath
    $identity = $manifest.Package.Identity
    $publisherDisplay = $manifest.Package.Properties.PublisherDisplayName

    $stringsRoot = Join-Path $script:IsolatedRepoRoot "Packaging\Windows\DevProjex.Store\Strings"
    $stringsFolders = @()
    if (Test-Path $stringsRoot) {
        $stringsFolders = Get-ChildItem -Path $stringsRoot -Directory | Select-Object -ExpandProperty Name
    }

    $missingResources = New-Object System.Collections.Generic.List[string]
    foreach ($resource in $manifest.Package.Resources.Resource) {
        $language = [string]$resource.Language
        $hasFolder = $false
        foreach ($folder in $stringsFolders) {
            if ($folder.Equals($language, [System.StringComparison]::OrdinalIgnoreCase)) {
                $hasFolder = $true
                break
            }
        }

        if (-not $hasFolder) {
            $missingResources.Add($language)
        }
    }

    if ($missingResources.Count -gt 0) {
        throw "Missing Store language resources in '$stringsRoot' for: $($missingResources -join ', ')"
    }

    if (Test-Path $listingCsvPath) {
        $listingHeaders = (Get-Content -Path $listingCsvPath -First 1) -split ','
        if (($listingHeaders -notcontains 'en-us') -or ($listingHeaders -notcontains 'ru-ru')) {
            throw "Store listing CSV must include 'en-us' and 'ru-ru' columns: $listingCsvPath"
        }
    }
    else {
        Write-Warning "Store listing CSV not found: $listingCsvPath"
    }

    $artifacts = @(
        Get-ChildItem -Path $publishStoreDir -Recurse -File -Include *.msixupload,*.msixbundle,*.msix -ErrorAction SilentlyContinue
        Get-ChildItem -Path (Join-Path $script:IsolatedRepoRoot "Packaging\Windows\DevProjex.Store\publish\store") -Recurse -File -Include *.msixupload,*.msixbundle,*.msix -ErrorAction SilentlyContinue
    ) | Sort-Object LastWriteTime -Descending

    if ($null -eq $artifacts -or $artifacts.Count -eq 0) {
        throw "Store artifact (.msixupload/.msixbundle/.msix) not found in publish\store or Packaging\Windows\DevProjex.Store\publish\store"
    }

    Write-Host ""
    Write-Host "Build output:"
    ($artifacts |
        Sort-Object Name -Unique |
        ForEach-Object {
            Write-Host "  Artifact: $($_.FullName)"
        })
    Write-Host "  Version: $($identity.Version)"
    Write-Host "  Identity.Name: $($identity.Name)"
    Write-Host "  Identity.Publisher: $($identity.Publisher)"
    Write-Host "  PublisherDisplayName: $publisherDisplay"
    Write-Host "  Architectures: $bundlePlatforms"
}

function Get-StoreArtifactPaths() {
    $candidateRoots = @(
        (Join-Path $script:IsolatedRepoRoot "publish\store"),
        (Join-Path $script:IsolatedRepoRoot "Packaging\Windows\DevProjex.Store\publish\store")
    )

    $artifacts = @()
    foreach ($candidateRoot in $candidateRoots) {
        if (-not (Test-Path $candidateRoot)) {
            continue
        }

        $artifacts += Get-ChildItem -Path $candidateRoot -Recurse -File -Include *.msixupload,*.msixbundle,*.msix -ErrorAction SilentlyContinue
    }

    if ($null -eq $artifacts -or $artifacts.Count -eq 0) {
        return @()
    }

    return @(
        $artifacts |
            Sort-Object LastWriteTime -Descending |
            Group-Object Name |
            ForEach-Object { $_.Group | Select-Object -First 1 }
    )
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

    $storeArtifacts = Get-StoreArtifactPaths
    if ($null -eq $storeArtifacts -or $storeArtifacts.Count -eq 0) {
        throw "MS Store artifacts (.msixupload/.msixbundle/.msix) not found in isolated workspace."
    }

    $sourceStoreDir = Join-Path $sourceRoot "publish\store\v$version"
    if (Test-Path $sourceStoreDir) {
        Remove-Item -Path $sourceStoreDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Path $sourceStoreDir -Force | Out-Null

    foreach ($storeArtifact in $storeArtifacts) {
        Copy-Item -Path $storeArtifact.FullName -Destination (Join-Path $sourceStoreDir $storeArtifact.Name) -Force
    }

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
    Build-GitHubArtifactsInWorkspace -version $resolvedVersion -configuration "Release"

    Write-Step "Building Microsoft Store package in isolated workspace"
    Build-StoreArtifactsInWorkspace -configuration "ReleaseStore" -platform "x64" -bundlePlatforms "x64|arm64" -packageVersion $resolvedVersion

    $finalPaths = Publish-ArtifactsToSource -sourceRoot $sourceRoot -version $resolvedVersion

    Write-Step "Done"
    Write-Host "GitHub artifacts: $($finalPaths.GitHub)"
    Write-Host "MS Store artifacts: $($finalPaths.Store)"
}
finally {
    Restore-NuGetEnvironment
    Cleanup-IsolatedWorkspace
}
