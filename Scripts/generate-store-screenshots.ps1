[CmdletBinding()]
param(
    [string]$PublishedExe,
    [string]$PartnerCenterCsv,
    [string]$ProjectPath,
    [string]$OutputRoot,
    [string[]]$Languages,
    [string[]]$Scenes,
    [switch]$SkipPublish,
    [switch]$KeepSessionData,
    [switch]$PlanOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Relative arguments follow the PowerShell location, which can differ from the process directory.
function Resolve-CallerPath([string]$Path) {
    return $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)
}

function Resolve-RepositoryRoot {
    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
}

function Resolve-PartnerCenterCsv([string]$RepositoryRoot, [string]$ExplicitPath) {
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $resolved = Resolve-CallerPath $ExplicitPath
        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "Partner Center CSV was not found: $resolved"
        }
        return $resolved
    }

    $candidates = @(@(
        Get-ChildItem -LiteralPath $RepositoryRoot -Filter "listingData-*.csv" -File -ErrorAction SilentlyContinue
        Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot "Packaging\Windows\StoreListing") -Filter "listingData-*.csv" -File -ErrorAction SilentlyContinue
        Get-ChildItem -LiteralPath (Join-Path $RepositoryRoot "Packaging\Windows\StoreListing") -Filter "Exported*.csv" -File -ErrorAction SilentlyContinue
    ) | Sort-Object LastWriteTimeUtc -Descending)
    if ($candidates.Count -eq 0) {
        throw "No Partner Center listingData export was found. Pass -PartnerCenterCsv explicitly."
    }
    return $candidates[0].FullName
}

function Get-StoreLocaleColumns([object[]]$Rows) {
    if ($Rows.Count -eq 0) {
        throw "Partner Center CSV is empty."
    }

    return @(Select-StoreLocaleColumns @($Rows[0].PSObject.Properties.Name))
}

