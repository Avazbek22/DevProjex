[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Image,
    [Parameter(Mandatory = $true)] [string] $Version,
    [Parameter(Mandatory = $true)]
    [ValidateSet('linux-x64', 'linux-arm64')]
    [string] $Rid
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

function Invoke-Docker([string[]] $Arguments) {
    $output = & docker @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "docker $($Arguments[0]) failed with exit code $LASTEXITCODE`: $($output | Out-String)"
    }
    return $output
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('devprojex-container-gate-' + [guid]::NewGuid().ToString('N'))
$publishRoot = Join-Path $temporaryRoot 'publish'
$channelDirectory = Join-Path $publishRoot "container/v$Version"
$payloadDirectory = Join-Path $channelDirectory $Rid
$containerName = 'devprojex-gate-' + [guid]::NewGuid().ToString('N')
$containerCreated = $false

[System.IO.Directory]::CreateDirectory($payloadDirectory) | Out-Null
try {
    [void](Invoke-Docker @('create', '--name', $containerName, $Image))
    $containerCreated = $true
    [void](Invoke-Docker @('cp', "${containerName}:/app/.", $payloadDirectory))
    [void](Invoke-Docker @(
        'cp',
        "${containerName}:/payload-receipt/publish-payload.$Rid.json",
        (Join-Path $channelDirectory "publish-payload.$Rid.json")))

    & (Join-Path $PSScriptRoot 'Test-ReleaseArtifacts.ps1') `
        -PublishRoot $publishRoot `
        -Version $Version `
        -Channels container `
        -Rids $Rid
    if ($LASTEXITCODE -ne 0) { throw 'Container payload validation failed.' }

    & (Join-Path $PSScriptRoot 'Test-ReleaseArtifactGateMutation.ps1') `
        -PublishRoot $publishRoot `
        -Version $Version `
        -Channels container `
        -Rids $Rid
    if ($LASTEXITCODE -ne 0) { throw 'Container mutation gate failed.' }
}
finally {
    if ($containerCreated) {
        [void](& docker rm $containerName 2>&1)
    }
    $resolvedTemporaryRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
    $resolvedSystemTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($resolvedTemporaryRoot.StartsWith($resolvedSystemTemp, [System.StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTemporaryRoot -PathType Container)) {
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}

Write-Host "Container payload and mutation gates passed for $Image ($Rid)."
