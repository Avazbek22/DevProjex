[CmdletBinding()]
param(
    [string] $PublishRoot = "",
    [Parameter(Mandatory = $true)] [string] $Version,
    [string[]] $Channels = @("github", "store"),
    [string[]] $Rids = @("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'release-archive-helpers.ps1')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
if ($null -eq ('DevProjex.ReleaseValidation.ReleasePayloadInspector' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'ReleasePayloadInspection.cs')
}

function ConvertTo-Tokens([string[]] $Values) {
    return @($Values | ForEach-Object { ([string]$_).Split(',') } |
        ForEach-Object { $_.Trim().ToLowerInvariant() } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
}

function Copy-ChannelFixture([string] $SourcePublishRoot, [string] $Channel) {
    $fixturePublishRoot = Join-Path $script:MutationRoot ([guid]::NewGuid().ToString('N'))
    $sourceDirectory = Join-Path $SourcePublishRoot "$Channel/v$Version"
    $destinationDirectory = Join-Path $fixturePublishRoot "$Channel/v$Version"
    if (-not (Test-Path -LiteralPath $sourceDirectory -PathType Container)) {
        throw "Mutation setup failed: channel directory was not found: $sourceDirectory"
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $destinationDirectory) -Force | Out-Null
    Copy-Item -LiteralPath $sourceDirectory -Destination $destinationDirectory -Recurse -Force
    return $fixturePublishRoot
}

function Write-ChannelChecksums(
    [string] $ChannelDirectory,
    [string] $ManifestName = 'SHA256SUMS.txt'
) {
    $artifacts = @(Get-ChildItem -LiteralPath $ChannelDirectory -File |
        Where-Object { $_.Name -notin @('SHA256SUMS.txt', 'SHA256SUMS.headless.txt', 'PARTIAL-BUILD.txt') } | Sort-Object Name)
    $lines = @($artifacts | ForEach-Object { "$(Get-FileSha256Hex -path $_.FullName) *$($_.Name)" })
    Write-ReleaseChecksumManifest -path (Join-Path $ChannelDirectory $ManifestName) -lines $lines
}

function Invoke-FailingValidation(
    [string] $FixturePublishRoot,
    [string] $Channel,
    [string] $ArtifactName,
    [string] $ExpectedEntry
) {
    $shellPath = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    $arguments = @('-NoLogo', '-NoProfile', '-File', (Join-Path $PSScriptRoot 'Test-ReleaseArtifacts.ps1'),
        '-PublishRoot', $FixturePublishRoot, '-Version', $Version, '-Channels', $Channel)
    if ($Channel -in @('github', 'headless', 'container')) {
        $arguments += @('-Rids', ($script:SelectedRids -join ','))
    }
    $output = & $shellPath @arguments 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0) {
        throw "Mutation gate failed open for '$ArtifactName' after changing '$ExpectedEntry'."
    }
    if ($output.IndexOf($ArtifactName, [System.StringComparison]::Ordinal) -lt 0 -or
        $output.IndexOf($ExpectedEntry, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Mutation gate failed without naming artifact '$ArtifactName' and '$ExpectedEntry': $output"
    }
    Write-Host "Mutation gate passed: $ArtifactName -> $ExpectedEntry"
}

function Read-PayloadReceipt([string] $ChannelDirectory, [string] $Rid) {
    $path = Join-Path $ChannelDirectory "publish-payload.$Rid.json"
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Mutation setup failed: payload receipt was not found: $path"
    }
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}

function Get-ReceiptMutationCases([object] $Receipt) {
    $fixedGrammar = $null
    $fixedLocalization = $null
    $resourceCandidates = New-Object 'System.Collections.Generic.List[object]'
	foreach ($file in @($Receipt.files)) {
        if ($file.PSObject.Properties.Name -notcontains 'managedResources') { continue }
        foreach ($resource in @($file.managedResources)) {
            $candidate = [pscustomobject]@{ File = [string]$file.path; Entry = [string]$resource }
            $resourceCandidates.Add($candidate)
            if ([string]$resource -ceq 'DevProjex.Grammars/tree-sitter-kotlin.dll') { $fixedGrammar = $candidate }
            if ([string]$resource -ceq 'DevProjex.Assets.Localization.en.json') { $fixedLocalization = $candidate }
		}
	}
	$grammarFile = @($Receipt.files | Where-Object {
		[string]$_.path -ceq 'grammars/tree-sitter-kotlin.dll'
	} | Select-Object -First 1)
	if ($grammarFile.Count -eq 1) {
		$fixedGrammar = [pscustomobject]@{
			Kind = 'File'
			File = [string]$grammarFile[0].path
			Entry = [string]$grammarFile[0].path
		}
	}
	if ($null -eq $fixedGrammar -or $null -eq $fixedLocalization) {
		throw 'Mutation setup failed: fixed grammar or localization resource is absent from the receipt.'
	}
	if ($fixedGrammar.PSObject.Properties.Name -notcontains 'Kind') {
		$fixedGrammar | Add-Member -NotePropertyName Kind -NotePropertyValue 'Resource'
	}
    $fixedNative = @($Receipt.files | Where-Object {
        $_.PSObject.Properties.Name -notcontains 'managedResources' -and
        ([string]$_.path -ceq 'libSkiaSharp.dll' -or
            [string]$_.path -cmatch '(^|/)(lib)?tree-sitter\.(dll|so|dylib)$')
    } | Sort-Object {
        if ([string]$_.path -ceq 'libSkiaSharp.dll') { 0 } else { 1 }
    }, { [string]$_.path } | Select-Object -First 1)
    if ($fixedNative.Count -ne 1) {
        throw "Mutation setup failed: no fixed native runtime library is present in the receipt."
    }
	$genericFile = @($Receipt.files | Where-Object {
		[long]$_.size -gt 0 -and [string]$_.path -cne [string]$fixedNative[0].path -and
		[string]$_.path -cne [string]$fixedGrammar.File -and
		$_.PSObject.Properties.Name -notcontains 'managedResources'
    } | Sort-Object { [string]$_.sha256 }, { [string]$_.path } | Select-Object -First 1)
    $genericResource = @($resourceCandidates | Where-Object {
        [string]$_.Entry -cne [string]$fixedGrammar.Entry -and
        [string]$_.Entry -cne [string]$fixedLocalization.Entry
    } | Sort-Object {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes("$($_.File)`0$($_.Entry)")
        [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($bytes))
    } | Select-Object -First 1)
    if ($genericFile.Count -ne 1 -or $genericResource.Count -ne 1) {
        throw 'Mutation setup failed: the receipt has no generic file or embedded-resource candidate.'
    }
    return @(
        [pscustomobject]@{ Label = 'deterministic file'; Kind = 'File'; File = [string]$genericFile[0].path; Entry = [string]$genericFile[0].path },
        [pscustomobject]@{ Label = 'deterministic resource'; Kind = 'Resource'; File = [string]$genericResource[0].File; Entry = [string]$genericResource[0].Entry },
		[pscustomobject]@{ Label = 'grammar'; Kind = [string]$fixedGrammar.Kind; File = [string]$fixedGrammar.File; Entry = [string]$fixedGrammar.Entry },
        [pscustomobject]@{ Label = 'localization resource'; Kind = 'Resource'; File = [string]$fixedLocalization.File; Entry = [string]$fixedLocalization.Entry },
        [pscustomobject]@{ Label = 'native library'; Kind = 'File'; File = [string]$fixedNative[0].path; Entry = [string]$fixedNative[0].path }
    )
}

function Remove-MutationDirectory([AllowNull()][string] $Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return }
    $mutationRoot = [System.IO.Path]::GetFullPath($script:MutationRoot)
    $mutationRoot = $mutationRoot.TrimEnd([char[]]@('\', '/')) + [System.IO.Path]::DirectorySeparatorChar
    $resolvedPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $resolvedPath.StartsWith($mutationRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Mutation cleanup refused path outside its temporary root: $resolvedPath"
    }
    if (Test-Path -LiteralPath $resolvedPath -PathType Container) {
        Remove-Item -LiteralPath $resolvedPath -Recurse -Force
    }
}

function Invoke-GitHubMutation([string] $SourcePublishRoot, [object] $Case) {
    $fixtureRoot = Copy-ChannelFixture -SourcePublishRoot $SourcePublishRoot -Channel 'github'
    try {
        $artifactName = "DevProjex.v$Version.win-x64.exe"
        $channelDirectory = Join-Path $fixtureRoot "github/v$Version"
        $artifactPath = Join-Path $channelDirectory $artifactName
        if ([string]$Case.Kind -ceq 'File') {
            [DevProjex.ReleaseValidation.ReleasePayloadInspector]::MutateBundleEntry($artifactPath, [string]$Case.File)
        }
        else {
            [void][DevProjex.ReleaseValidation.ReleasePayloadInspector]::MutateBundleResource(
                $artifactPath, [string]$Case.File, [string]$Case.Entry)
        }
        Write-ChannelChecksums -ChannelDirectory $channelDirectory
        Invoke-FailingValidation -FixturePublishRoot $fixtureRoot -Channel 'github' `
            -ArtifactName $artifactName -ExpectedEntry ([string]$Case.Entry)
    }
    finally {
        Remove-MutationDirectory -Path $fixtureRoot
    }
}

function Compress-Directory([string] $SourceDirectory, [string] $DestinationPath) {
    if (Test-Path -LiteralPath $DestinationPath) { Remove-Item -LiteralPath $DestinationPath -Force }
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $SourceDirectory, $DestinationPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
}

function Invoke-StoreMutation([string] $SourcePublishRoot, [object] $Case) {
    $fixtureRoot = Copy-ChannelFixture -SourcePublishRoot $SourcePublishRoot -Channel 'store'
    $workRoot = $null
    try {
        $channelDirectory = Join-Path $fixtureRoot "store/v$Version"
        $uploads = @(Get-ChildItem -LiteralPath $channelDirectory -File -Filter '*.msixupload')
        if ($uploads.Count -ne 1) { throw "Mutation setup failed: expected one .msixupload, found $($uploads.Count)." }
        $artifactName = $uploads[0].Name
        $workRoot = Join-Path $script:MutationRoot ('store-' + [guid]::NewGuid().ToString('N'))
        $uploadRoot = Join-Path $workRoot 'upload'
        $bundleRoot = Join-Path $workRoot 'bundle'
        $packageRoot = Join-Path $workRoot 'package'
        New-Item -ItemType Directory -Path $uploadRoot, $bundleRoot, $packageRoot -Force | Out-Null
        [System.IO.Compression.ZipFile]::ExtractToDirectory($uploads[0].FullName, $uploadRoot)
        $bundles = @(Get-ChildItem -LiteralPath $uploadRoot -Recurse -File -Filter '*.msixbundle')
        if ($bundles.Count -ne 1) { throw "Mutation setup failed: '$artifactName' must contain exactly one .msixbundle." }
        $bundleRelativePath = $bundles[0].FullName.Substring($uploadRoot.Length).TrimStart([char[]]@('\', '/'))
        [System.IO.Compression.ZipFile]::ExtractToDirectory($bundles[0].FullName, $bundleRoot)
        [xml]$bundleManifest = Get-Content -LiteralPath (Join-Path $bundleRoot 'AppxMetadata\AppxBundleManifest.xml') -Raw
        $packageNode = $bundleManifest.SelectSingleNode("//*[local-name()='Package' and translate(@Architecture,'X','x')='x64']")
        if ($null -eq $packageNode) { throw "Mutation setup failed: '$artifactName' has no x64 package." }
        $packagePath = Join-Path $bundleRoot (([string]$packageNode.GetAttribute('FileName')) -replace '/', [System.IO.Path]::DirectorySeparatorChar)
        [System.IO.Compression.ZipFile]::ExtractToDirectory($packagePath, $packageRoot)
        $payloadPath = Join-Path (Join-Path $packageRoot 'DevProjex.Avalonia') (([string]$Case.File) -replace '/', [System.IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path -LiteralPath $payloadPath -PathType Leaf)) {
            throw "Mutation setup failed: Store payload file '$($Case.File)' was not found."
        }
        if ([string]$Case.Kind -ceq 'File') {
            $bytes = [System.IO.File]::ReadAllBytes($payloadPath)
            if ($bytes.Length -eq 0) { throw "Mutation setup failed: Store payload file '$($Case.File)' is empty." }
            $offset = [int]($bytes.Length / 2)
            $bytes[$offset] = $bytes[$offset] -bxor 1
            [System.IO.File]::WriteAllBytes($payloadPath, $bytes)
        }
        else {
            [void][DevProjex.ReleaseValidation.ReleasePayloadInspector]::MutateManagedResource($payloadPath, [string]$Case.Entry)
        }
        Compress-Directory -SourceDirectory $packageRoot -DestinationPath $packagePath
        Compress-Directory -SourceDirectory $bundleRoot -DestinationPath $bundles[0].FullName
        $rebuiltBundlePath = Join-Path $uploadRoot ($bundleRelativePath -replace '/', [System.IO.Path]::DirectorySeparatorChar)
        if ($rebuiltBundlePath -cne $bundles[0].FullName) {
            Copy-Item -LiteralPath $bundles[0].FullName -Destination $rebuiltBundlePath -Force
        }
        Compress-Directory -SourceDirectory $uploadRoot -DestinationPath $uploads[0].FullName
        Write-ChannelChecksums -ChannelDirectory $channelDirectory
        Invoke-FailingValidation -FixturePublishRoot $fixtureRoot -Channel 'store' `
            -ArtifactName $artifactName -ExpectedEntry ([string]$Case.Entry)
    }
    finally {
        try {
            Remove-MutationDirectory -Path $workRoot
        }
        finally {
            Remove-MutationDirectory -Path $fixtureRoot
        }
    }
}

function Invoke-HeadlessMutation([string] $SourcePublishRoot, [object] $Case) {
    $fixtureRoot = Copy-ChannelFixture -SourcePublishRoot $SourcePublishRoot -Channel 'headless'
    $workRoot = $null
    try {
        $channelDirectory = Join-Path $fixtureRoot "headless/v$Version"
        $artifactName = "DevProjex-headless.v$Version.win-x64.zip"
        $artifactPath = Join-Path $channelDirectory $artifactName
        $workRoot = Join-Path $script:MutationRoot ('headless-' + [guid]::NewGuid().ToString('N'))
        [System.IO.Directory]::CreateDirectory($workRoot) | Out-Null
        [System.IO.Compression.ZipFile]::ExtractToDirectory($artifactPath, $workRoot)
        $binaryPath = Join-Path $workRoot 'devprojex.exe'
        if ([string]$Case.Kind -ceq 'File') {
            [DevProjex.ReleaseValidation.ReleasePayloadInspector]::MutateBundleEntry($binaryPath, [string]$Case.File)
        }
        else {
            [void][DevProjex.ReleaseValidation.ReleasePayloadInspector]::MutateBundleResource(
                $binaryPath, [string]$Case.File, [string]$Case.Entry)
        }
        Compress-Directory -SourceDirectory $workRoot -DestinationPath $artifactPath
        Write-ChannelChecksums -ChannelDirectory $channelDirectory -ManifestName 'SHA256SUMS.headless.txt'
        Invoke-FailingValidation -FixturePublishRoot $fixtureRoot -Channel 'headless' `
            -ArtifactName $artifactName -ExpectedEntry ([string]$Case.Entry)
    }
    finally {
        try { Remove-MutationDirectory -Path $workRoot }
        finally { Remove-MutationDirectory -Path $fixtureRoot }
    }
}

function Invoke-ContainerMutation([string] $SourcePublishRoot, [string] $Rid, [object] $Case) {
    $fixtureRoot = Copy-ChannelFixture -SourcePublishRoot $SourcePublishRoot -Channel 'container'
    try {
        $payloadPath = Join-Path (Join-Path $fixtureRoot "container/v$Version/$Rid") `
            (([string]$Case.File) -replace '/', [System.IO.Path]::DirectorySeparatorChar)
        if (-not (Test-Path -LiteralPath $payloadPath -PathType Leaf)) {
            throw "Mutation setup failed: container payload file '$($Case.File)' was not found."
        }
        if ([string]$Case.Kind -ceq 'File') {
            $bytes = [System.IO.File]::ReadAllBytes($payloadPath)
            if ($bytes.Length -eq 0) { throw "Mutation setup failed: container payload file '$($Case.File)' is empty." }
            $offset = [int]($bytes.Length / 2)
            $bytes[$offset] = $bytes[$offset] -bxor 1
            [System.IO.File]::WriteAllBytes($payloadPath, $bytes)
        }
        else {
            [void][DevProjex.ReleaseValidation.ReleasePayloadInspector]::MutateManagedResource(
                $payloadPath, [string]$Case.Entry)
        }
        Invoke-FailingValidation -FixturePublishRoot $fixtureRoot -Channel 'container' `
            -ArtifactName "container:$Rid" -ExpectedEntry ([string]$Case.Entry)
    }
    finally { Remove-MutationDirectory -Path $fixtureRoot }
}

$sourcePublishRoot = [System.IO.Path]::GetFullPath($(if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
    Join-Path (Split-Path -Parent $PSScriptRoot) 'publish'
} else { $PublishRoot }))
$selectedChannels = @(ConvertTo-Tokens $Channels)
$script:SelectedRids = @(ConvertTo-Tokens $Rids)
foreach ($channel in $selectedChannels) {
    if ($channel -notin @('github', 'store', 'headless', 'container')) { throw "Unknown release channel '$channel'." }
}
if (@($selectedChannels | Where-Object { $_ -in @('github', 'headless') }).Count -gt 0 -and
    'win-x64' -notin $script:SelectedRids) {
    throw 'GitHub and headless mutation gates require win-x64 in -Rids.'
}

$script:MutationRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('devprojex-release-mutation-' + [guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($script:MutationRoot) | Out-Null
try {
    if ('github' -in $selectedChannels) {
        $directory = Join-Path $sourcePublishRoot "github/v$Version"
        foreach ($case in @(Get-ReceiptMutationCases -Receipt (Read-PayloadReceipt $directory 'win-x64'))) {
            Invoke-GitHubMutation -SourcePublishRoot $sourcePublishRoot -Case $case
        }
    }
    if ('store' -in $selectedChannels) {
        $directory = Join-Path $sourcePublishRoot "store/v$Version"
        foreach ($case in @(Get-ReceiptMutationCases -Receipt (Read-PayloadReceipt $directory 'win-x64'))) {
            Invoke-StoreMutation -SourcePublishRoot $sourcePublishRoot -Case $case
        }
    }
    if ('headless' -in $selectedChannels) {
        $directory = Join-Path $sourcePublishRoot "headless/v$Version"
        foreach ($case in @(Get-ReceiptMutationCases -Receipt (Read-PayloadReceipt $directory 'win-x64'))) {
            Invoke-HeadlessMutation -SourcePublishRoot $sourcePublishRoot -Case $case
        }
    }
    if ('container' -in $selectedChannels) {
        $containerRid = @($script:SelectedRids | Where-Object { $_ -in @('linux-x64', 'linux-arm64') } | Select-Object -First 1)
        if ($containerRid.Count -ne 1) { throw 'Container mutation gate requires a Linux RID in -Rids.' }
        $directory = Join-Path $sourcePublishRoot "container/v$Version"
        foreach ($case in @(Get-ReceiptMutationCases -Receipt (Read-PayloadReceipt $directory $containerRid[0]))) {
            Invoke-ContainerMutation -SourcePublishRoot $sourcePublishRoot -Rid $containerRid[0] -Case $case
        }
    }
}
finally {
    $resolvedMutationRoot = [System.IO.Path]::GetFullPath($script:MutationRoot)
    $resolvedSystemTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($resolvedMutationRoot.StartsWith($resolvedSystemTemp, [System.StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedMutationRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$global:LASTEXITCODE = 0
