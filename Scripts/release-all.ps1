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
    [string]$Version = "",
    [switch]$ValidateConfigOnly,
    [switch]$SmokeStoreBuildOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "release-helpers.ps1")

$script:IsolatedWorkspaceRoot = $null
$script:IsolatedRepoRoot = $null
$script:OriginalNuGetPackages = [Environment]::GetEnvironmentVariable("NUGET_PACKAGES", "Process")
$script:OriginalNuGetHttpCachePath = [Environment]::GetEnvironmentVariable("NUGET_HTTP_CACHE_PATH", "Process")
$script:OriginalNuGetPluginsCachePath = [Environment]::GetEnvironmentVariable("NUGET_PLUGINS_CACHE_PATH", "Process")
$script:StoreApplicationId = "App"
$script:StoreExecutionAliasName = "devprojex.exe"
$script:StoreUiPackageExecutable = "DevProjex.Avalonia\DevProjex.exe"
$script:StoreFullTrustEntryPoint = "Windows.FullTrustApplication"

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

function Get-MsBuildPath() {
    $vswherePath = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswherePath) {
        try {
            $installationPath = & $vswherePath -latest -products * -requires Microsoft.Component.MSBuild -property installationPath 2>$null
            if ($LASTEXITCODE -eq 0 -and -not [string]::IsNullOrWhiteSpace($installationPath)) {
                $candidate = Join-Path $installationPath.Trim() "MSBuild\Current\Bin\MSBuild.exe"
                if (Test-Path $candidate) {
                    return $candidate
                }
            }
        }
        catch {
            # Fall back to PATH lookup below.
        }
    }

    $command = Get-Command msbuild.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    throw "MSBuild.exe not found. Install Visual Studio Build Tools with Desktop Bridge support."
}

function Get-InstalledWindowsUapSdkVersions() {
    $uapRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\DesignTime\CommonConfiguration\Neutral\UAP"
    if (-not (Test-Path $uapRoot)) {
        return @()
    }

    return Get-ChildItem -Path $uapRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.FullName "UAP.props") } |
        ForEach-Object {
            try {
                [pscustomobject]@{
                    Name = $_.Name
                    Version = [version]$_.Name
                }
            }
            catch {
                $null
            }
        } |
        Where-Object { $null -ne $_ } |
        Sort-Object Version -Descending
}

function Resolve-StoreTargetPlatformVersion([string]$projectPath) {
    [xml]$projectXml = Get-Content -Path $projectPath
    $xmlNamespace = New-Object System.Xml.XmlNamespaceManager($projectXml.NameTable)
    $xmlNamespace.AddNamespace("msb", "http://schemas.microsoft.com/developer/msbuild/2003")

    $requestedNode = $projectXml.SelectSingleNode("/msb:Project/msb:PropertyGroup/msb:TargetPlatformVersion", $xmlNamespace)
    $minNode = $projectXml.SelectSingleNode("/msb:Project/msb:PropertyGroup/msb:TargetPlatformMinVersion", $xmlNamespace)

    if ($null -eq $requestedNode -or [string]::IsNullOrWhiteSpace($requestedNode.InnerText)) {
        throw "TargetPlatformVersion was not found in $projectPath"
    }

    $requestedVersion = [version]$requestedNode.InnerText.Trim()
    $requestedVersionText = $requestedVersion.ToString()
    $minVersion = if ($null -ne $minNode -and -not [string]::IsNullOrWhiteSpace($minNode.InnerText)) {
        [version]$minNode.InnerText.Trim()
    }
    else {
        [version]"0.0.0.0"
    }

    $installedVersions = @(Get-InstalledWindowsUapSdkVersions)
    if ($installedVersions.Count -eq 0) {
        throw "No Windows UAP SDKs with UAP.props were found under Windows Kits."
    }

    $requestedInstalled = $installedVersions | Where-Object { $_.Version -eq $requestedVersion } | Select-Object -First 1
    if ($null -ne $requestedInstalled) {
        return [pscustomobject]@{
            ResolvedVersion = $requestedVersionText
            RequestedVersion = $requestedVersionText
            UsedFallback = $false
        }
    }

    $fallbackVersion = $installedVersions | Where-Object { $_.Version -ge $minVersion } | Select-Object -First 1
    if ($null -eq $fallbackVersion) {
        $availableVersionList = ($installedVersions | ForEach-Object { $_.Name }) -join ", "
        throw "Requested UAP SDK '$requestedVersionText' is not installed, and no installed UAP SDK satisfies TargetPlatformMinVersion '$($minVersion.ToString())'. Available SDKs: $availableVersionList"
    }

    return [pscustomobject]@{
        ResolvedVersion = [string]$fallbackVersion.Name
        RequestedVersion = $requestedVersionText
        UsedFallback = $true
    }
}

