#Requires -Version 7.0
# Records the desktop half of the README demo: selection, preview, name filter, secret masking,
# code compression and Live Context with a scripted agent. Writes <Output>.mkv and
# <Output>.events.json for readme-demo-gif.ps1.
param(
    [Parameter(Mandatory)] [string]$DesktopExe,
    [Parameter(Mandatory)] [string]$TerminalExe,
    [Parameter(Mandatory)] [string]$ProjectPath,
    [Parameter(Mandatory)] [string]$DataRoot,
    [Parameter(Mandatory)] [string]$Ffmpeg,
    [Parameter(Mandatory)] [string]$Output
)
. (Join-Path $PSScriptRoot 'readme-demo-common.ps1')

$layout = Get-DemoLayout
$region = $layout.Region

# A clean desktop session: only the appearance and view preferences survive between runs.
New-Item -ItemType Directory -Path (Join-Path $DataRoot 'DevProjex') -Force | Out-Null
Get-ChildItem (Join-Path $DataRoot 'DevProjex') -Force |
    Where-Object { $_.Name -notlike 'theme-settings.json*' -and $_.Name -notlike 'user-settings.json*' } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
$env:DEVPROJEX_INTERNAL_DATA_ROOT = $DataRoot
& $DesktopExe open $ProjectPath --language en | Out-Null
$deadline = [DateTime]::UtcNow.AddSeconds(30); $app = $null
while ([DateTime]::UtcNow -lt $deadline) {
    $app = Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $DesktopExe -and $_.MainWindowHandle -ne [IntPtr]::Zero } | Select-Object -First 1
    if ($app) { break }
    Start-Sleep -Milliseconds 300
}
if (-not $app) { throw 'The DevProjex window did not appear.' }
$h = $app.MainWindowHandle
Set-WindowBounds $h $layout.Window
Start-Sleep -Seconds 4
$root = Get-UiRoot $h
$win = Get-WindowRect $h
Hide-DemoOtherWindows $region $app.Id
Start-Sleep -Seconds 1
Set-DemoForeground $h; Start-Sleep -Milliseconds 600

function Node([string]$name) { Find-Ui -Root $root -Name $name -ControlType TreeItem }
function Chevron([string]$name) { Find-Ui -Root (Node $name) -AutomationId 'PART_ExpandCollapseChevron' }
function Check([string]$name) { Find-Ui -Root (Node $name) -Name $name -ControlType CheckBox }
function Btn([string]$id) { Find-Ui -Root $root -AutomationId $id }
function Named([string]$name, [string]$type) { Find-Ui -Root $root -Name $name -ControlType $type }
function At([int]$x, [int]$y) { [System.Drawing.Point]::new($win.X + $x, $win.Y + $y) }
# The delivery marker sits right after the file name.
function Marker([string]$file) {
    $r = (Find-Ui -Root (Node $file) -Name $file -ControlType Text).Current.BoundingRectangle
    return [System.Drawing.Point]::new([int]($r.Right + 13), [int]($r.Y + $r.Height / 2))
}

# Agent activity markers are a persisted view preference; turn them on before recording. The
# View menu entry carries its label in a child element, so it is looked up by name alone.
$activity = $null
for ($attempt = 0; $attempt -lt 3 -and $null -eq $activity; $attempt++) {
    Invoke-UiClick (Named 'View' 'MenuItem') -NoRing; Start-Sleep -Milliseconds 700
    $activity = Find-Ui -Root $root -Name 'Agent activity' -Optional -TimeoutSeconds 1.5
    if ($null -eq $activity) { Send-DemoKeys 'esc'; Start-Sleep -Milliseconds 300 }
}
if ($null -eq $activity) { throw 'The Agent activity menu entry was not found.' }
Invoke-UiClick $activity -NoRing; Start-Sleep -Milliseconds 800
if ($null -ne (Find-Ui -Root $root -Name 'Agent activity' -Optional -TimeoutSeconds 0.4)) { Send-DemoKeys 'esc'; Start-Sleep -Milliseconds 300 }

