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

$repoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "release-archive-helpers.ps1")

function ConvertTo-Selection(
    [string[]] $Values,
    [string[]] $Allowed,
    [string] $Kind
) {
    $selection = New-Object 'System.Collections.Generic.List[string]'
    foreach ($value in @($Values)) {
        foreach ($token in @(([string]$value).Split(','))) {
            $normalized = $token.Trim().ToLowerInvariant()
            if ([string]::IsNullOrWhiteSpace($normalized)) {
                continue
            }
            if ($normalized -notin $Allowed) {
                throw "Unknown release $Kind '$token'. Allowed values: $($Allowed -join ', ')."
            }
            if ($normalized -notin $selection) {
                $selection.Add($normalized)
            }
        }
    }
    if ($selection.Count -eq 0) {
        throw "At least one release $Kind must be selected."
    }
    return $selection.ToArray()
}

function Fail-Artifact([string] $ArtifactName, [string] $Problem) {
    throw "Artifact '$ArtifactName' is incomplete: $Problem."
}

function Assert-Artifact(
    [bool] $Condition,
    [string] $ArtifactName,
    [string] $Problem
) {
    if (-not $Condition) {
        Fail-Artifact -ArtifactName $ArtifactName -Problem $Problem
    }
}

function Get-StorePackageVersion([string] $DisplayVersion) {
    if ($DisplayVersion -notmatch '^\d+(\.\d+){0,3}$') {
        throw "Invalid release version '$DisplayVersion'. Expected 1-4 numeric segments."
    }
    $parts = New-Object 'System.Collections.Generic.List[string]'
    foreach ($part in $DisplayVersion.Split('.')) {
        $parts.Add($part)
    }
    while ($parts.Count -lt 4) {
        $parts.Add('0')
    }
    return $parts -join '.'
}

function Get-GitHubArtifactName([object] $Rid) {
    if ($Rid.os -ceq 'win32') {
        return "DevProjex.v$Version.$($Rid.rid).exe"
    }
    if ($Rid.os -ceq 'linux') {
        return "DevProjex.v$Version.$($Rid.rid).tar.gz"
    }
    if ($Rid.os -ceq 'darwin') {
        return "DevProjex.v$Version.$($Rid.rid).app.tar.gz"
    }
    throw "Unsupported release operating system '$($Rid.os)' for RID '$($Rid.rid)'."
}

