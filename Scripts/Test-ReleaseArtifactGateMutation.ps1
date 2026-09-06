[CmdletBinding()]
param(
    [string] $PublishRoot = "",

    [Parameter(Mandatory = $true)]
    [string] $Version,

    [string[]] $Channels = @("github", "store"),

    [string[]] $Rids = @(
        "win-x64",
        "win-arm64",
        "linux-x64",
        "linux-arm64",
        "osx-x64",
        "osx-arm64"
    )
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'release-archive-helpers.ps1')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function ConvertTo-Tokens([string[]] $Values) {
    return @(
        $Values |
            ForEach-Object { ([string]$_).Split(',') } |
            ForEach-Object { $_.Trim().ToLowerInvariant() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Select-Object -Unique
    )
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

function Replace-BytesInFile(
    [string] $Path,
    [string] $NeedleText,
    [string] $ReplacementText
) {
    $needle = [System.Text.Encoding]::ASCII.GetBytes($NeedleText)
    $replacement = [System.Text.Encoding]::ASCII.GetBytes($ReplacementText)
    if ($needle.Length -ne $replacement.Length) {
        throw "Mutation replacement must preserve byte length: '$NeedleText' -> '$ReplacementText'."
    }
    $bytes = [System.IO.File]::ReadAllBytes($Path)
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
        throw "Mutation setup failed: '$NeedleText' was not found in '$Path'."
    }
    [System.IO.File]::WriteAllBytes($Path, $bytes)
}

function Write-ChannelChecksums([string] $ChannelDirectory) {
    $artifacts = @(
        Get-ChildItem -LiteralPath $ChannelDirectory -File |
            Where-Object { $_.Name -notin @('SHA256SUMS.txt', 'PARTIAL-BUILD.txt') } |
            Sort-Object Name
    )
    $lines = @($artifacts | ForEach-Object { "$(Get-FileSha256Hex -path $_.FullName) *$($_.Name)" })
    Write-ReleaseChecksumManifest -path (Join-Path $ChannelDirectory 'SHA256SUMS.txt') -lines $lines
}

function Invoke-FailingValidation(
    [string] $FixturePublishRoot,
    [string] $Channel,
    [string] $ArtifactName,
    [string] $ExpectedMissing
) {
    $shellPath = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    $arguments = @(
        '-NoLogo',
        '-NoProfile',
        '-File', (Join-Path $PSScriptRoot 'Test-ReleaseArtifacts.ps1'),
        '-PublishRoot', $FixturePublishRoot,
        '-Version', $Version,
        '-Channels', $Channel
    )
    if ($Channel -ceq 'github') {
        $arguments += @('-Rids', ($script:SelectedRids -join ','))
    }
    $output = & $shellPath @arguments 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0) {
        throw "Mutation gate failed open for '$ArtifactName' after removing '$ExpectedMissing'."
    }
    if ($output.IndexOf($ArtifactName, [System.StringComparison]::Ordinal) -lt 0 -or
        $output.IndexOf($ExpectedMissing, [System.StringComparison]::Ordinal) -lt 0) {
        throw "Mutation gate failed without naming artifact '$ArtifactName' and '$ExpectedMissing': $output"
    }
    Write-Host "Mutation gate passed: $ArtifactName -> $ExpectedMissing"
}

function Invoke-GitHubMutation([string] $SourcePublishRoot, [string] $Needle, [string] $Replacement) {
    if ('win-x64' -notin $script:SelectedRids) {
        throw "GitHub mutation gate requires win-x64 in -Rids."
    }
    $fixtureRoot = Copy-ChannelFixture -SourcePublishRoot $SourcePublishRoot -Channel 'github'
    $artifactName = "DevProjex.v$Version.win-x64.exe"
    $channelDirectory = Join-Path $fixtureRoot "github/v$Version"
    Replace-BytesInFile -Path (Join-Path $channelDirectory $artifactName) -NeedleText $Needle -ReplacementText $Replacement
    Write-ChannelChecksums -ChannelDirectory $channelDirectory
    Invoke-FailingValidation `
        -FixturePublishRoot $fixtureRoot `
        -Channel 'github' `
        -ArtifactName $artifactName `
        -ExpectedMissing $Needle
}

function Compress-Directory([string] $SourceDirectory, [string] $DestinationPath) {
    if (Test-Path -LiteralPath $DestinationPath) {
        Remove-Item -LiteralPath $DestinationPath -Force
    }
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $SourceDirectory,
        $DestinationPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)
}

