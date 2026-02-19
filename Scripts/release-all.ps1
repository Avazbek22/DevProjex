<#
.SYNOPSIS
  Interactive release builder that runs in an isolated temporary workspace.

.DESCRIPTION
  This script keeps your local Rider workspace stable:
    1) asks for a version in 2-4 segment format (4.6 / 4.7.1 / 4.7.1.0)
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

function Try-ParseVersionInput([string]$rawVersion) {
    if ([string]::IsNullOrWhiteSpace($rawVersion)) {
        return $null
    }

    $trimmed = $rawVersion.Trim()
    if ($trimmed -notmatch '^\d+(\.\d+){0,3}$') {
        return $null
    }

    $parts = @($trimmed.Split('.'))
    if ($parts.Count -lt 1 -or $parts.Count -gt 4) {
        return $null
    }

    $displayVersion = ($parts -join '.')
    $storeParts = [System.Collections.Generic.List[string]]::new()
    foreach ($part in $parts) {
        $storeParts.Add($part)
    }
    while ($storeParts.Count -lt 4) {
        $storeParts.Add("0")
    }

    return @{
        DisplayVersion = $displayVersion
        StorePackageVersion = ($storeParts -join '.')
    }
}

function Get-VersionInteractive([string]$currentValue) {
    $value = $currentValue
    while ($true) {
        if ([string]::IsNullOrWhiteSpace($value)) {
            $value = Read-Host "Enter release version (5 / 4.6 / 4.7.1 / 4.7.1.0)"
        }

        $parsedVersion = Try-ParseVersionInput -rawVersion $value
        if ($null -ne $parsedVersion) {
            return $parsedVersion
        }

        Write-Host "Invalid version format. Use 1-4 numeric segments, for example: 5, 4.6, 4.7.1, 4.7.1.0" -ForegroundColor Yellow
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

function Get-AppCertToolPath() {
    $appCertPaths = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\App Certification Kit\appcert.exe",
        "$env:ProgramFiles\Windows Kits\10\App Certification Kit\appcert.exe"
    )

    return $appCertPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
}

function Get-LatestStoreArtifact([object[]]$storeArtifacts, [string]$extension) {
    if ($null -eq $storeArtifacts -or $storeArtifacts.Count -eq 0) {
        return $null
    }

    return $storeArtifacts |
        Where-Object { $_.Extension.Equals($extension, [System.StringComparison]::OrdinalIgnoreCase) } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

function Copy-AppCertLogsToReportDirectory([string]$reportDirectory) {
    $appCertLogRoot = Join-Path $env:LOCALAPPDATA "Microsoft\AppCertKit"
    if (-not (Test-Path $appCertLogRoot)) {
        return
    }

    Get-ChildItem -Path $appCertLogRoot -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 10 |
        ForEach-Object {
            try {
                Copy-Item -Path $_.FullName -Destination $reportDirectory -Force
            }
            catch {
                # Ignore transient locked log files.
            }
        }
}

function Invoke-WackForPackage(
    [string]$appCertPath,
    [string]$packagePath,
    [string]$reportDirectory,
    [string]$label
) {
    $safeLabel = ($label -replace '[^a-zA-Z0-9\-_]', '_').ToLowerInvariant()
    $reportPath = Join-Path $reportDirectory ("wack-" + $safeLabel + ".xml")

    & $appCertPath test -appxpackagepath "$packagePath" -reportoutputpath "$reportPath"
    $exitCode = $LASTEXITCODE

    return @{
        Label = $label
        PackagePath = $packagePath
        ExitCode = $exitCode
        Success = ($exitCode -eq 0)
        ReportPath = $reportPath
    }
}

function Get-InnerPackageFromMsixUpload([string]$msixUploadPath, [string]$tempDirectory) {
    if (Test-Path $tempDirectory) {
        Remove-Item -Path $tempDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Force -Path $tempDirectory | Out-Null

    # .msixupload is a ZIP container; use ZipFile API to avoid extension-based limitations of Expand-Archive.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($msixUploadPath, $tempDirectory)

    $innerBundle = Get-ChildItem -Path $tempDirectory -Recurse -File -Filter *.msixbundle -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -ne $innerBundle) {
        return $innerBundle.FullName
    }

    $innerMsix = Get-ChildItem -Path $tempDirectory -Recurse -File -Filter *.msix -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -ne $innerMsix) {
        return $innerMsix.FullName
    }

    return $null
}

function Invoke-WackValidationInWorkspace() {
    Write-Step "Running WACK validation"

    $storeArtifacts = Get-StoreArtifactPaths
    if ($null -eq $storeArtifacts -or $storeArtifacts.Count -eq 0) {
        throw "WACK validation failed: Store artifacts were not found in isolated workspace."
    }

    $appCert = Get-AppCertToolPath
    if ([string]::IsNullOrWhiteSpace($appCert)) {
        throw "WACK validation cannot start: appcert.exe not found. Install Windows SDK (App Certification Kit)."
    }

    $reportDir = Join-Path $script:IsolatedRepoRoot "publish\store\wack"
    New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
    $tempExtractionDir = Join-Path $reportDir "_temp_msixupload"
    $summaryLines = New-Object System.Collections.Generic.List[string]
    $anySuccess = $false

    Write-Host "WACK tool   : $appCert"

    try {
        $msixUploadCandidate = Get-LatestStoreArtifact -storeArtifacts $storeArtifacts -extension ".msixupload"
        $msixBundleCandidate = Get-LatestStoreArtifact -storeArtifacts $storeArtifacts -extension ".msixbundle"
        $msixCandidate = Get-LatestStoreArtifact -storeArtifacts $storeArtifacts -extension ".msix"

        $msixUploadPassed = $false
        $msixUploadFailed = $false

        if ($null -ne $msixUploadCandidate) {
            Write-Host "WACK stage 1: validating package from .msixupload"
            try {
                $innerPackagePath = Get-InnerPackageFromMsixUpload -msixUploadPath $msixUploadCandidate.FullName -tempDirectory $tempExtractionDir
                if ([string]::IsNullOrWhiteSpace($innerPackagePath)) {
                    throw "No .msixbundle/.msix was found inside .msixupload."
                }

                $uploadResult = Invoke-WackForPackage -appCertPath $appCert -packagePath $innerPackagePath -reportDirectory $reportDir -label "msixupload-inner"
                if ($uploadResult.Success) {
                    $summaryLines.Add("MSIXUPLOAD: PASS ($($uploadResult.PackagePath))")
                    $msixUploadPassed = $true
                    $anySuccess = $true
                }
                else {
                    $summaryLines.Add("MSIXUPLOAD: FAIL (exit code: $($uploadResult.ExitCode))")
                    $msixUploadFailed = $true
                }
            }
            catch {
                $summaryLines.Add("MSIXUPLOAD: FAIL ($($_.Exception.Message))")
                $msixUploadFailed = $true
            }
        }
        else {
            $summaryLines.Add("MSIXUPLOAD: SKIPPED (artifact not found)")
            $msixUploadFailed = $true
        }

        if ($msixUploadFailed -and -not $msixUploadPassed) {
            Write-Host "WACK stage 2: fallback to .msixbundle/.msix artifacts"

            if ($null -ne $msixBundleCandidate) {
                $bundleResult = Invoke-WackForPackage -appCertPath $appCert -packagePath $msixBundleCandidate.FullName -reportDirectory $reportDir -label "msixbundle"
                if ($bundleResult.Success) {
                    $summaryLines.Add("MSIXBUNDLE: PASS ($($bundleResult.PackagePath))")
                    $anySuccess = $true
                }
                else {
                    $summaryLines.Add("MSIXBUNDLE: FAIL (exit code: $($bundleResult.ExitCode))")
                }
            }
            else {
                $summaryLines.Add("MSIXBUNDLE: SKIPPED (artifact not found)")
            }

            if ($null -ne $msixCandidate) {
                $msixResult = Invoke-WackForPackage -appCertPath $appCert -packagePath $msixCandidate.FullName -reportDirectory $reportDir -label "msix"
                if ($msixResult.Success) {
                    $summaryLines.Add("MSIX: PASS ($($msixResult.PackagePath))")
                    $anySuccess = $true
                }
                else {
                    $summaryLines.Add("MSIX: FAIL (exit code: $($msixResult.ExitCode))")
                }
            }
            else {
                $summaryLines.Add("MSIX: SKIPPED (artifact not found)")
            }
        }
        else {
            $summaryLines.Add("MSIXBUNDLE: SKIPPED (not needed, msixupload passed)")
            $summaryLines.Add("MSIX: SKIPPED (not needed, msixupload passed)")
        }
    }
    finally {
        if (Test-Path $tempExtractionDir) {
            Remove-Item -Path $tempExtractionDir -Recurse -Force -ErrorAction SilentlyContinue
        }
        Copy-AppCertLogsToReportDirectory -reportDirectory $reportDir
    }

    $summaryPath = Join-Path $reportDir "summary.txt"
    Set-Content -Path $summaryPath -Value $summaryLines -Encoding UTF8

    Write-Host ""
    Write-Host "WACK summary:"
    foreach ($summaryLine in $summaryLines) {
        Write-Host "  $summaryLine"
    }
    Write-Host "  Report folder: $reportDir"

    if (-not $anySuccess) {
        throw "WACK validation failed. See summary and reports in: $reportDir"
    }
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

    $isolatedWackDir = Join-Path $script:IsolatedRepoRoot "publish\store\wack"
    if (Test-Path $isolatedWackDir) {
        Copy-Item -Path $isolatedWackDir -Destination (Join-Path $sourceStoreDir "wack") -Recurse -Force
    }

    return @{
        GitHub = $sourceGitHubDir
        Store = $sourceStoreDir
    }
}

function Start-DeferredCleanup([string]$targetPath) {
    $cleanupScriptPath = Join-Path $env:TEMP ("devprojex-cleanup-" + [Guid]::NewGuid().ToString("N") + ".ps1")
    $scriptContent = @'
param(
    [string]$TargetPath,
    [string]$SelfScriptPath
)

for ($attempt = 1; $attempt -le 120; $attempt++) {
    if (-not (Test-Path $TargetPath)) {
        break
    }

    try {
        Remove-Item -Path $TargetPath -Recurse -Force -ErrorAction Stop
    }
    catch {
        # Keep retrying; file handles can be released with delay after build tooling exits.
    }

    if (-not (Test-Path $TargetPath)) {
        break
    }

    Start-Sleep -Seconds 3
}

try {
    Remove-Item -Path $SelfScriptPath -Force -ErrorAction SilentlyContinue
}
catch {
    # Ignore cleanup script self-delete failures.
}
'@

    Set-Content -Path $cleanupScriptPath -Value $scriptContent -Encoding UTF8

    Start-Process -FilePath "powershell.exe" -WindowStyle Hidden -ArgumentList @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $cleanupScriptPath,
        "-TargetPath", $targetPath,
        "-SelfScriptPath", $cleanupScriptPath
    ) | Out-Null

    return $cleanupScriptPath
}

