<#
.SYNOPSIS
  Validates the Partner Center store-listing import folder before release.

.DESCRIPTION
  This script exists because Microsoft Partner Center can reject a CSV import
  with vague or even empty error messages. The rules below intentionally mirror
  the exact quirks that already broke real imports for this repository:

    - the CSV must stay close to a fresh Partner Center export template
    - encoding and line endings matter
    - asset paths must use the exact format that Partner Center accepts
    - keyword phrases must stay inside the undocumented total word budget

  The goal is to fail early in CI and in local release validation, instead of
  discovering a bad CSV only after manually uploading it in the Store portal.
#>
[CmdletBinding()]
param(
    [string]$RepositoryRoot = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "release-helpers.ps1")

function Assert-Condition([bool]$condition, [string]$message) {
    if (-not $condition) {
        throw $message
    }
}

function Get-StoreListingPaths([string]$repoRoot) {
    $storeListingRoot = Join-Path $repoRoot "Packaging\Windows\StoreListing"
    $importFolder = Join-Path $storeListingRoot "ImportFolder"
    $importCsvPath = Join-Path $importFolder "listingData.csv"

    $templateCandidates = @(
        @(
            Get-ChildItem -Path $storeListingRoot -Filter "listingData-*.csv" -File -ErrorAction SilentlyContinue
            Get-ChildItem -Path $storeListingRoot -Filter "Exported*.csv" -File -ErrorAction SilentlyContinue
        ) | Sort-Object LastWriteTimeUtc -Descending
    )

    Assert-Condition ($templateCandidates.Count -gt 0) "No fresh Partner Center export template was found under $storeListingRoot."

    return [pscustomobject]@{
        StoreListingRoot = $storeListingRoot
        ImportFolder = $importFolder
        ImportCsvPath = $importCsvPath
        TemplateCsvPath = $templateCandidates[0].FullName
    }
}

function Get-RequiredLocales() {
    # Keep only the baseline locales hardcoded.
    # Everything else is derived from the real CSV header so future language additions
    # do not require editing this script before the first validation run.
    return @("en-us", "ru-ru")
}

function Get-MetadataColumns() {
    return @("Field", "ID", "Type (Тип)", "default")
}

function Get-LocaleColumns([string[]]$headers) {
    $metadataColumns = @(Get-MetadataColumns)
    # Locale discovery must stay data-driven. If a new language is added in Partner Center
    # and exported into the CSV, validation should immediately start checking it.
    return @($headers | Where-Object { $metadataColumns -notcontains $_ })
}

function Get-CsvHeaderColumns([string]$path) {
    $headerLine = Get-Content -Path $path -First 1
    Assert-Condition (-not [string]::IsNullOrWhiteSpace($headerLine)) "CSV header row is empty: $path"

    return @(
        $headerLine -split "," | ForEach-Object {
            $_.Trim().Trim('"').Trim([char]0xFEFF)
        }
    )
}

function Get-CsvRows([string]$path) {
    # Import-Csv is sufficient here because the real file already uses proper CSV quoting,
    # including multiline Description fields. We intentionally validate the exact artifact
    # that Partner Center will ingest rather than a reconstructed in-memory model.
    return @(Import-Csv -Path $path)
}

function Count-Words([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value)) {
        return 0
    }

    return @($value -split "\s+" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }).Count
}

function Test-IsRemoteAsset([string]$value) {
    $uri = $null
    if (-not [Uri]::TryCreate($value, [System.UriKind]::Absolute, [ref]$uri)) {
        return $false
    }

    return $uri.Scheme -in @("http", "https")
}

function Resolve-ImportAssetPath([string]$importFolder, [string]$assetValue) {
    $relative = $assetValue
    if ($relative.StartsWith("ImportFolder/", [System.StringComparison]::OrdinalIgnoreCase)) {
        $relative = $relative.Substring("ImportFolder/".Length)
    }

    return Join-Path $importFolder ($relative.Replace("/", [IO.Path]::DirectorySeparatorChar))
}

