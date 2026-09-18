param([string]$ProjectRoot = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
# Compile the actual method bodies, with controlled game/optional-mod boundaries.
# This does not execute Unity or simulate the complete game inventory implementation.
function Read-Method([string]$file, [string]$name) {
    $source = [IO.File]::ReadAllText((Join-Path $ProjectRoot $file))
    $pattern = '(?ms)^    (?:private|internal|public) (?:static )?[^\r\n]*\b' +
        [regex]::Escape($name) + '\(.*?^    \}'
    $matches = [regex]::Matches($source, $pattern)
    if ($matches.Count -ne 1) { throw "Expected one method: $file / $name" }
    return $matches[0].Value
}
$bodies = @(
    (Read-Method 'PublicPortalInteraction.cs' 'SetConnectionStatusAfterPortalTag'),
    (Read-Method 'PortalCoinWallet.cs' 'TryRefundPhysicalCoins'),
    (Read-Method 'PortalCoinWallet.cs' 'TryRestoreCurrencyPocketPayment'),
    (Read-Method 'PublicPortalTravelCost.cs' 'TryGetCargoWeightUnits'),
    (Read-Method 'PublicPortalTravelCost.cs' 'CalculateFare')
) -join "`n"
$fixture = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'BehaviorChecks.cs'))
Add-Type -TypeDefinition ($fixture.Replace('/* PRODUCTION_METHODS */', $bodies))
[PortalRulesBehaviorChecks]::Run()

function Read-Property([string]$file, [string]$name) {
    $source = [IO.File]::ReadAllText((Join-Path $ProjectRoot $file))
    $pattern = '(?ms)^    (?:private|internal|public) (?:static )?[^\r\n]*\b' +
        [regex]::Escape($name) + '\s*=>.*?;'
    $matches = [regex]::Matches($source, $pattern)
    if ($matches.Count -ne 1) { throw "Expected one property: $file / $name" }
    return $matches[0].Value
}
$crossplay = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'CrossplayChecks.cs'))
$dataMethods = @('TryGetPeerAccountId', 'TryGetLocalAccountId', 'TryNormalizeAccountId',
    'TryNormalizePlayFabEntityId', 'TryNormalizeSteamId64') | ForEach-Object {
    Read-Method 'PublicPortalData.cs' $_
}
$storeMethods = @('TryRememberIdentity', 'TryResolveAccountId', 'SerializePlayerIdentities',
    'TryReadPlayerIdentities', 'ReplacePlayerIdentities') | ForEach-Object {
    Read-Method 'PortalAccountStore.cs' $_
}
$crossplay = $crossplay.Replace('/* DATA_METHODS */', ($dataMethods -join "`n")).
    Replace('/* STORE_METHODS */', ($storeMethods -join "`n")).
    Replace('/* DATA_PROPERTY */', (Read-Property 'PublicPortalData.cs' 'IsCrossplay')).
    Replace('/* CONFIG_PROPERTY */', (Read-Property 'PublicPortalConfig.cs' 'IsAccountPortalLimitEnabled')).
    Replace('/* STORE_PROPERTY */', (Read-Property 'PortalAccountStore.cs' 'IdentityFileName'))
$yamlDll = Join-Path $ProjectRoot 'bin\Debug\YamlDotNet.dll'
Add-Type -Path $yamlDll
$frameworkRefs = [string[]](Get-ChildItem (Join-Path $PSHOME 'ref') -Filter '*.dll' | ForEach-Object FullName)
Add-Type -TypeDefinition $crossplay -ReferencedAssemblies ($frameworkRefs + $yamlDll)
[PortalRulesCrossplayChecks.Checks]::Run()

$cursorFixture = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'MapCursorChecks.cs'))
$cursorFixture = $cursorFixture.Replace('/* CURSOR_METHOD */',
    (Read-Method 'PublicPortalMapController.cs' 'EnsureSelectionCursor')).Replace('/* OPEN_METHOD */',
    (Read-Method 'PublicPortalMapController.cs' 'OpenMapAt'))
Add-Type -TypeDefinition $cursorFixture
[PortalRulesMapCursorChecks.Checks]::Run()