function Cleanup-IsolatedWorkspace() {
    if ([string]::IsNullOrWhiteSpace($script:IsolatedWorkspaceRoot)) {
        return
    }

    if (-not (Test-Path $script:IsolatedWorkspaceRoot)) {
        return
    }

    # Remove read-only flag from files/folders in case package tools produced protected artifacts.
    try {
        Get-ChildItem -Path $script:IsolatedWorkspaceRoot -Recurse -Force -ErrorAction SilentlyContinue |
            ForEach-Object {
                try {
                    if (($_.Attributes -band [System.IO.FileAttributes]::ReadOnly) -ne 0) {
                        $_.Attributes = ($_.Attributes -bxor [System.IO.FileAttributes]::ReadOnly)
                    }
                }
                catch {
                    # Ignore per-file attribute errors; delete retries below can still succeed.
                }
            }
    }
    catch {
        # Ignore attribute pass issues and continue with deletion retries.
    }

    $maxAttempts = 20
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        try {
            Remove-Item -Path $script:IsolatedWorkspaceRoot -Recurse -Force -ErrorAction Stop
            if (-not (Test-Path $script:IsolatedWorkspaceRoot)) {
                return
            }
        }
        catch {
            # Retry because antivirus/indexer/MSBuild can temporarily hold handles.
        }

        Start-Sleep -Milliseconds (500 * $attempt)
    }

    # Fallback to cmd rmdir for stubborn directory trees.
    cmd /c "rmdir /s /q ""$script:IsolatedWorkspaceRoot""" | Out-Null
    if (Test-Path $script:IsolatedWorkspaceRoot) {
        $deferredScript = Start-DeferredCleanup -targetPath $script:IsolatedWorkspaceRoot
        Write-Warning "Immediate cleanup failed. Scheduled deferred cleanup for: $script:IsolatedWorkspaceRoot"
        Write-Warning "Deferred cleanup helper: $deferredScript"
    }
}

