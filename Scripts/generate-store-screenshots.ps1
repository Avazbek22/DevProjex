[CmdletBinding()]
param(
    [string]$PublishedExe,
    [string]$PartnerCenterCsv,
    [string]$ProjectPath,
    [string]$OutputRoot,
    [switch]$SkipPublish,
    [switch]$KeepSessionData,
    [switch]$PlanOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-RepositoryRoot {
    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
}

function Resolve-PartnerCenterCsv([string]$RepositoryRoot, [string]$ExplicitPath) {
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $resolved = [System.IO.Path]::GetFullPath($ExplicitPath)
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "Partner Center CSV was not found: $resolved"
        }
        return $resolved
    }

    $candidates = @(
        Get-ChildItem -LiteralPath $RepositoryRoot -Filter "listingData-*.csv" -File -ErrorAction SilentlyContinue
        Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot "Packaging\Windows\StoreListing") -Filter "listingData-*.csv" -File -ErrorAction SilentlyContinue
        Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot "Packaging\Windows\StoreListing") -Filter "Exported*.csv" -File -ErrorAction SilentlyContinue
    ) | Sort-Object LastWriteTimeUtc -Descending
    if ($candidates.Count -eq 0) {
        throw "No Partner Center listingData export was found. Pass -PartnerCenterCsv explicitly."
    }
    return $candidates[0].FullName
}

function Get-StoreLocaleColumns([object[]]$Rows) {
    if ($Rows.Count -eq 0) {
        throw "Partner Center CSV is empty."
    }

    $metadataColumns = @("Field", "ID", "Type", "default")
    $localizedTypeHeader = '^Type \(.+\)$'
    return @($Rows[0].PSObject.Properties.Name | Where-Object { $metadataColumns -notcontains $_ -and $_ -notmatch $localizedTypeHeader })
}

function Get-LocalizationCodes([string]$RepositoryRoot) {
    $localizationRoot = Join-Path $RepositoryRoot "Assets\Localization"
    $codes = @(
        Get-ChildItem -LiteralPath $localizationRoot -Filter "*.json" -File |
            ForEach-Object { $_.BaseName.ToLowerInvariant() }
    )
    if ($codes.Count -eq 0) {
        throw "No application localization catalogs were found."
    }
    return $codes
}

function Resolve-AppLanguageCode([string]$StoreLocale, [string[]]$SupportedCodes) {
    $normalized = $StoreLocale.Trim().Replace("_", "-").ToLowerInvariant()
    if ($SupportedCodes -contains $normalized) {
        return $normalized
    }

    $primary = $normalized.Split('-')[0]
    if ($SupportedCodes -contains $primary) {
        return $primary
    }

    throw "Store locale '$StoreLocale' cannot be mapped to an application localization catalog."
}

