param([string]$ProjectRoot = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
# Compile the actual method bodies, with controlled game/optional-mod boundaries.
# This does not execute Unity or simulate the complete game inventory implementation.
function Read-Method([string]$file, [string]$name) {
    $source = [IO.File]::ReadAllText((Join-Path $ProjectRoot $file))
    $pattern = '(?ms)^    (?:private|internal|public) static [^\r\n]*\b' +
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