function Assert-ExactNames(
    [string[]] $Expected,
    [string[]] $Actual,
    [string] $ArtifactName,
    [string] $Kind
) {
    $expectedNames = @($Expected | Sort-Object)
    $actualNames = @($Actual | Sort-Object)
    $differences = @(Compare-Object -ReferenceObject $expectedNames -DifferenceObject $actualNames -CaseSensitive)
    Assert-Artifact `
        -Condition ($differences.Count -eq 0) `
        -ArtifactName $ArtifactName `
        -Problem "missing or unexpected $Kind; expected: $($expectedNames -join ', '); found: $($actualNames -join ', ')"
}

function Get-ChecksumEntries([string] $DirectoryPath, [string[]] $ExpectedNames) {
    $manifestPath = Join-Path $DirectoryPath 'SHA256SUMS.txt'
    Assert-Artifact (Test-Path -LiteralPath $manifestPath -PathType Leaf) $DirectoryPath "missing SHA256SUMS.txt"

    $entries = @{}
    foreach ($line in @(Get-Content -LiteralPath $manifestPath)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        if ($line -notmatch '^(?<hash>[0-9a-f]{64}) \*(?<name>[^\r\n]+)$') {
            Fail-Artifact $manifestPath "invalid checksum line '$line'"
        }
        $name = [string]$Matches.name
        if ($entries.ContainsKey($name)) {
            Fail-Artifact $manifestPath "duplicate checksum for '$name'"
        }
        $entries[$name] = [string]$Matches.hash
    }

    Assert-ExactNames -Expected $ExpectedNames -Actual @($entries.Keys) -ArtifactName $manifestPath -Kind 'checksum entries'
    foreach ($name in $ExpectedNames) {
        $artifactPath = Join-Path $DirectoryPath $name
        Assert-Artifact (Test-Path -LiteralPath $artifactPath -PathType Leaf) $name "missing file"
        $actualHash = Get-FileSha256Hex -path $artifactPath
        Assert-Artifact ($actualHash -ceq [string]$entries[$name]) $name "invalid SHA-256; expected '$($entries[$name])', found '$actualHash'"
    }
    return $entries
}

function Assert-BinaryPayload(
    [byte[]] $Bytes,
    [object] $Rid,
    [string] $ArtifactName
) {
    $payload = [System.Text.Encoding]::Latin1.GetString($Bytes)
    $expectedPayloadNames = New-Object 'System.Collections.Generic.List[object]'
    foreach ($grammar in @($script:Manifest.grammars)) {
        $grammarName = "$($Rid.grammarPrefix)$grammar$($Rid.grammarExtension)"
        $expectedPayloadNames.Add([pscustomobject]@{ Name = $grammarName; Kind = 'grammar' })
    }
    foreach ($localization in @($script:Manifest.release.localizations)) {
        $expectedPayloadNames.Add([pscustomobject]@{ Name = [string]$localization; Kind = 'localization' })
    }
    $pattern = @($expectedPayloadNames |
        ForEach-Object { [System.Text.RegularExpressions.Regex]::Escape([string]$_.Name) } |
        Sort-Object { $_.Length } -Descending) -join '|'
    $foundNames = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
    foreach ($match in [System.Text.RegularExpressions.Regex]::Matches(
        $payload,
        $pattern,
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)) {
        [void]$foundNames.Add($match.Value)
    }
    foreach ($expected in $expectedPayloadNames) {
        Assert-Artifact `
            ($foundNames.Contains([string]$expected.Name)) `
            $ArtifactName `
            "missing $($expected.Kind) '$($expected.Name)'"
    }
    Assert-Artifact `
        ($payload.IndexOf($Version, [System.StringComparison]::Ordinal) -ge 0) `
        $ArtifactName `
        "invalid informational version; expected '$Version' in the uncompressed single-file payload"
}

function Assert-WindowsReleaseArtifact([string] $ArtifactPath, [object] $Rid) {
    $artifactName = [System.IO.Path]::GetFileName($ArtifactPath)
    $versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($ArtifactPath)
    Assert-Artifact `
        (-not [string]::IsNullOrWhiteSpace($versionInfo.FileVersion) -and
            $versionInfo.FileVersion.StartsWith($script:StorePackageVersion, [System.StringComparison]::OrdinalIgnoreCase)) `
        $artifactName `
        "invalid FileVersion; expected '$script:StorePackageVersion', found '$($versionInfo.FileVersion)'"
    Assert-Artifact `
        (-not [string]::IsNullOrWhiteSpace($versionInfo.ProductVersion) -and
            $versionInfo.ProductVersion.StartsWith($Version, [System.StringComparison]::OrdinalIgnoreCase)) `
        $artifactName `
        "invalid ProductVersion; expected '$Version', found '$($versionInfo.ProductVersion)'"
    Assert-BinaryPayload -Bytes ([System.IO.File]::ReadAllBytes($ArtifactPath)) -Rid $Rid -ArtifactName $artifactName
}

function Assert-LinuxReleaseArtifact([string] $ArtifactPath, [object] $Rid) {
    $artifactName = [System.IO.Path]::GetFileName($ArtifactPath)
    $entries = @(Read-UstarGzipArchive -archivePath $ArtifactPath -captureEntryNames @([string]$Rid.releaseBinary))
    Assert-Artifact ($entries.Count -eq 1) $artifactName "invalid archive layout; expected only '$($Rid.releaseBinary)'"
    $binary = $entries[0]
    Assert-Artifact ($binary.Name -ceq [string]$Rid.releaseBinary) $artifactName "missing executable '$($Rid.releaseBinary)'"
    Assert-Artifact (($binary.Mode -band 73) -eq 73) $artifactName "invalid executable mode for '$($Rid.releaseBinary)'"
    Assert-Artifact ($null -ne $binary.Bytes -and $binary.Bytes.Length -gt 0) $artifactName "empty executable '$($Rid.releaseBinary)'"
    Assert-BinaryPayload -Bytes $binary.Bytes -Rid $Rid -ArtifactName $artifactName
}

function Get-PlistValue([xml] $Document, [string] $Key) {
    $keyNode = $Document.SelectSingleNode("/plist/dict/key[text()='$Key']")
    if ($null -eq $keyNode) {
        return $null
    }
    $valueNode = $keyNode.NextSibling
    while ($null -ne $valueNode -and $valueNode.NodeType -ne [System.Xml.XmlNodeType]::Element) {
        $valueNode = $valueNode.NextSibling
    }
    if ($null -eq $valueNode -or $valueNode.LocalName -ne 'string') {
        return $null
    }
    return [string]$valueNode.InnerText
}

function Assert-MacReleaseArtifact([string] $ArtifactPath, [object] $Rid) {
    $artifactName = [System.IO.Path]::GetFileName($ArtifactPath)
    $binaryEntryName = 'DevProjex.app/Contents/MacOS/DevProjex'
    $plistEntryName = 'DevProjex.app/Contents/Info.plist'
    $expectedEntries = @(
        'DevProjex.app/',
        'DevProjex.app/Contents/',
        $plistEntryName,
        'DevProjex.app/Contents/MacOS/',
        $binaryEntryName,
        'DevProjex.app/Contents/Resources/',
        'DevProjex.app/Contents/Resources/app.icns'
    )
    $entries = @(Read-UstarGzipArchive -archivePath $ArtifactPath -captureEntryNames @($plistEntryName, $binaryEntryName))
    Assert-ExactNames -Expected $expectedEntries -Actual @($entries | ForEach-Object Name) -ArtifactName $artifactName -Kind 'macOS bundle entries'
    $binary = $entries | Where-Object { $_.Name -ceq $binaryEntryName } | Select-Object -First 1
    Assert-Artifact ($null -ne $binary -and ($binary.Mode -band 73) -eq 73) $artifactName "invalid executable mode for '$binaryEntryName'"
    Assert-BinaryPayload -Bytes $binary.Bytes -Rid $Rid -ArtifactName $artifactName
    $plist = $entries | Where-Object { $_.Name -ceq $plistEntryName } | Select-Object -First 1
    Assert-Artifact ($null -ne $plist -and $null -ne $plist.Bytes) $artifactName "missing Info.plist"
    [xml]$plistDocument = [System.Text.Encoding]::UTF8.GetString($plist.Bytes)
    foreach ($versionKey in @('CFBundleVersion', 'CFBundleShortVersionString')) {
        $actualVersion = Get-PlistValue -Document $plistDocument -Key $versionKey
        Assert-Artifact ($actualVersion -ceq $Version) $artifactName "invalid $versionKey; expected '$Version', found '$actualVersion'"
    }
}

function Test-GitHubArtifacts([object[]] $SelectedRids) {
    $directory = Join-Path $script:PublishPath "github/v$Version"
    Assert-Artifact (Test-Path -LiteralPath $directory -PathType Container) $directory "missing GitHub channel directory"
    $expectedNames = @($SelectedRids | ForEach-Object { Get-GitHubArtifactName $_ })
    $actualNames = @(
        Get-ChildItem -LiteralPath $directory -File |
            Where-Object { $_.Name -notin @('SHA256SUMS.txt', 'PARTIAL-BUILD.txt') } |
            ForEach-Object Name
    )
    Assert-ExactNames -Expected $expectedNames -Actual $actualNames -ArtifactName $directory -Kind 'GitHub artifact set'
    [void](Get-ChecksumEntries -DirectoryPath $directory -ExpectedNames $expectedNames)

    $partial = $SelectedRids.Count -ne @($script:Manifest.rids).Count
    $partialMarker = Join-Path $directory 'PARTIAL-BUILD.txt'
    if ($partial) {
        Assert-Artifact (Test-Path -LiteralPath $partialMarker -PathType Leaf) $directory "missing PARTIAL-BUILD.txt for a partial RID set"
        $marker = Get-Content -LiteralPath $partialMarker -Raw
        foreach ($rid in $SelectedRids) {
            Assert-Artifact ($marker.IndexOf([string]$rid.rid, [System.StringComparison]::Ordinal) -ge 0) $partialMarker "missing selected RID '$($rid.rid)'"
        }
    }
    else {
        Assert-Artifact (-not (Test-Path -LiteralPath $partialMarker)) $directory "unexpected PARTIAL-BUILD.txt for a complete RID set"
    }

    foreach ($rid in $SelectedRids) {
        $artifactPath = Join-Path $directory (Get-GitHubArtifactName $rid)
        if ($rid.os -ceq 'win32') {
            Assert-WindowsReleaseArtifact -ArtifactPath $artifactPath -Rid $rid
        }
        elseif ($rid.os -ceq 'linux') {
            Assert-LinuxReleaseArtifact -ArtifactPath $artifactPath -Rid $rid
        }
        else {
            Assert-MacReleaseArtifact -ArtifactPath $artifactPath -Rid $rid
        }
    }

    $status = if ($partial) { 'VALIDATED; PARTIAL (not release-ready)' } else { 'VALIDATED; COMPLETE' }
    return [pscustomobject]@{ Channel = 'github'; Directory = $directory; Status = $status; Artifacts = $expectedNames }
}

function Expand-Zip([string] $ArchivePath, [string] $DestinationPath) {
    [System.IO.Directory]::CreateDirectory($DestinationPath) | Out-Null
    [System.IO.Compression.ZipFile]::ExtractToDirectory($ArchivePath, $DestinationPath)
}

function Assert-StoreExecutionAlias([xml] $PackageManifest, [string] $PackageRoot, [string] $ArtifactName) {
    $applications = @($PackageManifest.SelectNodes("//*[local-name()='Application']"))
    Assert-Artifact ($applications.Count -eq 1) $ArtifactName "invalid Store Application count '$($applications.Count)'"
    $aliases = @($PackageManifest.SelectNodes("//*[local-name()='ExecutionAlias']"))
    Assert-Artifact ($aliases.Count -eq 1) $ArtifactName "invalid Store execution-alias count '$($aliases.Count)'"
    $alias = [string]$aliases[0].GetAttribute('Alias')
    Assert-Artifact ($alias -ceq [string]$script:Manifest.release.store.executionAlias) $ArtifactName "invalid execution alias '$alias'"
    $aliasExtensions = @($PackageManifest.SelectNodes("//*[local-name()='Extension' and @Category='windows.appExecutionAlias']"))
    Assert-Artifact ($aliasExtensions.Count -eq 1) $ArtifactName "missing windows.appExecutionAlias extension"
    $executable = [string]$aliasExtensions[0].GetAttribute('Executable')
    $expectedExecutable = [string]$script:Manifest.release.store.applicationExecutable
    Assert-Artifact ($executable -ceq $expectedExecutable) $ArtifactName "invalid execution-alias executable '$executable'; expected '$expectedExecutable'"
    $packagedExecutable = Join-Path $PackageRoot ($expectedExecutable -replace '[\\/]', [System.IO.Path]::DirectorySeparatorChar)
    Assert-Artifact (Test-Path -LiteralPath $packagedExecutable -PathType Leaf) $ArtifactName "missing execution-alias executable '$expectedExecutable'"
}

function Assert-StoreApplicationPackage(
    [string] $PackagePath,
    [string] $Architecture,
    [string] $ArtifactName,
    [string] $TemporaryRoot
) {
    $packageRoot = Join-Path $TemporaryRoot ("package-" + $Architecture + '-' + [guid]::NewGuid().ToString('N'))
    Expand-Zip -ArchivePath $PackagePath -DestinationPath $packageRoot
    $manifestPath = Join-Path $packageRoot 'AppxManifest.xml'
    Assert-Artifact (Test-Path -LiteralPath $manifestPath -PathType Leaf) $ArtifactName "missing AppxManifest.xml for '$Architecture'"
    [xml]$packageManifest = Get-Content -LiteralPath $manifestPath -Raw
    $identity = $packageManifest.SelectSingleNode("/*[local-name()='Package']/*[local-name()='Identity']")
    Assert-Artifact ($null -ne $identity) $ArtifactName "missing package identity for '$Architecture'"
    $actualArchitecture = [string]$identity.GetAttribute('ProcessorArchitecture')
    $actualVersion = [string]$identity.GetAttribute('Version')
    Assert-Artifact ($actualArchitecture.ToLowerInvariant() -ceq $Architecture) $ArtifactName "invalid package architecture '$actualArchitecture'; expected '$Architecture'"
    Assert-Artifact ($actualVersion -ceq $script:StorePackageVersion) $ArtifactName "invalid package version '$actualVersion'; expected '$script:StorePackageVersion'"
    Assert-StoreExecutionAlias -PackageManifest $packageManifest -PackageRoot $packageRoot -ArtifactName $ArtifactName

    $applicationDirectory = Split-Path -Path ([string]$script:Manifest.release.store.applicationExecutable) -Parent
    $grammarDirectory = Join-Path (Join-Path $packageRoot $applicationDirectory) 'grammars'
    Assert-Artifact (Test-Path -LiteralPath $grammarDirectory -PathType Container) $ArtifactName "missing grammar directory '$applicationDirectory\grammars'"
    $expectedGrammars = @($script:Manifest.grammars | ForEach-Object { "$_`.dll" })
    $actualGrammars = @(Get-ChildItem -LiteralPath $grammarDirectory -File -Filter 'tree-sitter-*.dll' | ForEach-Object Name)
    Assert-ExactNames -Expected $expectedGrammars -Actual $actualGrammars -ArtifactName $ArtifactName -Kind "grammar set for '$Architecture'"
}

