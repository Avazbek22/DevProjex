#Requires -Version 7.0
<#
.SYNOPSIS
    Records and encodes the README demo GIF: the desktop app, then Terminal Workspace.

.DESCRIPTION
    Drives real DevProjex windows with real mouse and keyboard input on the interactive desktop,
    records them with ffmpeg, and encodes one GIF with captions and click rings. The repository
    itself, as a clean `git archive` snapshot, is the showcase project.

    Requirements: Windows 11, the default wallpaper, Windows Terminal, a primary working area of at
    least 2048x1280 physical pixels at 125% scaling (the Terminal Workspace click positions are
    measured for it; see readme-demo-terminal.ps1), and a desktop left alone for about two minutes.
    Other windows over the recorded region are minimized and restored afterwards; the regional
    format is switched to en-US for the recording and restored. Every DevProjex process uses an
    isolated data root, so the user's settings, journal and profiles are not touched.

    ffmpeg and gifsicle are taken from -Ffmpeg and -Gifsicle, or installed with npm into the work
    directory. Without -DesktopExe and -TerminalExe the script publishes both for win-x64.

.EXAMPLE
    ./Scripts/readme-demo/generate-readme-demo.ps1
    ./Scripts/readme-demo/generate-readme-demo.ps1 -NoCaptions -Output ./demo-clean.gif
#>
[CmdletBinding()]
param(
    [string]$DesktopExe,
    [string]$TerminalExe,
    [string]$Output,
    [string]$Ffmpeg,
    [string]$Gifsicle,
    [int]$Width = 1440,
    [int]$Fps = 20,
    [int]$Lossy = 30,
    [switch]$NoCaptions,
    [switch]$KeepRecordings,
    [switch]$PlanOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (-not $Output) { $Output = Join-Path $repositoryRoot 'Docs\Media\readme-demo\devprojex-demo.gif' }
$Output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
if (-not $IsWindows) { throw 'The README demo is recorded on Windows.' }
if ($null -eq (Get-Command wt.exe -ErrorAction SilentlyContinue)) { throw 'Windows Terminal (wt.exe) is required.' }

. (Join-Path $PSScriptRoot 'readme-demo-common.ps1')
$layout = Get-DemoLayout
Write-Host "Frame: $($layout.Region); window: $($layout.Window)"
if ($PlanOnly) { return }

$work = Join-Path ([IO.Path]::GetTempPath()) ('devprojex-readme-demo-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$drive = $null
$originalCulture = (Get-Culture).Name
$cultureChanged = $false
try {
    if (-not $Ffmpeg -or -not $Gifsicle) {
        $tools = Join-Path $work 'tools'
        New-Item -ItemType Directory -Path $tools | Out-Null
        & npm install --prefix $tools --no-audit --no-fund ffmpeg-static@5 gifsicle@7 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Installing ffmpeg-static and gifsicle with npm failed.' }
        if (-not $Ffmpeg) { $Ffmpeg = Join-Path $tools 'node_modules\ffmpeg-static\ffmpeg.exe' }
        if (-not $Gifsicle) { $Gifsicle = Join-Path $tools 'node_modules\gifsicle\vendor\gifsicle.exe' }
    }

    $publishOptions = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '/p:PublishSingleFile=true',
        '/p:IncludeNativeLibrariesForSelfExtract=true', '/p:DebugType=None', '/p:DebugSymbols=false')
    if (-not $DesktopExe) {
        & dotnet publish (Join-Path $repositoryRoot 'Apps\Avalonia\DevProjex.Avalonia.csproj') @publishOptions -o (Join-Path $work 'desktop') | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Publishing the desktop app failed.' }
        $DesktopExe = Join-Path $work 'desktop\DevProjex.exe'
    }
    if (-not $TerminalExe) {
        & dotnet publish (Join-Path $repositoryRoot 'Apps\TerminalHost\DevProjex.TerminalHost.csproj') @publishOptions -o (Join-Path $work 'terminal') | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Publishing the terminal host failed.' }
        $TerminalExe = Join-Path $work 'terminal\devprojex.exe'
    }
    $DesktopExe = (Resolve-Path $DesktopExe).Path
    $TerminalExe = (Resolve-Path $TerminalExe).Path

    # The showcase project: a clean snapshot of HEAD behind a short drive path.
    $snapshot = Join-Path $work 'showcase\DevProjex'
    New-Item -ItemType Directory -Path $snapshot -Force | Out-Null
    & git -C $repositoryRoot archive --format=tar --output=(Join-Path $work 'showcase.tar') HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Creating the repository snapshot failed.' }
    & tar -xf (Join-Path $work 'showcase.tar') -C $snapshot
    if ($LASTEXITCODE -ne 0) { throw 'Extracting the repository snapshot failed.' }
    foreach ($letter in 'S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z') {
        if (Test-Path "$($letter):\") { continue }
        & subst "$($letter):" (Split-Path $snapshot -Parent)
        if ($LASTEXITCODE -eq 0) { $drive = "$($letter):"; break }
    }
    if (-not $drive) { throw 'No free drive letter was available for the showcase path.' }
    $project = "$drive\DevProjex"

    # English UI text also needs English number formatting.
    Set-Culture en-US
    $cultureChanged = $true
    $desktopStem = Join-Path $work 'desktop'
    $terminalStem = Join-Path $work 'terminal'
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'readme-demo-desktop.ps1') -DesktopExe $DesktopExe -TerminalExe $TerminalExe `
        -ProjectPath $project -DataRoot (Join-Path $work 'app-data') -Ffmpeg $Ffmpeg -Output $desktopStem
    if ($LASTEXITCODE -ne 0) { throw 'Recording the desktop part failed.' }
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'readme-demo-terminal.ps1') -TerminalExe $TerminalExe `
        -ProjectPath $project -SessionRoot (Join-Path $work 'terminal-session') -Ffmpeg $Ffmpeg -Output $terminalStem
    if ($LASTEXITCODE -ne 0) { throw 'Recording the terminal part failed.' }
    Set-Culture $originalCulture
    $cultureChanged = $false

    New-Item -ItemType Directory -Path (Split-Path $Output -Parent) -Force | Out-Null
    $encodeOptions = @{ Segments = "$desktopStem,$terminalStem"; Output = $Output; Ffmpeg = $Ffmpeg; Gifsicle = $Gifsicle
        Width = $Width; Fps = $Fps; Lossy = $Lossy; NoCaptions = [bool]$NoCaptions }
    & (Join-Path $PSScriptRoot 'readme-demo-gif.ps1') @encodeOptions
}
finally {
    # Set-Culture affects new processes only, so the current session cannot tell whether it changed.
    if ($cultureChanged) { Set-Culture $originalCulture }
    if ($drive) { & subst $drive /D | Out-Null }
    if ($KeepRecordings) { Write-Host "Recordings kept in $work" }
    else { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
}