$syncA = At 300 ($win.Height - 60)
$syncB = At 900 560
[ReadmeDemoNative]::SetCursorPos($syncA.X, $syncA.Y) | Out-Null
$recording = $null; $agent = $null
try {
    $recording = Start-DemoRecording $region "$Output.mkv" $Ffmpeg
    Wait-Demo 1.0
    # The pointer jump is the sync point the encoder looks for.
    [ReadmeDemoNative]::SetCursorPos($syncB.X, $syncB.Y) | Out-Null
    Add-DemoEvent @{ type = 'sync'; x = $syncB.X; y = $syncB.Y }
    Wait-Demo 0.7

    Set-DemoCaption 'Check what the AI should see — tokens are counted live'
    Invoke-UiClick (Chevron 'Application'); Wait-Demo 0.8
    foreach ($folder in @('Context', 'Ranking', 'Secrets', 'Selection')) { Invoke-UiClick (Check $folder) -Dwell 0.05; Wait-Demo 0.15 }
    Wait-Demo 0.2
    $status = At ($win.Width - 220) ($win.Height - 26)
    Move-DemoMouse $status; Wait-Demo 1.3

    Set-DemoCaption 'Preview the exact context before you send it'
    Invoke-UiClick (Btn 'PreviewToggleButton'); Wait-Demo 1.0
    foreach ($format in @('MD', 'JSON', 'ASCII')) { Invoke-UiClick (Named $format 'Button'); Wait-Demo 0.8 }
    Invoke-UiClick (Btn 'PreviewTreeAndContentModeButton'); Wait-Demo 0.9
    Move-DemoMouse (At 1000 600); Wait-Demo 0.15
    Invoke-DemoScroll -16 0.05; Wait-Demo 0.6; Invoke-DemoScroll 16 0.025; Wait-Demo 0.3

    Set-DemoCaption 'Filter the whole tree by name'
    Invoke-UiClick (Btn 'FilterToggleButton'); Wait-Demo 0.4
    Send-DemoText 'Safety' 0.07; Wait-Demo 1.3
    $file = Node 'CliProfileDataSafetyProcessTests.cs'
    Invoke-UiClick (Find-Ui -Root $file -Name 'CliProfileDataSafetyProcessTests.cs' -ControlType CheckBox); Wait-Demo 0.4
    Invoke-UiClick (Btn 'PreviewContentModeButton'); Wait-Demo 0.8

    # The test fixture's fake GitHub token is plainly visible on line 16; one checkbox masks it.
    Set-DemoCaption 'Hide secrets: keys are masked before they leave your machine'
    Move-DemoMouse (At 1010 552); Wait-Demo 1.3
    Invoke-UiClick (Named 'Hide secrets' 'CheckBox'); Wait-Demo 0.3
    Invoke-UiClick (Btn 'ApplySettingsButton'); Wait-Demo 1.0
    Move-DemoMouse (At 1046 552); Wait-Demo 2.0

    Set-DemoCaption 'Compress code: keep the signatures, drop the bodies'
    Invoke-UiClick (Named 'Compress code' 'CheckBox'); Wait-Demo 0.3
    Invoke-UiClick (Btn 'ApplySettingsButton'); Wait-Demo 1.4
    Move-DemoMouse (At 1000 640); Wait-Demo 0.15
    Invoke-DemoScroll -7 0.15; Wait-Demo 0.5
    Invoke-UiClick (Btn 'FilterCloseButton'); Wait-Demo 0.9
    Move-DemoMouse (At 1000 640); Wait-Demo 0.15
    Invoke-DemoScroll -12 0.11; Wait-Demo 0.4
    Move-DemoMouse $status; Wait-Demo 1.1

    Set-DemoCaption 'Live Context: your AI agent follows these checkboxes'
    Invoke-UiClick (Chevron 'Secrets'); Wait-Demo 0.6
    Move-DemoMouse (At 330 18); Wait-Demo 0.2
    $agent = Start-DemoAgent $TerminalExe $ProjectPath $DataRoot 'claude-code' -Live
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do { Start-Sleep -Milliseconds 150; $app.Refresh() } while ($app.MainWindowTitle -notlike '*Live*' -and [DateTime]::UtcNow -lt $deadline)
    # The window counts deliveries once it has picked up the session; point at the new title meanwhile.
    Move-DemoMouse (At 470 18); Wait-Demo 1.6
    Move-DemoMouse (At 420 760); Wait-Demo 1.4
    Invoke-DemoAgentTool $agent 'search_project' ([ordered]@{ pattern = 'SecretRedactionContext'; paths = @('Application/Secrets') }); Wait-Demo 1.0
    Invoke-DemoAgentTool $agent 'get_file' ([ordered]@{ path = 'Application/Secrets/SecretRedactionContext.cs' }); Wait-Demo 1.0
    Invoke-DemoAgentTool $agent 'get_file' ([ordered]@{ path = 'Application/Secrets/SecretScanCache.cs' }); Wait-Demo 1.0
    Invoke-DemoAgentTool $agent 'get_file' ([ordered]@{ requests = @(@{ path = 'Application/Secrets/SecretTokenBoundary.cs' }, @{ path = 'Application/Secrets/MarkedSecretsMatcher.cs' }) }); Wait-Demo 0.9
    Set-DemoCaption '✦ marks every file the agent received'
    Move-DemoMouse (Marker 'SecretRedactionContext.cs'); Wait-Demo 2.8
}
finally {
    if ($recording) { Stop-DemoRecording $recording "$Output.events.json" }
    Stop-DemoAgent $agent
    Restore-DemoOtherWindows
    if (-not $app.HasExited) { $app.CloseMainWindow() | Out-Null; if (-not $app.WaitForExit(15000)) { $app.Kill() } }
}
