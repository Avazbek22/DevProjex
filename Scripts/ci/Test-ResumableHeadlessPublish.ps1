[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$publishScript = Join-Path $repoRoot 'Scripts/Publish-HeadlessPackages.ps1'
$helpers = Join-Path $repoRoot 'Scripts/HeadlessPackagePublishing.ps1'
. $helpers

$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('devprojex-publish-fixture-' + [guid]::NewGuid().ToString('N'))
$artifacts = Join-Path $temporaryRoot 'artifacts'
$registry = Join-Path $temporaryRoot 'registry'
$version = '9.8.7'
$sourceSha = '0123456789abcdef0123456789abcdef01234567'

function Add-ZipText(
    [System.IO.Compression.ZipArchive] $Archive,
    [string] $Path,
    [string] $Value
) {
    $entry = $Archive.CreateEntry($Path)
    $stream = $entry.Open()
    try {
        $bytes = [System.Text.UTF8Encoding]::new($false).GetBytes($Value)
        $stream.Write($bytes, 0, $bytes.Length)
    }
    finally { $stream.Dispose() }
}

function New-NuGetFixture([string] $Id, [string] $FileName) {
    $path = Join-Path (Join-Path $artifacts 'nuget') $FileName
    $archive = [System.IO.Compression.ZipFile]::Open($path, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        Add-ZipText $archive "$Id.nuspec" "<?xml version=`"1.0`"?><package><metadata><id>$Id</id><version>$version</version></metadata></package>"
        Add-ZipText $archive 'tools/any/payload.txt' "payload:$Id@$version"
    }
    finally { $archive.Dispose() }
    Add-NuGetPayloadReceipt $path
}

function New-NpmFixture([string] $Name) {
    $stage = Join-Path $temporaryRoot ('npm-' + [guid]::NewGuid().ToString('N'))
    [System.IO.Directory]::CreateDirectory($stage) | Out-Null
    $package = [ordered]@{ name = $Name; version = $version; files = @('payload.txt') }
    [System.IO.File]::WriteAllText(
        (Join-Path $stage 'package.json'),
        (($package | ConvertTo-Json -Compress) + "`n"),
        [System.Text.UTF8Encoding]::new($false))
    [System.IO.File]::WriteAllText((Join-Path $stage 'payload.txt'), "payload:$Name@$version`n")
    Push-Location $stage
    try {
        & npm pack . --pack-destination (Join-Path $artifacts 'npm') --silent | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "npm pack failed for fixture '$Name'." }
    }
    finally { Pop-Location }
}

