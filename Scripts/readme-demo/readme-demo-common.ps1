#Requires -Version 7.0
# Shared helpers for the README demo recording: real mouse and keyboard input on real DevProjex
# windows, UI Automation lookups, a scripted MCP client, ffmpeg capture and the event log that the
# GIF encoder reads back (sync point, captions, clicks and, where needed, the pointer path).
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms

if (-not ('ReadmeDemoNative' -as [type])) {
    Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class ReadmeDemoNative {
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
    [DllImport("user32.dll")] public static extern bool SetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct WINDOWPLACEMENT { public int length, flags, showCmd; public POINT min, max; public RECT normal; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public INPUTUNION u; }
    public static void Mouse(uint flags, uint data) {
        var i = new INPUT[1]; i[0].type = 0; i[0].u.mi.dwFlags = flags; i[0].u.mi.mouseData = data;
        SendInput(1, i, Marshal.SizeOf(typeof(INPUT)));
    }
    public static void Key(ushort vk, ushort scan, uint flags) {
        var i = new INPUT[1]; i[0].type = 1; i[0].u.ki.wVk = vk; i[0].u.ki.wScan = scan; i[0].u.ki.dwFlags = flags;
        SendInput(1, i, Marshal.SizeOf(typeof(INPUT)));
    }
    // Visible, uncloaked top-level windows of other processes that overlap a rectangle.
    public static List<IntPtr> VisibleOn(int left, int top, int right, int bottom, uint keepPid) {
        var list = new List<IntPtr>();
        EnumWindows((h, l) => {
            if (!IsWindowVisible(h) || IsIconic(h) || GetWindowTextLength(h) == 0) return true;
            int cloaked; if (DwmGetWindowAttribute(h, 14, out cloaked, 4) == 0 && cloaked != 0) return true;
            var sb = new StringBuilder(256); GetClassName(h, sb, 256); var cls = sb.ToString();
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return true;
            uint pid; GetWindowThreadProcessId(h, out pid); if (pid == keepPid) return true;
            RECT r; GetWindowRect(h, out r);
            if (r.Right <= left || r.Left >= right || r.Bottom <= top || r.Top >= bottom) return true;
            list.Add(h); return true;
        }, IntPtr.Zero);
        return list;
    }
}
'@
}
# Per-monitor v2: every coordinate in these scripts is a physical pixel, like ffmpeg gdigrab's.
[ReadmeDemoNative]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null

$script:DemoClock = $null
$script:DemoEvents = [System.Collections.Generic.List[object]]::new()
$script:LogMousePath = $false
$script:LastMouseLog = -1
$script:HiddenWindows = [System.Collections.Generic.List[object]]::new()

function Get-DemoTime { if ($null -eq $script:DemoClock) { return 0.0 }; return $script:DemoClock.Elapsed.TotalSeconds }
function Add-DemoEvent([hashtable]$Event) { $Event['t'] = [Math]::Round((Get-DemoTime), 3); $script:DemoEvents.Add([pscustomobject]$Event) }
function Wait-Demo([double]$Seconds) { Start-Sleep -Milliseconds ([int]($Seconds * 1000)) }
function Set-DemoCaption([string]$Text) { Add-DemoEvent @{ type = 'caption'; text = $Text } }

# The frame is a 2048x1280 region centred in the primary working area, the window a 1740x1080
# rectangle centred in it: the proportions of the Store screenshots, with wallpaper around.
function Get-DemoLayout {
    $area = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    if ($area.Width -lt 2048 -or $area.Height -lt 1280) {
        throw "The primary working area must be at least 2048x1280 physical pixels; actual: $($area.Width)x$($area.Height)."
    }
    $region = [System.Drawing.Rectangle]::new($area.X + [int](($area.Width - 2048) / 2), $area.Y + [int](($area.Height - 1280) / 2), 2048, 1280)
    $window = [System.Drawing.Rectangle]::new($region.X + 154, $region.Y + 100, 1740, 1080)
    return [pscustomobject]@{ Region = $region; Window = $window }
}

