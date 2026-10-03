#Requires -Version 7.0
<#
.SYNOPSIS
    Creates the commit that Glama pins, with the MCP-focused README in place of the main one.

.DESCRIPTION
    Glama shows the README of the commit pinned in its server settings, and it has no separate
    README field. This script adds two commits on top of a master that is not behind origin:
      1. README.md replaced by Packaging/Glama/README.md (the snapshot to pin on Glama);
      2. README.md restored from the commit before it.
    The repository tree after the second commit is unchanged, and pushing both at once runs CI
    only for the restored head. Pin the printed snapshot SHA in the Glama server settings once
    Glama's copy of the repository has caught up with GitHub.

.EXAMPLE
    ./Scripts/New-GlamaReadmeSnapshot.ps1 -Push
#>
[CmdletBinding()]
param(
    [switch]$Push
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-Git {
    & git @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed with exit code $LASTEXITCODE." }
}

$repositoryRoot = (& git rev-parse --show-toplevel).Trim()
Push-Location $repositoryRoot
try {
    if ((& git branch --show-current).Trim() -cne 'master') { throw 'Run this on master.' }
    if (& git status --porcelain) { throw 'The working tree must be clean.' }
    Invoke-Git fetch origin master
    & git merge-base --is-ancestor origin/master HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Local master is behind or diverged from origin/master; update it first.' }

    $glamaReadme = Join-Path $repositoryRoot 'Packaging/Glama/README.md'
    if (-not (Test-Path -LiteralPath $glamaReadme -PathType Leaf)) { throw "Missing $glamaReadme." }
    Copy-Item -LiteralPath $glamaReadme -Destination (Join-Path $repositoryRoot 'README.md') -Force
    Invoke-Git add README.md
    Invoke-Git commit --quiet -m 'docs(readme): snapshot the MCP-focused README for the Glama listing'
    $snapshot = (& git rev-parse HEAD).Trim()

    Invoke-Git checkout HEAD~1 -- README.md
    Invoke-Git commit --quiet -m 'docs(readme): restore the main README after the Glama snapshot'

    if ($Push) { Invoke-Git push origin master }
    Write-Host "Pin this commit on Glama: $snapshot"
}
finally {
    Pop-Location
}