function Publish-StoreCaptureBinary([string]$RepositoryRoot, [string]$Destination) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    & dotnet publish (Join-Path $RepositoryRoot "Apps\Avalonia\DevProjex.Avalonia.csproj") `
        -c Release `
        -r win-x64 `
        --self-contained true `
        /p:PublishSingleFile=true `
        /p:IncludeNativeLibrariesForSelfExtract=true `
        /p:PublishReadyToRun=true `
        /p:PublishTrimmed=false `
        /p:EnableProjectLoadTiming=false `
        /p:DebugType=None `
        /p:DebugSymbols=false `
        -o $Destination | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "DevProjex win-x64 publish failed with exit code $LASTEXITCODE."
    }

    $files = @(Get-ChildItem -LiteralPath $Destination -File -Recurse)
    $binary = Join-Path $Destination "DevProjex.exe"
    if ($files.Count -ne 1 -or -not (Test-Path -LiteralPath $binary -PathType Leaf)) {
        throw "Store capture publish must contain exactly one primary DevProjex.exe."
    }
    return $binary
}

function New-CleanProjectSnapshot([string]$RepositoryRoot, [string]$SessionRoot) {
    $snapshotParent = Join-Path $SessionRoot "showcase"
    $snapshotRoot = Join-Path $snapshotParent "DevProjex"
    $archivePath = Join-Path $SessionRoot "showcase.tar"
    New-Item -ItemType Directory -Path $snapshotRoot -Force | Out-Null

    & git -C $RepositoryRoot archive --format=tar --output=$archivePath HEAD
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to create the read-only HEAD snapshot used by Store screenshots."
    }
    & tar -xf $archivePath -C $snapshotRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to extract the Store screenshot project snapshot."
    }
    Remove-Item -LiteralPath $archivePath -Force
    return $snapshotRoot
}

function Mount-CleanProjectSnapshot([string]$SnapshotRoot) {
    $snapshotParent = Split-Path -Parent $SnapshotRoot
    foreach ($letter in @("S", "T", "U", "V", "W", "X", "Y", "Z")) {
        $drive = "${letter}:"
        if (Test-Path -LiteralPath ($drive + "\")) {
            continue
        }

        & subst $drive $snapshotParent
        if ($LASTEXITCODE -eq 0 -and (Test-Path -LiteralPath ($drive + "\DevProjex"))) {
            return @{
                ProjectPath = $drive + "\DevProjex"
                Drive = $drive
            }
        }
    }

    throw "No free drive letter was available for the clean Store showcase path."
}

function Initialize-NativeCapture {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    if (-not ("DevProjex.StoreCapture.NativeMethods" -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

namespace DevProjex.StoreCapture
{
    public static class NativeMethods
    {
        [DllImport("user32.dll")]
        public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr awarenessContext);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool MoveWindow(IntPtr window, int x, int y, int width, int height, bool repaint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetCursorPos(int x, int y);
    }
}
"@
    }

    # PowerShell 5.1 is commonly system-DPI-aware. Switching only this capture thread to
    # per-monitor V2 keeps Win32 window coordinates and bitmap pixels in the same space.
    [DevProjex.StoreCapture.NativeMethods]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null
}

function Wait-Until([scriptblock]$Condition, [TimeSpan]$Timeout, [string]$FailureMessage) {
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.Elapsed -lt $Timeout) {
        if (& $Condition) {
            return
        }
        Start-Sleep -Milliseconds 40
    }
    throw $FailureMessage
}

function Wait-ForCaptureState([string]$SessionDirectory, [string]$FileName, [System.Diagnostics.Process]$Process) {
    $path = Join-Path $SessionDirectory $FileName
    Wait-Until {
        if (Test-Path -LiteralPath $path) {
            return $true
        }
        if (Test-Path -LiteralPath (Join-Path $SessionDirectory "failure.json")) {
            $failure = Get-Content -LiteralPath (Join-Path $SessionDirectory "failure.json") -Raw
            throw "DevProjex Store capture failed: $failure"
        }
        if ($Process.HasExited) {
            throw "DevProjex exited before Store capture state '$FileName' was ready. Exit code: $($Process.ExitCode)."
        }
        return $false
    } ([TimeSpan]::FromMinutes(3)) "Timed out waiting for Store capture state '$FileName'."
    return $path
}

function Set-CaptureWindowGeometry(
    [System.Diagnostics.Process]$Process,
    [object]$Manifest,
    [System.Drawing.Rectangle]$WorkingArea) {
    $captureWidth = [int]$Manifest.captureWidth
    $captureHeight = [int]$Manifest.captureHeight
    if ($WorkingArea.Width -lt $captureWidth -or $WorkingArea.Height -lt $captureHeight) {
        throw "The primary working area must be at least ${captureWidth}x${captureHeight}; actual: $($WorkingArea.Width)x$($WorkingArea.Height)."
    }

    Wait-Until {
        $Process.Refresh()
        $Process.MainWindowHandle -ne [IntPtr]::Zero
    } ([TimeSpan]::FromSeconds(30)) "DevProjex did not create a native main window."

    $captureX = $WorkingArea.X + [int](($WorkingArea.Width - $captureWidth) / 2)
    $captureY = $WorkingArea.Y + [int](($WorkingArea.Height - $captureHeight) / 2)
    $windowX = $captureX + [int](($captureWidth - [int]$Manifest.windowWidth) / 2)
    $windowY = $captureY + [int](($captureHeight - [int]$Manifest.windowHeight) / 2)
    [DevProjex.StoreCapture.NativeMethods]::ShowWindow($Process.MainWindowHandle, 9) | Out-Null
    if (-not [DevProjex.StoreCapture.NativeMethods]::MoveWindow(
            $Process.MainWindowHandle,
            $windowX,
            $windowY,
            [int]$Manifest.windowWidth,
            [int]$Manifest.windowHeight,
            $true)) {
        throw "Unable to position the DevProjex window for Store capture."
    }
    [DevProjex.StoreCapture.NativeMethods]::SetForegroundWindow($Process.MainWindowHandle) | Out-Null
    [DevProjex.StoreCapture.NativeMethods]::SetCursorPos(
        $WorkingArea.Right - 2,
        $WorkingArea.Bottom - 2) | Out-Null

    return [System.Drawing.Rectangle]::new($captureX, $captureY, $captureWidth, $captureHeight)
}

function Save-DesktopRegion([System.Drawing.Rectangle]$Region, [string]$Destination) {
    $directory = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $bitmap = [System.Drawing.Bitmap]::new(
        $Region.Width,
        $Region.Height,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.CopyFromScreen(
                $Region.Location,
                [System.Drawing.Point]::Empty,
                $Region.Size,
                [System.Drawing.CopyPixelOperation]::SourceCopy)
        }
        finally {
            $graphics.Dispose()
        }
        $bitmap.Save($Destination, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $bitmap.Dispose()
    }
}

function Start-LanguageCapture(
    [string]$Binary,
    [string]$LanguageCode,
    [string]$ShowcaseProject,
    [string]$SessionRoot,
    [string]$ScreenshotRoot,
    [object]$Manifest,
    [System.Drawing.Rectangle]$WorkingArea) {
    $languageSession = Join-Path $SessionRoot $LanguageCode
    $appData = Join-Path $languageSession "app-data"
    New-Item -ItemType Directory -Path $appData -Force | Out-Null
    $requestPath = Join-Path $languageSession "request.json"
    $requestJson = @{
        projectPath = $ShowcaseProject
        sessionDirectory = $languageSession
        appDataDirectory = $appData
        languageCode = $LanguageCode
    } | ConvertTo-Json
    [System.IO.File]::WriteAllText(
        $requestPath,
        $requestJson,
        (New-Object System.Text.UTF8Encoding($false)))

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($Binary)
    $startInfo.UseShellExecute = $false
    $startInfo.WorkingDirectory = Split-Path -Parent $Binary
    $startInfo.EnvironmentVariables["DEVPROJEX_INTERNAL_STORE_CAPTURE"] = $requestPath
    $process = [System.Diagnostics.Process]::Start($startInfo)
    try {
        Wait-ForCaptureState $languageSession "window-ready.json" $process | Out-Null
        $captureRegion = Set-CaptureWindowGeometry $process $Manifest $WorkingArea
        New-Item -ItemType File -Path (Join-Path $languageSession "window-positioned") -Force | Out-Null

        foreach ($scene in $Manifest.scenes) {
            $stem = "{0:D2}-{1}" -f [int]$scene.index, [string]$scene.name
            Wait-ForCaptureState $languageSession "ready-$stem.json" $process | Out-Null
            [DevProjex.StoreCapture.NativeMethods]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
            # The application has already completed its state/render barriers. This short
            # guard only lets DWM present that final frame before desktop pixel capture.
            Start-Sleep -Milliseconds ([int]$Manifest.captureSettleMilliseconds)
            $destination = Join-Path $ScreenshotRoot (Join-Path ([string]$scene.directory) ($LanguageCode.ToUpperInvariant() + ".png"))
            Save-DesktopRegion $captureRegion $destination
            New-Item -ItemType File -Path (Join-Path $languageSession "captured-$stem") -Force | Out-Null
            Write-Host "  [$LanguageCode] $($scene.index)/$($Manifest.scenes.Count): $destination"
        }

        Wait-ForCaptureState $languageSession "complete.json" $process | Out-Null
        if (-not $process.WaitForExit(30000)) {
            throw "DevProjex did not exit after completing the Store capture session."
        }
        if ($process.ExitCode -ne 0) {
            throw "DevProjex Store capture exited with code $($process.ExitCode)."
        }
    }
    finally {
        if (-not $process.HasExited) {
            $process.Kill()
            $process.WaitForExit()
        }
        $process.Dispose()
    }
}

function Update-ListingCsv(
    [object[]]$Rows,
    [string[]]$LocaleColumns,
    [hashtable]$LocaleMap,
    [object]$Manifest,
    [string]$Destination) {
    foreach ($scene in $Manifest.scenes) {
        $fieldName = "DesktopScreenshot$($scene.index)"
        $row = $Rows | Where-Object { $_.Field -eq $fieldName } | Select-Object -First 1
        if ($null -eq $row) {
            throw "Partner Center CSV does not contain '$fieldName'."
        }
        foreach ($locale in $LocaleColumns) {
            $languageCode = [string]$LocaleMap[$locale]
            $row.$locale = "ImportFolder/Screenshots/$($scene.directory)/$($languageCode.ToUpperInvariant()).png"
        }
    }

    $sourceColumns = @($Rows[0].PSObject.Properties.Name)
    $columns = @($sourceColumns | ForEach-Object {
        if ($_ -match '^Type \(.+\)$') { "Type" } else { $_ }
    })
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append((ConvertTo-StoreCsvRow $columns))
    [void]$builder.Append("`r`n")
    foreach ($row in $Rows) {
        $values = @($sourceColumns | ForEach-Object { [string]$row.$_ })
        [void]$builder.Append((ConvertTo-StoreCsvRow $values))
        [void]$builder.Append("`r`n")
    }
    $csvText = $builder.ToString()
    [System.IO.File]::WriteAllText(
        $Destination,
        $csvText,
        (New-Object System.Text.UTF8Encoding($false)))
}

function ConvertTo-StoreCsvRow([string[]]$Values) {
    $encoded = foreach ($value in $Values) {
        $normalized = $value.Replace("`r`n", "`n").Replace("`r", "`n")
        $normalized = [System.Text.RegularExpressions.Regex]::Replace(
            $normalized,
            "[ `t]+(?=`n|$)",
            "")
        $normalized = $normalized.Replace("`n", "`r`n")
        if ($normalized.IndexOfAny([char[]]@(',', '"', "`r", "`n")) -ge 0) {
            '"' + $normalized.Replace('"', '""') + '"'
        } else {
            $normalized
        }
    }
    return $encoded -join ','
}

