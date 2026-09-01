[CmdletBinding()]
param(
    [string]$CaptureHost,
    [string]$ProjectPath,
    [string]$OutputRoot,
    [string]$ListingCsv,
    [string[]]$Languages,
    [switch]$SkipBuild,
    [switch]$KeepSessionData,
    [switch]$PlanOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-RepositoryRoot {
    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
}

function Get-StoreLocaleColumns([object[]]$Rows) {
    if ($Rows.Count -eq 0) {
        throw "Store listing CSV is empty."
    }

    $metadataColumns = @("Field", "ID", "Type (Тип)", "default")
    return @($Rows[0].PSObject.Properties.Name | Where-Object { $metadataColumns -notcontains $_ })
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

function Wait-Until([scriptblock]$Condition, [TimeSpan]$Timeout, [string]$FailureMessage) {
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($stopwatch.Elapsed -lt $Timeout) {
        if (& $Condition) {
            return
        }
        Start-Sleep -Milliseconds 50
    }
    throw $FailureMessage
}

function Build-CaptureHost([string]$RepositoryRoot, [string]$Destination) {
    & dotnet publish (Join-Path $RepositoryRoot "Tools\StoreMediaCapture\DevProjex.StoreMediaCapture.csproj") `
        -c Release `
        -r win-x64 `
        --self-contained false `
        /p:DebugType=None `
        /p:DebugSymbols=false `
        -o $Destination | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "DevProjex Store media capture host build failed with exit code $LASTEXITCODE."
    }

    $binary = Join-Path $Destination "DevProjex.StoreMediaCapture.exe"
    if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) {
        throw "Store media capture host was not produced: $binary"
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
        throw "Unable to create the read-only HEAD snapshot used by Store TUI screenshots."
    }
    & tar -xf $archivePath -C $snapshotRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to extract the Store TUI screenshot project snapshot."
    }
    Remove-Item -LiteralPath $archivePath -Force
    return $snapshotRoot
}

function Initialize-ShowcaseRepository([string]$SnapshotRoot) {
    & git -C $SnapshotRoot init --quiet --initial-branch=main
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to initialize the temporary Store showcase repository."
    }

    $settings = @(
        @{ Name = "user.name"; Value = "DevProjex Store Capture" },
        @{ Name = "user.email"; Value = "store-capture@devprojex.local" },
        @{ Name = "commit.gpgSign"; Value = "false" },
        @{ Name = "core.autocrlf"; Value = "false" })
    foreach ($setting in $settings) {
        & git -C $SnapshotRoot config $setting.Name $setting.Value
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to configure the temporary Store showcase repository."
        }
    }

    & git -C $SnapshotRoot add --all
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to stage the temporary Store showcase repository."
    }
    & git -C $SnapshotRoot commit --quiet -m "Create Store showcase snapshot"
    if ($LASTEXITCODE -ne 0) {
        throw "Unable to commit the temporary Store showcase repository."
    }
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

function Initialize-TerminalCapture {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    if (-not ("DevProjex.StoreTuiCapture.NativeMethods" -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

namespace DevProjex.StoreTuiCapture
{
    public static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr awarenessContext);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr window, out Rect rect);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr window);

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
        public static extern bool BringWindowToTop(IntPtr window);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        public static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);

        [DllImport("user32.dll")]
        public static extern void mouse_event(uint flags, uint dx, uint dy, int data, UIntPtr extraInfo);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr LoadKeyboardLayout(string keyboardLayoutId, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    }
}
"@
    }

    [DevProjex.StoreTuiCapture.NativeMethods]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null
}

function Get-TerminalScreen {
    $primaryScreen = [System.Windows.Forms.Screen]::PrimaryScreen
    if ($null -eq $primaryScreen) {
        throw "The primary desktop screen is not available for Store TUI capture."
    }
    return $primaryScreen
}

