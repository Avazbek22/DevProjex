[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $ArtifactsRoot,
    [Parameter(Mandatory = $true)] [ValidateSet('nuget', 'npm', 'both')] [string] $Channels,
    [string] $NuGetSource = 'https://api.nuget.org/v3/index.json',
    [string] $NuGetFlatContainer = 'https://api.nuget.org/v3-flatcontainer',
    [string] $NuGetApiKey = '',
    [string] $NpmRegistry = 'https://registry.npmjs.org',
    [string] $FixtureRegistryRoot = '',
    [int] $SimulateFailureAfter = 0,
    [string] $ExpectedSourceSha = ''
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'HeadlessPackagePublishing.ps1')

$artifacts = [System.IO.Path]::GetFullPath($ArtifactsRoot)
if (-not [string]::IsNullOrWhiteSpace($ExpectedSourceSha)) {
    $sourcePath = Join-Path $artifacts 'source-sha.txt'
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { throw 'Headless package artifacts have no source SHA receipt.' }
    $actualSourceSha = (Get-Content -LiteralPath $sourcePath -Raw).Trim()
    if ($actualSourceSha -cne $ExpectedSourceSha) {
        throw "Headless package source SHA mismatch: expected '$ExpectedSourceSha', found '$actualSourceSha'."
    }
}

