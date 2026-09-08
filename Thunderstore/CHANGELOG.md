# Changelog

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
