param(
  [string]$Configuration = "ReleaseStore",
  [string]$Platform = "x64",
  [string]$BundlePlatforms = "x64"
)

$ErrorActionPreference = "Stop"

$project = "Packaging\Windows\DevProjex.Store\DevProjex.Store.wapproj"

Write-Host "Building MSIX bundle..."
Write-Host "  Project: $project"
Write-Host "  Configuration: $Configuration"
Write-Host "  Platform: $Platform"
Write-Host "  Bundle platforms: $BundlePlatforms"

$desktopBridgeTargets = @(
  "$env:ProgramFiles(x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets",
  "$env:ProgramFiles(x86)\Microsoft Visual Studio\2022\Community\MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets",
  "$env:ProgramFiles(x86)\Microsoft Visual Studio\2022\Professional\MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets",
  "$env:ProgramFiles(x86)\Microsoft Visual Studio\2022\Enterprise\MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets",
  "$env:ProgramFiles(x86)\MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets",
  "$env:ProgramFiles(x86)\Windows Kits\10\DesignTime\CommonConfiguration\Neutral\Microsoft.DesktopBridge.targets"
)

$desktopBridgeAvailable = $desktopBridgeTargets | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($null -eq $desktopBridgeAvailable) {
  Write-Host "Microsoft.DesktopBridge targets not found. Installing prerequisites..."

  $tempDir = Join-Path $env:TEMP "devprojex-msix-tools"
  New-Item -ItemType Directory -Force -Path $tempDir | Out-Null

  $vsBuildToolsUrl = "https://aka.ms/vs/17/release/vs_BuildTools.exe"
  $vsBuildToolsPath = Join-Path $tempDir "vs_BuildTools.exe"

  $winSdkUrl = "https://download.microsoft.com/download/5b75beb2-d0fa-4b8e-8e06-a3edc3647ca5/KIT_BUNDLE_WINDOWSSDK_MEDIACREATION/winsdksetup.exe"
  $winSdkPath = Join-Path $tempDir "winsdksetup.exe"

  Write-Host "Downloading Visual Studio Build Tools..."
  Invoke-WebRequest -Uri $vsBuildToolsUrl -OutFile $vsBuildToolsPath

  $buildToolsInstall = "C:\BuildTools"

  Write-Host "Installing Visual Studio Build Tools..."
  $vsProc = Start-Process -FilePath $vsBuildToolsPath -ArgumentList @(
    "--quiet",
    "--wait",
    "--norestart",
    "--nocache",
    "--installPath", $buildToolsInstall,
    "--add", "Microsoft.VisualStudio.Workload.MSBuildTools",
    "--add", "Microsoft.VisualStudio.Workload.UniversalBuildTools",
    "--add", "Microsoft.VisualStudio.Component.AppxPackage",
    "--add", "Microsoft.VisualStudio.Component.Windows10SDK.19041",
    "--includeRecommended"
  ) -Wait -PassThru

  if ($vsProc.ExitCode -ne 0) {
    Write-Host "Build Tools install failed with exit code $($vsProc.ExitCode)."
    exit $vsProc.ExitCode
  }

  Write-Host "Downloading Windows SDK..."
  Invoke-WebRequest -Uri $winSdkUrl -OutFile $winSdkPath

  Write-Host "Installing Windows SDK..."
  $sdkLayoutDir = Join-Path $tempDir "winsdk-layout"
  New-Item -ItemType Directory -Force -Path $sdkLayoutDir | Out-Null

  Start-Process -FilePath $winSdkPath -ArgumentList @(
    "/layout", $sdkLayoutDir,
    "/quiet",
    "/norestart"
  ) -Wait

  $layoutInstaller = Join-Path $sdkLayoutDir "winsdksetup.exe"
  if (-not (Test-Path $layoutInstaller)) {
    Write-Host "Windows SDK layout installer not found."
    exit 1
  }

  Start-Process -FilePath $layoutInstaller -ArgumentList @(
    "/quiet",
    "/norestart",
    "/features", "+"
  ) -Wait

  $desktopBridgeTargets = @(
    "C:\BuildTools\MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets",
    "$env:ProgramFiles(x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets",
    "$env:ProgramFiles(x86)\Microsoft Visual Studio\2022\Community\MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets",
    "$env:ProgramFiles(x86)\Microsoft Visual Studio\2022\Professional\MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets",
    "$env:ProgramFiles(x86)\Microsoft Visual Studio\2022\Enterprise\MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets",
    "$env:ProgramFiles(x86)\MSBuild\Microsoft\DesktopBridge\Microsoft.DesktopBridge.targets",
    "$env:ProgramFiles(x86)\Windows Kits\10\DesignTime\CommonConfiguration\Neutral\Microsoft.DesktopBridge.targets"
  )

  $desktopBridgeAvailable = $desktopBridgeTargets | Where-Object { Test-Path $_ } | Select-Object -First 1
  if ($null -eq $desktopBridgeAvailable) {
    Write-Host "Microsoft.DesktopBridge targets still not found after install."
    exit 1
  }
}

