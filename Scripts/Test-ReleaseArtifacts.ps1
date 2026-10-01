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

function Get-HeadlessArtifactName([object] $Rid) {
    $extension = if ($Rid.os -ceq 'win32') { 'zip' } else { 'tar.gz' }
    return "$($script:Manifest.release.headless.archivePrefix).v$Version.$($Rid.rid).$extension"
}

function Get-PayloadReceiptName([string] $Rid) {
    return "publish-payload.$Rid.json"
}

function Read-PayloadReceipt(
    [string] $DirectoryPath,
    [string] $Rid,
    [string] $ArtifactName
) {
    $receiptName = Get-PayloadReceiptName -Rid $Rid
    $receiptPath = Join-Path $DirectoryPath $receiptName
    Assert-Artifact (Test-Path -LiteralPath $receiptPath -PathType Leaf) $ArtifactName "missing payload receipt '$receiptName'"
    try {
        $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
    }
    catch {
        Fail-Artifact $ArtifactName "invalid payload receipt '$receiptName': $($_.Exception.Message)"
    }
    Assert-Artifact `
        ($receipt.PSObject.Properties.Name -contains 'schemaVersion') `
        $ArtifactName `
        "payload receipt '$receiptName' has no schemaVersion"
    Assert-Artifact ($receipt.schemaVersion -eq 1) $ArtifactName "unsupported payload receipt schema '$($receipt.schemaVersion)'"
    Assert-Artifact `
        ($receipt.PSObject.Properties.Name -contains 'rid') `
        $ArtifactName `
        "payload receipt '$receiptName' has no RID"
    Assert-Artifact ([string]$receipt.rid -ceq $Rid) $ArtifactName "payload receipt '$receiptName' names RID '$($receipt.rid)'"
    Assert-Artifact `
        ($receipt.PSObject.Properties.Name -contains 'files' -and $null -ne $receipt.files) `
        $ArtifactName `
        "payload receipt '$receiptName' has no files"
    return $receipt
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

function Get-ChecksumEntries(
    [string] $DirectoryPath,
    [string[]] $ExpectedNames,
    [string] $ManifestName = 'SHA256SUMS.txt'
) {
    $manifestPath = Join-Path $DirectoryPath $ManifestName
    Assert-Artifact (Test-Path -LiteralPath $manifestPath -PathType Leaf) $DirectoryPath "missing $ManifestName"

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

function Assert-PayloadDiff(
    [object] $Receipt,
    [object[]] $ActualFiles,
    [string] $ArtifactName
) {
    $expectedByPath = @{}
    foreach ($expected in @($Receipt.files)) {
        Assert-Artifact `
            ($null -ne $expected -and $expected.PSObject.Properties.Name -contains 'path') `
            $ArtifactName `
            'payload receipt contains a file without a path'
        $path = ([string]$expected.path).Replace('\', '/')
        Assert-Artifact (-not [string]::IsNullOrWhiteSpace($path)) $ArtifactName 'payload receipt contains an empty file path'
        Assert-Artifact (-not $expectedByPath.ContainsKey($path)) $ArtifactName "payload receipt contains duplicate file '$path'"
        Assert-Artifact `
            ($expected.PSObject.Properties.Name -contains 'size') `
            $ArtifactName `
            "payload receipt has no size for '$path'"
        Assert-Artifact `
            ($expected.PSObject.Properties.Name -contains 'sha256') `
            $ArtifactName `
            "payload receipt has no SHA-256 for '$path'"
        Assert-Artifact ([long]$expected.size -ge 0) $ArtifactName "payload receipt has an invalid size for '$path'"
        Assert-Artifact ([string]$expected.sha256 -cmatch '^[0-9a-f]{64}$') $ArtifactName "payload receipt has an invalid SHA-256 for '$path'"
        if ($expected.PSObject.Properties.Name -contains 'managedResources') {
            $resourceNames = @($expected.managedResources | ForEach-Object { [string]$_ })
            Assert-Artifact `
                (@($resourceNames | Sort-Object -Unique).Count -eq $resourceNames.Count) `
                $ArtifactName `
                "payload receipt has duplicate managed-resource names for '$path'"
        }
        $expectedByPath[$path] = $expected
    }

    $actualByPath = @{}
    foreach ($actual in @($ActualFiles)) {
        $path = ([string]$actual.Path).Replace('\', '/')
        Assert-Artifact (-not $actualByPath.ContainsKey($path)) $ArtifactName "artifact contains duplicate file '$path'"
        $actualByPath[$path] = $actual
    }

    foreach ($path in @($expectedByPath.Keys | Sort-Object)) {
        if (-not $actualByPath.ContainsKey($path)) {
            Fail-Artifact $ArtifactName "missing file '$path'"
        }
    }
    foreach ($path in @($actualByPath.Keys | Sort-Object)) {
        if (-not $expectedByPath.ContainsKey($path)) {
            Fail-Artifact $ArtifactName "unexpected file '$path'"
        }
    }

    foreach ($path in @($expectedByPath.Keys | Sort-Object)) {
        $expected = $expectedByPath[$path]
        $actual = $actualByPath[$path]
        $expectsResources = $expected.PSObject.Properties.Name -contains 'managedResources'
        if ($expectsResources) {
            Assert-Artifact ([bool]$actual.IsManagedAssembly) $ArtifactName "file '$path' is no longer a managed assembly"
            $expectedResources = @($expected.managedResources | ForEach-Object { [string]$_ })
            $actualResources = @($actual.ManagedResources | ForEach-Object { [string]$_ })
            foreach ($resource in @($expectedResources | Sort-Object)) {
                if ($resource -cnotin $actualResources) {
                    Fail-Artifact $ArtifactName "missing resource '$resource' from '$path'"
                }
            }
            foreach ($resource in @($actualResources | Sort-Object)) {
                if ($resource -cnotin $expectedResources) {
                    Fail-Artifact $ArtifactName "unexpected resource '$resource' in '$path'"
                }
            }
        }
        elseif ([bool]$actual.IsManagedAssembly) {
            Fail-Artifact $ArtifactName "unexpected managed assembly '$path'"
        }

        $expectedSize = [long]$expected.size
        $actualSize = [long]$actual.Size
        Assert-Artifact ($actualSize -eq $expectedSize) $ArtifactName "invalid size for '$path'; expected '$expectedSize', found '$actualSize'"
        $expectedHash = ([string]$expected.sha256).ToLowerInvariant()
        $actualHash = ([string]$actual.Sha256).ToLowerInvariant()
        Assert-Artifact ($actualHash -ceq $expectedHash) $ArtifactName "invalid SHA-256 for '$path'; expected '$expectedHash', found '$actualHash'"
    }
}

function Assert-SingleFilePayload(
    [byte[]] $Bytes,
    [object] $Receipt,
    [string] $ArtifactName
) {
    try {
        $inspection = [DevProjex.ReleaseValidation.ReleasePayloadInspector]::InspectBundle($Bytes)
    }
    catch {
        $problem = if ($null -ne $_.Exception.InnerException) { $_.Exception.InnerException.Message } else { $_.Exception.Message }
        Fail-Artifact $ArtifactName $problem
    }
    Assert-PayloadDiff -Receipt $Receipt -ActualFiles @($inspection.Files) -ArtifactName $ArtifactName
    $payload = [System.Text.Encoding]::Latin1.GetString($Bytes)
    Assert-Artifact `
        ($payload.IndexOf($Version, [System.StringComparison]::Ordinal) -ge 0) `
        $ArtifactName `
        "invalid informational version; expected '$Version' in the uncompressed single-file payload"
}

function Get-ZipEntryBytes([System.IO.Compression.ZipArchiveEntry] $Entry) {
    $entryStream = $Entry.Open()
    try {
        $memory = [System.IO.MemoryStream]::new()
        try {
            $entryStream.CopyTo($memory)
            return $memory.ToArray()
        }
        finally { $memory.Dispose() }
    }
    finally { $entryStream.Dispose() }
}

function Assert-HeadlessArtifact([string] $ArtifactPath, [object] $Rid, [object] $Receipt) {
    $artifactName = [System.IO.Path]::GetFileName($ArtifactPath)
    if ($Rid.os -ceq 'win32') {
        $archive = [System.IO.Compression.ZipFile]::OpenRead($ArtifactPath)
        try {
            $entries = @($archive.Entries | Where-Object { -not $_.FullName.EndsWith('/') })
            Assert-Artifact `
                ($entries.Count -eq 1 -and $entries[0].FullName -ceq [string]$Rid.binary) `
                $artifactName `
                "invalid archive layout; expected only '$($Rid.binary)'"
            $bytes = Get-ZipEntryBytes -Entry $entries[0]
        }
        finally { $archive.Dispose() }
    }
    else {
        $entries = @(Read-UstarGzipArchive -archivePath $ArtifactPath -captureEntryNames @([string]$Rid.binary))
        Assert-Artifact `
            ($entries.Count -eq 1 -and $entries[0].Name -ceq [string]$Rid.binary) `
            $artifactName `
            "invalid archive layout; expected only '$($Rid.binary)'"
        Assert-Artifact (($entries[0].Mode -band 73) -eq 73) $artifactName "invalid executable mode for '$($Rid.binary)'"
        $bytes = [byte[]]$entries[0].Bytes
    }

    Assert-Artifact ($null -ne $bytes -and $bytes.Length -gt 0) $artifactName "empty executable '$($Rid.binary)'"
    Assert-SingleFilePayload -Bytes $bytes -Receipt $Receipt -ArtifactName $artifactName
}

function Assert-WindowsReleaseArtifact([string] $ArtifactPath, [object] $Receipt) {
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
    Assert-SingleFilePayload -Bytes ([System.IO.File]::ReadAllBytes($ArtifactPath)) -Receipt $Receipt -ArtifactName $artifactName
}

function Assert-LinuxReleaseArtifact([string] $ArtifactPath, [object] $Rid, [object] $Receipt) {
    $artifactName = [System.IO.Path]::GetFileName($ArtifactPath)
    $entries = @(Read-UstarGzipArchive -archivePath $ArtifactPath -captureEntryNames @([string]$Rid.releaseBinary))
    Assert-Artifact ($entries.Count -eq 1) $artifactName "invalid archive layout; expected only '$($Rid.releaseBinary)'"
    $binary = $entries[0]
    Assert-Artifact ($binary.Name -ceq [string]$Rid.releaseBinary) $artifactName "missing executable '$($Rid.releaseBinary)'"
    Assert-Artifact (($binary.Mode -band 73) -eq 73) $artifactName "invalid executable mode for '$($Rid.releaseBinary)'"
    Assert-Artifact ($null -ne $binary.Bytes -and $binary.Bytes.Length -gt 0) $artifactName "empty executable '$($Rid.releaseBinary)'"
    Assert-SingleFilePayload -Bytes $binary.Bytes -Receipt $Receipt -ArtifactName $artifactName
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

function Assert-MacReleaseArtifact([string] $ArtifactPath, [object] $Rid, [object] $Receipt) {
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
    Assert-SingleFilePayload -Bytes $binary.Bytes -Receipt $Receipt -ArtifactName $artifactName
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
    $artifactNames = @($SelectedRids | ForEach-Object { Get-GitHubArtifactName $_ })
    $receiptNames = @($SelectedRids | ForEach-Object { Get-PayloadReceiptName -Rid ([string]$_.rid) })
    $expectedNames = @($artifactNames) + @($receiptNames)
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
        $receipt = Read-PayloadReceipt -DirectoryPath $directory -Rid ([string]$rid.rid) -ArtifactName ([System.IO.Path]::GetFileName($artifactPath))
        if ($rid.os -ceq 'win32') {
            Assert-WindowsReleaseArtifact -ArtifactPath $artifactPath -Receipt $receipt
        }
        elseif ($rid.os -ceq 'linux') {
            Assert-LinuxReleaseArtifact -ArtifactPath $artifactPath -Rid $rid -Receipt $receipt
        }
        else {
            Assert-MacReleaseArtifact -ArtifactPath $artifactPath -Rid $rid -Receipt $receipt
        }
    }

    $status = if ($partial) { 'VALIDATED; PARTIAL (not release-ready)' } else { 'VALIDATED; COMPLETE' }
    return [pscustomobject]@{ Channel = 'github'; Directory = $directory; Status = $status; Artifacts = $expectedNames }
}

function Test-HeadlessArtifacts([object[]] $SelectedRids) {
    $directory = Join-Path $script:PublishPath "headless/v$Version"
    Assert-Artifact (Test-Path -LiteralPath $directory -PathType Container) $directory "missing headless channel directory"
    $artifactNames = @($SelectedRids | ForEach-Object { Get-HeadlessArtifactName $_ })
    $receiptNames = @($SelectedRids | ForEach-Object { Get-PayloadReceiptName -Rid ([string]$_.rid) })
    $expectedNames = @($artifactNames) + @($receiptNames)
    $actualNames = @(Get-ChildItem -LiteralPath $directory -File |
        Where-Object { $_.Name -cne [string]$script:Manifest.release.headless.checksumFile } |
        ForEach-Object Name)
    Assert-ExactNames -Expected $expectedNames -Actual $actualNames -ArtifactName $directory -Kind 'headless artifact set'
    [void](Get-ChecksumEntries `
        -DirectoryPath $directory `
        -ExpectedNames $expectedNames `
        -ManifestName ([string]$script:Manifest.release.headless.checksumFile))

    foreach ($rid in $SelectedRids) {
        $artifactName = Get-HeadlessArtifactName $rid
        $receipt = Read-PayloadReceipt `
            -DirectoryPath $directory `
            -Rid ([string]$rid.rid) `
            -ArtifactName $artifactName
        Assert-HeadlessArtifact `
            -ArtifactPath (Join-Path $directory $artifactName) `
            -Rid $rid `
            -Receipt $receipt
    }

    $partial = $SelectedRids.Count -ne @($script:Manifest.rids).Count
    $status = if ($partial) { 'VALIDATED; PARTIAL RID set' } else { 'VALIDATED; COMPLETE' }
    return [pscustomobject]@{ Channel = 'headless'; Directory = $directory; Status = $status; Artifacts = $expectedNames }
}

function Get-FolderPayloadFiles([string] $DirectoryPath) {
    $payloadFiles = New-Object 'System.Collections.Generic.List[object]'
    foreach ($file in @(Get-ChildItem -LiteralPath $DirectoryPath -Recurse -File)) {
        $relativePath = $file.FullName.Substring($DirectoryPath.Length).TrimStart('\', '/').Replace('\', '/')
        $resources = [DevProjex.ReleaseValidation.ReleasePayloadInspector]::TryReadManagedResources($file.FullName)
        $payloadFiles.Add([pscustomobject]@{
            Path = $relativePath
            Size = $file.Length
            Sha256 = Get-FileSha256Hex -path $file.FullName
            IsManagedAssembly = $null -ne $resources
            ManagedResources = if ($null -eq $resources) { @() } else { @($resources) }
        })
    }
    return $payloadFiles.ToArray()
}

function Test-ContainerArtifacts([object[]] $SelectedRids) {
    $directory = Join-Path $script:PublishPath "container/v$Version"
    Assert-Artifact (Test-Path -LiteralPath $directory -PathType Container) $directory "missing container channel directory"
    $expectedNames = @($SelectedRids | ForEach-Object { [string]$_.rid }) +
        @($SelectedRids | ForEach-Object { Get-PayloadReceiptName -Rid ([string]$_.rid) })
    $actualNames = @(Get-ChildItem -LiteralPath $directory | ForEach-Object Name)
    Assert-ExactNames -Expected $expectedNames -Actual $actualNames -ArtifactName $directory -Kind 'container payload set'

    foreach ($rid in $SelectedRids) {
        $ridName = [string]$rid.rid
        $payloadDirectory = Join-Path $directory $ridName
        Assert-Artifact (Test-Path -LiteralPath $payloadDirectory -PathType Container) $ridName "missing extracted image payload"
        $receipt = Read-PayloadReceipt -DirectoryPath $directory -Rid $ridName -ArtifactName $ridName
        Assert-PayloadDiff `
            -Receipt $receipt `
            -ActualFiles @(Get-FolderPayloadFiles -DirectoryPath $payloadDirectory) `
            -ArtifactName "container:$ridName"
        $binaryPath = Join-Path $payloadDirectory ([string]$rid.binary)
        Assert-Artifact (Test-Path -LiteralPath $binaryPath -PathType Leaf) "container:$ridName" "missing executable '$($rid.binary)'"
    }

    $partial = $SelectedRids.Count -ne 2
    $status = if ($partial) { 'VALIDATED; PARTIAL RID set' } else { 'VALIDATED; COMPLETE' }
    return [pscustomobject]@{ Channel = 'container'; Directory = $directory; Status = $status; Artifacts = @() }
}

function Test-AppImageArtifacts([object[]] $SelectedRids) {
	$directory = Join-Path $script:PublishPath "appimage/v$Version"
	Assert-Artifact (Test-Path -LiteralPath $directory -PathType Container) $directory "missing AppImage publish channel directory"
	$expectedNames = @($SelectedRids | ForEach-Object { [string]$_.rid }) +
		@($SelectedRids | ForEach-Object { Get-PayloadReceiptName -Rid ([string]$_.rid) })
	$actualNames = @(Get-ChildItem -LiteralPath $directory | ForEach-Object Name)
	Assert-ExactNames -Expected $expectedNames -Actual $actualNames -ArtifactName $directory -Kind 'AppImage publish payload set'

	$receiptNames = New-Object 'System.Collections.Generic.List[string]'
	foreach ($rid in $SelectedRids) {
		$ridName = [string]$rid.rid
		$artifactName = "AppImage publish:$ridName"
		$payloadDirectory = Join-Path $directory $ridName
		Assert-Artifact (Test-Path -LiteralPath $payloadDirectory -PathType Container) $artifactName "missing publish directory '$ridName'"
		$binaryName = [string]$rid.releaseBinary
		$payloadNames = @(Get-ChildItem -LiteralPath $payloadDirectory -File | ForEach-Object Name)
		Assert-ExactNames -Expected @($binaryName) -Actual $payloadNames -ArtifactName $artifactName -Kind 'AppImage publish file set'
		$binaryPath = Join-Path $payloadDirectory $binaryName
		$receipt = Read-PayloadReceipt -DirectoryPath $directory -Rid $ridName -ArtifactName $artifactName
		Assert-SingleFilePayload -Bytes ([System.IO.File]::ReadAllBytes($binaryPath)) -Receipt $receipt -ArtifactName $artifactName
		$receiptNames.Add((Get-PayloadReceiptName -Rid $ridName))
	}

	$partial = $SelectedRids.Count -ne 2
	$status = if ($partial) { 'VALIDATED; PARTIAL RID set' } else { 'VALIDATED; COMPLETE' }
	return [pscustomobject]@{ Channel = 'appimage'; Directory = $directory; Status = $status; Artifacts = $receiptNames.ToArray() }
}

function Expand-Zip([string] $ArchivePath, [string] $DestinationPath) {
    [System.IO.Directory]::CreateDirectory($DestinationPath) | Out-Null
    [System.IO.Compression.ZipFile]::ExtractToDirectory($ArchivePath, $DestinationPath)
}

function Test-StoreChannelAddition([string] $Path) {
    $normalized = $Path.Replace('\', '/')
    # Package identity and application declarations are generated by Desktop Bridge.
    if ($normalized -ceq 'AppxManifest.xml') { return $true }
    # The block map is generated from the final package layout.
    if ($normalized -ceq 'AppxBlockMap.xml') { return $true }
    # OPC content types describe the MSIX container itself.
    if ($normalized -ceq '[Content_Types].xml') { return $true }
    # Package signing adds this file after payload collection.
    if ($normalized -ceq 'AppxSignature.p7x') { return $true }
    # PRI data is generated from Store resource declarations.
    if ($normalized -ceq 'resources.pri') { return $true }
    # Store logos and tiles belong to the packaging project, not the application publish.
    if ($normalized.StartsWith('Assets/', [System.StringComparison]::Ordinal)) { return $true }
    return $false
}

function Get-StorePayloadFiles(
    [string] $PackageRoot,
    [string] $ApplicationDirectory,
    [string] $ArtifactName
) {
    $applicationPrefix = $ApplicationDirectory.Replace('\', '/').TrimEnd('/') + '/'
    $payloadFiles = New-Object 'System.Collections.Generic.List[object]'
    foreach ($file in @(Get-ChildItem -LiteralPath $PackageRoot -Recurse -File)) {
        $relativePath = $file.FullName.Substring($PackageRoot.Length).TrimStart('\', '/').Replace('\', '/')
        if (-not $relativePath.StartsWith($applicationPrefix, [System.StringComparison]::Ordinal)) {
            if (-not (Test-StoreChannelAddition -Path $relativePath)) {
                Fail-Artifact $ArtifactName "unexpected file '$relativePath'"
            }
            continue
        }

        $payloadPath = $relativePath.Substring($applicationPrefix.Length)
        $resources = [DevProjex.ReleaseValidation.ReleasePayloadInspector]::TryReadManagedResources($file.FullName)
        $payloadFiles.Add([pscustomobject]@{
            Path = $payloadPath
            Size = $file.Length
            Sha256 = Get-FileSha256Hex -path $file.FullName
            IsManagedAssembly = $null -ne $resources
            ManagedResources = if ($null -eq $resources) { @() } else { @($resources) }
        })
    }
    return $payloadFiles.ToArray()
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
    [string] $TemporaryRoot,
    [object] $Receipt
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
    $payloadFiles = @(Get-StorePayloadFiles `
        -PackageRoot $packageRoot `
        -ApplicationDirectory $applicationDirectory `
        -ArtifactName $ArtifactName)
    Assert-PayloadDiff -Receipt $Receipt -ActualFiles $payloadFiles -ArtifactName $ArtifactName
}

function Assert-StoreBundle(
    [string] $BundlePath,
    [string] $ArtifactName,
    [string] $TemporaryRoot,
    [hashtable] $Receipts
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
        Assert-StoreApplicationPackage `
            -PackagePath $packagePath `
            -Architecture $platform `
            -ArtifactName $ArtifactName `
            -TemporaryRoot $TemporaryRoot `
            -Receipt $Receipts[$platform]
    }
}

function Assert-StoreUpload([string] $UploadPath, [string] $TemporaryRoot, [hashtable] $Receipts) {
    $artifactName = [System.IO.Path]::GetFileName($UploadPath)
    $uploadRoot = Join-Path $TemporaryRoot 'upload'
    Expand-Zip -ArchivePath $UploadPath -DestinationPath $uploadRoot
    $bundles = @(Get-ChildItem -LiteralPath $uploadRoot -Recurse -File -Filter '*.msixbundle')
    Assert-Artifact ($bundles.Count -eq 1) $artifactName "missing one x64|arm64 .msixbundle"
    Assert-StoreBundle `
        -BundlePath $bundles[0].FullName `
        -ArtifactName $artifactName `
        -TemporaryRoot $TemporaryRoot `
        -Receipts $Receipts
}

function Test-StoreArtifacts() {
    $directory = Join-Path $script:PublishPath "store/v$Version"
    Assert-Artifact (Test-Path -LiteralPath $directory -PathType Container) $directory "missing Store channel directory"
    $packageNames = @(
        "DevProjex.Store_$($script:StorePackageVersion)_x64_arm64_bundle_ReleaseStore.msixupload",
        "DevProjex.Store_$($script:StorePackageVersion)_x64_arm64_ReleaseStore.msixbundle",
        "DevProjex.Store_$($script:StorePackageVersion)_x64_ReleaseStore.msix"
    )
    $storeRidNames = @($script:Manifest.release.store.platforms | ForEach-Object { "win-$(([string]$_).ToLowerInvariant())" })
    $receiptNames = @($storeRidNames | ForEach-Object { Get-PayloadReceiptName -Rid $_ })
    $expectedNames = @($packageNames) + @($receiptNames)
    $packages = @(Get-ChildItem -LiteralPath $directory -File |
        Where-Object { $_.Extension -in @('.msixupload', '.msixbundle', '.msix') })
    $actualNames = @(Get-ChildItem -LiteralPath $directory -File |
        Where-Object { $_.Name -notin @('SHA256SUMS.txt', 'msix-build.log') } |
        ForEach-Object Name)
    Assert-ExactNames -Expected $expectedNames -Actual $actualNames -ArtifactName $directory -Kind 'Store artifact set'
    $uploads = @($packages | Where-Object { $_.Extension -ceq '.msixupload' })
    Assert-Artifact ($uploads.Count -eq 1) $directory "missing exactly one .msixupload; found '$($uploads.Count)'"
    [void](Get-ChecksumEntries -DirectoryPath $directory -ExpectedNames $expectedNames)

    $receipts = @{}
    foreach ($platform in @($script:Manifest.release.store.platforms)) {
        $platformName = ([string]$platform).ToLowerInvariant()
        $rid = "win-$platformName"
        $receipts[$platformName] = Read-PayloadReceipt -DirectoryPath $directory -Rid $rid -ArtifactName $directory
    }

    $temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("devprojex-release-artifacts-" + [guid]::NewGuid().ToString('N'))
    [System.IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
    try {
        Assert-StoreUpload -UploadPath $uploads[0].FullName -TemporaryRoot $temporaryRoot -Receipts $receipts
        Assert-StoreBundle `
            -BundlePath (Join-Path $directory $packageNames[1]) `
            -ArtifactName $packageNames[1] `
            -TemporaryRoot $temporaryRoot `
            -Receipts $receipts
        Assert-StoreApplicationPackage `
            -PackagePath (Join-Path $directory $packageNames[2]) `
            -Architecture 'x64' `
            -ArtifactName $packageNames[2] `
            -TemporaryRoot $temporaryRoot `
            -Receipt $receipts['x64']
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
if ($null -eq ('DevProjex.ReleaseValidation.ReleasePayloadInspector' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'ReleasePayloadInspection.cs')
}

$manifestPath = Join-Path $repoRoot 'Packaging/Headless/payload-manifest.json'
$script:Manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
Assert-Artifact ($script:Manifest.schemaVersion -eq 1) $manifestPath "unsupported schema version '$($script:Manifest.schemaVersion)'"
Assert-Artifact ($null -ne $script:Manifest.release) $manifestPath "missing release contract"
$script:StorePackageVersion = Get-StorePackageVersion -DisplayVersion $Version
$script:PublishPath = [System.IO.Path]::GetFullPath($(if ([string]::IsNullOrWhiteSpace($PublishRoot)) { Join-Path $repoRoot 'publish' } else { $PublishRoot }))
$allowedChannels = @('github', 'store', 'headless', 'container', 'appimage')
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
if ('headless' -in $selectedChannels) {
    $results.Add((Test-HeadlessArtifacts -SelectedRids $selectedRids))
}
if ('container' -in $selectedChannels) {
    $containerRids = @($selectedRids | Where-Object { $_.rid -in @('linux-x64', 'linux-arm64') })
    Assert-Artifact ($containerRids.Count -eq $selectedRids.Count) 'container' 'non-Linux RID selection'
    $results.Add((Test-ContainerArtifacts -SelectedRids $containerRids))
}
if ('appimage' -in $selectedChannels) {
	$appImageRids = @($selectedRids | Where-Object { $_.rid -in @('linux-x64', 'linux-arm64') })
	Assert-Artifact ($appImageRids.Count -eq $selectedRids.Count) 'appimage' 'non-Linux RID selection'
	$results.Add((Test-AppImageArtifacts -SelectedRids $appImageRids))
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