function Focus-CaptureWindow([IntPtr]$Window) {
    if (-not [DevProjex.StoreTuiCapture.NativeMethods]::IsWindow($Window)) {
        throw "The Store TUI capture window no longer exists."
    }

    $noMoveOrSize = [uint32]0x0003
    $showWindow = [uint32]0x0040
    [DevProjex.StoreTuiCapture.NativeMethods]::ShowWindow($Window, 9) | Out-Null
    [DevProjex.StoreTuiCapture.NativeMethods]::SetWindowPos(
        $Window, [IntPtr](-1), 0, 0, 0, 0, $noMoveOrSize -bor $showWindow) | Out-Null
    [DevProjex.StoreTuiCapture.NativeMethods]::SetWindowPos(
        $Window, [IntPtr](-2), 0, 0, 0, 0, $noMoveOrSize -bor $showWindow) | Out-Null
    [DevProjex.StoreTuiCapture.NativeMethods]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero)
    [DevProjex.StoreTuiCapture.NativeMethods]::keybd_event(0x12, 0, 0x0002, [UIntPtr]::Zero)
    [DevProjex.StoreTuiCapture.NativeMethods]::BringWindowToTop($Window) | Out-Null
    [DevProjex.StoreTuiCapture.NativeMethods]::SetForegroundWindow($Window) | Out-Null

    Wait-Until {
        [DevProjex.StoreTuiCapture.NativeMethods]::GetForegroundWindow() -eq $Window
    } ([TimeSpan]::FromSeconds(5)) "Windows would not grant foreground focus to the isolated Store TUI window."
}

function Send-CaptureKeys([IntPtr]$Window, [string]$Keys, [int]$SettleMilliseconds) {
    Focus-CaptureWindow $Window
    if ([DevProjex.StoreTuiCapture.NativeMethods]::GetForegroundWindow() -ne $Window) {
        throw "Refusing to send input because the Store TUI window is not foreground."
    }
    [System.Windows.Forms.SendKeys]::SendWait($Keys)
    Start-Sleep -Milliseconds $SettleMilliseconds
}