$msbuildCandidates = @(
  "C:\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
  "$env:ProgramFiles(x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
  "$env:ProgramFiles(x86)\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
)
$msbuildExe = $msbuildCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($null -eq $msbuildExe) {
  Write-Host "MSBuild.exe not found after Build Tools install."
  exit 1
}

Write-Host "Restoring packages..."
$restoreRid = "win-$Platform"
dotnet restore "Apps\Avalonia\DevProjex.Avalonia\DevProjex.Avalonia.csproj" `
  /p:Configuration=$Configuration `
  /p:RuntimeIdentifier="$restoreRid"

if ($LASTEXITCODE -ne 0) {
  exit $LASTEXITCODE
}

$logDir = "publish\store"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$buildLog = Join-Path $logDir "msix-build.log"

if ($BundlePlatforms -like "*|*") {
  $bundleMode = "Always"
} else {
  $bundleMode = "Never"
}

& $msbuildExe $project `
  /p:Configuration=$Configuration `
  /p:Platform=$Platform `
  /p:AppxBundle=$bundleMode `
  /p:AppxBundlePlatforms=$BundlePlatforms `
  /p:UapAppxPackageBuildMode=StoreUpload `
  /p:AppxPackageDir=publish\store\ `
  /flp:"logfile=$buildLog;verbosity=normal"

if ($LASTEXITCODE -ne 0) {
  exit $LASTEXITCODE
}

$manifestPath = "Packaging\Windows\DevProjex.Store\Package.appxmanifest"
[xml]$manifest = Get-Content -Path $manifestPath
$identity = $manifest.Package.Identity
$publisherDisplay = $manifest.Package.Properties.PublisherDisplayName

$artifact = @(
  Get-ChildItem -Path publish\store -Recurse -File -Include *.msixbundle,*.msix -ErrorAction SilentlyContinue
  Get-ChildItem -Path Packaging\Windows\DevProjex.Store\publish\store -Recurse -File -Include *.msixbundle,*.msix -ErrorAction SilentlyContinue
) | Sort-Object LastWriteTime -Descending | Select-Object -First 1

if ($null -eq $artifact) {
  Write-Host "MSIX artifact not found in publish\\store or Packaging\\Windows\\DevProjex.Store\\publish\\store"
  exit 1
}

Write-Host ""
Write-Host "Build output:"
Write-Host "  Artifact: $($artifact.FullName)"
Write-Host "  Version: $($identity.Version)"
Write-Host "  Identity.Name: $($identity.Name)"
Write-Host "  Identity.Publisher: $($identity.Publisher)"
Write-Host "  PublisherDisplayName: $publisherDisplay"
Write-Host "  Architectures: $BundlePlatforms"