function Set-StoreProjectTargetPlatformVersion(
    [string]$projectPath,
    [string]$targetPlatformVersion
) {
    [xml]$projectXml = Get-Content -Path $projectPath
    $xmlNamespace = New-Object System.Xml.XmlNamespaceManager($projectXml.NameTable)
    $xmlNamespace.AddNamespace("msb", "http://schemas.microsoft.com/developer/msbuild/2003")

    $targetVersionNode = $projectXml.SelectSingleNode("/msb:Project/msb:PropertyGroup/msb:TargetPlatformVersion", $xmlNamespace)
    if ($null -eq $targetVersionNode) {
        throw "TargetPlatformVersion was not found in $projectPath"
    }

    $currentVersion = $targetVersionNode.InnerText.Trim()
    if ($currentVersion -eq $targetPlatformVersion) {
        return $false
    }

    $targetVersionNode.InnerText = $targetPlatformVersion
    $projectXml.Save($projectPath)
    return $true
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
            $prompt = "Enter release version"
            if (-not [string]::IsNullOrWhiteSpace($currentValue)) {
                $prompt += " [$currentValue]"
            }

            $prompt += " (1-4 numeric segments)"
            $value = Read-Host $prompt
            if ([string]::IsNullOrWhiteSpace($value)) {
                $value = $currentValue
            }
        }

        $parsedVersion = Try-ParseVersionInput -rawVersion $value
        if ($null -ne $parsedVersion) {
            return $parsedVersion
        }

        Write-Host "Invalid version format. Use 1-4 numeric segments, for example: 5, 4.6, 4.7.1, 4.7.1.0" -ForegroundColor Yellow
        $value = ""
    }
}

function Assert-Condition([bool]$condition, [string]$message) {
    if (-not $condition) {
        throw $message
    }
}

function Assert-VersionFormat([string]$value, [string]$name, [string]$pattern) {
    if ($value -notmatch $pattern) {
        throw "$name has invalid format '$value'."
    }
}

function Get-XmlChildElementsByLocalName(
    [System.Xml.XmlNode]$node,
    [string]$localName
) {
    return @(
        $node.ChildNodes |
            Where-Object { $_.NodeType -eq [System.Xml.XmlNodeType]::Element -and $_.LocalName -eq $localName }
    )
}

function Get-XmlDescendantElementsByLocalName(
    [System.Xml.XmlNode]$node,
    [string]$localName
) {
    return @($node.SelectNodes(".//*[local-name()='$localName']"))
}

