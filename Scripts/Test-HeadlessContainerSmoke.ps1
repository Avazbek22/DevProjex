[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Image,
    [Parameter(Mandatory = $true)] [string] $Version
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

function Invoke-Container([string[]] $Arguments, [int] $ExpectedExitCode = 0) {
    $output = & docker run --rm --read-only --tmpfs /tmp --volume "${script:SampleRoot}:/project:ro" $Image @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne $ExpectedExitCode) {
        throw "Container command '$($Arguments -join ' ')' exited $exitCode instead of $ExpectedExitCode`: $($output | Out-String)"
    }
    return @($output)
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$script:SampleRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('devprojex-container-smoke-' + [guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($script:SampleRoot) | Out-Null
try {
    @'
namespace Sample;
public static class Program {
    public static int Compute(int value) {
        var implementationMustDisappear = value * 2;
        return implementationMustDisappear;
    }
}
'@ | Set-Content -LiteralPath (Join-Path $script:SampleRoot 'Program.cs') -Encoding utf8NoBOM
    $secret = 'ghp_123456789012345678901234567890123456'
    "token=$secret" | Set-Content -LiteralPath (Join-Path $script:SampleRoot 'settings.txt') -Encoding utf8NoBOM

    $actualVersion = ((Invoke-Container @('--version')) -join "`n").Trim()
    if ($actualVersion -cne $Version) {
        throw "Container version mismatch: expected '$Version', found '$actualVersion'."
    }

    $tree = (Invoke-Container @('tree', '/project', '--git-mode', 'none', '--exclude', 'none')) -join "`n"
    if (-not $tree.Contains('Program.cs', [System.StringComparison]::Ordinal)) {
        throw 'Container tree smoke did not report Program.cs.'
    }

    $full = ((Invoke-Container @(
        'analyze', '/project', '--format', 'json', '--git-mode', 'none', '--exclude', 'none', '-o', '-')) -join "`n") |
        ConvertFrom-Json
    $compressed = ((Invoke-Container @(
        'analyze', '/project', '--format', 'json', '--git-mode', 'none', '--exclude', 'none',
        '--compress-code', '-o', '-')) -join "`n") | ConvertFrom-Json
    if ($compressed.metrics.content.chars -ge $full.metrics.content.chars) {
        throw 'Container compression smoke did not reduce content characters.'
    }

    $findings = (Invoke-Container @(
        'analyze', '/project', '--format', 'json', '--git-mode', 'none', '--exclude', 'none',
        '--findings', '--fail-on-findings', '-o', '-') 3) -join "`n"
    if ($findings.Contains($secret, [System.StringComparison]::Ordinal)) {
        throw 'Container secret smoke leaked the planted value.'
    }
    $findingsJson = $findings | ConvertFrom-Json
    if ([int]$findingsJson.findingCount -le 0) {
        throw 'Container secret smoke returned no findings.'
    }

    if ($null -eq (Get-Command node -ErrorAction SilentlyContinue)) {
        Write-Host 'SKIPPED: Node is unavailable; MCP initialize smoke was not run.'
    }
    else {
        & node (Join-Path $repoRoot 'Scripts/smoke-headless-mcp.mjs') docker /project `
            run --rm -i --read-only --tmpfs /tmp --volume "${script:SampleRoot}:/project:ro" $Image
        if ($LASTEXITCODE -ne 0) { throw 'Container MCP initialize smoke failed.' }
    }
}
finally {
    $resolvedSampleRoot = [System.IO.Path]::GetFullPath($script:SampleRoot)
    $resolvedSystemTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($resolvedSampleRoot.StartsWith($resolvedSystemTemp, [System.StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedSampleRoot -PathType Container)) {
        Remove-Item -LiteralPath $resolvedSampleRoot -Recurse -Force
    }
}

Write-Host "Read-only container smoke passed for $Image."