function Invoke-StoreGrammarMutation([string] $SourcePublishRoot) {
    $fixtureRoot = Copy-ChannelFixture -SourcePublishRoot $SourcePublishRoot -Channel 'store'
    $channelDirectory = Join-Path $fixtureRoot "store/v$Version"
    $uploads = @(Get-ChildItem -LiteralPath $channelDirectory -File -Filter '*.msixupload')
    if ($uploads.Count -ne 1) {
        throw "Mutation setup failed: expected one .msixupload, found $($uploads.Count)."
    }
    $artifactName = $uploads[0].Name
    $workRoot = Join-Path $script:MutationRoot ('store-' + [guid]::NewGuid().ToString('N'))
    $uploadRoot = Join-Path $workRoot 'upload'
    $bundleRoot = Join-Path $workRoot 'bundle'
    New-Item -ItemType Directory -Path $uploadRoot, $bundleRoot -Force | Out-Null
    [System.IO.Compression.ZipFile]::ExtractToDirectory($uploads[0].FullName, $uploadRoot)
    $bundles = @(Get-ChildItem -LiteralPath $uploadRoot -Recurse -File -Filter '*.msixbundle')
    if ($bundles.Count -ne 1) {
        throw "Mutation setup failed: '$artifactName' must contain exactly one .msixbundle."
    }
    $bundleRelativePath = $bundles[0].FullName.Substring($uploadRoot.Length).TrimStart([char[]]@('\', '/'))
    [System.IO.Compression.ZipFile]::ExtractToDirectory($bundles[0].FullName, $bundleRoot)

    $mutatedPackage = $null
    foreach ($package in @(Get-ChildItem -LiteralPath $bundleRoot -Recurse -File -Filter '*.msix')) {
        $packageRoot = Join-Path $workRoot ('package-' + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
        [System.IO.Compression.ZipFile]::ExtractToDirectory($package.FullName, $packageRoot)
        $grammar = Get-ChildItem -LiteralPath $packageRoot -Recurse -File -Filter 'tree-sitter-kotlin.dll' | Select-Object -First 1
        if ($null -eq $grammar) {
            Remove-Item -LiteralPath $packageRoot -Recurse -Force
            continue
        }
        Rename-Item -LiteralPath $grammar.FullName -NewName 'tree-sitter-Xotlin.dll'
        Compress-Directory -SourceDirectory $packageRoot -DestinationPath $package.FullName
        $mutatedPackage = $package
        break
    }
    if ($null -eq $mutatedPackage) {
        throw "Mutation setup failed: Store grammar 'tree-sitter-kotlin.dll' was not found."
    }

    Compress-Directory -SourceDirectory $bundleRoot -DestinationPath $bundles[0].FullName
    $rebuiltBundlePath = Join-Path $uploadRoot ($bundleRelativePath -replace '/', [System.IO.Path]::DirectorySeparatorChar)
    if ($rebuiltBundlePath -cne $bundles[0].FullName) {
        Copy-Item -LiteralPath $bundles[0].FullName -Destination $rebuiltBundlePath -Force
    }
    Compress-Directory -SourceDirectory $uploadRoot -DestinationPath $uploads[0].FullName
    Write-ChannelChecksums -ChannelDirectory $channelDirectory
    Invoke-FailingValidation `
        -FixturePublishRoot $fixtureRoot `
        -Channel 'store' `
        -ArtifactName $artifactName `
        -ExpectedMissing 'tree-sitter-kotlin.dll'
}

$sourcePublishRoot = [System.IO.Path]::GetFullPath($(if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
    Join-Path (Split-Path -Parent $PSScriptRoot) 'publish'
} else {
    $PublishRoot
}))
$selectedChannels = @(ConvertTo-Tokens $Channels)
$script:SelectedRids = @(ConvertTo-Tokens $Rids)
foreach ($channel in $selectedChannels) {
    if ($channel -notin @('github', 'store')) {
        throw "Unknown release channel '$channel'."
    }
}

$script:MutationRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('devprojex-release-mutation-' + [guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($script:MutationRoot) | Out-Null
try {
    if ('github' -in $selectedChannels) {
        Invoke-GitHubMutation `
            -SourcePublishRoot $sourcePublishRoot `
            -Needle 'tree-sitter-kotlin.dll' `
            -Replacement 'tree-sitter-Xotlin.dll'
        Invoke-GitHubMutation `
            -SourcePublishRoot $sourcePublishRoot `
            -Needle 'en.json' `
            -Replacement 'eX.json'
    }
    if ('store' -in $selectedChannels) {
        Invoke-StoreGrammarMutation -SourcePublishRoot $sourcePublishRoot
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
