function Get-MaximumContainerVersion([string[]] $Tags) {
    $maximum = $null
    foreach ($tag in $Tags) {
        if ($tag -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)(?:\.(0|[1-9]\d*))?$') {
            continue
        }
        $candidate = [System.Management.Automation.SemanticVersion]::Parse($tag)
        if ($null -eq $maximum -or $candidate -gt $maximum) { $maximum = $candidate }
    }
    return $maximum
}

function Test-ContainerLatestPromotion([string] $Version, [string[]] $PublishedTags) {
    if ($Version.Contains('-', [System.StringComparison]::Ordinal)) { return $false }
    $current = [System.Management.Automation.SemanticVersion]::Parse($Version)
    $maximum = Get-MaximumContainerVersion $PublishedTags
    return $null -eq $maximum -or $current -ge $maximum
}