Ensure-DotnetAvailable
$versionInfo = Get-VersionInteractive -currentValue $Version
$resolvedVersion = [string]$versionInfo.DisplayVersion
$storePackageVersion = [string]$versionInfo.StorePackageVersion
$sourceRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$finalPaths = $null

Write-Step "Release plan"
Write-Host "Version: $resolvedVersion"
Write-Host "Store package version: $storePackageVersion"
Write-Host "Build mode  : isolated workspace (local source tree untouched)"
Write-Host "GitHub build: Release (single-file, self-contained)"
Write-Host "Store build : ReleaseStore (.msixupload, x64|arm64)"
Write-Host "Store check : WACK (must pass before artifacts are published)"
Write-Host "Store listing CSV: not modified"

try {
    Create-IsolatedWorkspace -sourceRoot $sourceRoot
    Configure-IsolatedNuGetCache

    Write-Step "Building GitHub artifacts in isolated workspace"
    Build-GitHubArtifactsInWorkspace -version $resolvedVersion -configuration "Release"

    Write-Step "Building Microsoft Store package in isolated workspace"
    Build-StoreArtifactsInWorkspace -configuration "ReleaseStore" -platform "x64" -bundlePlatforms "x64|arm64" -packageVersion $storePackageVersion

    Invoke-WackValidationInWorkspace

    $finalPaths = Publish-ArtifactsToSource -sourceRoot $sourceRoot -version $resolvedVersion

    Write-Step "Done"
    Write-Host "GitHub artifacts: $($finalPaths.GitHub)"
    Write-Host "MS Store artifacts: $($finalPaths.Store)"
}
finally {
    Restore-NuGetEnvironment
    Cleanup-IsolatedWorkspace
}
