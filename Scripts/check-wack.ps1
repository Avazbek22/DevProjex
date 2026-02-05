param(
  [string]$PackagePath = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($PackagePath)) {
  $searchRoots = @(
    "publish\\store",
    "Packaging\\Windows\\DevProjex.Store\\publish\\store"
  )
  $artifact = $searchRoots |
    Where-Object { Test-Path $_ } |
    ForEach-Object { Get-ChildItem -Path $_ -Recurse -File -Include *.msixbundle,*.msix } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
  if ($null -eq $artifact) {
    Write-Host "MSIX artifact not found in publish\store\\ or Packaging\\Windows\\DevProjex.Store\\publish\\store\\"
    exit 1
  }
  $PackagePath = $artifact.FullName
}

$appCertPaths = @(
  "${env:ProgramFiles(x86)}\\Windows Kits\\10\\App Certification Kit\\appcert.exe",
  "$env:ProgramFiles\Windows Kits\10\App Certification Kit\appcert.exe"
)

$appCert = $appCertPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($null -eq $appCert) {
  Write-Host "Windows App Certification Kit not found. Install Windows 10/11 SDK and retry."
  exit 1
}

Write-Host "Running WACK..."
Write-Host "  AppCert: $appCert"
Write-Host "  Package: $PackagePath"

$reportDir = Join-Path (Split-Path $PackagePath -Parent) "wack"
New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
$reportPath = Join-Path $reportDir "wack-report.xml"

& $appCert test -appxpackagepath "$PackagePath" -reportoutputpath "$reportPath"

$exitCode = $LASTEXITCODE

$appCertLogRoot = Join-Path $env:LOCALAPPDATA "Microsoft\\AppCertKit"
if (Test-Path $appCertLogRoot) {
  Get-ChildItem -Path $appCertLogRoot -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 10 |
    ForEach-Object {
      try {
        Copy-Item -Force $_.FullName -Destination $reportDir
      } catch {
        # Ignore locked log files; WACK can keep them open briefly.
      }
    }
}

if (Test-Path $reportPath) {
  Write-Host "WACK report: $reportPath"
} else {
  $fallbackReport = Join-Path $reportDir "wack-result.txt"
  $summary = @(
    "WACK exit code: $exitCode",
    "Package: $PackagePath",
    "Timestamp: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
  )
  Set-Content -Path $fallbackReport -Value $summary -Encoding UTF8
  Write-Host "WACK summary: $fallbackReport"
}

exit $exitCode