function New-ContactSheet(
    [string]$ScreenshotRoot,
    [string[]]$LanguageCodes,
    [object]$Manifest,
    [string]$Destination) {
    $thumbnailWidth = 512
    $thumbnailHeight = 320
    $labelHeight = 28
    $sheet = [System.Drawing.Bitmap]::new(
        $thumbnailWidth * $Manifest.scenes.Count,
        ($thumbnailHeight + $labelHeight) * $LanguageCodes.Count)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($sheet)
        try {
            $graphics.Clear([System.Drawing.Color]::FromArgb(24, 24, 27))
            $font = [System.Drawing.Font]::new("Segoe UI", 12, [System.Drawing.FontStyle]::Bold)
            try {
                for ($languageIndex = 0; $languageIndex -lt $LanguageCodes.Count; $languageIndex++) {
                    $language = $LanguageCodes[$languageIndex]
                    $rowY = $languageIndex * ($thumbnailHeight + $labelHeight)
                    foreach ($scene in $Manifest.scenes) {
                        $column = [int]$scene.index - 1
                        $sourcePath = Join-Path $ScreenshotRoot (Join-Path ([string]$scene.directory) ($language.ToUpperInvariant() + ".png"))
                        $source = [System.Drawing.Image]::FromFile($sourcePath)
                        try {
                            $graphics.DrawImage($source, $column * $thumbnailWidth, $rowY, $thumbnailWidth, $thumbnailHeight)
                        }
                        finally {
                            $source.Dispose()
                        }
                        $graphics.DrawString(
                            "$language · $($scene.index) $($scene.name)",
                            $font,
                            [System.Drawing.Brushes]::White,
                            $column * $thumbnailWidth + 8,
                            $rowY + $thumbnailHeight + 3)
                    }
                }
            }
            finally {
                $font.Dispose()
            }
        }
        finally {
            $graphics.Dispose()
        }
        $sheet.Save($Destination, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $sheet.Dispose()
    }
}

