$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $repoRoot 'Scripts/release-archive-helpers.ps1')

$gatePath = Join-Path $repoRoot 'Scripts/Test-HeadlessPackages.ps1'
$tokens = $null
$parseErrors = $null
$gateAst = [System.Management.Automation.Language.Parser]::ParseFile(
    $gatePath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    throw "Could not parse headless package gate: $($parseErrors[0].Message)"
}
foreach ($name in @('Assert-Artifact', 'Assert-GrammarPayload')) {
    $definition = $gateAst.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -ceq $name
    }, $true)
    if ($null -eq $definition) {
        throw "Headless package gate is missing '$name'."
    }
    . ([scriptblock]::Create($definition.Extent.Text))
}

$manifest = [pscustomobject]@{ grammars = @('alpha', 'beta') }
$rid = [pscustomobject]@{ grammarPrefix = 'tree-sitter-'; grammarExtension = '.dll' }
$bytes = [byte[]]::new(1024 * 1024 + 128)
$first = [System.Text.Encoding]::ASCII.GetBytes('tree-sitter-alpha.dll')
$second = [System.Text.Encoding]::ASCII.GetBytes('tree-sitter-beta.dll')
[System.Array]::Copy($first, 0, $bytes, 1024 * 1024 - 5, $first.Length)
[System.Array]::Copy($second, 0, $bytes, $bytes.Length - $second.Length, $second.Length)
$stream = [System.IO.MemoryStream]::new($bytes, $false)
try {
    Assert-GrammarPayload $stream $rid 'synthetic'
}
finally {
    $stream.Dispose()
}

$missingStream = [System.IO.MemoryStream]::new($first, $false)
try {
    $missingRejected = $false
    try {
        Assert-GrammarPayload $missingStream $rid 'synthetic'
    }
    catch {
        if ($_.Exception.Message -notlike "*grammar 'tree-sitter-beta.dll'*") {
            throw
        }
        $missingRejected = $true
    }
    if (-not $missingRejected) {
        throw 'The gate accepted a missing grammar.'
    }
}
finally {
    $missingStream.Dispose()
}

$temporaryParent = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$temporaryRoot = Join-Path $temporaryParent ('devprojex-gate-streaming-' + [guid]::NewGuid().ToString('N'))
[System.IO.Directory]::CreateDirectory($temporaryRoot) | Out-Null
try {
    $sourcePath = Join-Path $temporaryRoot 'payload.bin'
    $archivePath = Join-Path $temporaryRoot 'payload.tar.gz'
    $payload = [byte[]]::new(3 * 1024 * 1024 + 17)
    [System.Security.Cryptography.RandomNumberGenerator]::Fill($payload)
    [System.IO.File]::WriteAllBytes($sourcePath, $payload)
    New-UstarGzipArchive -archivePath $archivePath -entries @(
        [pscustomobject]@{
            Name = 'payload.bin'
            Mode = 493
            IsDirectory = $false
            SourcePath = $sourcePath
            Bytes = $null
        })

    $hashed = @(Read-UstarGzipArchive -archivePath $archivePath -hashEntryNames @('payload.bin'))
    if ($hashed.Count -ne 1 -or
        $hashed[0].Sha256 -cne (Get-FileSha256Hex -path $sourcePath) -or
        $null -ne $hashed[0].Bytes) {
        throw 'Streaming USTAR hash did not match the source payload.'
    }
    $captured = @(Read-UstarGzipArchive -archivePath $archivePath -captureEntryNames @('payload.bin'))
    if ($captured.Count -ne 1 -or $captured[0].Bytes.Length -ne $payload.Length) {
        throw 'Existing USTAR capture behavior changed.'
    }
}
finally {
    $resolvedRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
    $expectedPrefix = $temporaryParent.TrimEnd([System.IO.Path]::DirectorySeparatorChar) +
        [System.IO.Path]::DirectorySeparatorChar
    if (-not $resolvedRoot.StartsWith($expectedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected fixture path '$resolvedRoot'."
    }
    Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
}

Write-Host 'Headless package streaming gate passed.'