function Test-PngSignature([string]$path) {
    $expected = [byte[]](0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)
    $bytes = [IO.File]::ReadAllBytes($path)

    if ($bytes.Length -lt $expected.Length) {
        return $false
    }

    for ($index = 0; $index -lt $expected.Length; $index++) {
        if ($bytes[$index] -ne $expected[$index]) {
            return $false
        }
    }

    return $true
}

function Add-ValidationError(
    [System.Collections.Generic.List[string]]$errors,
    [string]$code,
    [string]$message
) {
    $errors.Add("${code}: ${message}")
}

function Validate-DirectoryShape(
    [pscustomobject]$paths,
    [System.Collections.Generic.List[string]]$errors
) {
    Assert-Condition (Test-Path $paths.StoreListingRoot) "Store listing root was not found: $($paths.StoreListingRoot)"
    Assert-Condition (Test-Path $paths.ImportFolder) "Import folder was not found: $($paths.ImportFolder)"
    Assert-Condition (Test-Path $paths.ImportCsvPath) "Import CSV was not found: $($paths.ImportCsvPath)"
    Assert-Condition (Test-Path $paths.TemplateCsvPath) "Export template CSV was not found: $($paths.TemplateCsvPath)"

    $csvFiles = @(Get-ChildItem -Path $paths.ImportFolder -Filter "*.csv" -File -ErrorAction SilentlyContinue)
    if ($csvFiles.Count -ne 1) {
        Add-ValidationError $errors "SLP001" "Import folder must contain exactly one CSV file. Found: $($csvFiles.Count)"
    }
}

function Validate-Encoding(
    [string]$csvPath,
    [System.Collections.Generic.List[string]]$errors
) {
    $bytes = [IO.File]::ReadAllBytes($csvPath)

    # Partner Center imported this repository successfully only after we switched
    # to UTF-8 without BOM. Keep this rule explicit so editors cannot silently
    # reintroduce the broken encoding later.
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        Add-ValidationError $errors "SLP002" "Import CSV must be saved as UTF-8 without BOM."
    }

    $text = [IO.File]::ReadAllText($csvPath, [Text.UTF8Encoding]::new($false, $true))
    for ($index = 0; $index -lt $text.Length; $index++) {
        if ($text[$index] -eq "`n" -and ($index -eq 0 -or $text[$index - 1] -ne "`r")) {
            Add-ValidationError $errors "SLP003" "Import CSV must use CRLF line endings only."
            break
        }
    }
}

function Validate-HeaderAndSchema(
    [string]$importCsvPath,
    [string]$templateCsvPath,
    [System.Collections.Generic.List[string]]$errors
) {
    $importHeaders = @(Get-CsvHeaderColumns -path $importCsvPath)
    $templateHeaders = @(Get-CsvHeaderColumns -path $templateCsvPath)
    $separator = [string][char]31

    foreach ($header in $importHeaders) {
        if ([string]::IsNullOrWhiteSpace($header)) {
            Add-ValidationError $errors "SLP023" "Import CSV contains an empty header column."
        }
    }

    $duplicateHeaders = @(
        $importHeaders |
            Group-Object |
            Where-Object { $_.Count -gt 1 } |
            Select-Object -ExpandProperty Name
    )

    foreach ($duplicateHeader in $duplicateHeaders) {
        Add-ValidationError $errors "SLP024" "Import CSV contains a duplicate header column: '$duplicateHeader'."
    }

    # Microsoft explicitly allows new Store listings to be created by appending locale
    # columns to an exported CSV. The exported header must therefore remain an exact
    # prefix, while additional locales are allowed only after every template column.
    $preservesTemplatePrefix = $importHeaders.Count -ge $templateHeaders.Count -and
        (@($importHeaders[0..($templateHeaders.Count - 1)] | ForEach-Object { $_ }) -join $separator) -ceq
        (@($templateHeaders | ForEach-Object { $_ }) -join $separator)
    if (-not $preservesTemplatePrefix) {
        Add-ValidationError $errors "SLP004" "Import CSV does not preserve the latest Partner Center export header prefix."
    }

    $importRows = @(Get-CsvRows -path $importCsvPath)
    $templateRows = @(Get-CsvRows -path $templateCsvPath)
    $importFields = @($importRows | ForEach-Object { [string]$_.Field })
    $templateFields = @($templateRows | ForEach-Object { [string]$_.Field })

    if (-not ($importFields.Count -eq $templateFields.Count -and (@($importFields | ForEach-Object { $_ }) -join $separator) -ceq (@($templateFields | ForEach-Object { $_ }) -join $separator))) {
        Add-ValidationError $errors "SLP005" "Import CSV field order drifted away from the export template."
    }

    $duplicateFields = @(
        $importFields |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Group-Object |
            Where-Object { $_.Count -gt 1 } |
            Select-Object -ExpandProperty Name
    )

    foreach ($duplicateField in $duplicateFields) {
        Add-ValidationError $errors "SLP025" "Import CSV contains a duplicate field row: '$duplicateField'."
    }
}