$repositoryRoot = Resolve-RepositoryRoot
$storeListingRoot = Join-Path $repositoryRoot "Packaging\Windows\StoreListing"
$manifest = Get-Content -LiteralPath (Join-Path $storeListingRoot "store-screenshots.json") -Raw | ConvertFrom-Json
$partnerCsvPath = Resolve-PartnerCenterCsv $repositoryRoot $PartnerCenterCsv
$rows = @(Import-Csv -LiteralPath $partnerCsvPath)
$localeColumns = @(Get-StoreLocaleColumns $rows)
$supportedCodes = @(Get-LocalizationCodes $repositoryRoot)
$localeMap = @{}
foreach ($locale in $localeColumns) {
    $localeMap[$locale] = Resolve-AppLanguageCode $locale $supportedCodes
}
$captureLanguages = @($supportedCodes | Sort-Object -Unique)

Write-Host "Partner Center CSV: $partnerCsvPath"
Write-Host "Store locales: $($localeColumns -join ', ')"
Write-Host "Application captures: $($captureLanguages -join ', ')"
if ($PlanOnly) {
    return
}
$isWindowsPlatform = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [System.Runtime.InteropServices.OSPlatform]::Windows)
if (-not $isWindowsPlatform -or -not [Environment]::UserInteractive) {
    throw "Real Store screenshot generation requires an interactive Windows desktop session."
}

$resolvedOutputRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $storeListingRoot "ImportFolder"
} else {
    [System.IO.Path]::GetFullPath($OutputRoot)
}
$screenshotRoot = Join-Path $resolvedOutputRoot "Screenshots"
New-Item -ItemType Directory -Path $screenshotRoot -Force | Out-Null

$resolvedPublishedExe = if (-not [string]::IsNullOrWhiteSpace($PublishedExe)) {
    [System.IO.Path]::GetFullPath($PublishedExe)
} else {
    Join-Path $repositoryRoot "artifacts\store-screenshots\publish\DevProjex.exe"
}
if (-not $SkipPublish) {
    $resolvedPublishedExe = Publish-StoreCaptureBinary $repositoryRoot (Split-Path -Parent $resolvedPublishedExe)
}
if (-not (Test-Path -LiteralPath $resolvedPublishedExe -PathType Leaf)) {
    throw "Published DevProjex.exe was not found: $resolvedPublishedExe"
}

$sessionRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("DevProjex\store-screenshot-captures\" + [Guid]::NewGuid().ToString("N"))
$snapshotDrive = $null
New-Item -ItemType Directory -Path $sessionRoot -Force | Out-Null
try {
    $showcaseProject = if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
        $snapshotRoot = New-CleanProjectSnapshot $repositoryRoot $sessionRoot
        $mountedSnapshot = Mount-CleanProjectSnapshot $snapshotRoot
        $snapshotDrive = [string]$mountedSnapshot.Drive
        [string]$mountedSnapshot.ProjectPath
    } else {
        [System.IO.Path]::GetFullPath($ProjectPath)
    }
    if (-not (Test-Path -LiteralPath $showcaseProject -PathType Container)) {
        throw "Store showcase project was not found: $showcaseProject"
    }

    Initialize-NativeCapture
    $workingArea = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    foreach ($language in $captureLanguages) {
        Start-LanguageCapture `
            $resolvedPublishedExe `
            $language `
            $showcaseProject `
            $sessionRoot `
            $screenshotRoot `
            $manifest `
            $workingArea
    }

    $listingCsv = Join-Path $resolvedOutputRoot "listingData.csv"
    Update-ListingCsv $rows $localeColumns $localeMap $manifest $listingCsv
    $contactSheet = Join-Path $repositoryRoot "artifacts\store-screenshots\contact-sheet.png"
    New-Item -ItemType Directory -Path (Split-Path -Parent $contactSheet) -Force | Out-Null
    New-ContactSheet $screenshotRoot $captureLanguages $manifest $contactSheet

    & (Join-Path $repositoryRoot "Scripts\validate-store-listing.ps1")
    if ($LASTEXITCODE -ne 0) {
        throw "Store listing validation failed with exit code $LASTEXITCODE."
    }

    Write-Host "Store screenshots: $screenshotRoot"
    Write-Host "Import CSV: $listingCsv"
    Write-Host "Contact sheet: $contactSheet"
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($snapshotDrive)) {
        & subst $snapshotDrive /D | Out-Null
    }
    if (-not $KeepSessionData -and (Test-Path -LiteralPath $sessionRoot)) {
        Remove-Item -LiteralPath $sessionRoot -Recurse -Force
    } elseif ($KeepSessionData) {
        Write-Host "Capture session retained: $sessionRoot"
    }
}