try {
    [System.IO.Directory]::CreateDirectory((Join-Path $artifacts 'nuget')) | Out-Null
    [System.IO.Directory]::CreateDirectory((Join-Path $artifacts 'npm')) | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $artifacts 'source-sha.txt'), "$sourceSha`n")

    foreach ($rid in @('linux-arm64', 'linux-x64', 'osx-arm64', 'osx-x64', 'win-arm64', 'win-x64')) {
        New-NuGetFixture "devprojex.$rid" "devprojex.$rid.$version.nupkg"
    }
    New-NuGetFixture 'devprojex' "devprojex.$version.nupkg"
    foreach ($platform in @('darwin-arm64', 'darwin-x64', 'linux-arm64', 'linux-x64', 'win32-arm64', 'win32-x64')) {
        New-NpmFixture "@devprojex/cli-$platform"
    }
    New-NpmFixture 'devprojex'

    $pointerPackage = Join-Path $artifacts "nuget/devprojex.$version.nupkg"
    $pointerReceipt = Join-Path $temporaryRoot 'installed-payload-receipt.json'
    $pointerArchive = [System.IO.Compression.ZipFile]::OpenRead($pointerPackage)
    try {
        $receiptEntry = $pointerArchive.GetEntry('devprojex/payload-receipt.json')
        $receiptStream = $receiptEntry.Open()
        try {
            $receiptFile = [System.IO.File]::Create($pointerReceipt)
            try { $receiptStream.CopyTo($receiptFile) }
            finally { $receiptFile.Dispose() }
        }
        finally { $receiptStream.Dispose() }
    }
    finally { $pointerArchive.Dispose() }
    if (-not (Test-InstalledNuGetPayloadReceipt $pointerPackage $pointerReceipt)) {
        throw 'An extracted payload receipt did not match its source package.'
    }
    $modifiedReceipt = Get-Content -LiteralPath $pointerReceipt -Raw | ConvertFrom-Json
    $modifiedReceipt.packageVersion = '0.0.0'
    [System.IO.File]::WriteAllText(
        $pointerReceipt,
        ($modifiedReceipt | ConvertTo-Json -Depth 6 -Compress),
        [System.Text.UTF8Encoding]::new($false))
    if (Test-InstalledNuGetPayloadReceipt $pointerPackage $pointerReceipt) {
        throw 'A modified extracted payload receipt was accepted.'
    }

    $failedAsPlanned = $false
    try {
        & $publishScript -ArtifactsRoot $artifacts -Channels both -FixtureRegistryRoot $registry `
            -SimulateFailureAfter 3 -ExpectedSourceSha $sourceSha
    }
    catch {
        if (-not $_.Exception.Message.Contains('Simulated registry failure after 3 packages.', [System.StringComparison]::Ordinal)) { throw }
        $failedAsPlanned = $true
        Write-Host 'Observed the planned interruption after three packages.'
    }
    if (-not $failedAsPlanned) { throw 'The planned publication interruption did not occur.' }
    if (@(Get-ChildItem -LiteralPath $registry -File -Recurse).Count -ne 3) {
        throw 'The interrupted fixture must contain exactly three published packages.'
    }

    & $publishScript -ArtifactsRoot $artifacts -Channels both -FixtureRegistryRoot $registry -ExpectedSourceSha $sourceSha
    if (@(Get-ChildItem -LiteralPath $registry -File -Recurse).Count -ne 14) {
        throw 'The resumed fixture did not publish all fourteen packages.'
    }
    $resumeLog = @(& $publishScript -ArtifactsRoot $artifacts -Channels both `
        -FixtureRegistryRoot $registry -ExpectedSourceSha $sourceSha 6>&1) -join "`n"
    if (@([regex]::Matches($resumeLog, 'Skipping (?:NuGet|npm) ')).Count -ne 14) {
        throw 'A completed retry did not skip all fourteen identical packages.'
    }
    Write-Host $resumeLog

    $publishedNuGet = Get-ChildItem -LiteralPath (Join-Path $registry 'nuget') -Filter '*.nupkg' -File -Recurse |
        Sort-Object FullName | Select-Object -First 1
    $archive = [System.IO.Compression.ZipFile]::Open($publishedNuGet.FullName, [System.IO.Compression.ZipArchiveMode]::Update)
    try {
        $payload = $archive.GetEntry('tools/any/payload.txt')
        $payload.Delete()
        Add-ZipText $archive 'tools/any/payload.txt' 'different payload'
    }
    finally { $archive.Dispose() }
    $mismatchRejected = $false
    try {
        & $publishScript -ArtifactsRoot $artifacts -Channels nuget -FixtureRegistryRoot $registry `
            -ExpectedSourceSha $sourceSha
    }
    catch {
        if (-not $_.Exception.Message.Contains('payload receipt', [System.StringComparison]::OrdinalIgnoreCase)) { throw }
        $mismatchRejected = $true
        Write-Host 'Rejected a published NuGet package whose payload differs from its receipt.'
    }
    if (-not $mismatchRejected) { throw 'A mismatched published package was not rejected.' }

    $publishedNpm = Get-ChildItem -LiteralPath (Join-Path $registry 'npm') -Filter '*.tgz' -File -Recurse |
        Sort-Object FullName | Select-Object -First 1
    $stream = [System.IO.File]::Open($publishedNpm.FullName, [System.IO.FileMode]::Append)
    try { $stream.WriteByte(0) }
    finally { $stream.Dispose() }
    $npmMismatchRejected = $false
    try {
        & $publishScript -ArtifactsRoot $artifacts -Channels npm -FixtureRegistryRoot $registry `
            -ExpectedSourceSha $sourceSha
    }
    catch {
        if (-not $_.Exception.Message.Contains('integrity', [System.StringComparison]::OrdinalIgnoreCase)) { throw }
        $npmMismatchRejected = $true
        Write-Host 'Rejected a published npm package whose integrity differs from the local tarball.'
    }
    if (-not $npmMismatchRejected) { throw 'A mismatched published npm package was not rejected.' }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot -PathType Container) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

Write-Host 'Resumable headless package publication fixture passed.'