function Validate-LocaleColumns(
    [string[]]$headers,
    [System.Collections.Generic.List[string]]$errors
) {
    $localeColumns = @(Get-LocaleColumns $headers)
    if ($localeColumns.Count -eq 0) {
        Add-ValidationError $errors "SLP006" "No locale columns were found in the import CSV."
        return
    }

    foreach ($locale in Get-RequiredLocales) {
        if ($headers -notcontains $locale) {
            Add-ValidationError $errors "SLP006" "Required locale column is missing: $locale"
        }
    }
}

function Validate-RequiredFields(
    [object[]]$rows,
    [string[]]$localeColumns,
    [System.Collections.Generic.List[string]]$errors
) {
    $requiredFields = @("Title", "Description", "ShortDescription", "DesktopScreenshot1")

    foreach ($field in $requiredFields) {
        $row = $rows | Where-Object { $_.Field -eq $field } | Select-Object -First 1
        if ($null -eq $row) {
            Add-ValidationError $errors "SLP007" "Required field row is missing: $field"
            continue
        }

        foreach ($locale in $localeColumns) {
            $value = [string]$row.$locale
            if ([string]::IsNullOrWhiteSpace($value)) {
                Add-ValidationError $errors "SLP008" "Field '$field' is empty for locale '$locale'."
            }
        }
    }
}

function Validate-Keywords(
    [object[]]$rows,
    [string[]]$localeColumns,
    [System.Collections.Generic.List[string]]$errors
) {
    foreach ($locale in $localeColumns) {
        $terms = New-Object System.Collections.Generic.List[string]

        foreach ($index in 1..7) {
            $row = $rows | Where-Object { $_.Field -eq "SearchTerm$index" } | Select-Object -First 1
            if ($null -eq $row) {
                continue
            }

            $value = [string]$row.$locale
            if (-not [string]::IsNullOrWhiteSpace($value)) {
                $terms.Add($value)
            }
        }

        if ($terms.Count -gt 7) {
            Add-ValidationError $errors "SLP009" "Locale '$locale' exceeds the 7 keyword limit."
        }

        foreach ($term in $terms) {
            if ($term.Length -gt 40) {
                Add-ValidationError $errors "SLP010" "Locale '$locale' has a keyword longer than 40 characters: '$term'."
            }
        }

        # Partner Center does not show this constraint clearly in the UI,
        # but imports can still fail late if the combined word count is too high.
        $wordCount = 0
        foreach ($term in $terms) {
            $wordCount += Count-Words $term
        }

        if ($wordCount -gt 21) {
            Add-ValidationError $errors "SLP011" "Locale '$locale' exceeds the 21-word keyword budget ($wordCount)."
        }
    }
}

function Test-RequiresTrimmedLocalizedValue([string]$field) {
    if ($field -in @("Title", "ShortDescription", "ReleaseNotes", "StoreLogo300x300")) {
        return $true
    }

    return $field.StartsWith("Feature", [System.StringComparison]::Ordinal) -or
           $field.StartsWith("SearchTerm", [System.StringComparison]::Ordinal) -or
           $field.StartsWith("DesktopScreenshot", [System.StringComparison]::Ordinal)
}

