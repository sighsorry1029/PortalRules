param(
    [string]$ProjectRoot = (Split-Path $PSScriptRoot -Parent),
    [string]$StuWardProjectRoot
)
$ErrorActionPreference = 'Stop'
# Compile the complete production service. Unity, transport, Clan and persistence
# are controlled doubles; this does not execute Harmony or a Valheim server.
$source = [IO.File]::ReadAllText((Join-Path $ProjectRoot 'PublicPortalProtection.cs'))
$fixture = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'PortalProtectionChecks.cs'))
$source = $source.Replace('namespace PortalRules;', 'namespace PortalRules {') + "`n}`n"
if ($StuWardProjectRoot) {
    $api = [IO.File]::ReadAllText((Join-Path $StuWardProjectRoot 'WardAccessApi.cs'))
    $api = [regex]::Replace($api, '(?m)^using [^;]+;\r?\n', '')
    $source = "#define STUWARD_API`n" + $source + $api.Replace('namespace STUWard;', 'namespace STUWard {') + "`n}`n"
}
$fixture = [regex]::Replace($fixture, '(?m)^using [^;]+;\r?\n', '')
Add-Type -TypeDefinition ("#nullable enable annotations`n#pragma warning disable CS0649`n" + $source + $fixture.Replace('#nullable enable', '#nullable enable annotations'))
[PortalRules.ProtectionChecks]::Run()
