# PortalRules: Valheim 1.0.7 patch

Baseline: PortalRules 1.0.3, commit `1fbc5e922a8677770e2b30cbe9db257666a281c0`.
Released result: PortalRules 1.0.4.
Target originals: Windows x64 client Steam 25185596 and dedicated server 25185644.
No game DLL is publicized or rewritten. This patch does not introduce an old-game
compatibility layer, change the mod version, or create a Release package.

## Jotunn removal

The only direct uses were two admin portal clones, their Hammer registration, and
the AveriaSerif font in three UI creation paths. PortalRules retains its own
localization, RPC/authorization, optional integrations, and embedded ServerSync.
The project reference, hard dependency, manifest requirement and unused local
Jotunn DLL are removed. A Jotunn installation used by other mods is untouched.

`AdminPortalPrefabManager` owns two inactive templates per ZNetScene. Its Awake
prefix clones the already loaded native portal prefabs and inserts the templates
before the game's hash dictionary is built. Templates are children of that scene,
not persistent clones depending on assets from a previous world. No new
SoftReference asset IDs or independent asset-loading system are introduced; the
source scene retains the original asset dependencies. Scene destruction removes
owned entries from registered Hammer tables and releases templates. Failed Awake
also cleans up through a Harmony finalizer.

ObjectDB Awake/CopyOtherDB and scene registration both attempt Hammer registration
to cover their initialization order. Duplicate object insertion is prevented and
the game's cached build-piece list is invalidated when needed. Available, enabled,
and per-category lists share the existing admin+debug restriction. The stable
`admin_portal_wood` / `admin_portal_stone` names and hashes are preserved.

Legacy Text labels borrow and cache the loaded AveriaSerifLibre-Regular font at UI
creation. If unavailable, they use Unity's built-in LegacyRuntime font and log a
warning. No font scan runs in Update. The existing favorites width remains 292.
Exact appearance, language glyphs, unload/reload behavior and coexistence with
other asset/build-menu mods still require game execution.

## Game changes

- `AddIfPortal(ZDO,int)` observation checks the declared and actual prefab,
  registry membership and insertion into ZDOMan before observing authority. The
  early local CreateNewZDO path waits for TeleportWorld.Awake. RPC_ZDOData's
  authenticated Prefix/Finalizer context is preserved.
- Portal membership uses the sector bucket; whole snapshots use GetPortalList.
- ServerLoadWorld completion checks server status and m_loadError. Session
  readiness is idempotent and covers the game's own normal load branches.
- Actual authority metadata changes mark the separate portal save chunk dirty.
  Admin creator platform indices are cleared alongside the existing creator data.
- Right click and touch long-press toggle favorites during portal selection through
  RemovePinUnderPointer; normal pin deletion passes through. Coordinates use
  pointerPosition. Hover status handles the new one-space separator while
  preserving the exact tag boundary and native censorship/help lines.
- IsTeleportable receives the source's allowAllItems flag. The game decides absolute
  restrictions even for all-items travel. This adopts the new game's policy.
- Physical Coin refunds explicitly disable AddItem's automatic world drop. Actual
  inventory deltas and CurrencyPocket shortfall recovery still determine success.
  Existing partial failure reporting and ticket/duplicate guards are retained.

## Embedded ServerSync

Only the bundled ServerSync binary is changed; no shared installed library is
replaced. PortalRules vendors the reviewed shared baseline `valheim-1.0.7-r1`,
compiled from preserved upstream source against the original client game DLL. It
removes the three obsolete `ZRoutedRpc.Everybody` field loads, uses the public
1.0.7 administrator check, and preserves the game's player/history/admin/time
message order while initial configuration data is buffered. Assembly identity,
configuration keys, wire format and version contract are retained. The MIT notice
is retained.

Original SHA-256:
`166956302A294E224474B26F4C7D58409084AD3F48BD0AF1FEB7551F229C8F60`

Selected baseline SHA-256:
`B4DD786997F4E90D770F09EF3E9D64154754FE7E8EDFB4841795751895B35846`

The fixed source, manifest and reproducible build procedure are stored under
`C:/Users/blizz/.codex/references/valheim/integrations/serversync/versions/valheim-1.0.7-r1`.
The project keeps a pinned copy in `Libs` and does not read a moving global file at
build time. An upstream library update requires a fresh review rather than silently
replacing this binary.

## Validation

- Build: `dotnet build publicportal.csproj -c Debug -p:DeployToGame=true` compiles
  against installed original game DLLs, merges dependencies and deploys the final
  plugin DLL. Compare its SHA-256 with BepInEx/plugins/PortalRules.dll.
- Behavior checks: `pwsh -File tests/Run-BehaviorChecks.ps1` compiles the actual
  selected source methods with controlled inventory/player/optional-mod boundaries.
  Covers hover/tag boundaries and localized text, full/partial/exception refunds,
  total inventory+pocket+world compensation, and all-items API delegation.
- Static compatibility: `tests/Check-Compatibility.ps1 -GameManaged <Managed>
  -BepInExCore <core> -CecilDll <Mono.Cecil.dll>` checks final game references,
  explicit Harmony targets/arguments/field injection, selected cached private
  fields, both ObjectDB targets, and the wheel-transpiler anchor. Run for client
  and dedicated-server originals. Also rejects invalid short branches, literal
  fields used as static storage and a remaining Jotunn assembly reference.

These checks are not actual Unity execution or a complete Harmony installation
test. The following remain necessary on disposable test worlds:

1. Start with PortalRules and its minimum dependencies, without Jotunn. Then test
   with a compatible Jotunn used by another mod; check initialization and duplicate
   registration. An incompatible external Jotunn can still break the game.
2. Local host, dedicated server and remote client: create/restore both admin portals,
   reload worlds repeatedly, check resource unload, placement ghost, effects and
   creator display. Test original Hammer tabs and new favorite/recent/search/material
   views with admin/debug toggles and server-side rejection of unauthorized placement.
3. After a clean save, change only an unconnected portal's mode/GlobalKey/tag, save,
   stop and reload without other portal creation/connection changes. Check save
   failure/retry and changes during saving. Check a new world and failed world load.
4. Tagged reciprocal/nonreciprocal and other modes, all-items headers, normal and
   absolutely blocked items, map entry and delayed approval, mouse/controller/touch,
   censored and multiline tags, Korean font and the 292-wide favorites panel.
5. With/without Clan, YNW and CurrencyPocket: ready/unready integrations, reconnect,
   ownership and range rejection, duplicate/reordered grant/commit/cancel, full and
   partially full inventory during failed travel. Compare total currency before and
   after. Do not interpret a successful API bind as integration readiness.

No new game session, host or dedicated-server runtime validation was performed as
part of the automated checks. Other mods' compatibility fixes, global DLL changes,
uploader checks and Release/publication are outside this patch.