function Get-WindowRect([IntPtr]$Handle) {
    $r = New-Object ReadmeDemoNative+RECT
    [ReadmeDemoNative]::GetWindowRect($Handle, [ref]$r) | Out-Null
    return [System.Drawing.Rectangle]::new($r.Left, $r.Top, $r.Right - $r.Left, $r.Bottom - $r.Top)
}

function Set-WindowBounds([IntPtr]$Handle, [System.Drawing.Rectangle]$Bounds) {
    [ReadmeDemoNative]::ShowWindow($Handle, 9) | Out-Null
    [ReadmeDemoNative]::SetWindowPos($Handle, [IntPtr]::Zero, $Bounds.X, $Bounds.Y, $Bounds.Width, $Bounds.Height, 0x0040) | Out-Null
}

function Set-DemoForeground([IntPtr]$Handle) {
    $foreground = [ReadmeDemoNative]::GetForegroundWindow()
    $current = [ReadmeDemoNative]::GetCurrentThreadId()
    $other = [ReadmeDemoNative]::GetWindowThreadProcessId($foreground, [IntPtr]::Zero)
    [ReadmeDemoNative]::AttachThreadInput($current, $other, $true) | Out-Null
    [ReadmeDemoNative]::SetForegroundWindow($Handle) | Out-Null
    [ReadmeDemoNative]::AttachThreadInput($current, $other, $false) | Out-Null
}

# Minimizes every other window that overlaps the recorded region and remembers its placement.
function Hide-DemoOtherWindows([System.Drawing.Rectangle]$Region, [int]$KeepProcessId) {
    foreach ($handle in [ReadmeDemoNative]::VisibleOn($Region.Left, $Region.Top, $Region.Right, $Region.Bottom, [uint32]$KeepProcessId)) {
        $placement = New-Object ReadmeDemoNative+WINDOWPLACEMENT
        $placement.length = [System.Runtime.InteropServices.Marshal]::SizeOf($placement)
        [ReadmeDemoNative]::GetWindowPlacement($handle, [ref]$placement) | Out-Null
        $script:HiddenWindows.Add([pscustomobject]@{ Handle = $handle; Placement = $placement })
        [ReadmeDemoNative]::ShowWindow($handle, 6) | Out-Null
    }
}

function Restore-DemoOtherWindows {
    for ($i = $script:HiddenWindows.Count - 1; $i -ge 0; $i--) {
        $placement = $script:HiddenWindows[$i].Placement
        [ReadmeDemoNative]::SetWindowPlacement($script:HiddenWindows[$i].Handle, [ref]$placement) | Out-Null
    }
    $script:HiddenWindows.Clear()
}

# ---------------- UI Automation ----------------
function Get-UiRoot([IntPtr]$Handle) { [System.Windows.Automation.AutomationElement]::FromHandle($Handle) }

function Find-Ui {
    param($Root, [string]$Name, [string]$AutomationId, [string]$ControlType, [double]$TimeoutSeconds = 8, [switch]$Optional)
    $conditions = [System.Collections.Generic.List[System.Windows.Automation.Condition]]::new()
    if ($Name) { $conditions.Add([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name)) }
    if ($AutomationId) { $conditions.Add([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)) }
    if ($ControlType) {
        $type = [System.Windows.Automation.ControlType]::$ControlType
        $conditions.Add([System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $type))
    }
    $condition = if ($conditions.Count -eq 1) { $conditions[0] } else { [System.Windows.Automation.AndCondition]::new($conditions.ToArray()) }
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $found = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            if ($null -ne $found) { return $found }
        }
        catch { }
        Start-Sleep -Milliseconds 120
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($Optional) { return $null }
    throw "UI element not found: name='$Name' id='$AutomationId' type='$ControlType'."
}

function Get-UiPoint($Element) {
    $r = $Element.Current.BoundingRectangle
    return [System.Drawing.Point]::new([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2))
}

