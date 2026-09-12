# Changelog

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
