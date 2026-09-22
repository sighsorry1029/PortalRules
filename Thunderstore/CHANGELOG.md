# Changelog

## 1.0.10

- Respect Valheim's final modded item-teleportability result before PortalRules blocks travel or calculates a fare. Items explicitly allowed by another mod, including absolute-restriction items, can travel without a PortalRules Coin fare while the existing vanilla and fare rules remain unchanged otherwise.
- Show unlimited own-portal map labels as `My portal n` instead of `My portal n/∞`; finite account limits retain the `n/limit` format.

## 1.0.9

- Added the server-synchronized `Default Portal Mode` setting for newly placed ordinary player portals, with `Personal`, `Clan`, and `Public` choices. Clan safely falls back to Personal when membership or quota cannot be confirmed, while Public follows the existing expiration rule.
- Changed ordinary portal mode cycling to `Personal → Clan → Public → Invite → Tagged → Personal`; Admin remains available only to Admin portal prefabs, while an existing ordinary Admin-mode portal can still cycle out to Public.
- Clarified insufficient-fare messages so players know Coins are required because they are carrying items that cannot normally be teleported.
- Centralized network-session travel cleanup and simplified positive-fare UI rendering without changing authorization, transaction, refund, or display behavior.

## 1.0.8

- Added Crossplay portal ownership based on the authenticated PlayFab entity, covering placement, Builder editing, Personal access, Invite limits and cooldowns, and travel authorization. Steam and PlayFab ownership remain separate.
- Automatically disables the overall account portal limit during Crossplay without changing its saved setting, while keeping Invite and Clan rules independent.
- Added a second-line Crossplay diagnostic when portal placement identity verification fails, and a separate PlayFab character identity registry for safe persistence across reconnects.
- Kept the mouse cursor visible and unlocked when the portal destination map opens after remote server approval.

## 1.0.7

- Changed the account portal limit default to Off for new configurations while preserving existing saved values.
- Added a specific two-line placement message when account portal limits are enabled with Crossplay/PlayFab, explaining that Steamworks is required and that players can disable Crossplay or the account portal limit.

## 1.0.6

- Debounced local configuration saves and external-file reloads, preventing repeated writes caused by clustered setting and file-watcher events while keeping live configuration updates.
- Cleaned up configuration callbacks and file watching during shutdown or reinitialization so handlers do not accumulate in the same game process.
- Reorganized configuration ownership and portal lifecycle Harmony wiring to reduce maintenance and verification cost without changing settings, network messages, access rules, or travel behavior.

## 1.0.5

- Simplified fare options to (distance)*(weight of non teleportable items)*k

## 1.0.4

- Updated PortalRules for Valheim 1.0.7, including portal registration, portal-list access, map pointer/right-click handling, hover status formatting, and changed game method signatures.
- Removed the hard Jotunn dependency. Admin portal cloning, network-scene and Hammer registration, cleanup, and UI font selection are now owned by PortalRules while preserving the existing admin portal prefab names and hashes.
- Preserved portal authority changes in Valheim's separate portal save chunk, including admin creator metadata, and made server-world readiness idempotent across the new load path.
- Applied Valheim's absolute item restrictions to all-items portals and prevented refund overflow from creating world drops before CurrencyPocket fallback, reducing item loss or duplication risk.
- Updated the bundled ServerSync to the reviewed `valheim-1.0.7-r1` build, preserving configuration/version contracts while fixing 1.0.7 API access and connection-initialization message ordering.
- Updated the required BepInExPack Valheim dependency to 5.4.2350 and added client/dedicated static compatibility and focused behavior checks.

## 1.0.3

- Show CONNECTED only for Tagged portals with a reciprocal connection; hide connection status in other access modes.
- Check restricted items before opening the portal map and again after server approval, showing Valheim's item restriction message when travel is blocked.
- Add an All Items Teleportable header above portal hover details, with English and Korean translations.
- Remove restricted-item icons from map pins and favorites, and reduce the favorites panel width from 320 to 292. Travel validation, fares, and Invite cooldown indicators remain in place.
- Reduce redundant portal checks, repeated UI lookups, and allocations in idle travel-ticket and reservation cleanup paths.
- Support automatic local Debug DLL deployment through DeployToGame=true after assembly merging.

## 1.0.2

- Fixed dedicated-server character verification blocking portal use and access-mode changes.
- Fixed authenticated server admins being unable to place Admin portals on dedicated servers.
- Added safe Public handling for creatorless server and location portals.

## 1.0.1

- Fixed portal catalog updates occasionally being missed after portal changes.
- Enforced Tagged portal connection scopes even when Portal Map is disabled.
- Improved compatibility and payment handling for CurrencyPocket.
- Reduced unnecessary server synchronization traffic and improved state-file write safety.
- Improved resilience to Valheim UI and portal connection API changes.

## 1.0.0

- Initial release.