function Validate-TrimmedCriticalValues(
    [object[]]$rows,
    [string[]]$localeColumns,
    [System.Collections.Generic.List[string]]$errors
) {
    foreach ($row in $rows) {
        $field = [string]$row.Field
        if (-not (Test-RequiresTrimmedLocalizedValue -field $field)) {
            continue
        }

        foreach ($locale in $localeColumns) {
            $value = [string]$row.$locale
            if ([string]::IsNullOrEmpty($value)) {
                continue
            }

            # Hidden whitespace is one of the easiest ways to create confusing import failures,
            # especially for file paths and search terms. Treat it as invalid data, not as
            # something the validator should silently trim for the user.
            if ($value -cne $value.Trim()) {
                Add-ValidationError $errors "SLP026" "Field '$field' contains leading or trailing whitespace for locale '$locale'."
            }
        }
    }
}

function Validate-ScreenshotCoverage(
    [object[]]$rows,
    [string[]]$localeColumns,
    [System.Collections.Generic.List[string]]$errors
) {
    $screenshotRows = @(
        $rows |
            Where-Object { [string]$_.Field -match '^DesktopScreenshot\d+$' } |
            Sort-Object { [int]([string]$_.Field).Substring("DesktopScreenshot".Length) }
    )

    if ($screenshotRows.Count -eq 0) {
        Add-ValidationError $errors "SLP018" "No DesktopScreenshot rows were found in the import CSV."
        return
    }

    $referenceCoverage = $null
    $referenceLocale = $null

    foreach ($locale in $localeColumns) {
        $coverage = New-Object System.Collections.Generic.List[int]

        foreach ($row in $screenshotRows) {
            $index = [int]([string]$row.Field).Substring("DesktopScreenshot".Length)
            $value = [string]$row.$locale
            if (-not [string]::IsNullOrWhiteSpace($value)) {
                $coverage.Add($index)
            }
        }

        # The listing should stay flexible: 3 screenshots or 6 are both fine.
        # What must stay true is that every locale has at least one screenshot,
        # locales use the same slots, and there are no gaps like 1,2,4.
        if ($coverage.Count -lt 1) {
            Add-ValidationError $errors "SLP019" "Locale '$locale' has no screenshots."
            continue
        }

        $expectedCoverage = 1..($coverage[$coverage.Count - 1])
        if ((@($coverage) -join ",") -cne (@($expectedCoverage) -join ",")) {
            Add-ValidationError $errors "SLP021" "Locale '$locale' uses non-contiguous screenshot slots ($(@($coverage) -join ', '))."
        }

        if ($null -eq $referenceCoverage) {
            $referenceCoverage = @($coverage)
            $referenceLocale = $locale
            continue
        }

        if ((@($coverage) -join ",") -cne (@($referenceCoverage) -join ",")) {
            Add-ValidationError $errors "SLP022" "Locale '$locale' uses screenshot slots [$(@($coverage) -join ', ')], which does not match locale '$referenceLocale' [$(@($referenceCoverage) -join ', ')]."
        }
    }
}

