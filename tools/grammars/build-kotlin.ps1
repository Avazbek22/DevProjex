[CmdletBinding()]
param(
    [string]$ZigPath,
    [switch]$VerifyOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$arguments = @{
    GrammarName = @('tree-sitter-kotlin')
    VerifyOnly = $VerifyOnly
}
if (-not [string]::IsNullOrWhiteSpace($ZigPath))
{
    $arguments.ZigPath = $ZigPath
}

& (Join-Path $PSScriptRoot 'build-vendored-grammars.ps1') @arguments