function Assert-StoreBundle(
    [string] $BundlePath,
    [string] $ArtifactName,
    [string] $TemporaryRoot
) {
    $bundleRoot = Join-Path $TemporaryRoot ('bundle-' + [guid]::NewGuid().ToString('N'))
    Expand-Zip -ArchivePath $BundlePath -DestinationPath $bundleRoot
    $bundleManifestPath = Join-Path $bundleRoot 'AppxMetadata/AppxBundleManifest.xml'
    Assert-Artifact (Test-Path -LiteralPath $bundleManifestPath -PathType Leaf) $ArtifactName "missing AppxBundleManifest.xml"
    [xml]$bundleManifest = Get-Content -LiteralPath $bundleManifestPath -Raw
    $bundleIdentity = $bundleManifest.SelectSingleNode("/*[local-name()='Bundle']/*[local-name()='Identity']")
    Assert-Artifact ($null -ne $bundleIdentity) $ArtifactName "missing bundle identity"
    $bundleVersion = [string]$bundleIdentity.GetAttribute('Version')
    Assert-Artifact ($bundleVersion -ceq $script:StorePackageVersion) $ArtifactName "invalid bundle version '$bundleVersion'; expected '$script:StorePackageVersion'"

    $packageNodes = @($bundleManifest.SelectNodes("//*[local-name()='Package' and @Architecture]"))
    $expectedPlatforms = @($script:Manifest.release.store.platforms | ForEach-Object { ([string]$_).ToLowerInvariant() })
    $actualPlatforms = @($packageNodes | ForEach-Object { ([string]$_.GetAttribute('Architecture')).ToLowerInvariant() } | Sort-Object -Unique)
    Assert-ExactNames -Expected $expectedPlatforms -Actual $actualPlatforms -ArtifactName $ArtifactName -Kind 'Store bundle platforms'

    $languageNodes = @($bundleManifest.SelectNodes("//*[local-name()='Resource' and @Language]"))
    $actualLanguages = @($languageNodes | ForEach-Object { ([string]$_.GetAttribute('Language')).ToLowerInvariant() } | Sort-Object -Unique)
    $expectedLanguages = @($script:Manifest.release.store.resourceLanguages | ForEach-Object { ([string]$_).ToLowerInvariant() })
    Assert-ExactNames -Expected $expectedLanguages -Actual $actualLanguages -ArtifactName $ArtifactName -Kind 'Store resource languages'

    foreach ($platform in $expectedPlatforms) {
        $matches = @($packageNodes | Where-Object { ([string]$_.GetAttribute('Architecture')).ToLowerInvariant() -ceq $platform })
        Assert-Artifact ($matches.Count -eq 1) $ArtifactName "missing one application package for '$platform'"
        $fileName = [string]$matches[0].GetAttribute('FileName')
        $packagePath = Join-Path $bundleRoot ($fileName -replace '/', [System.IO.Path]::DirectorySeparatorChar)
        Assert-Artifact (Test-Path -LiteralPath $packagePath -PathType Leaf) $ArtifactName "missing inner package '$fileName'"
        Assert-StoreApplicationPackage -PackagePath $packagePath -Architecture $platform -ArtifactName $ArtifactName -TemporaryRoot $TemporaryRoot
    }
}

