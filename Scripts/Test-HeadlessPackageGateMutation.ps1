[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ArtifactsRoot,

    [Parameter(Mandatory = $true)]
    [string] $Version
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$sourceRoot = [System.IO.Path]::GetFullPath($ArtifactsRoot)
$mutationRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("devprojex-headless-mutation-" + [guid]::NewGuid().ToString("N"))
$extractRoot = Join-Path $mutationRoot "package"
[System.IO.Directory]::CreateDirectory($mutationRoot) | Out-Null
try {
    $mutatedArtifacts = Join-Path $mutationRoot "artifacts"
    foreach ($channel in @("nuget", "npm")) {
        $sourceChannel = Join-Path $sourceRoot $channel
        $targetChannel = Join-Path $mutatedArtifacts $channel
        [System.IO.Directory]::CreateDirectory($targetChannel) | Out-Null
        foreach ($sourcePackage in Get-ChildItem -LiteralPath $sourceChannel -File) {
            $targetPackage = Join-Path $targetChannel $sourcePackage.Name
            try {
                [System.IO.File]::CreateHardLink($targetPackage, $sourcePackage.FullName)
            }
            catch {
                Copy-Item -LiteralPath $sourcePackage.FullName -Destination $targetPackage
            }
        }
    }
    $packageName = "devprojex.win-x64.$Version.nupkg"
    $packagePath = Join-Path (Join-Path $mutatedArtifacts "nuget") $packageName
    [System.IO.Directory]::CreateDirectory($extractRoot) | Out-Null
    [System.IO.Compression.ZipFile]::ExtractToDirectory($packagePath, $extractRoot)

    $carriers = @(Get-ChildItem -LiteralPath $extractRoot -Filter "Infrastructure.dll" -File -Recurse)
    if ($carriers.Count -ne 1) {
        throw "Mutation setup failed: '$packageName' must contain exactly one Infrastructure.dll; found $($carriers.Count)."
    }
    $carrier = $carriers[0]

    $bytes = [System.IO.File]::ReadAllBytes($carrier.FullName)
    $needle = [System.Text.Encoding]::ASCII.GetBytes("tree-sitter-kotlin")
    $replacement = [System.Text.Encoding]::ASCII.GetBytes("tree-sitter-Xotlin")
    $replacements = 0
    for ($offset = 0; $offset -le $bytes.Length - $needle.Length; $offset++) {
        $match = $true
        for ($index = 0; $index -lt $needle.Length; $index++) {
            if ($bytes[$offset + $index] -ne $needle[$index]) {
                $match = $false
                break
            }
        }
        if (-not $match) {
            continue
        }
        [System.Array]::Copy($replacement, 0, $bytes, $offset, $replacement.Length)
        $replacements++
        $offset += $needle.Length - 1
    }
    if ($replacements -eq 0) {
        throw "Mutation setup failed: Kotlin grammar resource was not found in '$packageName'."
    }
    [System.IO.File]::WriteAllBytes($carrier.FullName, $bytes)
    Remove-Item -LiteralPath $packagePath -Force
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $extractRoot,
        $packagePath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)

    $failedClosed = $false
    $failureMessage = ""
    try {
        & (Join-Path $PSScriptRoot "Test-HeadlessPackages.ps1") `
            -ArtifactsRoot $mutatedArtifacts `
            -Version $Version
    }
    catch {
        $failedClosed = $true
        $failureMessage = $_.Exception.Message
    }

    if (-not $failedClosed) {
        throw "Mutation gate failed open after removing the Kotlin grammar from '$packageName'."
    }
    if (-not $failureMessage.Contains($packageName, [System.StringComparison]::Ordinal) -or
        -not $failureMessage.Contains("tree-sitter-kotlin", [System.StringComparison]::Ordinal)) {
        throw "Mutation gate failed without naming the artifact and missing grammar: $failureMessage"
    }

    Write-Host "Mutation gate passed: $failureMessage"
}
finally {
    $resolvedMutationRoot = [System.IO.Path]::GetFullPath($mutationRoot)
    $resolvedSystemTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($resolvedMutationRoot.StartsWith($resolvedSystemTemp, [System.StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedMutationRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
