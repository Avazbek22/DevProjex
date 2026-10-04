#Requires -Version 7.0
<#
.SYNOPSIS
    Puts the MCP-focused README at the head of master for Glama to pin, then restores the main one.

.DESCRIPTION
    Glama shows the README of the commit pinned in its server settings, has no separate README
    field, and only accepts the current head of the default branch as that commit. The listing
    therefore needs two steps with a wait in between:

      1. Without -Restore: commit Packaging/Glama/README.md as README.md and push it as the head
         of master. The commit message carries [skip ci], because the main README's contract tests
         do not apply to this snapshot.
      2. Wait until Glama shows that SHA as the repository head, pin it, build, and press Sync.
      3. With -Restore: commit the main README back and push. CI runs on this commit as usual.

.EXAMPLE
    ./Scripts/New-GlamaReadmeSnapshot.ps1 -Push
    ./Scripts/New-GlamaReadmeSnapshot.ps1 -Restore -Push
#>
[CmdletBinding()]
param(
    [switch]$Restore,
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

    $glamaReadme = 'Packaging/Glama/README.md'
    $headIsSnapshot = ((& git show "HEAD:README.md") -join "`n") -ceq ((& git show "HEAD:$glamaReadme") -join "`n")

    if ($Restore) {
        if (-not $headIsSnapshot) { throw 'HEAD does not hold the Glama README; nothing to restore.' }
        Invoke-Git checkout HEAD~1 -- README.md
        Invoke-Git commit --quiet -m 'docs(readme): restore the main README after the Glama listing snapshot'
        if ($Push) { Invoke-Git push origin master }
        Write-Host "Main README restored at $((& git rev-parse HEAD).Trim())."
        return
    }

    if ($headIsSnapshot) { throw 'HEAD already holds the Glama README.' }
    Copy-Item -LiteralPath (Join-Path $repositoryRoot $glamaReadme) -Destination (Join-Path $repositoryRoot 'README.md') -Force
    Invoke-Git add README.md
    Invoke-Git commit --quiet -m 'docs(readme): MCP-focused README for the Glama listing [skip ci]'
    if ($Push) { Invoke-Git push origin master }
    $snapshot = (& git rev-parse HEAD).Trim()
    Write-Host "Wait until Glama shows $snapshot as the repository head, pin it, build, and press Sync."
    Write-Host 'Then run this script again with -Restore -Push.'
}
finally {
    Pop-Location
}
