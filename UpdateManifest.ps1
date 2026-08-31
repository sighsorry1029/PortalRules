param (
    [Parameter(Mandatory = $true)]
    [string] $manifestFile,

    [Parameter(Mandatory = $true)]
    [string] $versionString
)

$ErrorActionPreference = 'Stop'

try {
    if ($versionString -notmatch '^\d+\.\d+\.\d+$') {
        throw "Invalid package version '$versionString'."
    }

    $resolvedManifest = (Resolve-Path -LiteralPath $manifestFile).Path
    $manifest = [System.IO.File]::ReadAllText($resolvedManifest)
    $versionPattern = '"version_number"\s*:\s*"[^"]*"'
    $versionMatches = [System.Text.RegularExpressions.Regex]::Matches($manifest, $versionPattern)
    if ($versionMatches.Count -ne 1) {
        throw "Expected exactly one version_number property, found $($versionMatches.Count)."
    }

    $versionRegex = [System.Text.RegularExpressions.Regex]::new($versionPattern)
    $updatedManifest = $versionRegex.Replace(
        $manifest,
        '"version_number": "' + $versionString + '"',
        1)
    $parsedManifest = $updatedManifest | ConvertFrom-Json
    if ([string] $parsedManifest.version_number -ne $versionString) {
        throw 'The updated manifest did not contain the requested version.'
    }

    $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($resolvedManifest, $updatedManifest, $utf8WithoutBom)
}
catch {
    throw "Failed to update manifest version: $($_.Exception.Message)"
}
