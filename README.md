# PortalRules

PortalRules adds map-based portal travel, access modes, portal limits, Admin portals, and optional Coins fares to Valheim.

The server filters the portal catalog for each player and authorizes every handled trip. Clients receive catalog entries only for portals they are allowed to use.

This source targets Valheim 1.0.7 and BepInExPack Valheim 5.4.2350. Jotunn is no longer required by PortalRules. Other installed mods may still require it. Install PortalRules on the server and clients; its existing ServerSync configuration/version checks remain in place.

All-items portals bypass ordinary material restrictions while respecting Valheim's absolute item restrictions. In Off mode, restricted cargo is rejected before the destination map opens. In Pay mode, ordinary restricted cargo opens the map and is charged by weight and distance; absolute restrictions are still rejected. Compatibility details and the remaining in-game validation checklist are in [the 1.0.7 notes](docs/valheim-1.0.7.md).

## Showcase

### Portal map and favorites

Browse every portal available to your character from the destination map. Pin overlays show travel restrictions, Coins fares, ownership limits, access modes, and portal tags.

![Portal map overview](https://i.ibb.co/RpKBZKGV/portalmap.png)

![Portal map destination selection](https://i.ibb.co/d0HPpqWP/portalmap2.png)

![Portal pin travel, fare, ownership, mode, and tag indicators](https://i.ibb.co/v6qPM7bq/portaliconshowing-notteleportable-coins-myportal-tag-andstuff.png)

Favorites provide quick travel rows and focus the map on a destination when hovered.

![Favorite portal hover and map focus](https://i.ibb.co/8SXKw7B/favoriteportalhover.gif)

### Access modes

Cycle access modes at the portal, then identify Public and reciprocal Tagged connections at a glance.

![Changing a portal access mode](https://i.ibb.co/gMtyYSpp/portalmodechange.gif)

![Public portal](https://i.ibb.co/5XxGzsbk/publicportal.png)

![Tagged portal](https://i.ibb.co/0RK1gXMh/taggedportal.png)

Clan portals expose their bound access and quota state both on the map and in hover details.

![Clan portal map pin](https://i.ibb.co/fY1cySWD/clanportalmap.png)

![Clan portal hover details](https://i.ibb.co/NgtYZp1P/clanportalhover.png)

Invite portals use dedicated map and hover indicators, with cooldown feedback when travel is temporarily unavailable.

![Invite portal map pin](https://i.ibb.co/8L9MQNWP/inviteportalmappin.png)

![Invite portal hover details](https://i.ibb.co/MxYG77C6/inviteportalhover.png)

![Invite portal cooldown](https://i.ibb.co/FbrMCsPn/invitecooldown.png)

### Admin and location portals

Admin portal pieces appear to server administrators in debug mode. Their controls are protected, and Required GlobalKeys can gate progression.

![Admin portal pieces shown in debug mode](https://i.ibb.co/5WYRJhHF/adminportashowuplwhiledebugmode.png)

![Admin portal Required GlobalKey restriction](https://i.ibb.co/gLRRZ0Ww/adminportalrestriction.gif)

![Location Admin portal GlobalKey](https://i.ibb.co/KxPfY3Yw/locatoinadminportalglobalkey.png)

Admin portals can also be embedded into generated world locations.

![Location portal in action](https://i.ibb.co/tpfLhwTr/locationportal.gif)

Expand World Data can place Admin portals inside generated locations. Add an Admin portal object under each target prefab in `expand_locations.yml`, then reset the affected location instances to generate the new portals.

```yaml
- prefab: BogWitch_Camp
  objects:
  - admin_portal_stone, 0,15,0.5, 180,0,0

- prefab: Hildir_camp
  objects:
  - admin_portal_stone, -0.5,14,-0.5, 180,0,0

- prefab: Vendor_BlackForest
  objects:
  - admin_portal_stone, -7,7,-0.5, 135,0,0
```

Adjust the position and rotation for each location. Editing the YAML does not modify instances that already exist until those locations are reset.

## Quick start

| Action | Default control |
|---|---|
| Set a portal tag | `Interact` |
| Change access mode | `LeftShift + Interact` |
| Show or hide accessible pins | `P` on the large map |
| Add or remove a favorite | Right-click a portal pin |
| Travel to a favorite | Left-click its row |
| Remove a favorite | Right-click its row |
| Focus the map on a favorite | Hover its row |
| Edit an Admin portal GlobalKey | `LeftAlt/RightAlt + Interact` |

When `Enable Portal Map` is On, enter a handled non-Tagged portal to open the destination map. Select an accessible portal pin to travel.

With `Portal Fare Mode` set to `Off`, a restricted source carrying a non-teleportable item shows Valheim's item restriction message without opening the map. In `Pay` mode, ordinary restricted cargo opens the map and shows its fare; absolutely restricted items remain blocked unless another mod explicitly overrides Valheim's final teleportability check. Portals that allow every item show **All Items Teleportable** above their hover details.

The arrow beside **Favorite Portals** collapses the list. Its state is saved locally.

## Access modes

| Mode | Who can use it |
|---|---|
| `Personal` | The immutable portal Builder. |
| `Admin` | The Builder and server-approved administrators. |
| `Public` | Everyone. Eligible player portals later return to Personal. |
| `Invite` | Everyone, without the Public timer. Only the Builder may enter or leave this mode; limits and cooldowns apply. |
| `Clan` | The Builder and members of the Builder's bound primary Clan. Primary and Guest members may use it, but Guest-only membership cannot assign it. |
| `Tagged` | Uses Valheim's reciprocal tag connection. Ordinary portals are limited to the Builder and the Builder's bound primary Clan, including Guest members. |

Normal portals cycle:

```text
Personal → Clan → Public → Invite → Tagged → Personal
```

Unavailable modes are skipped. Admin portal prefabs default to Public and cycle only:

```text
Admin → Public → Tagged
```

Admin is not selectable on ordinary portals. Existing ordinary portals saved in Admin mode keep that mode until cycled, then switch to Public.

The server-synchronized **Default Portal Mode** setting chooses `Personal` (default), `Clan`, or `Public` for newly placed ordinary player portals. `Clan` falls back to `Personal` if the Builder's primary Clan cannot be verified or its portal limit is reached or unavailable. `Public` follows **Public Access Duration Seconds**, returning to Personal on expiry; a duration of `0` disables that timer. Changing the default affects future placements only, including during a running session. Existing portals, saved-world reloads, Admin portals and creatorless system portals retain their existing rules. Portals generated by another mod with a verified player creator follow the existing player-attribution rules.

An ordinary portal's modes may be cycled by its immutable Builder, Primary or Guest members of the Builder's current primary Clan, or a server administrator in debug mode. Invite entry and exit remain Builder-only. Personal, Clan, and Tagged attribution always comes from that Builder, not the player interacting with it. If the Builder has no resolvable primary Clan, only the Builder or an administrator in debug mode may change it.

A Required GlobalKey, when present, is checked in addition to the access mode.

## Key defaults

| Rule | Default |
|---|---:|
| Portal map | On |
| Default portal mode | Personal |
| Public duration | `900` seconds / 15 minutes |
| Account portal limit | Off (`10` portals when enabled) |
| Invite portals per Steam account | `1` |
| Invite departure / arrival cooldown | `1` hour / `1` hour |
| Clan portals per Clan | `5` |
| Coins fares | Off |

All settings are documented in `sighsorry.PortalRules.cfg`. Map keys, auto-close grace, map zoom, and the access modifier are client-local; the remaining gameplay rules are server-synchronized and can be locked by the server.

Setting an effective Invite limit to `0` returns eligible Invite portals to their Builder's Personal mode. Raising the limit later does not restore Invite automatically.

Public expiry uses an absolute UTC deadline, so server downtime and unloaded regions still count. Admin portal prefabs and Builderless portals are excluded from that timer.

Disabling `Enable Account Portal Limit` bypasses only the overall account limit and `portal_limit` overrides. Invite and Clan limits remain active.

The account portal limit is Off by default and automatically inactive during Crossplay/PlayFab sessions, even if the saved setting is On. The saved value is preserved for Steamworks sessions. This applies to placement checks, server enforcement, and displayed limits.

Crossplay portal ownership uses the connected PlayFab entity identity, not the platform ID reported by a remote client. Personal ownership, Builder editing, Invite limits, cooldowns, and travel authorization use that same identity. Failed placement identity verification includes Crossplay guidance on the second line. Steam and PlayFab ownership remain separate: switching backends does not transfer existing portals or merge accounts. The optional Clan integration still requires its existing verified Steam identity for remote membership lookup; this change does not grant Clan access or remote administrator privileges through a client-reported platform ID.

Overall limits are shared by every character on the authenticated Steam account. Invite limits remain active and are shared by authenticated account (Steam on Steamworks, PlayFab entity on Crossplay). The configurable default counted prefabs are `portal_wood`, `portal`, and `portal_stone`; Admin portals and normal portals with neither a valid Builder nor a creator ID are excluded. Lowering the overall limit never destroys existing portals.

## Portal map and favorites

- Favorites survive world reloads, but dismantling and rebuilding creates a new portal identity.
- Rows stay in add order from oldest to newest; re-adding one moves it to the bottom.
- At the 10-favorite limit, a new favorite may replace the oldest saved portal that is no longer available.
- Invite cooldowns and fares appear on pins or favorite rows.
- `My portal n/limit`, `Invite n/limit`, and `Clan n/limit` show quota positions. When the overall portal limit is unlimited, the own-portal label shows only `My portal n`.
- Tagged Admin portal prefabs remain usable as connected portals by every eligible player, but their map pins are visible only to server administrators in debug mode.

If `Enable Portal Map` is Off, PortalRules uses connected destinations for all modes while still applying server authorization.

The **CONNECTED** hover label is shown only for Tagged portals with a reciprocal connection. Other access modes omit the connection status label.

## Admin portals and GlobalKeys

Admin Wood and Admin Stone appear under Hammer → Misc only for server administrators in debug mode. They are creatorless, Builderless, quota-exempt, and restricted to Admin, Public, and Tagged access. Their Tagged mode is a separate public connection pool; Required GlobalKeys still apply.

Placed frames are invisible and non-solid, with a whirling marker while a player is nearby. Placement, editing, and dismantling require administrator debug access; dismantling returns no resources.

Use physical `Alt + Interact` to set or clear a Required GlobalKey. Without YouAreNotWorthy, PortalRules checks shared world GlobalKeys. With a compatible YNW API, ordinary progression keys can be evaluated per character.

## Coins fares

```text
fare = ceil(non-teleportable cargo weight
          × XZ distance in kilometers
          × Coins Per Weight Kilometer)
```

| Mode | Behavior |
|---|---|
| `Off` | Valheim's normal item teleport restrictions apply. |
| `Pay` | Ordinary non-teleportable items may travel from a restricted portal for the calculated fare. |

In Pay mode, a restricted source portal keeps its whirling effect active for payable cargo and opens the destination map. Map pins and favorite rows show the calculated fare. Tagged portals keep direct connected travel and show the same fare in the connected hover badge. All-items portals and the `TeleportAll` world modifier remain free because they already allow the cargo.

The server calculates the route and final fare from the client cargo-weight snapshot, and the client verifies that exact weight again before taking Coins and starting the teleport. Denied trips and teleports that fail to start do not consume Coins. Valheim 1.0's absolute item restrictions remain blocked in both modes unless another mod explicitly overrides the game's final teleportability check. Cargo allowed by such an override is free because PortalRules no longer treats it as restricted cargo.

## Server files

PortalRules stores server-only data under `BepInEx/config/PortalRules/`.

| File | Use |
|---|---|
| `player-identities.yml` | Generated character-to-Steam identity registry. Do not edit while running. |
| `player-identities-playfab.yml` | Generated character-to-PlayFab identity registry for Crossplay; kept separate to preserve Steam mappings. Do not edit while running. |
| `portal-limit-overrides.yml` | Administrator-edited account and Invite limits; hot-reloaded. |
| `invite-travel-cooldowns.yml` | Generated per-world cooldown history. Do not edit while running. |
| `admin-portal-biome-global-keys.yml` | Administrator-edited biome defaults for new Admin portals; hot-reloaded. |

### Account overrides

```yaml
format_version: 2
overrides:
  "76561198000000000": 10
  "76561198000000001": 10, 1
```

The first value is `portal_limit`. The optional second value is `invite_limit`; when omitted, it follows the global Invite setting.

Override keys remain SteamID64 values. Crossplay uses the global Invite limit; Steam overrides do not apply to PlayFab identities.

Values are `-1` for unlimited, `0` to block or disable, or `1..10000` for a custom limit.

Use `overrides: {}` to clear the table. Invalid reloads keep the last valid data. Because Invite-to-Personal conversion is one-way, load positive or unlimited exceptions and wait for the successful `Loaded ... account limit override(s)` server log before changing the global Invite limit to `0`.

### Admin portal biome keys

```yaml
format_version: 1
biomes:
  Meadows:
  BlackForest: defeated_eikthyr
  Swamp: defeated_gdking
  MyCustomBiome:
```

An empty value means no requirement. The file is generated once with vanilla biomes and registered Expand World Data biome identifiers. For custom biomes, use the stable `biome:` identifier rather than a translated name or runtime number.

Biome defaults apply only when a new Admin portal first receives authority. They do not rewrite existing portals. An explicitly stored value, including an explicit empty value, overrides the biome default.

## World-editing mods

- InfinityHammer blueprints must use `data=true`; creatorless normal-portal materialization restores only a complete current PortalRules authority stamp in Public mode. Use an Admin portal prefab for creatorless Tagged connections.
- After `zones_reset`, run a non-empty `zones_generate` or visit the zone to recreate the portal.
- In Expand World Data locations, put `PortalRules RequiredGlobalKey` in child `objectData` or `locationObjectData`. Explicit child or blueprint data overrides the biome default.

## Misc

Optional integrations:

- Clan API v4 on the server for Clan access; without it, Clan selection and non-Builder access fail closed
- A compatible YouAreNotWorthy installation for character GlobalKeys and its localized missing-key message when available

If YouAreNotWorthy is installed but its compatible API is unavailable, key-gated portals fail closed.
