<#
.SYNOPSIS
    Generates app.ico for Windows from Store visual assets.

.DESCRIPTION
    Delegates to generate-app-ico.py. The Python generator reuses the same
    Square44x44Logo.targetsize-* PNG files that are used by the Microsoft Store
    package, so portable EXE icons stay visually aligned with the Store build.

.EXAMPLE
    .\generate-app-ico.ps1

.NOTES
    Requires Python and Pillow: pip install Pillow
#>

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$python = Get-Command "python" -ErrorAction SilentlyContinue
if (-not $python) {
    throw "Python is required to generate app.ico."
}

& $python.Source (Join-Path $scriptDir "generate-app-ico.py")
if ($LASTEXITCODE -ne 0) {
    throw "generate-app-ico.py failed with exit code $LASTEXITCODE"
}