function Send-CommandActivation([IntPtr]$Window, [int]$SettleMilliseconds) {
    Focus-CaptureWindow $Window
    if ([DevProjex.StoreTuiCapture.NativeMethods]::GetForegroundWindow() -ne $Window) {
        throw "Refusing to activate the command line because the Store TUI window is not foreground."
    }

    [DevProjex.StoreTuiCapture.NativeMethods]::keybd_event(0x10, 0, 0, [UIntPtr]::Zero)
    [DevProjex.StoreTuiCapture.NativeMethods]::keybd_event(0xBA, 0, 0, [UIntPtr]::Zero)
    [DevProjex.StoreTuiCapture.NativeMethods]::keybd_event(0xBA, 0, 0x0002, [UIntPtr]::Zero)
    [DevProjex.StoreTuiCapture.NativeMethods]::keybd_event(0x10, 0, 0x0002, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds $SettleMilliseconds
}

function Send-ControlKey([IntPtr]$Window, [byte]$VirtualKey, [int]$SettleMilliseconds) {
    Focus-CaptureWindow $Window
    if ([DevProjex.StoreTuiCapture.NativeMethods]::GetForegroundWindow() -ne $Window) {
        throw "Refusing to send a shortcut because the Store TUI window is not foreground."
    }

    [DevProjex.StoreTuiCapture.NativeMethods]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)
    [DevProjex.StoreTuiCapture.NativeMethods]::keybd_event($VirtualKey, 0, 0, [UIntPtr]::Zero)
    [DevProjex.StoreTuiCapture.NativeMethods]::keybd_event($VirtualKey, 0, 0x0002, [UIntPtr]::Zero)
    [DevProjex.StoreTuiCapture.NativeMethods]::keybd_event(0x11, 0, 0x0002, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds $SettleMilliseconds
}

function Reset-TerminalAppearance([IntPtr]$Window) {
    Focus-CaptureWindow $Window
    Send-CaptureKeys $Window "^0" 250

    $rect = New-Object DevProjex.StoreTuiCapture.NativeMethods+Rect
    if (-not [DevProjex.StoreTuiCapture.NativeMethods]::GetWindowRect($Window, [ref]$rect)) {
        throw "Unable to resolve the Store TUI window before resetting its opacity."
    }
    [DevProjex.StoreTuiCapture.NativeMethods]::SetCursorPos(
        [int](($rect.Left + $rect.Right) / 2),
        [int](($rect.Top + $rect.Bottom) / 2)) | Out-Null

    [DevProjex.StoreTuiCapture.NativeMethods]::keybd_event(0x11, 0, 0, [UIntPtr]::Zero)
    [DevProjex.StoreTuiCapture.NativeMethods]::keybd_event(0x10, 0, 0, [UIntPtr]::Zero)
    try {
        for ($step = 0; $step -lt 20; $step++) {
            [DevProjex.StoreTuiCapture.NativeMethods]::mouse_event(
                0x0800, 0, 0, 120, [UIntPtr]::Zero)
        }
    }
    finally {
        [DevProjex.StoreTuiCapture.NativeMethods]::keybd_event(0x10, 0, 0x0002, [UIntPtr]::Zero)
        [DevProjex.StoreTuiCapture.NativeMethods]::keybd_event(0x11, 0, 0x0002, [UIntPtr]::Zero)
    }
    Start-Sleep -Milliseconds 500
}

function Save-TerminalFrame(
    [IntPtr]$Window,
    [object]$TuiManifest,
    [string]$Destination) {
    $rect = New-Object DevProjex.StoreTuiCapture.NativeMethods+Rect
    if (-not [DevProjex.StoreTuiCapture.NativeMethods]::GetWindowRect($Window, [ref]$rect)) {
        throw "Unable to resolve the Store TUI capture window bounds."
    }

    $windowDpi = [DevProjex.StoreTuiCapture.NativeMethods]::GetDpiForWindow($Window)
    if ($windowDpi -eq 0) {
        throw "Unable to resolve the Store TUI capture window DPI."
    }
    $dpiScale = [double]$windowDpi / 96.0
    $chromeLeft = [int][Math]::Round(
        [int]$TuiManifest.chromeLeft * $dpiScale,
        [MidpointRounding]::AwayFromZero)
    $chromeTop = [int][Math]::Round(
        [int]$TuiManifest.chromeTop * $dpiScale,
        [MidpointRounding]::AwayFromZero)
    $chromeRight = [int][Math]::Round(
        [int]$TuiManifest.chromeRight * $dpiScale,
        [MidpointRounding]::AwayFromZero)
    $chromeBottom = [int][Math]::Round(
        [int]$TuiManifest.chromeBottom * $dpiScale,
        [MidpointRounding]::AwayFromZero)

    $left = $rect.Left + $chromeLeft
    $top = $rect.Top + $chromeTop
    $width = ($rect.Right - $rect.Left) - $chromeLeft - $chromeRight
    $height = ($rect.Bottom - $rect.Top) - $chromeTop - $chromeBottom
    if ($width -le 0 -or $height -le 0) {
        throw "The configured Store TUI chrome crop is larger than the terminal window."
    }

    $frame = [System.Drawing.Bitmap]::new(
        $width,
        $height,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($frame)
        try {
            $graphics.CopyFromScreen(
                [System.Drawing.Point]::new($left, $top),
                [System.Drawing.Point]::Empty,
                [System.Drawing.Size]::new($width, $height),
                [System.Drawing.CopyPixelOperation]::SourceCopy)
        }
        finally {
            $graphics.Dispose()
        }

        Assert-TerminalFrameEdges $frame

        $canvas = [System.Drawing.Bitmap]::new(
            [int]$TuiManifest.outputWidth,
            [int]$TuiManifest.outputHeight,
            [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
        try {
            $canvasGraphics = [System.Drawing.Graphics]::FromImage($canvas)
            try {
                $canvasGraphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                $canvasGraphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
                $canvasGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $canvasGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
                $canvasGraphics.DrawImage(
                    $frame,
                    [System.Drawing.Rectangle]::new(
                        0,
                        0,
                        [int]$TuiManifest.outputWidth,
                        [int]$TuiManifest.outputHeight),
                    0,
                    0,
                    $width,
                    $height,
                    [System.Drawing.GraphicsUnit]::Pixel)
            }
            finally {
                $canvasGraphics.Dispose()
            }

            New-Item -ItemType Directory -Path (Split-Path -Parent $Destination) -Force | Out-Null
            $canvas.Save($Destination, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $canvas.Dispose()
        }
    }
    finally {
        $frame.Dispose()
    }
}

function Assert-TerminalFrameEdges([System.Drawing.Bitmap]$Frame) {
    $edgeWidth = 3
    $expected = $Frame.GetPixel([int]($Frame.Width / 2), 0)
    if ($expected.R -gt 24 -or $expected.G -gt 24 -or $expected.B -gt 24) {
        throw "The Store TUI frame still contains bright terminal chrome at its top edge."
    }

    $isBackgroundPixel = {
        param([int]$x, [int]$y)
            $pixel = $Frame.GetPixel($x, $y)
            return [Math]::Abs([int]$pixel.R - [int]$expected.R) -le 2 -and
                [Math]::Abs([int]$pixel.G - [int]$expected.G) -le 2 -and
                [Math]::Abs([int]$pixel.B - [int]$expected.B) -le 2
    }

    $edges = @(
        @{ Name = "top"; Horizontal = $true; Start = 0 },
        @{ Name = "bottom"; Horizontal = $true; Start = $Frame.Height - $edgeWidth },
        @{ Name = "left"; Horizontal = $false; Start = 0 },
        @{ Name = "right"; Horizontal = $false; Start = $Frame.Width - $edgeWidth })
    foreach ($edge in $edges) {
        $mismatchCount = 0
        $pixelCount = 0
        for ($offset = 0; $offset -lt $edgeWidth; $offset++) {
            $length = if ($edge.Horizontal) { $Frame.Width } else { $Frame.Height }
            for ($position = 0; $position -lt $length; $position++) {
                $x = if ($edge.Horizontal) { $position } else { $edge.Start + $offset }
                $y = if ($edge.Horizontal) { $edge.Start + $offset } else { $position }
                if (-not (& $isBackgroundPixel $x $y)) {
                    $mismatchCount++
                }
                $pixelCount++
            }
        }
        if ($mismatchCount -gt [int]($pixelCount * 0.10)) {
            throw "The Store TUI frame contains non-content pixels across its $($edge.Name) edge."
        }
    }
}

function Write-TerminalBootstrap(
    [string]$Destination,
    [string]$HostBinary,
    [string]$ShowcaseProject,
    [string]$SessionRoot,
    [object]$TuiManifest,
    [string]$LanguageCode) {
    $argumentsPath = Join-Path $SessionRoot "host-arguments.txt"
    @(
        "tui",
        $ShowcaseProject,
        "--profile", "standard",
        "--screen", "alternate",
        "--no-mouse",
        "--color", "always",
        "--language", $LanguageCode
    ) | Set-Content -LiteralPath $argumentsPath -Encoding UTF8

    $dataRoot = Join-Path $SessionRoot "app-data"
    $bootstrapTemplate = @'
$ErrorActionPreference = "Stop"
$CaptureHost = '__CAPTURE_HOST__'
$ArgumentsPath = '__ARGUMENTS_PATH__'
$SessionRoot = '__SESSION_ROOT__'
$DataRoot = '__DATA_ROOT__'
$env:DEVPROJEX_INTERNAL_DATA_ROOT = $DataRoot
$env:TERM = "xterm-256color"
$env:NO_COLOR = $null
$env:CI = $null
[Console]::Clear()
[System.IO.File]::WriteAllText((Join-Path $SessionRoot "terminal-ready"), "ready")
while (-not (Test-Path -LiteralPath (Join-Path $SessionRoot "launch"))) {
    Start-Sleep -Milliseconds 50
}
try {
    $arguments = @(Get-Content -LiteralPath $ArgumentsPath -Encoding UTF8)
    $quotedArguments = @($arguments | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' })
    $process = Start-Process -FilePath $CaptureHost -ArgumentList $quotedArguments -NoNewWindow -PassThru
    [System.IO.File]::WriteAllText((Join-Path $SessionRoot "host.pid"), [string]$process.Id)
    $process.WaitForExit()
    [System.IO.File]::WriteAllText((Join-Path $SessionRoot "host-exit.txt"), [string]$process.ExitCode)
}
catch {
    [System.IO.File]::WriteAllText((Join-Path $SessionRoot "bootstrap-error.txt"), ($_ | Out-String))
    exit 91
}
'@
    $bootstrap = $bootstrapTemplate.Replace("__CAPTURE_HOST__", $HostBinary.Replace("'", "''"))
    $bootstrap = $bootstrap.Replace("__ARGUMENTS_PATH__", $argumentsPath.Replace("'", "''"))
    $bootstrap = $bootstrap.Replace("__SESSION_ROOT__", $SessionRoot.Replace("'", "''"))
    $bootstrap = $bootstrap.Replace("__DATA_ROOT__", $dataRoot.Replace("'", "''"))
    [System.IO.File]::WriteAllText(
        $Destination,
        $bootstrap,
        (New-Object System.Text.UTF8Encoding($false)))
    return $argumentsPath
}

function Start-TerminalWindow(
    [string]$Title,
    [string]$BootstrapPath,
    [string]$SessionRoot,
    [object]$TuiManifest) {
    $dataRoot = Join-Path $SessionRoot "app-data"
    New-Item -ItemType Directory -Path $dataRoot -Force | Out-Null
    $terminalArguments = @(
        "-w", "new",
        "--size", "$([int]$TuiManifest.initialColumns),$([int]$TuiManifest.initialRows)",
        "new-tab",
        "--title", $Title,
        "--suppressApplicationTitle",
        "--colorScheme", "Campbell",
        "powershell.exe",
        "-NoLogo",
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $BootstrapPath
    )
    & wt.exe @terminalArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Windows Terminal failed to create the Store TUI capture window."
    }

    $window = [IntPtr]::Zero
    $windowStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($window -eq [IntPtr]::Zero -and $windowStopwatch.Elapsed -lt [TimeSpan]::FromSeconds(20)) {
        $window = [DevProjex.StoreTuiCapture.NativeMethods]::FindWindow(
            "CASCADIA_HOSTING_WINDOW_CLASS",
            $Title)
        if ($window -eq [IntPtr]::Zero) {
            Start-Sleep -Milliseconds 50
        }
    }
    if ($window -eq [IntPtr]::Zero) {
        throw "The isolated Windows Terminal capture window was not found."
    }
    Wait-Until {
        Test-Path -LiteralPath (Join-Path $SessionRoot "terminal-ready")
    } ([TimeSpan]::FromSeconds(20)) "The isolated Windows Terminal bootstrap did not start."
    return $window
}

function Set-TerminalGeometry([IntPtr]$Window, [object]$TuiManifest) {
    $workingArea = (Get-TerminalScreen).WorkingArea
    $width = [int]$TuiManifest.windowWidth
    $height = [int]$TuiManifest.windowHeight
    if ($workingArea.Width -lt $width -or $workingArea.Height -lt $height) {
        throw "A desktop working area of at least ${width}x${height} is required for Store TUI capture."
    }
    $x = $workingArea.X + [int](($workingArea.Width - $width) / 2)
    $y = $workingArea.Y + [int](($workingArea.Height - $height) / 2)
    [DevProjex.StoreTuiCapture.NativeMethods]::ShowWindow($Window, 9) | Out-Null
    if (-not [DevProjex.StoreTuiCapture.NativeMethods]::MoveWindow(
            $Window, $x, $y, $width, $height, $true)) {
        throw "Unable to position the isolated Store TUI capture window."
    }
    $englishLayout = [DevProjex.StoreTuiCapture.NativeMethods]::LoadKeyboardLayout("00000409", 1)
    if ($englishLayout -eq [IntPtr]::Zero) {
        throw "Unable to load the English keyboard layout for deterministic Store TUI input."
    }
    [DevProjex.StoreTuiCapture.NativeMethods]::PostMessage(
        $Window, 0x0050, [IntPtr]::Zero, $englishLayout) | Out-Null
    Focus-CaptureWindow $Window
}

function Enter-TerminalScene([IntPtr]$Window, [string]$State, [object]$TuiManifest) {
    switch ($State) {
        "workspace" { return }
        "command-schema" {
            Send-CommandActivation $Window 200
            Send-CaptureKeys $Window "format " ([int]$TuiManifest.inputSettleMilliseconds)
            return
        }
        "action-palette" {
            Send-CaptureKeys $Window "{ESC}" 250
            Send-ControlKey $Window 0x50 ([int]$TuiManifest.inputSettleMilliseconds)
            return
        }
        "markdown" {
            Send-CaptureKeys $Window "{ESC}" 250
            Send-CommandActivation $Window 200
            Send-CaptureKeys $Window "format markdown{ENTER}" ([int]$TuiManifest.commandSettleMilliseconds)
            return
        }
        "json" {
            Send-CommandActivation $Window 200
            Send-CaptureKeys $Window "format json{ENTER}" ([int]$TuiManifest.commandSettleMilliseconds)
            return
        }
        default { throw "Unknown Store TUI scene state: $State" }
    }
}

function Stop-TerminalSession([IntPtr]$Window, [string]$SessionRoot) {
    if ([DevProjex.StoreTuiCapture.NativeMethods]::IsWindow($Window)) {
        try {
            Send-CaptureKeys $Window "q" 350
            Send-CaptureKeys $Window "{ENTER}" 700
            if (Test-Path -LiteralPath (Join-Path $SessionRoot "host.pid")) {
                Start-Sleep -Milliseconds 300
            }
            if ([DevProjex.StoreTuiCapture.NativeMethods]::IsWindow($Window)) {
                Send-CaptureKeys $Window "q" 350
                Send-CaptureKeys $Window "{ENTER}" 700
            }
        }
        catch {
            Write-Warning "Graceful Store TUI shutdown did not complete: $($_.Exception.Message)"
        }
    }

    $exitPath = Join-Path $SessionRoot "host-exit.txt"
    try {
        Wait-Until { Test-Path -LiteralPath $exitPath } ([TimeSpan]::FromSeconds(10)) "Store TUI host did not exit gracefully."
    }
    catch {
        $pidPath = Join-Path $SessionRoot "host.pid"
        if (Test-Path -LiteralPath $pidPath) {
            $captureHostPid = [int](Get-Content -LiteralPath $pidPath -Raw)
            Stop-Process -Id $captureHostPid -Force -ErrorAction SilentlyContinue
        }
    }
    if ([DevProjex.StoreTuiCapture.NativeMethods]::IsWindow($Window)) {
        [DevProjex.StoreTuiCapture.NativeMethods]::PostMessage($Window, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    }
}

function Start-TuiCapture(
    [string]$HostBinary,
    [string]$ShowcaseProject,
    [string]$SessionRoot,
    [string]$ScreenshotRoot,
    [object]$TuiManifest,
    [string]$LanguageCode) {
    $title = "DevProjex Store TUI $LanguageCode " + [Guid]::NewGuid().ToString("N")
    $bootstrapPath = Join-Path $SessionRoot "terminal-bootstrap.ps1"
    $argumentsPath = Write-TerminalBootstrap `
        $bootstrapPath `
        $HostBinary `
        $ShowcaseProject `
        $SessionRoot `
        $TuiManifest `
        $LanguageCode
    $window = [IntPtr]::Zero
    try {
        $window = Start-TerminalWindow $title $bootstrapPath $SessionRoot $TuiManifest
        Set-TerminalGeometry $window $TuiManifest
        Reset-TerminalAppearance $window
        New-Item -ItemType File -Path (Join-Path $SessionRoot "launch") -Force | Out-Null
        Wait-Until {
            $bootstrapError = Join-Path $SessionRoot "bootstrap-error.txt"
            if (Test-Path -LiteralPath $bootstrapError) {
                throw "Store TUI bootstrap failed: $(Get-Content -LiteralPath $bootstrapError -Raw)"
            }
            Test-Path -LiteralPath (Join-Path $SessionRoot "host.pid")
        } ([TimeSpan]::FromSeconds(30)) "The Store TUI capture host did not start."
        Start-Sleep -Milliseconds ([int]$TuiManifest.startupSettleMilliseconds)

        foreach ($scene in $TuiManifest.scenes) {
            Enter-TerminalScene $window ([string]$scene.state) $TuiManifest
            Focus-CaptureWindow $window
            $destination = Join-Path $ScreenshotRoot (
                Join-Path ([string]$scene.directory) ($LanguageCode.ToUpperInvariant() + ".png"))
            Save-TerminalFrame $window $TuiManifest $destination
            Write-Host "  [TUI:$LanguageCode] $($scene.index)/$($TuiManifest.scenes.Count + 5): $destination"
        }
    }
    finally {
        if ($window -ne [IntPtr]::Zero) {
            Stop-TerminalSession $window $SessionRoot
        }
    }
}

function ConvertTo-StoreCsvRow([string[]]$Values) {
    $encoded = foreach ($value in $Values) {
        $normalized = $value.Replace("`r`n", "`n").Replace("`r", "`n")
        $normalized = [System.Text.RegularExpressions.Regex]::Replace($normalized, "[ `t]+(?=`n|$)", "")
        $normalized = $normalized.Replace("`n", "`r`n")
        if ($normalized.IndexOfAny([char[]]@(',', '"', "`r", "`n")) -ge 0) {
            '"' + $normalized.Replace('"', '""') + '"'
        } else {
            $normalized
        }
    }
    return $encoded -join ','
}

function Update-TuiListingCsv(
    [string]$CsvPath,
    [object]$TuiManifest,
    [string[]]$LocaleColumns,
    [hashtable]$LocaleMap) {
    if (-not (Test-Path -LiteralPath $CsvPath -PathType Leaf)) {
        throw "Store listing CSV was not found: $CsvPath"
    }
    $rows = @(Import-Csv -LiteralPath $CsvPath)
    foreach ($scene in $TuiManifest.scenes) {
        $fieldName = "DesktopScreenshot$($scene.index)"
        $row = $rows | Where-Object { $_.Field -eq $fieldName } | Select-Object -First 1
        if ($null -eq $row) {
            throw "Store listing CSV does not contain '$fieldName'."
        }
        foreach ($locale in $LocaleColumns) {
            $languageCode = [string]$LocaleMap[$locale]
            $path = "ImportFolder/Screenshots/$($scene.directory)/$($languageCode.ToUpperInvariant()).png"
            $row.$locale = $path
        }
    }

    $columns = @($rows[0].PSObject.Properties.Name)
    $builder = New-Object System.Text.StringBuilder
    [void]$builder.Append((ConvertTo-StoreCsvRow $columns))
    [void]$builder.Append("`r`n")
    foreach ($row in $rows) {
        $values = @($columns | ForEach-Object { [string]$row.$_ })
        [void]$builder.Append((ConvertTo-StoreCsvRow $values))
        [void]$builder.Append("`r`n")
    }
    [System.IO.File]::WriteAllText(
        $CsvPath,
        $builder.ToString(),
        (New-Object System.Text.UTF8Encoding($false)))
}

function New-TuiContactSheet(
    [string]$ScreenshotRoot,
    [string[]]$LanguageCodes,
    [object]$TuiManifest,
    [string]$Destination) {
    $thumbnailWidth = 384
    $thumbnailHeight = 240
    $labelHeight = 30
    $sheet = [System.Drawing.Bitmap]::new(
        $thumbnailWidth * $TuiManifest.scenes.Count,
        ($thumbnailHeight + $labelHeight) * $LanguageCodes.Count)
    try {
        $graphics = [System.Drawing.Graphics]::FromImage($sheet)
        try {
            $graphics.Clear([System.Drawing.Color]::FromArgb(18, 18, 18))
            $font = [System.Drawing.Font]::new("Segoe UI", 11, [System.Drawing.FontStyle]::Bold)
            try {
                for ($languageIndex = 0; $languageIndex -lt $LanguageCodes.Count; $languageIndex++) {
                    $languageCode = $LanguageCodes[$languageIndex]
                    $rowY = $languageIndex * ($thumbnailHeight + $labelHeight)
                    foreach ($scene in $TuiManifest.scenes) {
                        $column = [int]$scene.index - 6
                        $sourcePath = Join-Path $ScreenshotRoot (
                            Join-Path ([string]$scene.directory) ($languageCode.ToUpperInvariant() + ".png"))
                        $source = [System.Drawing.Image]::FromFile($sourcePath)
                        try {
                            $graphics.DrawImage(
                                $source,
                                $column * $thumbnailWidth,
                                $rowY,
                                $thumbnailWidth,
                                $thumbnailHeight)
                        }
                        finally {
                            $source.Dispose()
                        }
                        $graphics.DrawString(
                            "$languageCode · $($scene.index) $($scene.name)",
                            $font,
                            [System.Drawing.Brushes]::White,
                            $column * $thumbnailWidth + 8,
                            $rowY + $thumbnailHeight + 4)
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
        New-Item -ItemType Directory -Path (Split-Path -Parent $Destination) -Force | Out-Null
        $sheet.Save($Destination, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $sheet.Dispose()
    }
}

$repositoryRoot = Resolve-RepositoryRoot
$storeListingRoot = Join-Path $repositoryRoot "Packaging\Windows\StoreListing"
$manifest = Get-Content -LiteralPath (Join-Path $storeListingRoot "store-screenshots.json") -Raw | ConvertFrom-Json
if ($null -eq $manifest.tui -or $manifest.tui.scenes.Count -ne 5) {
    throw "store-screenshots.json must declare exactly five TUI scenes."
}

$resolvedOutputRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    Join-Path $storeListingRoot "ImportFolder"
} else {
    [System.IO.Path]::GetFullPath($OutputRoot)
}
$resolvedListingCsv = if ([string]::IsNullOrWhiteSpace($ListingCsv)) {
    Join-Path $resolvedOutputRoot "listingData.csv"
} else {
    [System.IO.Path]::GetFullPath($ListingCsv)
}
$listingRows = @(Import-Csv -LiteralPath $resolvedListingCsv)
$localeColumns = @(Get-StoreLocaleColumns $listingRows)
$supportedCodes = @(Get-LocalizationCodes $repositoryRoot)
$localeMap = @{}
foreach ($locale in $localeColumns) {
    $localeMap[$locale] = Resolve-AppLanguageCode $locale $supportedCodes
}
$captureLanguages = if ($null -ne $Languages -and $Languages.Count -gt 0) {
    @(
        $Languages |
            ForEach-Object { $_ -split ',' } |
            ForEach-Object { $_.Trim().Replace("_", "-").ToLowerInvariant() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Sort-Object -Unique |
            ForEach-Object {
                if ($supportedCodes -notcontains $_) {
                    throw "TUI capture language '$_' has no application localization catalog."
                }
                $_
            }
    )
} else {
    @($supportedCodes | Sort-Object -Unique)
}

Write-Host "TUI scenes: $(@($manifest.tui.scenes | ForEach-Object { $_.name }) -join ', ')"
Write-Host "Store locales: $($localeColumns -join ', ')"
Write-Host "TUI captures: $($captureLanguages -join ', ')"
if ($PlanOnly) {
    return
}

$isWindowsPlatform = [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [System.Runtime.InteropServices.OSPlatform]::Windows)
if (-not $isWindowsPlatform -or -not [Environment]::UserInteractive) {
    throw "Real Store TUI screenshot generation requires an interactive Windows desktop session."
}
if ($null -eq (Get-Command wt.exe -ErrorAction SilentlyContinue)) {
    throw "Windows Terminal (wt.exe) is required for Store TUI capture."
}

$resolvedCaptureHost = if (-not [string]::IsNullOrWhiteSpace($CaptureHost)) {
    [System.IO.Path]::GetFullPath($CaptureHost)
} else {
    Join-Path $repositoryRoot "artifacts\store-screenshots\tui-host\DevProjex.StoreMediaCapture.exe"
}
if (-not $SkipBuild) {
    $resolvedCaptureHost = Build-CaptureHost $repositoryRoot (Split-Path -Parent $resolvedCaptureHost)
}
if (-not (Test-Path -LiteralPath $resolvedCaptureHost -PathType Leaf)) {
    throw "Store TUI capture host was not found: $resolvedCaptureHost"
}

$screenshotRoot = Join-Path $resolvedOutputRoot "Screenshots"
$sessionRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "DevProjex\store-tui-screenshot-captures\" + [Guid]::NewGuid().ToString("N"))
$snapshotDrive = $null
New-Item -ItemType Directory -Path $sessionRoot -Force | Out-Null
try {
    $showcaseProject = if ([string]::IsNullOrWhiteSpace($ProjectPath)) {
        $snapshotRoot = New-CleanProjectSnapshot $repositoryRoot $sessionRoot
        Initialize-ShowcaseRepository $snapshotRoot
        $mountedSnapshot = Mount-CleanProjectSnapshot $snapshotRoot
        $snapshotDrive = [string]$mountedSnapshot.Drive
        [string]$mountedSnapshot.ProjectPath
    } else {
        [System.IO.Path]::GetFullPath($ProjectPath)
    }
    if (-not (Test-Path -LiteralPath $showcaseProject -PathType Container)) {
        throw "Store showcase project was not found: $showcaseProject"
    }

    Initialize-TerminalCapture
    foreach ($languageCode in $captureLanguages) {
        $languageSession = Join-Path $sessionRoot $languageCode
        New-Item -ItemType Directory -Path $languageSession -Force | Out-Null
        Start-TuiCapture `
            $resolvedCaptureHost `
            $showcaseProject `
            $languageSession `
            $screenshotRoot `
            $manifest.tui `
            $languageCode
    }
    Update-TuiListingCsv $resolvedListingCsv $manifest.tui $localeColumns $localeMap
    $contactSheet = Join-Path $repositoryRoot "artifacts\store-screenshots\tui-contact-sheet.png"
    New-TuiContactSheet $screenshotRoot $captureLanguages $manifest.tui $contactSheet

    & (Join-Path $repositoryRoot "Scripts\validate-store-listing.ps1")
    if ($LASTEXITCODE -ne 0) {
        throw "Store listing validation failed with exit code $LASTEXITCODE."
    }

    Write-Host "Store TUI screenshots: $screenshotRoot"
    Write-Host "Import CSV: $resolvedListingCsv"
    Write-Host "TUI contact sheet: $contactSheet"
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($snapshotDrive)) {
        & subst $snapshotDrive /D | Out-Null
    }
    if (-not $KeepSessionData -and (Test-Path -LiteralPath $sessionRoot)) {
        Remove-Item -LiteralPath $sessionRoot -Recurse -Force
    } elseif ($KeepSessionData) {
        Write-Host "TUI capture session retained: $sessionRoot"
    }
}