# ---------------- Mouse ----------------
function Get-CursorPoint {
    $p = New-Object ReadmeDemoNative+POINT
    [ReadmeDemoNative]::GetCursorPos([ref]$p) | Out-Null
    return [System.Drawing.Point]::new($p.X, $p.Y)
}

# A human-like move: ease-in-out timing on a gently bowed path; longer moves take longer.
function Move-DemoMouse([System.Drawing.Point]$To, [double]$DurationSeconds = 0) {
    $from = Get-CursorPoint
    $dx = $To.X - $from.X; $dy = $To.Y - $from.Y
    $distance = [Math]::Sqrt($dx * $dx + $dy * $dy)
    if ($distance -lt 2) { [ReadmeDemoNative]::SetCursorPos($To.X, $To.Y) | Out-Null; return }
    if ($DurationSeconds -le 0) { $DurationSeconds = [Math]::Min(0.75, 0.2 + $distance / 2300.0) }
    $bow = [Math]::Min(60, $distance * 0.06) * $(if ($dx -ge 0) { 1 } else { -1 })
    $nx = -$dy / $distance; $ny = $dx / $distance
    $clock = [System.Diagnostics.Stopwatch]::StartNew(); $script:LastMouseLog = -1
    if ($script:LogMousePath) { Add-DemoEvent @{ type = 'mouse'; x = $from.X; y = $from.Y } }
    while ($true) {
        $k = [Math]::Min(1.0, $clock.Elapsed.TotalSeconds / $DurationSeconds)
        $e = if ($k -lt 0.5) { 4 * $k * $k * $k } else { 1 - [Math]::Pow(-2 * $k + 2, 3) / 2 }
        $arc = [Math]::Sin([Math]::PI * $e) * $bow
        $x = [int][Math]::Round($from.X + $dx * $e + $nx * $arc); $y = [int][Math]::Round($from.Y + $dy * $e + $ny * $arc)
        [ReadmeDemoNative]::SetCursorPos($x, $y) | Out-Null
        if ($script:LogMousePath -and $clock.Elapsed.TotalSeconds - $script:LastMouseLog -ge 0.02) {
            Add-DemoEvent @{ type = 'mouse'; x = $x; y = $y }
            $script:LastMouseLog = $clock.Elapsed.TotalSeconds
        }
        if ($k -ge 1.0) { break }
        Start-Sleep -Milliseconds 7
    }
    if ($script:LogMousePath) { Add-DemoEvent @{ type = 'mouse'; x = $To.X; y = $To.Y } }
}

function Invoke-DemoClick([System.Drawing.Point]$At, [double]$Dwell = 0.12, [switch]$NoRing) {
    Move-DemoMouse $At
    Wait-Demo $Dwell
    if (-not $NoRing) { Add-DemoEvent @{ type = 'click'; x = $At.X; y = $At.Y } }
    [ReadmeDemoNative]::Mouse(0x0002, 0); Start-Sleep -Milliseconds 70; [ReadmeDemoNative]::Mouse(0x0004, 0)
}

function Invoke-UiClick($Element, [double]$Dwell = 0.12, [switch]$NoRing) { Invoke-DemoClick (Get-UiPoint $Element) -Dwell $Dwell -NoRing:$NoRing }
function Invoke-DemoHover($Element) { Move-DemoMouse (Get-UiPoint $Element) }

# Wheel scrolling in single notches reads as a smooth scroll in the recording.
function Invoke-DemoScroll([int]$Notches, [double]$Interval = 0.09) {
    $delta = if ($Notches -lt 0) { [uint32](0x100000000 - 120) } else { [uint32]120 }
    for ($i = 0; $i -lt [Math]::Abs($Notches); $i++) { [ReadmeDemoNative]::Mouse(0x0800, $delta); Wait-Demo $Interval }
}

# ---------------- Keyboard ----------------
$script:VirtualKeys = @{ ctrl = 0x11; shift = 0x10; alt = 0x12; enter = 0x0D; esc = 0x1B; tab = 0x09; space = 0x20; up = 0x26; down = 0x28 }