$fixtureMode = -not [string]::IsNullOrWhiteSpace($FixtureRegistryRoot)
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('devprojex-package-publish-' + [guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
$script:publishedThisRun = 0

function Complete-Publish([string] $Label) {
    $script:publishedThisRun++
    Write-Host "Published $Label."
    if ($SimulateFailureAfter -gt 0 -and $script:publishedThisRun -eq $SimulateFailureAfter) {
        throw "Simulated registry failure after $SimulateFailureAfter packages."
    }
}

function Get-FixturePath([string] $Channel, [string] $Identity, [string] $Version, [string] $Extension) {
    $identityBytes = [System.Text.Encoding]::UTF8.GetBytes($Identity)
    $safeIdentity = [Convert]::ToHexString($identityBytes).ToLowerInvariant()
    return Join-Path ([System.IO.Path]::GetFullPath($FixtureRegistryRoot)) "$Channel/$safeIdentity/$Version/package.$Extension"
}

function Try-Download([string] $Uri, [string] $Destination) {
    try {
        Invoke-WebRequest -Uri $Uri -OutFile $Destination -MaximumRetryCount 2 -RetryIntervalSec 2
        return $true
    }
    catch {
        $statusCode = if ($null -ne $_.Exception.Response) { $_.Exception.Response.StatusCode.value__ } else { $null }
        if ($statusCode -eq 404) { return $false }
        throw
    }
}

function Publish-NuGetPackage([System.IO.FileInfo] $Package) {
    $local = Read-NuGetPayloadReceipt $Package.FullName
    $publishedPath = Join-Path $temporaryRoot ($Package.Name + '.published')
    $exists = $false
    if ($fixtureMode) {
        $publishedPath = Get-FixturePath 'nuget' $local.Id $local.Version 'nupkg'
        $exists = Test-Path -LiteralPath $publishedPath -PathType Leaf
    }
    else {
        $id = $local.Id.ToLowerInvariant()
        $version = $local.Version.ToLowerInvariant()
        $uri = "$($NuGetFlatContainer.TrimEnd('/'))/$id/$version/$id.$version.nupkg"
        $exists = Try-Download $uri $publishedPath
    }
    if ($exists) {
        if (-not (Test-NuGetPayloadEquivalent $Package.FullName $publishedPath)) {
            throw "Published NuGet package '$($local.Id)@$($local.Version)' differs from the local payload receipt."
        }
        Write-Host "Skipping NuGet $($local.Id)@$($local.Version): published payload receipt is identical."
        return
    }
    if ($fixtureMode) {
        [System.IO.Directory]::CreateDirectory((Split-Path -Parent $publishedPath)) | Out-Null
        Copy-Item -LiteralPath $Package.FullName -Destination $publishedPath
    }
    else {
        if ([string]::IsNullOrWhiteSpace($NuGetApiKey)) { throw 'NuGet API key is required for publication.' }
        & dotnet nuget push $Package.FullName --api-key $NuGetApiKey --source $NuGetSource
        if ($LASTEXITCODE -ne 0) { throw "NuGet push failed for $($Package.Name)." }
    }
    Complete-Publish "NuGet $($local.Id)@$($local.Version)"
}

function Get-NpmPublishedIntegrity([string] $Name, [string] $Version) {
    if ($fixtureMode) {
        $path = Get-FixturePath 'npm' $Name $Version 'tgz'
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
        return Get-NpmPackageIntegrity $path
    }
    $stderrPath = Join-Path $temporaryRoot (([guid]::NewGuid().ToString('N')) + '.npm-view.log')
    $viewOutput = @(& npm view "$Name@$Version" dist.integrity --registry $NpmRegistry --json 2> $stderrPath)
    if ($LASTEXITCODE -ne 0) {
        $failure = ((@($viewOutput) + @(Get-Content -LiteralPath $stderrPath -ErrorAction SilentlyContinue)) -join "`n")
        if ($failure.Contains('E404', [System.StringComparison]::OrdinalIgnoreCase) -or
            $failure.Contains('404 Not Found', [System.StringComparison]::OrdinalIgnoreCase)) {
            return $null
        }
        throw "Could not inspect npm package '$Name@$Version': $failure"
    }
    $integrity = [string](($viewOutput -join "`n") | ConvertFrom-Json)
    if ([string]::IsNullOrWhiteSpace($integrity)) {
        throw "Published npm package '$Name@$Version' has no dist.integrity."
    }
    return $integrity
}

function Publish-NpmPackage([System.IO.FileInfo] $Package) {
    $identity = Get-NpmPackageIdentity $Package.FullName
    $localIntegrity = Get-NpmPackageIntegrity $Package.FullName
    $publishedIntegrity = Get-NpmPublishedIntegrity $identity.Name $identity.Version
    if ($null -ne $publishedIntegrity) {
        if ($publishedIntegrity -cne $localIntegrity) {
            throw "Published npm package '$($identity.Name)@$($identity.Version)' has integrity '$publishedIntegrity', expected '$localIntegrity'."
        }
        Write-Host "Skipping npm $($identity.Name)@$($identity.Version): dist.integrity is identical."
        return
    }
    if ($fixtureMode) {
        $destination = Get-FixturePath 'npm' $identity.Name $identity.Version 'tgz'
        [System.IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
        Copy-Item -LiteralPath $Package.FullName -Destination $destination
    }
    else {
        & npm publish $Package.FullName --access public --registry $NpmRegistry
        if ($LASTEXITCODE -ne 0) { throw "npm publish failed for $($Package.Name)." }
    }
    Complete-Publish "npm $($identity.Name)@$($identity.Version)"
}

try {
    if ($Channels -in @('nuget', 'both')) {
        $nuget = @(Get-ChildItem -LiteralPath (Join-Path $artifacts 'nuget') -Filter '*.nupkg' -File)
        $ridPackages = @($nuget | Where-Object { $_.BaseName -match '^devprojex\.[^.]+-[^.]+\.' } | Sort-Object Name)
        $pointer = @($nuget | Where-Object { $_.BaseName -match '^devprojex\.\d' })
        if ($ridPackages.Count -ne 6 -or $pointer.Count -ne 1) { throw 'Expected six NuGet RID packages and one pointer package.' }
        foreach ($package in $ridPackages) { Publish-NuGetPackage $package }
        Publish-NuGetPackage $pointer[0]
    }
    if ($Channels -in @('npm', 'both')) {
        $npm = @(Get-ChildItem -LiteralPath (Join-Path $artifacts 'npm') -Filter '*.tgz' -File)
        $platformPackages = @($npm | Where-Object { $_.BaseName.StartsWith('devprojex-cli-', [System.StringComparison]::Ordinal) } | Sort-Object Name)
        $launcher = @($npm | Where-Object { -not $_.BaseName.StartsWith('devprojex-cli-', [System.StringComparison]::Ordinal) })
        if ($platformPackages.Count -ne 6 -or $launcher.Count -ne 1) { throw 'Expected six npm platform packages and one launcher package.' }
        foreach ($package in $platformPackages) { Publish-NpmPackage $package }
        Publish-NpmPackage $launcher[0]
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot -PathType Container) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
