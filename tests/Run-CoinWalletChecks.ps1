param([string]$ProjectRoot = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'

# Compile the complete production wallet, not a reimplementation of its algorithms.
# Only its namespace is isolated; game, Unity and optional-mod boundaries are doubles.
# Run in a fresh PowerShell process. No project build, DLL deployment or ZIP is made.
$wallet = [IO.File]::ReadAllText((Join-Path $ProjectRoot 'PortalCoinWallet.cs'))
$namespace = 'namespace PortalRules;'
if (($wallet.Split($namespace).Count - 1) -ne 1) {
    throw 'Expected the single production PortalRules file-scoped namespace.'
}
$wallet = $wallet.Replace($namespace, 'namespace PortalRulesCoinWalletChecks {')
$fixture = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'CoinWalletChecks.cs'))
$source = "#nullable enable annotations`n" + $wallet + "`n}`n" + $fixture
Add-Type -TypeDefinition $source
[PortalRulesCoinWalletChecks.Checks]::Run()