function Assert-StoreUpload([string] $UploadPath, [string] $TemporaryRoot) {
    $artifactName = [System.IO.Path]::GetFileName($UploadPath)
    $uploadRoot = Join-Path $TemporaryRoot 'upload'
    Expand-Zip -ArchivePath $UploadPath -DestinationPath $uploadRoot
    $bundles = @(Get-ChildItem -LiteralPath $uploadRoot -Recurse -File -Filter '*.msixbundle')
    Assert-Artifact ($bundles.Count -eq 1) $artifactName "missing one x64|arm64 .msixbundle"
    Assert-StoreBundle `
        -BundlePath $bundles[0].FullName `
        -ArtifactName $artifactName `
        -TemporaryRoot $TemporaryRoot
}

function Test-StoreArtifacts() {
    $directory = Join-Path $script:PublishPath "store/v$Version"
    Assert-Artifact (Test-Path -LiteralPath $directory -PathType Container) $directory "missing Store channel directory"
    $expectedNames = @(
        "DevProjex.Store_$($script:StorePackageVersion)_x64_arm64_bundle_ReleaseStore.msixupload",
        "DevProjex.Store_$($script:StorePackageVersion)_x64_arm64_ReleaseStore.msixbundle",
        "DevProjex.Store_$($script:StorePackageVersion)_x64_ReleaseStore.msix"
    )
    $packages = @(Get-ChildItem -LiteralPath $directory -File |
        Where-Object { $_.Name -notin @('SHA256SUMS.txt', 'msix-build.log') })
    $packageNames = @($packages | ForEach-Object Name)
    Assert-ExactNames -Expected $expectedNames -Actual $packageNames -ArtifactName $directory -Kind 'Store artifact set'
    $uploads = @($packages | Where-Object { $_.Extension -ceq '.msixupload' })
    Assert-Artifact ($uploads.Count -eq 1) $directory "missing exactly one .msixupload; found '$($uploads.Count)'"
    [void](Get-ChecksumEntries -DirectoryPath $directory -ExpectedNames $expectedNames)

    $temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("devprojex-release-artifacts-" + [guid]::NewGuid().ToString('N'))
    [System.IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    try {
        Assert-StoreUpload -UploadPath $uploads[0].FullName -TemporaryRoot $temporaryRoot
        Assert-StoreBundle `
            -BundlePath (Join-Path $directory $expectedNames[1]) `
            -ArtifactName $expectedNames[1] `
            -TemporaryRoot $temporaryRoot
        Assert-StoreApplicationPackage `
            -PackagePath (Join-Path $directory $expectedNames[2]) `
            -Architecture 'x64' `
            -ArtifactName $expectedNames[2] `
            -TemporaryRoot $temporaryRoot
    }
    finally {
        $resolvedTemporaryRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
        $resolvedSystemTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        if ($resolvedTemporaryRoot.StartsWith($resolvedSystemTemp, [System.StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    return [pscustomobject]@{ Channel = 'store'; Directory = $directory; Status = 'VALIDATED; WACK status separate'; Artifacts = $expectedNames }
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$manifestPath = Join-Path $repoRoot 'Packaging/Headless/payload-manifest.json'
$script:Manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
Assert-Artifact ($script:Manifest.schemaVersion -eq 1) $manifestPath "unsupported schema version '$($script:Manifest.schemaVersion)'"
Assert-Artifact ($null -ne $script:Manifest.release) $manifestPath "missing release contract"
$script:StorePackageVersion = Get-StorePackageVersion -DisplayVersion $Version
$script:PublishPath = [System.IO.Path]::GetFullPath($(if ([string]::IsNullOrWhiteSpace($PublishRoot)) { Join-Path $repoRoot 'publish' } else { $PublishRoot }))
$allowedChannels = @('github', 'store')
$allowedRids = @($script:Manifest.rids | ForEach-Object { [string]$_.rid })
$selectedChannels = @(ConvertTo-Selection -Values $Channels -Allowed $allowedChannels -Kind 'channel')
$selectedRidNames = @(ConvertTo-Selection -Values $Rids -Allowed $allowedRids -Kind 'RID')
$selectedRids = @($selectedRidNames | ForEach-Object {
    $ridName = $_
    $script:Manifest.rids | Where-Object { $_.rid -ceq $ridName } | Select-Object -First 1
})

$results = New-Object 'System.Collections.Generic.List[object]'
if ('github' -in $selectedChannels) {
    $results.Add((Test-GitHubArtifacts -SelectedRids $selectedRids))
}
if ('store' -in $selectedChannels) {
    $results.Add((Test-StoreArtifacts))
}

Write-Host 'Release artifact validation summary:'
foreach ($result in $results) {
    Write-Host "  Channel $($result.Channel): $($result.Status)"
    foreach ($artifactName in @($result.Artifacts | Sort-Object)) {
        $artifactPath = Join-Path $result.Directory $artifactName
        $item = Get-Item -LiteralPath $artifactPath
        Write-Host "    $artifactName | $($item.Length) bytes | SHA-256 $(Get-FileSha256Hex -path $artifactPath)"
    }
    Write-Host "    Output: $($result.Directory)"
}