function Assert-StoreExecutionAliasManifestContract(
    [string]$manifestPath,
    [switch]$RequirePackagedApplicationExecutable,
    [string]$packageRoot = ""
) {
    Assert-Condition (Test-Path $manifestPath) "Store manifest was not found: $manifestPath"

    [xml]$manifest = Get-Content -Path $manifestPath
    $applications = @($manifest.SelectNodes("//*[local-name()='Application']"))
    Assert-Condition ($applications.Count -eq 1) "Store package must expose exactly one Application. Found: $($applications.Count)."

    $application = $applications[0]
    $applicationId = [string]$application.GetAttribute("Id")
    $applicationExecutable = [string]$application.GetAttribute("Executable")
    $applicationEntryPoint = [string]$application.GetAttribute("EntryPoint")

    Assert-Condition ($applicationId -eq $script:StoreApplicationId) "Store Application Id must stay '$script:StoreApplicationId'. Found: '$applicationId'."
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($applicationExecutable)) "Store Application executable is missing."
    Assert-Condition ($applicationExecutable.IndexOf("Cli", [System.StringComparison]::OrdinalIgnoreCase) -lt 0) "Store Application must point to the UI executable, not a separate CLI executable: '$applicationExecutable'."
    Assert-Condition ($applicationEntryPoint -eq $script:StoreFullTrustEntryPoint) "Store Application EntryPoint must stay '$script:StoreFullTrustEntryPoint'. Found: '$applicationEntryPoint'."

    if ($RequirePackagedApplicationExecutable) {
        Assert-Condition ($applicationExecutable -eq $script:StoreUiPackageExecutable) "Packaged Store Application executable must be '$script:StoreUiPackageExecutable'. Found: '$applicationExecutable'."
    }

    $extensionsContainer = Get-XmlChildElementsByLocalName -node $application -localName "Extensions" | Select-Object -First 1
    Assert-Condition ($null -ne $extensionsContainer) "Store Application must declare an Extensions element for AppExecutionAlias."

    $aliasExtensions = @(
        Get-XmlChildElementsByLocalName -node $extensionsContainer -localName "Extension" |
            Where-Object { [string]$_.GetAttribute("Category") -eq "windows.appExecutionAlias" }
    )
    Assert-Condition ($aliasExtensions.Count -eq 1) "Store Application must declare exactly one windows.appExecutionAlias extension. Found: $($aliasExtensions.Count)."

    $aliasExtension = $aliasExtensions[0]
    $aliasExecutable = [string]$aliasExtension.GetAttribute("Executable")
    $aliasEntryPoint = [string]$aliasExtension.GetAttribute("EntryPoint")

    Assert-Condition ($aliasExecutable -eq $script:StoreUiPackageExecutable) "Store execution alias must start the packaged UI executable '$script:StoreUiPackageExecutable'. Found: '$aliasExecutable'."
    Assert-Condition ($aliasEntryPoint -eq $script:StoreFullTrustEntryPoint) "Store execution alias EntryPoint must stay '$script:StoreFullTrustEntryPoint'. Found: '$aliasEntryPoint'."
    Assert-Condition ($aliasExecutable.IndexOf("Cli", [System.StringComparison]::OrdinalIgnoreCase) -lt 0) "Store execution alias must not point to a separate CLI executable: '$aliasExecutable'."

    $appExecutionAliases = @(Get-XmlDescendantElementsByLocalName -node $aliasExtension -localName "AppExecutionAlias")
    Assert-Condition ($appExecutionAliases.Count -eq 1) "Store execution alias extension must contain exactly one AppExecutionAlias element. Found: $($appExecutionAliases.Count)."

    $executionAliases = @(Get-XmlDescendantElementsByLocalName -node $appExecutionAliases[0] -localName "ExecutionAlias")
    Assert-Condition ($executionAliases.Count -eq 1) "Store AppExecutionAlias must contain exactly one ExecutionAlias element. Found: $($executionAliases.Count)."

    $aliasName = [string]$executionAliases[0].GetAttribute("Alias")
    Assert-Condition ($aliasName -eq $script:StoreExecutionAliasName) "Store execution alias must stay '$script:StoreExecutionAliasName'. Found: '$aliasName'."
    Assert-Condition ($aliasName.EndsWith(".exe", [System.StringComparison]::Ordinal)) "Store execution alias must end with '.exe'. Found: '$aliasName'."
    Assert-Condition ($aliasName -eq $aliasName.ToLowerInvariant()) "Store execution alias must stay lowercase for a stable terminal contract. Found: '$aliasName'."
    Assert-Condition (-not ($aliasName -match '[<>:"/\\|?*]')) "Store execution alias contains a character that Windows aliases do not allow: '$aliasName'."

    if (-not [string]::IsNullOrWhiteSpace($packageRoot)) {
        $platformExecutablePath = $script:StoreUiPackageExecutable -replace '[\\/]', [System.IO.Path]::DirectorySeparatorChar
        $packagedExecutablePath = Join-Path $packageRoot $platformExecutablePath
        Assert-Condition (Test-Path $packagedExecutablePath) "Store package manifest points to '$script:StoreUiPackageExecutable', but that file was not found in the package."
    }
}