function Send-DemoKeys([string]$Chord) {
    $codes = foreach ($part in $Chord.ToLowerInvariant().Split('+')) {
        if ($script:VirtualKeys.ContainsKey($part)) { [uint16]$script:VirtualKeys[$part] }
        elseif ($part.Length -eq 1) { [uint16][char]$part.ToUpperInvariant() }
        else { throw "Unknown key: $part" }
    }
    foreach ($code in $codes) { [ReadmeDemoNative]::Key($code, 0, 0); Start-Sleep -Milliseconds 25 }
    [Array]::Reverse($codes)
    foreach ($code in $codes) { [ReadmeDemoNative]::Key($code, 0, 2); Start-Sleep -Milliseconds 25 }
}

# Unicode typing at a readable, slightly irregular pace; independent of the keyboard layout.
function Send-DemoText([string]$Text, [double]$CharSeconds = 0.075) {
    $i = 0
    foreach ($ch in $Text.ToCharArray()) {
        [ReadmeDemoNative]::Key(0, [uint16]$ch, 0x0004); [ReadmeDemoNative]::Key(0, [uint16]$ch, 0x0006)
        Start-Sleep -Milliseconds ([int](($CharSeconds + (($i * 37) % 7) / 100.0) * 1000)); $i++
    }
}

# ---------------- Scripted MCP client ----------------
# A real `devprojex mcp` process driven over stdio, the way an agent such as Claude Code drives it.
function Start-DemoAgent([string]$Binary, [string]$ProjectPath, [string]$DataRoot, [string]$ClientName, [switch]$Live) {
    $arguments = "mcp --root `"$ProjectPath`""
    if ($Live) { $arguments += ' --live' }
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($Binary, $arguments)
    $startInfo.UseShellExecute = $false; $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardInput = $true; $startInfo.RedirectStandardOutput = $true; $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $startInfo.EnvironmentVariables['DEVPROJEX_INTERNAL_DATA_ROOT'] = $DataRoot
    $process = [System.Diagnostics.Process]::Start($startInfo)
    $agent = [pscustomobject]@{ Process = $process; NextId = 1; Errors = $process.StandardError.ReadToEndAsync() }
    Invoke-DemoAgentRequest $agent 'initialize' ([ordered]@{
        protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = [ordered]@{ name = $ClientName; version = '2.0.0' } }) | Out-Null
    Send-DemoAgentMessage $agent ([ordered]@{ jsonrpc = '2.0'; method = 'notifications/initialized' })
    return $agent
}

function Send-DemoAgentMessage($Agent, $Message) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($Message | ConvertTo-Json -Depth 32 -Compress) + "`n")
    $Agent.Process.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length)
    $Agent.Process.StandardInput.BaseStream.Flush()
}

function Invoke-DemoAgentRequest($Agent, [string]$Method, $Params) {
    $id = $Agent.NextId; $Agent.NextId = $id + 1
    Send-DemoAgentMessage $Agent ([ordered]@{ jsonrpc = '2.0'; id = $id; method = $Method; params = $Params })
    $deadline = [DateTime]::UtcNow.AddSeconds(60)
    while ([DateTime]::UtcNow -lt $deadline) {
        $read = $Agent.Process.StandardOutput.ReadLineAsync()
        if (-not $read.Wait($deadline - [DateTime]::UtcNow)) { break }
        $line = $read.Result
        if ($null -eq $line) { throw "The MCP server closed its output. $($Agent.Errors.Result)" }
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $message = $line | ConvertFrom-Json
        $names = @($message.PSObject.Properties.Name)
        if ($names -contains 'id' -and [int]$message.id -eq $id) {
            if ($names -contains 'error') { throw "MCP '$Method' failed: $($message.error.message)" }
            return $message.result
        }
    }
    throw "MCP '$Method' timed out."
}

function Invoke-DemoAgentTool($Agent, [string]$Name, $Arguments) {
    Invoke-DemoAgentRequest $Agent 'tools/call' ([ordered]@{ name = $Name; arguments = $Arguments }) | Out-Null
    Add-DemoEvent @{ type = 'agent'; tool = $Name }
}

