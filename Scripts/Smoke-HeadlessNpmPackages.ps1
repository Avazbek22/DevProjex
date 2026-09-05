[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ArtifactsRoot,

    [Parameter(Mandatory = $true)]
    [string] $Version,

    [Parameter(Mandatory = $true)]
    [string] $ExpectedDisplayVersion
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactsPath = [System.IO.Path]::GetFullPath($ArtifactsRoot)
$npmPath = Join-Path $artifactsPath "npm"
$temporaryRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    "devprojex-headless-npm-smoke-" + [guid]::NewGuid().ToString("N"))
$registryRoot = Join-Path $temporaryRoot "registry"
$configPath = Join-Path $registryRoot "config.yaml"
$registry = "http://127.0.0.1:4873"
$server = $null
$previousUserConfig = [Environment]::GetEnvironmentVariable("NPM_CONFIG_USERCONFIG")
[System.IO.Directory]::CreateDirectory($registryRoot) | Out-Null

function Invoke-Npm([string[]] $Arguments, [string] $Failure) {
    & npm @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Failure (npm exit code $LASTEXITCODE)."
    }
}

try {
    $configuration = @"
storage: ./storage
max_body_size: 400mb
auth:
  htpasswd:
    file: ./htpasswd
    max_users: 1
uplinks: {}
packages:
  '@*/*':
    access: `$all
    publish: `$all
  '**':
    access: `$all
    publish: `$all
log: { type: stdout, format: pretty, level: warn }
"@
    [System.IO.File]::WriteAllText(
        $configPath,
        $configuration,
        [System.Text.UTF8Encoding]::new($false))

    $verdaccio = Join-Path ((npm root --global).Trim()) "verdaccio/bin/verdaccio"
    if (-not (Test-Path -LiteralPath $verdaccio -PathType Leaf)) {
        throw "Verdaccio is not installed globally; run 'npm install --global verdaccio@6'."
    }
    $node = (Get-Command node).Source
    $serverArguments = @($verdaccio, "--config", $configPath, "--listen", "127.0.0.1:4873")
    $start = @{
        FilePath = $node
        ArgumentList = $serverArguments
        PassThru = $true
        RedirectStandardOutput = (Join-Path $temporaryRoot "verdaccio.stdout.log")
        RedirectStandardError = (Join-Path $temporaryRoot "verdaccio.stderr.log")
    }
    if ($IsWindows) {
        $start.WindowStyle = "Hidden"
    }
    $server = Start-Process @start

    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try {
            Invoke-WebRequest -Uri $registry -UseBasicParsing | Out-Null
            break
        }
        catch {
            if ($attempt -eq 29) {
                throw "Verdaccio did not become ready at $registry."
            }
            Start-Sleep -Seconds 1
        }
    }

    $loginBody = @{
        name = "devprojex-smoke"
        password = "local-registry-only"
        email = "smoke@devprojex.invalid"
        type = "user"
        roles = @()
        date = [DateTimeOffset]::UtcNow.ToString("O")
    } | ConvertTo-Json
    $login = Invoke-RestMethod `
        -Method Put `
        -Uri "$registry/-/user/org.couchdb.user:devprojex-smoke" `
        -ContentType "application/json" `
        -Body $loginBody
    if ([string]::IsNullOrWhiteSpace([string]$login.token)) {
        throw "Verdaccio did not return a temporary publish token."
    }
    $npmConfigPath = Join-Path $temporaryRoot ".npmrc"
    $npmConfig = "registry=$registry/`n//127.0.0.1:4873/:_authToken=$($login.token)`n"
    [System.IO.File]::WriteAllText(
        $npmConfigPath,
        $npmConfig,
        [System.Text.UTF8Encoding]::new($false))
    $env:NPM_CONFIG_USERCONFIG = $npmConfigPath

    Get-ChildItem -LiteralPath $npmPath -Filter "devprojex-cli-*.tgz" -File |
        Sort-Object Name |
        ForEach-Object {
            Invoke-Npm @("publish", $_.FullName, "--registry", $registry) `
                "Failed to publish $($_.Name) to the smoke registry"
        }
    $launcherPackage = Join-Path $npmPath "devprojex-$Version.tgz"
    Invoke-Npm @("publish", $launcherPackage, "--registry", $registry) `
        "Failed to publish the launcher to the smoke registry"

    $sample = Join-Path $repoRoot "Apps/TerminalHost"
    $npxRoot = Join-Path $temporaryRoot "npx-empty"
    [System.IO.Directory]::CreateDirectory($npxRoot) | Out-Null
    Push-Location $npxRoot
    try {
        $actualVersion = (& npx --yes --registry $registry "devprojex@$Version" --version).Trim()
        if ($LASTEXITCODE -ne 0 -or $actualVersion -cne $ExpectedDisplayVersion) {
            throw "npx version mismatch: expected '$ExpectedDisplayVersion', got '$actualVersion'."
        }
        $tree = & npx --yes --registry $registry "devprojex@$Version" tree $sample --git-mode none --exclude none
        $treeText = $tree -join [Environment]::NewLine
        if ($LASTEXITCODE -ne 0 -or
            -not $treeText.Contains("Program.cs", [System.StringComparison]::Ordinal)) {
            throw "npx tree smoke failed."
        }
    }
    finally {
        Pop-Location
    }

    $installRoot = Join-Path $temporaryRoot "ignore-scripts"
    [System.IO.Directory]::CreateDirectory($installRoot) | Out-Null
    Push-Location $installRoot
    try {
        Invoke-Npm @("install", "--ignore-scripts", "--registry", $registry, "devprojex@$Version") `
            "npm install --ignore-scripts failed"
        $actualVersion = (& node "node_modules/devprojex/bin/devprojex.js" --version).Trim()
        if ($LASTEXITCODE -ne 0 -or $actualVersion -cne $ExpectedDisplayVersion) {
            throw "The --ignore-scripts installation did not run the expected binary."
        }
    }
    finally {
        Pop-Location
    }

    $omitRoot = Join-Path $temporaryRoot "omit-optional"
    [System.IO.Directory]::CreateDirectory($omitRoot) | Out-Null
    Push-Location $omitRoot
    try {
        Invoke-Npm @("install", "--ignore-scripts", "--omit=optional", "--registry", $registry, "devprojex@$Version") `
            "npm install --omit=optional setup failed"
        $errorLines = & node "node_modules/devprojex/bin/devprojex.js" --version 2>&1
        $launcherExitCode = $LASTEXITCODE
        $errorText = $errorLines -join [Environment]::NewLine
        if ($launcherExitCode -ne 1 -or
            -not $errorText.Contains("--omit=optional", [System.StringComparison]::Ordinal) -or
            -not $errorText.Contains("musl", [System.StringComparison]::Ordinal)) {
            throw "The documented --omit=optional error contract was not observed: $errorText"
        }
    }
    finally {
        Pop-Location
    }

    Write-Host "Headless npm smoke passed through Verdaccio for $Version."
}
catch {
    foreach ($logName in @("verdaccio.stdout.log", "verdaccio.stderr.log")) {
        $logPath = Join-Path $temporaryRoot $logName
        if (Test-Path -LiteralPath $logPath) {
            Write-Host "--- $logName ---"
            Get-Content -LiteralPath $logPath
        }
    }
    throw
}
finally {
    if ($null -ne $server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
        $null = $server.WaitForExit(5000)
    }
    $resolvedTemporaryRoot = [System.IO.Path]::GetFullPath($temporaryRoot)
    $resolvedSystemTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($resolvedTemporaryRoot.StartsWith($resolvedSystemTemp, [System.StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($null -eq $previousUserConfig) {
        Remove-Item Env:NPM_CONFIG_USERCONFIG -ErrorAction SilentlyContinue
    }
    else {
        $env:NPM_CONFIG_USERCONFIG = $previousUserConfig
    }
}