function Select-StoreLocaleColumns([string[]]$Headers) {
    $metadataColumns = @("Field", "ID", "Type", "default")
    $localizedTypeHeader = '^Type \(.+\)$'
    return @($Headers | Where-Object { $metadataColumns -notcontains $_ -and $_ -notmatch $localizedTypeHeader })
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

function Assert-GuiSceneDeclarations([object]$Manifest, [string]$RepositoryRoot) {
    $scenes = @($Manifest.scenes)
    $indices = @($scenes | ForEach-Object { [int]$_.index } | Sort-Object)
    if ($scenes.Count -ne 5 -or ($indices -join ',') -ne "1,2,3,4,5") {
        throw "store-screenshots.json must declare GUI scenes 1-5 exactly once."
    }
    foreach ($scene in $scenes) {
        if ([string]$scene.directory -cne ("{0}_{1}" -f [int]$scene.index, [string]$scene.name)) {
            throw "GUI scene '$($scene.name)' must use the directory '$($scene.index)_$($scene.name)'."
        }
    }

    $agentSessions = if ($null -ne $Manifest.PSObject.Properties["agentSessions"]) { $Manifest.agentSessions } else { $null }
    if ($null -eq $agentSessions -or
        $null -eq $agentSessions.PSObject.Properties["live"] -or
        $null -eq $agentSessions.PSObject.Properties["earlier"] -or
        $null -eq $agentSessions.PSObject.Properties["liveSelection"]) {
        throw "store-screenshots.json must declare the agent sessions shown by the Live context scenes."
    }
    $selection = @($agentSessions.liveSelection)
    if ($selection.Count -eq 0) {
        throw "store-screenshots.json must declare a Live context selection."
    }
    foreach ($relativePath in $selection) {
        $path = [string]$relativePath
        if ([System.IO.Path]::IsPathRooted($path) -or @($path -split '[\\/]') -contains ".." -or
            -not (Test-Path -LiteralPath (Join-Path $RepositoryRoot $path))) {
            throw "Live context selection entry '$path' must name an existing project path."
        }
    }
    foreach ($session in @(@($agentSessions.earlier) + @($agentSessions.live))) {
        if ([string]::IsNullOrWhiteSpace([string]$session.clientName) -or @($session.calls).Count -eq 0) {
            throw "Every scripted agent session must name its client and declare at least one call."
        }
    }
}

function Select-GuiScenes([object]$Manifest, [string[]]$Requested) {
    # Scenes can be named by index, name or directory ("3", "Mcp_Menu", "3_Mcp_Menu"). Without a
    # selection every declared scene is captured.
    $declared = @($Manifest.scenes)
    $tokens = @(
        @($Requested) |
            Where-Object { $null -ne $_ } |
            ForEach-Object { ([string]$_) -split ',' } |
            ForEach-Object { $_.Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )
    if ($tokens.Count -eq 0) {
        return $declared
    }

    $selected = @{}
    foreach ($token in $tokens) {
        $match = @($declared | Where-Object {
            [string]::Equals([string]$_.index, $token, [System.StringComparison]::Ordinal) -or
            [string]::Equals([string]$_.name, $token, [System.StringComparison]::OrdinalIgnoreCase) -or
            [string]::Equals([string]$_.directory, $token, [System.StringComparison]::OrdinalIgnoreCase)
        })
        if ($match.Count -ne 1) {
            throw "GUI scene '$token' is not declared in store-screenshots.json."
        }
        $selected[[int]$match[0].index] = $match[0]
    }
    return @($selected.Keys | Sort-Object | ForEach-Object { $selected[$_] })
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

    if (-not ("DevProjex.StoreCapture.WindowActivation" -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

namespace DevProjex.StoreCapture
{
    public static class WindowActivation
    {
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachInput);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool BringWindowToTop(IntPtr window);
    }
}
"@
    }

    # PowerShell 5.1 is commonly system-DPI-aware. Switching only this capture thread to
    # per-monitor V2 keeps Win32 window coordinates and bitmap pixels in the same space.
    [DevProjex.StoreCapture.NativeMethods]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null
}

function Set-CaptureForegroundWindow([IntPtr]$Window) {
    # Windows lets a process take the foreground only while it owns the latest input. A capture
    # started from a background shell would otherwise leave the window inactive, with a dimmed
    # title bar, so this thread briefly shares input state with the current foreground window.
    $activation = [DevProjex.StoreCapture.WindowActivation]
    $foreground = $activation::GetForegroundWindow()
    if ($foreground -eq $Window) {
        return
    }
    $currentThread = $activation::GetCurrentThreadId()
    $foregroundThread = $activation::GetWindowThreadProcessId($foreground, [IntPtr]::Zero)
    $attached = $foregroundThread -ne 0 -and
        $foregroundThread -ne $currentThread -and
        $activation::AttachThreadInput($currentThread, $foregroundThread, $true)
    try {
        $activation::BringWindowToTop($Window) | Out-Null
        [DevProjex.StoreCapture.NativeMethods]::SetForegroundWindow($Window) | Out-Null
    }
    finally {
        if ($attached) {
            $activation::AttachThreadInput($currentThread, $foregroundThread, $false) | Out-Null
        }
    }
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
    Set-CaptureForegroundWindow $Process.MainWindowHandle
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

function Assert-CaptureProcessRunning([string]$SessionDirectory, [System.Diagnostics.Process]$Process, [string]$Expected) {
    $failurePath = Join-Path $SessionDirectory "failure.json"
    if (Test-Path -LiteralPath $failurePath) {
        $failure = Get-Content -LiteralPath $failurePath -Raw
        throw "DevProjex Store capture failed: $failure"
    }
    if ($Process.HasExited) {
        throw "DevProjex exited before Store capture state '$Expected' was ready. Exit code: $($Process.ExitCode)."
    }
}

function Wait-ForCaptureEvent(
    [string]$SessionDirectory,
    [System.Diagnostics.Process]$Process,
    [string[]]$PendingStems,
    [bool]$AcceptLiveSessionRequest) {
    # The application decides the scene order. The controller reacts to whichever declared
    # scene is ready next and to the one-time request to start the live agent session.
    $found = @{ Value = $null }
    Wait-Until {
        if ($AcceptLiveSessionRequest -and
            (Test-Path -LiteralPath (Join-Path $SessionDirectory "live-session-request.json"))) {
            $found.Value = "live-session-request"
            return $true
        }
        foreach ($stem in $PendingStems) {
            if (Test-Path -LiteralPath (Join-Path $SessionDirectory "ready-$stem.json")) {
                $found.Value = $stem
                return $true
            }
        }
        Assert-CaptureProcessRunning $SessionDirectory $Process "next scene"
        return $false
    } ([TimeSpan]::FromMinutes(3)) "Timed out waiting for the next Store capture scene."
    return [string]$found.Value
}

function Write-CaptureMarker([string]$SessionDirectory, [string]$FileName, [string]$Content) {
    $path = Join-Path $SessionDirectory $FileName
    $temporaryPath = "$path.tmp"
    [System.IO.File]::WriteAllText($temporaryPath, $Content, (New-Object System.Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temporaryPath -Destination $path -Force
}

function ConvertTo-ProcessArgument([string]$Value) {
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') {
        return $Value
    }
    $escaped = [System.Text.RegularExpressions.Regex]::Replace($Value, '(\\*)"', '$1$1\"')
    $escaped = [System.Text.RegularExpressions.Regex]::Replace($escaped, '(\\+)$', '$1$1')
    return '"' + $escaped + '"'
}

function Start-StoreAgentServer([string]$Binary, [string]$ProjectPath, [string]$DataRoot, [bool]$Live) {
    $arguments = "mcp --root " + (ConvertTo-ProcessArgument $ProjectPath)
    if ($Live) {
        $arguments += " --live"
    }
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($Binary, $arguments)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WorkingDirectory = Split-Path -Parent $Binary
    $startInfo.RedirectStandardInput = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = New-Object System.Text.UTF8Encoding($false)
    # The journal, the live-session heartbeat and the saved window selection all resolve
    # under this root, which is the same isolated directory the capture window uses.
    $startInfo.EnvironmentVariables["DEVPROJEX_INTERNAL_DATA_ROOT"] = $DataRoot
    $startInfo.EnvironmentVariables.Remove("DEVPROJEX_INTERNAL_STORE_CAPTURE")
    $process = [System.Diagnostics.Process]::Start($startInfo)
    return [pscustomobject]@{
        Process = $process
        StandardError = $process.StandardError.ReadToEndAsync()
        NextId = 1
    }
}

function Send-StoreAgentMessage([object]$Server, [object]$Message) {
    $json = ($Message | ConvertTo-Json -Depth 32 -Compress) + "`n"
    $bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($json)
    $stream = $Server.Process.StandardInput.BaseStream
    $stream.Write($bytes, 0, $bytes.Length)
    $stream.Flush()
}

function Invoke-StoreAgentRequest([object]$Server, [string]$Method, [object]$Params) {
    $id = [int]$Server.NextId
    $Server.NextId = $id + 1
    Send-StoreAgentMessage $Server ([ordered]@{ jsonrpc = "2.0"; id = $id; method = $Method; params = $Params })

    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    while ($true) {
        $remaining = $deadline - [DateTime]::UtcNow
        if ($remaining -le [TimeSpan]::Zero) {
            throw "The DevProjex MCP server did not answer '$Method' in time."
        }
        $read = $Server.Process.StandardOutput.ReadLineAsync()
        if (-not $read.Wait($remaining)) {
            throw "The DevProjex MCP server did not answer '$Method' in time."
        }
        $line = $read.Result
        if ($null -eq $line) {
            throw "The DevProjex MCP server closed its output during '$Method'. $($Server.StandardError.Result)"
        }
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        $message = $line | ConvertFrom-Json
        $names = @($message.PSObject.Properties.Name)
        if ($names -notcontains "id" -or [int]$message.id -ne $id) {
            continue
        }
        if ($names -contains "error") {
            throw "The DevProjex MCP server rejected '$Method': $($message.error.message)"
        }
        return $message.result
    }
}

function Initialize-StoreAgentSession([object]$Server, [object]$Session) {
    Invoke-StoreAgentRequest $Server "initialize" ([ordered]@{
        protocolVersion = "2025-06-18"
        capabilities = @{}
        clientInfo = [ordered]@{
            name = [string]$Session.clientName
            version = [string]$Session.clientVersion
        }
    }) | Out-Null
    Send-StoreAgentMessage $Server ([ordered]@{ jsonrpc = "2.0"; method = "notifications/initialized" })
}

function Invoke-StoreAgentCalls([object]$Server, [object]$Session) {
    $calls = @($Session.calls)
    foreach ($call in $calls) {
        $result = Invoke-StoreAgentRequest $Server "tools/call" ([ordered]@{
            name = [string]$call.name
            arguments = $call.arguments
        })
        if (@($result.PSObject.Properties.Name) -contains "isError" -and $result.isError) {
            throw "The scripted MCP call '$($call.name)' failed: $(@($result.content)[0].text)"
        }
    }
    return $calls.Count
}

function Stop-StoreAgentServer([object]$Server) {
    $process = $Server.Process
    try {
        if (-not $process.HasExited) {
            # Closing stdin is how a real client ends the session; the server then writes the
            # journal end record and removes its live-session heartbeat.
            $process.StandardInput.Close()
            if (-not $process.WaitForExit(20000)) {
                $process.Kill()
                $process.WaitForExit()
                return $false
            }
        }
        return $process.ExitCode -eq 0
    }
    finally {
        $process.Dispose()
    }
}

function Invoke-StoreAgentSession([string]$Binary, [string]$ProjectPath, [string]$DataRoot, [object]$Session) {
    $server = Start-StoreAgentServer $Binary $ProjectPath $DataRoot ([bool]$Session.live)
    $stopped = $false
    try {
        Initialize-StoreAgentSession $server $Session
        Invoke-StoreAgentCalls $server $Session | Out-Null
        $stopped = $true
        if (-not (Stop-StoreAgentServer $server)) {
            throw "The scripted '$($Session.clientName)' MCP session did not end cleanly."
        }
    }
    finally {
        if (-not $stopped) {
            Stop-StoreAgentServer $server | Out-Null
        }
    }
}

function Start-StoreLiveAgentSession(
    [string]$Binary,
    [string]$ProjectPath,
    [string]$DataRoot,
    [string]$SessionDirectory,
    [object]$Session,
    [System.Diagnostics.Process]$AppProcess) {
    $server = Start-StoreAgentServer $Binary $ProjectPath $DataRoot $true
    try {
        Initialize-StoreAgentSession $server $Session
        Write-CaptureMarker $SessionDirectory "live-session-connected" "connected"
        # The window marks only calls made after it first observed the session.
        Wait-ForCaptureState $SessionDirectory "live-session-observed.json" $AppProcess | Out-Null
        $callCount = Invoke-StoreAgentCalls $server $Session
        Write-CaptureMarker $SessionDirectory "live-session-calls-complete.json" (@{ calls = $callCount } | ConvertTo-Json -Compress)
        return $server
    }
    catch {
        Stop-StoreAgentServer $server | Out-Null
        throw
    }
}

function Start-LanguageCapture(
    [string]$Binary,
    [string]$LanguageCode,
    [string]$ShowcaseProject,
    [string]$SessionRoot,
    [string]$ScreenshotRoot,
    [object]$Manifest,
    [object[]]$SelectedScenes,
    [System.Drawing.Rectangle]$WorkingArea) {
    $languageSession = Join-Path $SessionRoot $LanguageCode
    $appData = Join-Path $languageSession "app-data"
    New-Item -ItemType Directory -Path $appData -Force | Out-Null
    $agentSessions = $Manifest.agentSessions
    # Earlier sessions complete before the window starts, so the journal already holds a short
    # history when the live session begins.
    foreach ($earlierSession in @($agentSessions.earlier)) {
        Invoke-StoreAgentSession $Binary $ShowcaseProject $appData $earlierSession
    }

    $requestPath = Join-Path $languageSession "request.json"
    $requestJson = @{
        projectPath = $ShowcaseProject
        sessionDirectory = $languageSession
        appDataDirectory = $appData
        languageCode = $LanguageCode
        liveContextSelection = [string[]]@($agentSessions.liveSelection)
    } | ConvertTo-Json
    [System.IO.File]::WriteAllText(
        $requestPath,
        $requestJson,
        (New-Object System.Text.UTF8Encoding($false)))

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($Binary)
    $startInfo.UseShellExecute = $false
    $startInfo.WorkingDirectory = Split-Path -Parent $Binary
    $startInfo.EnvironmentVariables["DEVPROJEX_INTERNAL_STORE_CAPTURE"] = $requestPath
    # Settings and journals already follow the capture request; this keeps the window's
    # desktop-control registration out of the user's state directory as well.
    $startInfo.EnvironmentVariables["DEVPROJEX_INTERNAL_DATA_ROOT"] = $appData
    $process = [System.Diagnostics.Process]::Start($startInfo)
    $liveServer = $null
    try {
        Wait-ForCaptureState $languageSession "window-ready.json" $process | Out-Null
        $captureRegion = Set-CaptureWindowGeometry $process $Manifest $WorkingArea
        New-Item -ItemType File -Path (Join-Path $languageSession "window-positioned") -Force | Out-Null

        $pendingScenes = [ordered]@{}
        foreach ($scene in $Manifest.scenes) {
            $pendingScenes[("{0:D2}-{1}" -f [int]$scene.index, [string]$scene.name)] = $scene
        }
        while ($pendingScenes.Count -gt 0) {
            $captureEvent = Wait-ForCaptureEvent `
                $languageSession `
                $process `
                ([string[]]@($pendingScenes.Keys)) `
                ($null -eq $liveServer)
            if ($captureEvent -eq "live-session-request") {
                $liveServer = Start-StoreLiveAgentSession `
                    $Binary `
                    $ShowcaseProject `
                    $appData `
                    $languageSession `
                    $agentSessions.live `
                    $process
                continue
            }

            $stem = $captureEvent
            $scene = $pendingScenes[$stem]
            $pendingScenes.Remove($stem)
            # The application always walks through every scene, because later scenes build on
            # the state of earlier ones. Scenes outside the selection are acknowledged unsaved,
            # so their images in the output folder stay untouched.
            if (@($SelectedScenes | Where-Object { [int]$_.index -eq [int]$scene.index }).Count -eq 0) {
                New-Item -ItemType File -Path (Join-Path $languageSession "captured-$stem") -Force | Out-Null
                continue
            }
            $readyState = Get-Content -LiteralPath (Join-Path $languageSession "ready-$stem.json") -Raw | ConvertFrom-Json
            if ([string]$readyState.foreground -ne "owned") {
                Set-CaptureForegroundWindow $process.MainWindowHandle
            }
            # The application has already completed its state/render barriers. This short
            # guard only lets DWM present that final frame before desktop pixel capture.
            Start-Sleep -Milliseconds ([int]$Manifest.captureSettleMilliseconds)
            $destination = Join-Path $ScreenshotRoot (Join-Path ([string]$scene.directory) ($LanguageCode.ToUpperInvariant() + ".png"))
            Save-DesktopRegion $captureRegion $destination
            New-Item -ItemType File -Path (Join-Path $languageSession "captured-$stem") -Force | Out-Null
            Write-Host "  [$LanguageCode] $($scene.index) $($scene.name): $destination"
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
        if ($null -ne $liveServer -and -not (Stop-StoreAgentServer $liveServer)) {
            Write-Warning "The live MCP session for '$LanguageCode' did not end cleanly."
        }
    }
}

function Update-ListingCsvScreenshotRows(
    [string]$Path,
    [hashtable]$LocaleMap,
    [object]$Manifest) {
    # An existing import CSV is edited in place: only the GUI screenshot rows are rewritten, so
    # its encoding, header, quoting, text rows and the TUI rows stay byte-for-byte unchanged.
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $encoding = New-Object System.Text.UTF8Encoding($hasBom, $true)
    $offset = if ($hasBom) { 3 } else { 0 }
    $text = $encoding.GetString($bytes, $offset, $bytes.Length - $offset)
    $headerEnd = $text.IndexOf("`r`n", [System.StringComparison]::Ordinal)
    if ($headerEnd -lt 0) {
        throw "Import CSV has no CRLF-terminated header: $Path"
    }
    $headers = @($text.Substring(0, $headerEnd).Split(','))
    $localeColumns = @(Select-StoreLocaleColumns $headers)

    foreach ($scene in $Manifest.scenes) {
        $field = "DesktopScreenshot$($scene.index)"
        $pattern = "(?m)^" + [System.Text.RegularExpressions.Regex]::Escape($field) + ",[^`r`n]*(?=`r`n|$)"
        $rowMatches = [System.Text.RegularExpressions.Regex]::Matches($text, $pattern)
        if ($rowMatches.Count -ne 1) {
            throw "Import CSV must contain exactly one '$field' row; found $($rowMatches.Count)."
        }
        $line = $rowMatches[0].Value
        $values = @($line.Split(','))
        if ($line.Contains('"') -or $values.Count -ne $headers.Count) {
            throw "Import CSV row '$field' is not a plain single-line record."
        }
        foreach ($locale in $localeColumns) {
            $column = [Array]::IndexOf($headers, $locale)
            if (-not $LocaleMap.ContainsKey($locale)) {
                throw "Import CSV locale '$locale' is not present in the Partner Center export."
            }
            $languageCode = [string]$LocaleMap[$locale]
            $values[$column] = "ImportFolder/Screenshots/$($scene.directory)/$($languageCode.ToUpperInvariant()).png"
        }
        $text = $text.Remove($rowMatches[0].Index, $rowMatches[0].Length).Insert($rowMatches[0].Index, ($values -join ','))
    }

    $output = New-Object System.IO.MemoryStream
    try {
        $encoded = $encoding.GetBytes($text)
        if ($hasBom) {
            $output.Write($bytes, 0, 3)
        }
        $output.Write($encoded, 0, $encoded.Length)
        [System.IO.File]::WriteAllBytes($Path, $output.ToArray())
    }
    finally {
        $output.Dispose()
    }
}

function Update-ListingCsv(
    [object[]]$Rows,
    [string[]]$LocaleColumns,
    [hashtable]$LocaleMap,
    [object]$Manifest,
    [string]$Destination) {
    if (Test-Path -LiteralPath $Destination -PathType Leaf) {
        Update-ListingCsvScreenshotRows $Destination $LocaleMap $Manifest
        return
    }

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
    [object[]]$Scenes,
    [string]$Destination) {
    $thumbnailWidth = 512
    $thumbnailHeight = 320
    $labelHeight = 28
    $sheet = [System.Drawing.Bitmap]::new(
        $thumbnailWidth * $Scenes.Count,
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
                    for ($column = 0; $column -lt $Scenes.Count; $column++) {
                        $scene = $Scenes[$column]
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
$captureLanguages = if ($null -ne $Languages -and $Languages.Count -gt 0) {
    @(
        $Languages |
            ForEach-Object { $_ -split ',' } |
            ForEach-Object { $_.Trim().Replace("_", "-").ToLowerInvariant() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Sort-Object -Unique |
            ForEach-Object {
                if ($supportedCodes -notcontains $_) {
                    throw "GUI capture language '$_' has no application localization catalog."
                }
                $_
            }
    )
} else {
    @($supportedCodes | Sort-Object -Unique)
}
$captureLanguages = @($captureLanguages)
Assert-GuiSceneDeclarations $manifest $repositoryRoot
$captureScenes = @(Select-GuiScenes $manifest $Scenes)

Write-Host "Partner Center CSV: $partnerCsvPath"
Write-Host "Store locales: $($localeColumns -join ', ')"
Write-Host "Application captures: $($captureLanguages -join ', ')"
Write-Host "GUI scenes: $(@($captureScenes | ForEach-Object { $_.name }) -join ', ')"
Write-Host "Agent sessions: $(@(@($manifest.agentSessions.earlier) + @($manifest.agentSessions.live) | ForEach-Object { $_.clientName }) -join ', ')"
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
    Resolve-CallerPath $OutputRoot
}
$screenshotRoot = Join-Path $resolvedOutputRoot "Screenshots"
New-Item -ItemType Directory -Path $screenshotRoot -Force | Out-Null

$resolvedPublishedExe = if (-not [string]::IsNullOrWhiteSpace($PublishedExe)) {
    Resolve-CallerPath $PublishedExe
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
        Resolve-CallerPath $ProjectPath
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
            $captureScenes `
            $workingArea
    }

    $listingCsv = Join-Path $resolvedOutputRoot "listingData.csv"
    Update-ListingCsv $rows $localeColumns $localeMap $manifest $listingCsv
    $contactSheet = Join-Path $repositoryRoot "artifacts\store-screenshots\contact-sheet.png"
    New-Item -ItemType Directory -Path (Split-Path -Parent $contactSheet) -Force | Out-Null
    New-ContactSheet $screenshotRoot $captureLanguages $captureScenes $contactSheet

    # The validator checks the repository import folder, so it only applies when that folder
    # is the output; a scratch output root is reviewed through its contact sheet instead.
    $repositoryImportFolder = [System.IO.Path]::GetFullPath((Join-Path $storeListingRoot "ImportFolder"))
    if ([string]::Equals(
            $resolvedOutputRoot.TrimEnd('\'),
            $repositoryImportFolder.TrimEnd('\'),
            [System.StringComparison]::OrdinalIgnoreCase)) {
        & (Join-Path $repositoryRoot "Scripts\validate-store-listing.ps1")
        if ($LASTEXITCODE -ne 0) {
            throw "Store listing validation failed with exit code $LASTEXITCODE."
        }
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