function Stop-DemoAgent($Agent) {
    if ($null -eq $Agent) { return }
    try {
        if (-not $Agent.Process.HasExited) {
            $Agent.Process.StandardInput.Close()
            if (-not $Agent.Process.WaitForExit(20000)) { $Agent.Process.Kill() }
        }
    }
    finally { $Agent.Process.Dispose() }
}

# ---------------- Sync and recording ----------------
# A small square flashed on the wallpaper outside the window marks a known log time in the video
# when the pointer itself is not captured: Windows Terminal hides it from GDI capture.
function Start-DemoFlashHelper([System.Drawing.Rectangle]$Bounds, [string]$TriggerPath) {
    Remove-Item $TriggerPath -ErrorAction SilentlyContinue
    $code = @"
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type 'using System; using System.Runtime.InteropServices; public static class B { [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c); }'
[B]::SetThreadDpiAwarenessContext([IntPtr](-4)) | Out-Null
while (-not (Test-Path '$TriggerPath')) { Start-Sleep -Milliseconds 4 }
`$f = New-Object System.Windows.Forms.Form
`$f.FormBorderStyle = 'None'; `$f.ShowInTaskbar = `$false; `$f.StartPosition = 'Manual'; `$f.TopMost = `$true
`$f.BackColor = [System.Drawing.Color]::Magenta
`$f.Bounds = New-Object System.Drawing.Rectangle($($Bounds.X), $($Bounds.Y), $($Bounds.Width), $($Bounds.Height))
`$f.Show(); [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 260; `$f.Close()
"@
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($code))
    return Start-Process pwsh -ArgumentList @('-NoProfile', '-EncodedCommand', $encoded) -PassThru -WindowStyle Hidden
}

function Invoke-DemoFlashSync([System.Drawing.Rectangle]$Bounds, [string]$TriggerPath, [IntPtr]$Refocus) {
    Set-Content -Path $TriggerPath -Value go
    Add-DemoEvent @{ type = 'sync'; x = $Bounds.X + [int]($Bounds.Width / 2); y = $Bounds.Y + [int]($Bounds.Height / 2) }
    Start-Sleep -Milliseconds 400
    Set-DemoForeground $Refocus
}

# Lossless capture of the region; the clock starts once ffmpeg is running and the sync event later
# measures the exact offset between this clock and the video timeline.
function Start-DemoRecording([System.Drawing.Rectangle]$Region, [string]$Output, [string]$Ffmpeg) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($Ffmpeg)
    foreach ($argument in @('-hide_banner', '-loglevel', 'error', '-y', '-f', 'gdigrab', '-framerate', '30', '-draw_mouse', '1',
            '-offset_x', "$($Region.X)", '-offset_y', "$($Region.Y)", '-video_size', "$($Region.Width)x$($Region.Height)",
            '-i', 'desktop', '-c:v', 'libx264rgb', '-preset', 'ultrafast', '-crf', '0', $Output)) {
        $startInfo.ArgumentList.Add($argument)
    }
    $startInfo.UseShellExecute = $false; $startInfo.RedirectStandardInput = $true; $startInfo.CreateNoWindow = $true
    $process = [System.Diagnostics.Process]::Start($startInfo)
    Start-Sleep -Milliseconds 700
    $script:DemoClock = [System.Diagnostics.Stopwatch]::StartNew()
    $script:DemoEvents.Clear()
    Add-DemoEvent @{ type = 'start'; x = $Region.X; y = $Region.Y; w = $Region.Width; h = $Region.Height }
    return $process
}

function Stop-DemoRecording($Process, [string]$EventsPath) {
    Add-DemoEvent @{ type = 'end' }
    $Process.StandardInput.Write('q'); $Process.StandardInput.Flush()
    if (-not $Process.WaitForExit(30000)) { $Process.Kill() }
    $script:DemoEvents | ConvertTo-Json -Depth 5 | Set-Content -Path $EventsPath -Encoding utf8
}
