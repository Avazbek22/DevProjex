[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Rid,

    [Parameter(Mandatory = $true)]
    [string] $ItemsPath,

    [Parameter(Mandatory = $true)]
    [string] $OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Add-Type -Path (Join-Path $PSScriptRoot 'ReleasePayloadInspection.cs')

$files = New-Object 'System.Collections.Generic.List[object]'
$seenPaths = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
foreach ($line in @(Get-Content -LiteralPath $ItemsPath)) {
    if ([string]::IsNullOrWhiteSpace($line)) {
        continue
    }

    $parts = $line.Split("`t", 2)
    if ($parts.Count -ne 2) {
        throw "Invalid publish payload item '$line'."
    }

    $sourcePath = [System.IO.Path]::GetFullPath($parts[0])
    $relativePath = $parts[1].Replace('\', '/').TrimStart('/')
    if ([string]::IsNullOrWhiteSpace($relativePath) -or -not $seenPaths.Add($relativePath)) {
        throw "Duplicate or empty publish payload path '$relativePath'."
    }
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Publish payload source '$sourcePath' for '$relativePath' does not exist."
    }

    $item = Get-Item -LiteralPath $sourcePath
    $resources = [DevProjex.ReleaseValidation.ReleasePayloadInspector]::TryReadManagedResources($sourcePath)
    $entry = [ordered]@{
        path = $relativePath
        size = $item.Length
        sha256 = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    if ($null -ne $resources) {
        $entry.managedResources = @($resources)
    }
    $files.Add($entry)
}

$receiptDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($receiptDirectory)) {
    [System.IO.Directory]::CreateDirectory($receiptDirectory) | Out-Null
}

$receipt = [ordered]@{
    schemaVersion = 1
    rid = $Rid
    files = @($files | Sort-Object { [string]$_.path })
}
$json = $receipt | ConvertTo-Json -Depth 8
[System.IO.File]::WriteAllText(
    [System.IO.Path]::GetFullPath($OutputPath),
    $json + [Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false))

Write-Host "Release payload receipt: $OutputPath ($($files.Count) files)"
