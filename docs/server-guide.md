# Server and administrator guide

[PortalRules overview and showcase](https://github.com/sighsorry1029/PortalRules/blob/main/README.md) · [Player guide](https://github.com/sighsorry1029/PortalRules/blob/main/docs/user-guide.md)

## Installation

Install the same PortalRules version on the server and every client, with **BepInExPack Valheim 5.4.2351**. For a manual installation, put `PortalRules.dll` in `BepInEx/plugins/`. Configuration and version checks use the bundled ServerSync integration.

PortalRules does not require Jotunn; leave it installed if another mod needs it. **TargetPortal is incompatible with PortalRules.**

Optional integrations:

- **Clan API v4 on the server** enables Clan membership checks. Without a ready compatible integration, Clan selection and non-Builder Clan access are denied.
- **YouAreNotWorthy** can check Required GlobalKeys per character; see [GlobalKeys](#required-globalkeys).
- **CurrencyPocket 1.0.15** or **EndosCoinPurse 1.0.5** supports wallet Coins for fares. Use one wallet mod at a time. Wallet Coins are used before inventory Coins.
- **Expand World Data** and **InfinityHammer** can create location or blueprint portals; see [world-editing mods](#world-editing-mods).

For historical compatibility work and its validation limits, see the [Valheim 1.0.7 notes](https://github.com/sighsorry1029/PortalRules/blob/main/docs/valheim-1.0.7.md) and [Crossplay identity notes](https://github.com/sighsorry1029/PortalRules/blob/main/docs/crossplay-identity.md). These notes distinguish automated checks from live-game verification.

## Configuration

The main file is `BepInEx/config/sighsorry.PortalRules.cfg`. It is generated on first startup and watches for saved changes. Descriptions in the file identify server-synchronized and client-local settings.

### Server-synchronized settings

| Setting | Default | Effect |
|---|---|---|
| `Lock Configuration` | `On` | Restricts configuration changes to server administrators. |
| `Enable Portal Map` | `On` | Opens destination selection for handled non-Tagged portals. `Off` uses connected destinations while retaining server authorization. |
| `Default Portal Mode` | `Personal` | Initial mode for newly placed ordinary player portals: `Personal`, `Clan`, or `Public`. |
| `Public Access Duration Seconds` | `900` | Returns eligible Public portals to Personal after 15 minutes. `0` disables expiry. |
| `Max Invite Portals Per Account` | `0` | Invite portals per authenticated account. `-1` is unlimited; `0` disables Invite. |
| `Invite Departure Cooldown Hours` | `1` | Per-account, per-Invite-portal departure cooldown. `0` disables it. |
| `Invite Arrival Cooldown Hours` | `1` | Separate per-account, per-Invite-portal arrival cooldown. `0` disables it. |
| `Max Clan Portals Per Clan` | `5` | Portals assigned to one Clan. `-1` is unlimited; `0` prevents entering Clan mode. |
| `Enable Account Portal Limit` | `Off` | Enables the overall Steam-account limit. Automatically inactive in Crossplay. |
| `Max Portals Per Account` | `10` | Counted portals per account in the world when the overall limit is enabled. `-1` is unlimited; `0` blocks new counted portals. |
| `Counted Portal Prefabs` | `portal_wood,portal,portal_stone` | Comma-separated prefab names counted by the overall account limit. Admin prefabs are always excluded. |
| `Portal Fare Mode` | `Off` | `Off` keeps normal item restrictions; `Pay` permits ordinary restricted cargo for Coins. |
| `Coins Per Weight Kilometer` | `0.1` | Fare coefficient; the final fare is rounded up. |

Limits accept `-1..10000`. Public duration accepts `0..604800` seconds, and each Invite cooldown accepts `0..8760` hours.

Invite is disabled by default for new configurations. Updating does not overwrite existing saved settings or account overrides; set `Max Invite Portals Per Account` to `0` yourself if you want to disable it on an existing server, taking any Invite overrides into account.

### Client-local settings

These are not forced by the server:

| Setting | Default | Effect |
|---|---|---|
| `Toggle Accessible Portal Pins Key` | `P` | Shows or hides accessible pins on the large map. |
| `Auto Close Grace Seconds` | `0.5` | Delay after leaving the source portal area before its destination map closes. `0` disables automatic closing. |
| `Portal Map Wheel Zoom Multiplier` | `3` | Destination-map mouse-wheel zoom speed; `1` uses the normal rate. |
| `Favorite Portal List Collapsed` | `Off` | Remembers the favorites panel's collapsed state. |
| `Portal Access Modifier Key` | `LeftShift` | Hold with Interact to cycle a portal's mode. |

## Defaults, ownership, and limits

### New placements and Public expiry

Changing `Default Portal Mode` affects future ordinary player placements only, including during a running session. Existing portals, saved-world reloads, Admin prefabs, and creatorless system portals retain their own rules. Portals created by another mod with a verified player creator follow player-attribution rules.

`Clan` falls back to `Personal` if the Builder's primary Clan cannot be verified or its quota is full or unavailable. `Public` uses the configured Public timer. Expiry is an absolute UTC deadline: server downtime and unloaded regions still count. Admin prefabs and Builderless portals are excluded.

The Builder is immutable; interacting with a portal does not transfer its attribution. Mode changes normally allow the Builder, Primary or Guest members of the Builder's current primary Clan, or a server administrator in debug mode. Entering or leaving Invite remains Builder-only. Without a resolvable primary Clan, mode changes are limited to the Builder or an administrator in debug mode. See the [player guide](https://github.com/sighsorry1029/PortalRules/blob/main/docs/user-guide.md) for each mode's travel rules.

### Overall, Invite, and Clan limits

The overall limit is shared by all characters on an authenticated Steam account. Admin portals and ordinary portals with neither a valid Builder nor a creator ID are excluded. Lowering the overall limit does not destroy existing portals.

Turning `Enable Account Portal Limit` Off bypasses the overall limit and all `portal_limit` overrides, but **Invite and Clan limits remain active**. Setting `Max Portals Per Account` to `-1` instead makes only the global overall default unlimited; account overrides can still apply.

Invite quotas and cooldowns use the authenticated account, not an individual character. Cooldowns are stored separately by world, account, portal, and departure/arrival direction. An effective Invite limit of `0` returns eligible Invite portals to the Builder's Personal mode. This is one-way: increasing the limit does not restore Invite automatically. Load any account exceptions before disabling Invite globally.

### Accounts and Crossplay

In Crossplay sessions, the overall account portal limit is inactive for placement, server enforcement, and displayed limits, even if its saved setting is On. The setting is preserved for a later Steamworks session.

Personal ownership, Builder editing, Invite limits, cooldowns, and travel authorization use the connected PlayFab entity identity. A platform ID supplied by a remote client is not used as authenticated Steam ownership. Crossplay uses the global Invite limit; SteamID64 overrides do not apply.

Steam and PlayFab ownership are separate. Switching networking backends neither transfers existing portals nor merges accounts. The optional Clan integration still needs its verified Steam identity for remote membership lookup; a client-reported platform ID does not grant Clan access or remote administrator privileges. Placement identity failures include Crossplay guidance in the message's second line.

Keep matching world and PortalRules data backups before changing backends or downgrading. Older binaries cannot read PlayFab entries in the cooldown file; do not downgrade a used Crossplay world without restoring its matching pre-update data. Further details are in the [Crossplay notes](https://github.com/sighsorry1029/PortalRules/blob/main/docs/crossplay-identity.md).

## Server data files

Server-only YAML files live in `BepInEx/config/PortalRules/`:

| File | Purpose | Editing |
|---|---|---|
| `player-identities.yml` | Generated character-to-Steam identity registry. | Do not edit while running. |
| `player-identities-playfab.yml` | Separate generated character-to-PlayFab registry. | Do not edit while running. |
| `portal-limit-overrides.yml` | Steam-account overall and Invite limits. | Administrator-edited; hot-reloaded. |
| `invite-travel-cooldowns.yml` | Generated per-world/account/portal cooldown history. | Do not edit while running. |
| `admin-portal-biome-global-keys.yml` | Default GlobalKeys for newly initialized Admin portals. | Administrator-edited; hot-reloaded. |

Back up these files with the world. Identity files use `format_version: 1` and an `identities` mapping. The cooldown file uses `format_version: 1` and a `worlds` mapping containing account and portal records with `departure_last_utc` and `arrival_last_utc`. These generated files are not account-override tables.

### Account overrides

Edit `portal-limit-overrides.yml` using this schema:

```yaml
format_version: 2
overrides:
  "76561198000000000": 10
  "76561198000000001": 10, 1
```

Keys must be bare 17-digit SteamID64 values. The first value is `portal_limit`; the optional second is `invite_limit`. Omitting the second value follows the current global Invite setting. Omit the comma too; a trailing comma is invalid.

Each value accepts `-1` for unlimited, `0` to block new counted portals or disable Invite respectively, or `1..10000` for a custom limit. Schema version 1 is not supported.

Use `overrides: {}` to clear the table. Invalid reloads or a missing file keep the last valid overrides. Before setting the global Invite limit to `0`, save positive or unlimited Invite exceptions and wait for the server log `Loaded ... account limit override(s)`. Otherwise existing Invite portals may already have returned to Personal.

### Admin portal biome keys

The initial `admin-portal-biome-global-keys.yml` includes these vanilla defaults, plus registered custom biome identifiers with empty requirements:

```yaml
format_version: 1
biomes:
  Meadows:
  BlackForest: defeated_eikthyr
  Swamp: defeated_gdking
  Ocean: defeated_bonemass
  Mountain: defeated_bonemass
  Plains: defeated_dragon
  Mistlands: defeated_goblinking
  AshLands: defeated_queen
  DeepNorth: defeated_fader
```

An empty value means no requirement. Biome names are case-insensitive. For Expand World Data custom biomes, use the stable `biome:` identifier from its biome YAML, not a translated display name or runtime number.

The file is generated once after the biome registry is ready, not continuously extended or overwritten. Add newly introduced custom biomes yourself. If deliberately regenerating it, back it up and remove it before the next server session; a new template does not preserve your edits.

A default is selected from the portal's actual world position only when that Admin portal first receives authority. Reloading this file does not rewrite existing portals. An explicit `PortalRules RequiredGlobalKey` value stored on the portal, including an explicitly empty value, takes precedence over the biome default. This includes child/location data and blueprint data.

## Admin portals

`admin_portal_wood` and `admin_portal_stone` appear as Admin Wood and Admin Stone under **Hammer → Misc** for server administrators in debug mode. Placement, editing, and dismantling require that access.

These portals are creatorless, Builderless, quota-exempt, and excluded from Public expiry. They start in Public and cycle `Admin → Public → Tagged → Admin`. Admin mode is not selectable on ordinary portals; older ordinary portals already saved in Admin can leave it by cycling to Public.

| Action | Control |
|---|---|
| Edit the tag | Interact |
| Cycle mode | Portal Access Modifier + Interact (`LeftShift` by default) |
| Set or clear Required GlobalKey | Physical `LeftAlt` or `RightAlt` + Interact |

Placed frames are invisible and non-solid, with a whirling marker while a player is nearby. Dismantling returns no resources. They allow all ordinary items, while Valheim's absolute item restrictions still apply.

Tagged Admin portals use a separate public reciprocal connection pool. Eligible players can use their connections, but map pins are visible only to server administrators in debug mode. Required GlobalKeys still apply.

### Required GlobalKeys

A Required GlobalKey is an additional travel requirement, not a replacement for the access mode. Clearing it explicitly also suppresses any biome default for that portal.

Without YouAreNotWorthy, PortalRules checks shared world GlobalKeys. With its compatible Required GlobalKey API, ordinary progression keys can be checked per character, and its localized missing-key message is used when available.

If YouAreNotWorthy is installed but its API is incompatible or unavailable, key-gated travel is denied; PortalRules does not silently fall back to shared world progression. Invalid stored requirements likewise deny access.

## Coins fares

In `Pay` mode:

```text
fare = ceil(ordinary non-teleportable cargo weight
          × XZ distance in kilometers
          × Coins Per Weight Kilometer)
```

Restricted source portals accept payable cargo, keep their whirling effect active, and open the destination map. Pins and favorites show fares; Tagged portals retain direct connected travel and show their fare in the connected hover badge. All-items portals and the `TeleportAll` world modifier remain free.

The server calculates the route and final fare from the client cargo-weight snapshot; the client checks the same weight before taking Coins and starting travel. Denied trips and teleports that fail to start do not consume Coins. Absolute item restrictions remain blocked in both fare modes unless another mod explicitly overrides the game's final teleportability check; cargo allowed by that override is not charged as restricted cargo.

## World-editing mods

Use Admin prefabs for permanent system portals and creatorless Tagged connections. For Expand World Data placement coordinates and the complete three-location example, see [Admin and location portals in the main README](https://github.com/sighsorry1029/PortalRules/blob/main/README.md#admin-and-location-portals).

- **InfinityHammer:** use `data=true` when saving blueprint data that must retain a Required GlobalKey. Merely copying a visible portal or its tag does not preserve that requirement. For new creatorless ordinary portals, PortalRules initializes Public system access without Builder attribution; do not rely on a copied authority stamp to restore player ownership, modes, or a Required GlobalKey. Prefer Admin prefabs for key-gated blueprint portals.
- **Expand World Data:** place `PortalRules RequiredGlobalKey` on the portal child through `objectData` or `locationObjectData`. Explicit child or blueprint data, including an empty requirement, overrides the biome default.
- **Existing locations:** editing `expand_locations.yml` does not modify already-created instances. Reset only the intended location instances after backing up the world. After `zones_reset`, run `zones_generate` so it actually generates the affected zones, or visit those zones. A reset alone does not recreate a portal in an unloaded zone; generation must occur before it becomes available again.

World-editing commands belong to the relevant world-editing mod. Check their scope before applying them to a live world.
