#Requires -Version 7.0
# Records the Terminal Workspace half of the README demo in Windows Terminal: mouse selection,
# the : command line, :grep and the action palette. Windows Terminal hides the pointer from GDI
# capture, so the pointer path is logged and drawn by readme-demo-gif.ps1.
param(
    [Parameter(Mandatory)] [string]$TerminalExe,
    [Parameter(Mandatory)] [string]$ProjectPath,
    [Parameter(Mandatory)] [string]$SessionRoot,
    [Parameter(Mandatory)] [string]$Ffmpeg,
    [Parameter(Mandatory)] [string]$Output,
    # Cell size and the centre of the first text row, in physical pixels, for Windows Terminal's
    # default font at 125% scaling. Other setups pass their own measurements.
    [double]$CellWidth = 12.1,
    [double]$RowHeight = 23,
    [double]$FirstRowY = 72
)
. (Join-Path $PSScriptRoot 'readme-demo-common.ps1')

$layout = Get-DemoLayout
$region = $layout.Region
Remove-Item $SessionRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path (Join-Path $SessionRoot 'app-data') -Force | Out-Null

# The terminal starts a bootstrap that waits for a go signal, so the window can be placed and its
# appearance reset before the workspace draws its first frame.
$title = 'DevProjex README demo ' + [DateTime]::Now.ToString('HHmmss')
$bootstrap = Join-Path $SessionRoot 'bootstrap.ps1'
$launch = Join-Path $SessionRoot 'launch'
@"
`$env:DEVPROJEX_INTERNAL_DATA_ROOT = '$(Join-Path $SessionRoot 'app-data')'
`$env:TERM = 'xterm-256color'
[Console]::Clear()
while (-not (Test-Path '$launch')) { Start-Sleep -Milliseconds 50 }
& '$TerminalExe' tui '$ProjectPath' --profile standard --screen alternate --mouse --color always --language en
"@ | Set-Content -Path $bootstrap -Encoding utf8
& wt.exe -w new --size 160,46 new-tab --title $title --suppressApplicationTitle --colorScheme Campbell powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File $bootstrap
$deadline = [DateTime]::UtcNow.AddSeconds(20); $h = [IntPtr]::Zero
while ([DateTime]::UtcNow -lt $deadline -and $h -eq [IntPtr]::Zero) {
    $h = [ReadmeDemoNative]::FindWindow('CASCADIA_HOSTING_WINDOW_CLASS', $title); Start-Sleep -Milliseconds 100
}
if ($h -eq [IntPtr]::Zero) { throw 'The Windows Terminal window was not found.' }
Set-WindowBounds $h $layout.Window
Start-Sleep -Milliseconds 600
Set-DemoForeground $h
# Default font size and full opacity, whatever the user's terminal profile says.
Send-DemoKeys 'ctrl+0'; Start-Sleep -Milliseconds 300
$center = [System.Drawing.Point]::new($layout.Window.X + 870, $layout.Window.Y + 540)
[ReadmeDemoNative]::SetCursorPos($center.X, $center.Y) | Out-Null
[ReadmeDemoNative]::Key(0x11, 0, 0); [ReadmeDemoNative]::Key(0x10, 0, 0)
for ($i = 0; $i -lt 20; $i++) { [ReadmeDemoNative]::Mouse(0x0800, 120) }
[ReadmeDemoNative]::Key(0x10, 0, 2); [ReadmeDemoNative]::Key(0x11, 0, 2)
Start-Sleep -Milliseconds 400
Set-Content -Path $launch -Value go
Start-Sleep -Seconds 9
Set-WindowBounds $h $layout.Window; Start-Sleep -Milliseconds 800
$win = Get-WindowRect $h
$terminalProcess = Get-Process WindowsTerminal | Where-Object { $_.Id -ne 0 } | Select-Object -First 1
Hide-DemoOtherWindows $region $terminalProcess.Id
Start-Sleep -Milliseconds 500
Set-DemoForeground $h; Start-Sleep -Milliseconds 500

function RowY([int]$row) { [int]($win.Y + $FirstRowY + $RowHeight * $row) }
# Tree rows: the checkbox of a first-level item is two cells right of its expand arrow.
function TreeCheck([int]$row) { [System.Drawing.Point]::new([int]($win.X + 31 + 5.3 * $CellWidth), (RowY $row)) }
function TreeArrow([int]$row) { [System.Drawing.Point]::new([int]($win.X + 31 + 2.6 * $CellWidth), (RowY $row)) }

$flashBounds = [System.Drawing.Rectangle]::new($region.X + 20, $region.Y + 20, 70, 50)
$trigger = Join-Path $SessionRoot 'flash-trigger'
Start-DemoFlashHelper $flashBounds $trigger | Out-Null
Start-Sleep -Seconds 2
Set-DemoForeground $h; Start-Sleep -Milliseconds 300
$restPoint = [System.Drawing.Point]::new($win.X + 900, $win.Y + 560)
[ReadmeDemoNative]::SetCursorPos($restPoint.X, $restPoint.Y) | Out-Null
$script:LogMousePath = $true
$recording = $null
try {
    $recording = Start-DemoRecording $region "$Output.mkv" $Ffmpeg
    Add-DemoEvent @{ type = 'mouse'; x = $restPoint.X; y = $restPoint.Y }
    Wait-Demo 1.0
    Invoke-DemoFlashSync $flashBounds $trigger $h
    Add-DemoEvent @{ type = 'mouse'; x = $restPoint.X; y = $restPoint.Y }
    Wait-Demo 0.6

    Set-DemoCaption 'The same workflow in your terminal'
    Wait-Demo 0.6
    foreach ($row in @(4, 7)) { Invoke-DemoClick (TreeCheck $row) -Dwell 0.06; Wait-Demo 0.35 }   # Application, Docs
    Invoke-DemoClick (TreeArrow 4); Wait-Demo 1.3

    Set-DemoCaption 'Type : for commands with inline hints'
    Move-DemoMouse ([System.Drawing.Point]::new($win.X + 900, $win.Y + 700)); Wait-Demo 0.2
    Send-DemoText ':' 0.3; Send-DemoText 'format markdown' 0.07; Wait-Demo 0.6
    Send-DemoKeys 'enter'; Wait-Demo 1.6

    Set-DemoCaption 'Search the selection with :grep'
    Send-DemoText ':' 0.3; Send-DemoText 'grep SecretRedactionContext' 0.06; Wait-Demo 0.3
    Send-DemoKeys 'enter'; Wait-Demo 3.2
    Send-DemoKeys 'esc'; Wait-Demo 0.6

    Set-DemoCaption 'Every action is one search away: Ctrl+P'
    Send-DemoKeys 'ctrl+p'; Wait-Demo 0.8
    Send-DemoText 'json' 0.09; Wait-Demo 0.8
    Send-DemoKeys 'enter'; Wait-Demo 0.8
    Send-DemoKeys 'down'; Wait-Demo 0.5
    Send-DemoKeys 'enter'; Wait-Demo 1.2
    Move-DemoMouse ([System.Drawing.Point]::new($win.X + 1000, $win.Y + 520)); Wait-Demo 0.2
    Invoke-DemoScroll -10 0.1; Wait-Demo 1.4
}
finally {
    if ($recording) { Stop-DemoRecording $recording "$Output.events.json" }
    $script:LogMousePath = $false
    Restore-DemoOtherWindows
    Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $TerminalExe } | Stop-Process -Force
}
