param([string]$ProjectRoot = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'

# Compile the entire production integration, including reflection binding and result handling.
# Only game/BepInEx/YNW boundaries are doubles; this does not execute Unity or load a save.
# Run in a fresh PowerShell 7 process. No build, deployment or ZIP is made.
$integration = [IO.File]::ReadAllText((Join-Path $ProjectRoot 'RequiredGlobalKeyAccess.cs'))
$namespace = 'namespace PortalRules;'
if (($integration.Split($namespace).Count - 1) -ne 1) {
    throw 'Expected the single production PortalRules file-scoped namespace.'
}
$integration = $integration.Replace($namespace, 'namespace PortalRulesRequiredGlobalKeyChecks {')
$fixture = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'RequiredGlobalKeyChecks.cs'))
Add-Type -TypeDefinition ("#nullable enable annotations`n" + $integration + "`n}`n" + $fixture)
[PortalRulesRequiredGlobalKeyChecks.Checks]::Run()