function Expand-ZipContainer(
    [string]$archivePath,
    [string]$destinationPath
) {
    if (Test-Path $destinationPath) {
        Remove-Item -Path $destinationPath -Recurse -Force -ErrorAction SilentlyContinue
    }

    New-Item -ItemType Directory -Force -Path $destinationPath | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $destinationPath)
}

function Assert-StoreArtifactContainsExecutionAliasCore(
    [string]$artifactPath,
    [string]$tempDirectory
) {
    $extension = [System.IO.Path]::GetExtension($artifactPath)
    $safeName = ([System.IO.Path]::GetFileNameWithoutExtension($artifactPath) -replace '[^a-zA-Z0-9._-]', '_')
    $extractionPath = Join-Path $tempDirectory ($safeName + "-" + [Guid]::NewGuid().ToString("N"))

    Expand-ZipContainer -archivePath $artifactPath -destinationPath $extractionPath

    if ($extension.Equals(".msix", [System.StringComparison]::OrdinalIgnoreCase)) {
        $manifestPath = Join-Path $extractionPath "AppxManifest.xml"
        Assert-Condition (Test-Path $manifestPath) "MSIX package '$artifactPath' does not contain AppxManifest.xml."

        [xml]$packageManifest = Get-Content -Path $manifestPath
        $applications = @($packageManifest.SelectNodes("//*[local-name()='Application']"))
        if ($applications.Count -eq 0) {
            Write-Host "Skipping Store resource package without Application element: $artifactPath"
            return 0
        }

        Assert-StoreExecutionAliasManifestContract -manifestPath $manifestPath -RequirePackagedApplicationExecutable -packageRoot $extractionPath
        return 1
    }

    if ($extension.Equals(".msixbundle", [System.StringComparison]::OrdinalIgnoreCase)) {
        $msixPackages = @(Get-ChildItem -Path $extractionPath -Recurse -File -Filter *.msix -ErrorAction SilentlyContinue)
        Assert-Condition ($msixPackages.Count -gt 0) "MSIX bundle '$artifactPath' does not contain platform .msix packages."

        $validatedPackageCount = 0
        foreach ($msixPackage in $msixPackages) {
            $validatedPackageCount += Assert-StoreArtifactContainsExecutionAliasCore -artifactPath $msixPackage.FullName -tempDirectory $tempDirectory
        }

        return $validatedPackageCount
    }

    if ($extension.Equals(".msixupload", [System.StringComparison]::OrdinalIgnoreCase)) {
        $innerPackages = @(
            Get-ChildItem -Path $extractionPath -Recurse -File -Include *.msixbundle,*.msix -ErrorAction SilentlyContinue |
                Sort-Object FullName
        )
        Assert-Condition ($innerPackages.Count -gt 0) "MSIX upload '$artifactPath' does not contain a .msixbundle or .msix package."

        $validatedPackageCount = 0
        foreach ($innerPackage in $innerPackages) {
            $validatedPackageCount += Assert-StoreArtifactContainsExecutionAliasCore -artifactPath $innerPackage.FullName -tempDirectory $tempDirectory
        }

        return $validatedPackageCount
    }

    throw "Unsupported Store artifact type for execution alias validation: $artifactPath"
}