function Validate-AssetValue(
    [string]$assetValue,
    [string]$field,
    [string]$locale,
    [string]$importFolder,
    [System.Collections.Generic.List[string]]$errors
) {
    if ([string]::IsNullOrWhiteSpace($assetValue)) {
        return
    }

    if (Test-IsRemoteAsset -value $assetValue) {
        # Partner Center may export URLs for assets that are already stored remotely.
        # Those values are valid and should not be forced into the ImportFolder/... pattern.
        return
    }

    if ($assetValue.Contains('\')) {
        Add-ValidationError $errors "SLP012" "Asset path must use forward slashes only: $field/$locale"
    }

    if ($assetValue.Contains("..")) {
        Add-ValidationError $errors "SLP013" "Asset path may not escape the import folder: $field/$locale"
    }

    if ($assetValue -match "TJ\.png$") {
        Add-ValidationError $errors "SLP014" "Legacy TJ screenshot suffix is not allowed anymore: $field/$locale"
    }

    if (-not $assetValue.StartsWith("ImportFolder/", [System.StringComparison]::Ordinal)) {
        Add-ValidationError $errors "SLP015" "Local asset path must start with 'ImportFolder/': $field/$locale"
        return
    }

    $fullPath = Resolve-ImportAssetPath -importFolder $importFolder -assetValue $assetValue
    if (-not (Test-Path $fullPath)) {
        Add-ValidationError $errors "SLP016" "Asset file does not exist for $field/${locale}: $assetValue"
        return
    }

    if ([IO.Path]::GetExtension($fullPath).Equals(".png", [System.StringComparison]::OrdinalIgnoreCase) -and -not (Test-PngSignature -path $fullPath)) {
        Add-ValidationError $errors "SLP017" "PNG asset is invalid or unreadable for $field/${locale}: $assetValue"
    }
}

function Validate-Assets(
    [object[]]$rows,
    [string[]]$localeColumns,
    [string]$importFolder,
    [System.Collections.Generic.List[string]]$errors
) {
    Validate-ScreenshotCoverage -rows $rows -localeColumns $localeColumns -errors $errors

    $screenshotRows = @($rows | Where-Object { [string]$_.Field -match '^DesktopScreenshot\d+$' })
    foreach ($row in $screenshotRows) {
        foreach ($locale in $localeColumns) {
            $assetValue = [string]$row.$locale
            if ([string]::IsNullOrWhiteSpace($assetValue)) {
                continue
            }

            # Coverage validation decides whether an empty screenshot cell is acceptable.
            # Once we get here, every remaining asset value should point to something usable.
            Validate-AssetValue -assetValue $assetValue -field $row.Field -locale $locale -importFolder $importFolder -errors $errors
        }
    }

    $logoRow = $rows | Where-Object { $_.Field -eq "StoreLogo300x300" } | Select-Object -First 1
    if ($null -ne $logoRow) {
        foreach ($locale in $localeColumns) {
            $assetValue = [string]$logoRow.$locale
            if ([string]::IsNullOrWhiteSpace($assetValue)) {
                continue
            }

            Validate-AssetValue -assetValue $assetValue -field $logoRow.Field -locale $locale -importFolder $importFolder -errors $errors
        }
    }

    $screenshotFiles = @(Get-ChildItem -Path (Join-Path $importFolder "Screenshots") -Filter "*.png" -File -Recurse -ErrorAction SilentlyContinue)
    if ($screenshotFiles.Count -lt $localeColumns.Count) {
        Add-ValidationError $errors "SLP020" "ImportFolder/Screenshots contains fewer PNG files than locale columns."
    }
}

$repoRoot = if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    Get-DevProjexRepoRoot -startPath $PSScriptRoot
}
else {
    (Resolve-Path -Path $RepositoryRoot).Path
}

$paths = Get-StoreListingPaths -repoRoot $repoRoot
$errors = [System.Collections.Generic.List[string]]::new()

Validate-DirectoryShape -paths $paths -errors $errors
Validate-Encoding -csvPath $paths.ImportCsvPath -errors $errors
Validate-HeaderAndSchema -importCsvPath $paths.ImportCsvPath -templateCsvPath $paths.TemplateCsvPath -errors $errors

$headers = @(Get-CsvHeaderColumns -path $paths.ImportCsvPath)
$localeColumns = @(Get-LocaleColumns $headers)
$rows = @(Get-CsvRows -path $paths.ImportCsvPath)

Validate-LocaleColumns -headers $headers -errors $errors
Validate-RequiredFields -rows $rows -localeColumns $localeColumns -errors $errors
Validate-TrimmedCriticalValues -rows $rows -localeColumns $localeColumns -errors $errors
Validate-Keywords -rows $rows -localeColumns $localeColumns -errors $errors
Validate-Assets -rows $rows -localeColumns $localeColumns -importFolder $paths.ImportFolder -errors $errors

if ($errors.Count -gt 0) {
    throw ("Store listing validation failed:" + [Environment]::NewLine + ($errors -join [Environment]::NewLine))
}

Write-Host "Store listing validation passed." -ForegroundColor Green
Write-Host "  Import folder : $($paths.ImportFolder)"
Write-Host "  Import CSV    : $($paths.ImportCsvPath)"
Write-Host "  Template CSV  : $($paths.TemplateCsvPath)"
Write-Host "  Locales       : $($localeColumns.Count)"