function Assert-StoreArtifactsContainExecutionAlias(
    [object[]]$artifacts,
    [string]$tempDirectory
) {
    Assert-Condition ($null -ne $artifacts -and $artifacts.Count -gt 0) "No Store artifacts were provided for execution alias validation."

    if (Test-Path $tempDirectory) {
        Remove-Item -Path $tempDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Force -Path $tempDirectory | Out-Null

    try {
        $validatedPackageCount = 0
        foreach ($artifact in $artifacts) {
            $artifactPath = if ($artifact -is [System.IO.FileInfo]) {
                [string]$artifact.FullName
            }
            else {
                [string]$artifact
            }

            Write-Host "Validating Store execution alias in artifact: $artifactPath"
            $validatedPackageCount += Assert-StoreArtifactContainsExecutionAliasCore -artifactPath $artifactPath -tempDirectory $tempDirectory
        }

        Assert-Condition ($validatedPackageCount -gt 0) "Store artifact validation did not inspect any platform MSIX package."
        Write-Host "Store execution alias validated in $validatedPackageCount platform package(s)."
    }
    finally {
        if (Test-Path $tempDirectory) {
            Remove-Item -Path $tempDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Get-CsvHeaderColumns([string]$path) {
    $headerLine = Get-Content -Path $path -First 1
    if ([string]::IsNullOrWhiteSpace($headerLine)) {
        return @()
    }

    # Store listing CSVs may contain quoted headers and a UTF-8 BOM.
    # Normalize them so validation stays consistent on Windows and Linux.
    return @(
        $headerLine -split ',' | ForEach-Object {
            $_.Trim().Trim('"').Trim([char]0xFEFF)
        }
    )
}

function Invoke-StoreListingValidationScript([string]$repoRoot) {
    $validationScriptPath = Join-Path $repoRoot "Scripts\validate-store-listing.ps1"
    Assert-Condition (Test-Path $validationScriptPath) "Store listing validation script was not found: $validationScriptPath"

    # Keep release validation and local script execution on the exact same code path.
    # If Partner Center quirks change, we want one authoritative script to update.
    & $validationScriptPath -RepositoryRoot $repoRoot
}

function Invoke-ReleaseConfigValidation([string]$repoRoot) {
    $versionInfo = Get-DefaultReleaseVersionInfo -repoRoot $repoRoot

    Assert-VersionFormat -value ([string]$versionInfo.DisplayVersion) -name "DevProjexVersion" -pattern '^\d+(\.\d+){0,3}$'
    Assert-VersionFormat -value ([string]$versionInfo.AssemblyVersion) -name "DevProjexAssemblyVersion" -pattern '^\d+\.\d+\.\d+\.\d+$'
    Assert-VersionFormat -value ([string]$versionInfo.FileVersion) -name "DevProjexFileVersion" -pattern '^\d+\.\d+\.\d+\.\d+$'
    Assert-VersionFormat -value ([string]$versionInfo.StorePackageVersion) -name "DevProjexStorePackageVersion" -pattern '^\d+\.\d+\.\d+\.\d+$'
    Assert-Condition ($versionInfo.AssemblyVersion -eq $versionInfo.FileVersion) "AssemblyVersion and FileVersion must stay identical."
    Assert-Condition ($versionInfo.AssemblyVersion -eq $versionInfo.StorePackageVersion) "AssemblyVersion and StorePackageVersion must stay identical."

    $appProjectPath = Join-Path $repoRoot "Apps\Avalonia\DevProjex.Avalonia\DevProjex.Avalonia.csproj"
    $appProjectContent = Get-Content -Path $appProjectPath -Raw
    foreach ($versionPropertyName in @("Version", "AssemblyVersion", "FileVersion", "InformationalVersion")) {
        Assert-Condition (-not ($appProjectContent -match "<$versionPropertyName>")) "App project must inherit $versionPropertyName from Directory.Build.props: $appProjectPath"
    }

    $manifestPath = Join-Path $repoRoot "Packaging\Windows\DevProjex.Store\Package.appxmanifest"
    [xml]$manifest = Get-Content -Path $manifestPath
    $manifestVersion = [string]$manifest.Package.Identity.Version
    Assert-Condition ($manifestVersion -eq $versionInfo.StorePackageVersion) "Store manifest version '$manifestVersion' does not match DevProjexStorePackageVersion '$($versionInfo.StorePackageVersion)'."
    Assert-StoreExecutionAliasManifestContract -manifestPath $manifestPath

    $wapprojPath = Join-Path $repoRoot "Packaging\Windows\DevProjex.Store\DevProjex.Store.wapproj"
    [xml]$wapproj = Get-Content -Path $wapprojPath
    $xmlNamespace = New-Object System.Xml.XmlNamespaceManager($wapproj.NameTable)
    $xmlNamespace.AddNamespace("msb", "http://schemas.microsoft.com/developer/msbuild/2003")
    $bundlePlatformsNode = $wapproj.SelectSingleNode("/msb:Project/msb:PropertyGroup/msb:AppxBundlePlatforms", $xmlNamespace)
    Assert-Condition ($null -ne $bundlePlatformsNode) "AppxBundlePlatforms was not found in $wapprojPath"
    Assert-Condition ([string]$bundlePlatformsNode.InnerText -eq "x64|arm64") "AppxBundlePlatforms must stay x64|arm64."

    $listingCsvPath = Join-Path $repoRoot "Packaging\Windows\StoreListing\listing.csv"
    $listingHeaders = Get-CsvHeaderColumns -path $listingCsvPath
    Assert-Condition (($listingHeaders -contains 'en-us') -and ($listingHeaders -contains 'ru-ru')) "Store listing CSV must include en-us and ru-ru columns."
    Invoke-StoreListingValidationScript -repoRoot $repoRoot

    $releaseAllPath = Join-Path $repoRoot "Scripts\release-all.ps1"
    $releaseAllContent = Get-Content -Path $releaseAllPath -Raw
    Assert-Condition ($releaseAllContent.Contains('release-helpers.ps1')) "release-all.ps1 must use release-helpers.ps1 for shared release metadata."

    $wingetScriptPath = Join-Path $repoRoot "Scripts\winget-update.ps1"
    $wingetScriptContent = Get-Content -Path $wingetScriptPath -Raw
    Assert-Condition ($wingetScriptContent.Contains('release-helpers.ps1')) "winget-update.ps1 must use release-helpers.ps1 for shared release metadata."
    Assert-Condition ($wingetScriptContent.Contains('Get-DefaultReleaseVersionInfo')) "winget-update.ps1 must load default release version info from release-helpers.ps1."
    Assert-Condition ($wingetScriptContent.Contains('defaultValue ([string]$defaultReleaseVersionInfo.DisplayVersion)')) "winget-update.ps1 must use shared default display version instead of a hardcoded prompt value."

    $macReadmePath = Join-Path $repoRoot "Packaging\MacOS\README.md"
    $macReadmeContent = Get-Content -Path $macReadmePath -Raw
    Assert-Condition ($macReadmeContent.Contains('YOUR_RELEASE_VERSION')) "Packaging/MacOS/README.md must use a release-version placeholder instead of a stale hardcoded example."

    Write-Host "Release configuration is consistent."
    Write-Host "  DisplayVersion      : $($versionInfo.DisplayVersion)"
    Write-Host "  StorePackageVersion : $($versionInfo.StorePackageVersion)"
    Write-Host "  AppxBundlePlatforms : x64|arm64"
}

function Invoke-StoreSmokeBuild([string]$repoRoot, [string]$versionOverride) {
    $defaultVersionInfo = Get-DefaultReleaseVersionInfo -repoRoot $repoRoot
    $resolvedDisplayVersion = if ([string]::IsNullOrWhiteSpace($versionOverride)) { [string]$defaultVersionInfo.DisplayVersion } else { $versionOverride.Trim() }
    if ($resolvedDisplayVersion -notmatch '^\d+(\.\d+){0,3}$') {
        throw "Invalid version '$resolvedDisplayVersion'."
    }

    $storePackageVersion = if ($resolvedDisplayVersion -match '^\d+\.\d+\.\d+\.\d+$') {
        $resolvedDisplayVersion
    }
    else {
        $versionParts = [System.Collections.Generic.List[string]]::new()
        foreach ($part in $resolvedDisplayVersion.Split('.')) {
            $versionParts.Add($part)
        }

        while ($versionParts.Count -lt 4) {
            $versionParts.Add("0")
        }

        $versionParts -join '.'
    }

    $versionProperties = Get-BuildVersionProperties -displayVersion $resolvedDisplayVersion -storePackageVersion $storePackageVersion
    $projectPath = Join-Path $repoRoot "Packaging\Windows\DevProjex.Store\DevProjex.Store.wapproj"
    $appProjectPath = Join-Path $repoRoot "Apps\Avalonia\DevProjex.Avalonia\DevProjex.Avalonia.csproj"
    $msbuildPath = Get-MsBuildPath
    $targetPlatformResolution = Resolve-StoreTargetPlatformVersion -projectPath $projectPath
    $outputRoot = Join-Path $env:TEMP ("devprojex-store-smoke-" + [Guid]::NewGuid().ToString("N"))
    $originalProjectContent = $null
    $projectPatched = $false

    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null

    try {
        if ($targetPlatformResolution.UsedFallback) {
            Write-Warning "Requested UAP SDK $($targetPlatformResolution.RequestedVersion) is not installed. Using $($targetPlatformResolution.ResolvedVersion) for Store smoke build."
        }
        else {
            Write-Host "Using requested UAP SDK $($targetPlatformResolution.ResolvedVersion) for Store smoke build."
        }

        $originalProjectContent = Get-Content -Path $projectPath -Raw
        $projectPatched = Set-StoreProjectTargetPlatformVersion -projectPath $projectPath -targetPlatformVersion $targetPlatformResolution.ResolvedVersion

        Invoke-ExternalCommand -filePath "dotnet" -arguments (@("restore", $appProjectPath, "/p:Configuration=ReleaseStore") + $versionProperties) -failureMessage "dotnet restore failed for store smoke build" -workingDirectory $repoRoot

        $msbuildArgs = @(
            $projectPath,
            "/p:Configuration=ReleaseStore",
            "/p:Platform=x64",
            "/p:AppxBundle=Always",
            "/p:AppxBundlePlatforms=x64|arm64",
            "/p:UapAppxPackageBuildMode=StoreUpload",
            "/p:GenerateAppInstallerFile=false",
            "/p:AppxPackageDir=$outputRoot\"
        ) + $versionProperties

        Invoke-ExternalCommand -filePath $msbuildPath -arguments $msbuildArgs -failureMessage "MS Store smoke build failed" -workingDirectory $repoRoot

        $artifacts = Get-ChildItem -Path $outputRoot -Recurse -File -Include *.msixupload,*.msixbundle,*.msix -ErrorAction SilentlyContinue
        if ($null -eq $artifacts -or $artifacts.Count -eq 0) {
            throw "Store smoke build did not produce any MSIX artifacts."
        }

        Assert-StoreArtifactsContainExecutionAlias -artifacts @($artifacts) -tempDirectory (Join-Path $outputRoot "_execution_alias_validation")

        Write-Host "Store smoke build succeeded."
        $artifacts | Sort-Object Name | ForEach-Object { Write-Host "  $($_.FullName)" }
    }
    finally {
        if ($projectPatched -and $null -ne $originalProjectContent) {
            Set-Content -Path $projectPath -Value $originalProjectContent -Encoding UTF8
        }

        if (Test-Path $outputRoot) {
            Remove-Item -Path $outputRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Assert-WindowsArtifactVersion(
    [string]$artifactPath,
    [string]$displayVersion,
    [string]$storePackageVersion
) {
    $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($artifactPath)
    if ([string]::IsNullOrWhiteSpace($versionInfo.FileVersion)) {
        throw "FileVersion is missing for '$artifactPath'."
    }

    if (-not $versionInfo.FileVersion.StartsWith($storePackageVersion, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "FileVersion '$($versionInfo.FileVersion)' does not match expected store package version '$storePackageVersion' for '$artifactPath'."
    }

    if ([string]::IsNullOrWhiteSpace($versionInfo.ProductVersion)) {
        throw "ProductVersion is missing for '$artifactPath'."
    }

    if (-not $versionInfo.ProductVersion.StartsWith($displayVersion, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "ProductVersion '$($versionInfo.ProductVersion)' does not match expected display version '$displayVersion' for '$artifactPath'."
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

    $defaultReleaseVersionInfo = Get-DefaultReleaseVersionInfo -repoRoot $script:IsolatedRepoRoot
    $versionProperties = Get-BuildVersionProperties -displayVersion $version -storePackageVersion ([string]$defaultReleaseVersionInfo.StorePackageVersion)

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
            # ReadyToRun reduces JIT work for packaged RID-specific desktop binaries.
            "/p:PublishReadyToRun=true",
            "/p:PublishTrimmed=false",
            "/p:EnableProjectLoadTiming=false",
            "/p:DebugType=None",
            "/p:DebugSymbols=false",
            "-o", $ridOutDir
        ) + $versionProperties

        Invoke-ExternalCommand -filePath "dotnet" -arguments $publishArgs -failureMessage "dotnet publish failed for RID: $rid" -workingDirectory $script:IsolatedRepoRoot

        $sourcePath = Join-Path $ridOutDir ([string]$target.Binary)
        if (-not (Test-Path $sourcePath)) {
            throw "Single-file artifact not found: $sourcePath"
        }

        $destinationPath = Join-Path $releaseDir ([string]$target.Name)
        Copy-Item -Path $sourcePath -Destination $destinationPath -Force

        if ($rid.StartsWith("win-", [System.StringComparison]::OrdinalIgnoreCase)) {
            Assert-WindowsArtifactVersion -artifactPath $destinationPath -displayVersion $version -storePackageVersion ([string]$defaultReleaseVersionInfo.StorePackageVersion)
        }
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
    [string]$displayVersion,
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

    $versionProperties = Get-BuildVersionProperties -displayVersion $displayVersion -storePackageVersion $packageVersion
    $targetPlatformResolution = Resolve-StoreTargetPlatformVersion -projectPath $project

    if ($targetPlatformResolution.UsedFallback) {
        Write-Warning "Requested UAP SDK $($targetPlatformResolution.RequestedVersion) is not installed. Using $($targetPlatformResolution.ResolvedVersion) for Store build."
    }
    else {
        Write-Host "  UAP SDK: $($targetPlatformResolution.ResolvedVersion)"
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
    Set-StoreProjectTargetPlatformVersion -projectPath $project -targetPlatformVersion $targetPlatformResolution.ResolvedVersion | Out-Null
    Invoke-ExternalCommand -filePath "dotnet" -arguments (@("restore", $avaloniaProjectPath, "/p:Configuration=$configuration") + $versionProperties) -failureMessage "dotnet restore failed for store build" -workingDirectory $script:IsolatedRepoRoot

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
        "/p:EnableProjectLoadTiming=false",
        "/flp:logfile=$buildLogRelative;verbosity=normal"
    ) + $versionProperties
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
        $listingHeaders = Get-CsvHeaderColumns -path $listingCsvPath
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

    Assert-StoreArtifactsContainExecutionAlias -artifacts @($artifacts) -tempDirectory (Join-Path $publishStoreDir "_execution_alias_validation")

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

    $appCertOutput = & $appCertPath test -appxpackagepath "$packagePath" -reportoutputpath "$reportPath"
    $exitCode = $LASTEXITCODE
    foreach ($line in @($appCertOutput)) {
        if (-not [string]::IsNullOrWhiteSpace([string]$line)) {
            Write-Host "  $line"
        }
    }

    return [pscustomobject]@{
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
$repoRoot = Get-DevProjexRepoRoot -startPath $PSScriptRoot

if ($ValidateConfigOnly -and $SmokeStoreBuildOnly) {
    throw "Use either -ValidateConfigOnly or -SmokeStoreBuildOnly, not both."
}

if ($ValidateConfigOnly) {
    Invoke-ReleaseConfigValidation -repoRoot $repoRoot
    return
}

if ($SmokeStoreBuildOnly) {
    Invoke-StoreSmokeBuild -repoRoot $repoRoot -versionOverride $Version
    return
}

$defaultReleaseVersionInfo = Get-DefaultReleaseVersionInfo -repoRoot $repoRoot
$initialVersion = if ([string]::IsNullOrWhiteSpace($Version)) { [string]$defaultReleaseVersionInfo.DisplayVersion } else { $Version }
$versionInfo = Get-VersionInteractive -currentValue $initialVersion
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
Write-Step "Validating release config"
Invoke-ReleaseConfigValidation -repoRoot $repoRoot

try {
    Create-IsolatedWorkspace -sourceRoot $sourceRoot
    Configure-IsolatedNuGetCache

    Write-Step "Building GitHub artifacts in isolated workspace"
    Build-GitHubArtifactsInWorkspace -version $resolvedVersion -configuration "Release"

    Write-Step "Building Microsoft Store package in isolated workspace"
    Build-StoreArtifactsInWorkspace -displayVersion $resolvedVersion -configuration "ReleaseStore" -platform "x64" -bundlePlatforms "x64|arm64" -packageVersion $storePackageVersion

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
