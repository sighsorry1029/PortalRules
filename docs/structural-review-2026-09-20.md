# PortalRules structural review — 2026-09-20

## Baseline and scope

Work was performed directly on `main`, starting at
`34b3685cc5b55e580aede09848a728b4386454de` (1.0.8).
The pre-existing changes in `PublicPortalInteraction.cs`, `README.md`,
`translations/English.yml`, and `translations/Korean.yml` were preserved and
excluded from the structural commits. They change the ordinary mode cycle and
the insufficient-Coins message. The Debug builds include those working-tree changes.

The global `C:/Users/blizz/.codex/AGENTS.md` and Valheim reference `INDEX.md` were
consulted; no additional AGENTS.md was found in this checkout or its ancestor chain.
Existing project notes and Git history were checked against current code.
Parallel reviewers were read-only; one session edited, built, and committed.

- Build target: `publicportal.csproj`, .NET Framework 4.8 library `PortalRules.dll`.
  `PortalRulesPlugin : BaseUnityPlugin` is the BepInEx entrypoint, not a preloader
  patcher. `Awake` binds configuration/integrations and installs Harmony;
  `Update` drives stores, transactions, prefabs and selection; `OnDestroy` cleans up.
- `PublicPortalInteraction.Lifecycle` owns game/network/save Harmony wiring.
  `PublicPortalMap`, `PublicPortalConnectedHud`, `PublicPortalTaggedConnections`,
  `AdminPortalPrefabManager`, localization and pin registration have their own
  Harmony boundaries. `RPC_ZDOData` and placement Prefix/Finalizer state restoration
  and existing cursor priority were inspected and left intact.
- `PublicPortalConfig` owns keys/defaults/live handlers, ServerSync setup and
  debounced persistence. Account overrides/identity YAML, Invite cooldown YAML,
  biome defaults, ZDO authority fields, favorites and bounded RPC formats are in scope.
- Resources: embedded English/Korean YAML and third-party notices, external
  translation override handling, scene-owned admin prefab clones, generated icons,
  borrowed game fonts/sprites, Hammer registration and package inputs were reviewed.
  No game asset extraction or visual asset audit was performed.
- Soft dependencies: Clan, CurrencyPocket, YouAreNotWorthy; Expand World Data is
  discovered by the optional biome path. TargetPortal remains incompatible.
  Reflection binding/readiness and failure behavior remain separate contracts.
- `ILRepack.targets` merges the primary DLL, pinned `Libs/ServerSync.dll` and
  YamlDotNet 16.3.0 into the final DLL. ServerSync SHA-256 remains
  `B4DD786997F4E90D770F09EF3E9D64154754FE7E8EDFB4841795751895B35846`.
  PlayFab is a game reference, not a bundled library. Jotunn is absent.
  `CopyOutputDLL` runs after merging/version inspection for `DeployToGame=true`;
  ZIP targets are Release-only. Manifest BepInEx dependency remains 5.4.2350.
- Tests: existing behavior, Crossplay and cursor fixtures compile selected real
  methods against controlled boundaries; `Check-Compatibility.ps1` inspects the
  final DLL's game references and selected Harmony/reflection/config contracts.

Generated `bin`, `obj`, package staging/ZIPs, vendor/NuGet internals, game/BepInEx/
Unity implementations outside the relevant contracts, other mods' full source,
uploader operation and site publication were excluded from structural changes.
This was a focused responsibility/call-path review, not an exhaustive proof of every
branch, every asset, arbitrary third-party reflection or every runtime mod combination.

## Game version evidence

README and the original compatibility note name Valheim **1.0.7**. Crossplay notes
also document original **1.0.14** client/server analysis. The current environment
selects installed original **1.0.15** client DLLs (Steam build **25390630**).
The installed `assembly_valheim.dll` matches the preserved 1.0.15 client original:
SHA-256 `59F53FB55D99D22A33E8ED094EEC8D21E9F133543BCE92BC3D80DCE44033ADB1`.
No publicizer is configured. Support declarations and dependencies were not changed.

Existing snapshots were reused for baseline and final static checks:

| Role/version | Snapshot under the global `references/valheim/snapshots` directory |
| --- | --- |
| Client 1.0.7 | `client-b25185596-windows-x64-20260909T131109Z` |
| Dedicated 1.0.7 | `dedicated-server-b25185644-windows-x64-20260909T131109Z` |
| Client 1.0.15 | `client-b25390630-windows-x64-20260918T131715Z` |
| Dedicated 1.0.15 | `dedicated-server-b25390671-windows-x64-20260918T185703Z-depot-restored` |

Original 1.0.14 source/metadata was also used for relevant UI/prefab and identity
boundaries. Private `ZNet.m_steamPlatform`, `ZNetScene.m_namedPrefabs`,
`ObjectDB.m_buildPieces`, `PieceTable.m_availablePiecesByCategory` and
`Player.UpdateAvailablePiecesList` remain explicitly accessed through cached
Harmony/reflection bindings. No private access path was introduced or changed.
The 1.0.12→1.0.14 guide was consulted only for input, inventory and lifecycle
overlap. Compile/static success is not proof of runtime private access or an
expansion of the advertised support range.

## Structural judgment

| Area | Judgment and evidence |
| --- | --- |
| Config and plugin lifecycle | Balanced. `6072063` debounced saves, `07107e2` fixed handler cleanup, and `1d9c5a2` co-located config state/handlers/persistence. Splitting them again would increase change propagation. |
| Catalog and network partial | Concentrated, but authority, Builder/favorite indexes, ZDO writes and recipient visibility require joint consistency. The wire/chunk/ACK partial is a useful boundary. No broad split justified. |
| Travel and transactions | Balanced boundaries with one duplicated teardown. Authorization versus commit/cancel/refund must retain separate validation points. `fe9276b` fare and `34b3685` identity changes legitimately crossed these files. |
| Map, pins, favorites, connected HUD | Ownership boundaries are useful: game-owned pin UI, panel-owned buttons/icons and TMP hover layout differ. `3464feb` isolated lifecycle Harmony wiring. Two obsolete rendering branches can be removed locally. |
| Stores and integrations | Balanced. Atomic file replacement is shared; watcher readiness, last-valid overrides, identity retry and durable cooldown commit policies differ. Broad unification would hide those differences. |
| Admin prefabs/resources | Balanced. Scene templates, Hammer tables and cleanup share a lifecycle. Startup/finalizer/teardown and duplicate-registration guards remain necessary. |

No new production file, interface, generic UI layer or cache was introduced.

## Implemented steps

1. **`2f41835` — reuse travel session teardown.**
   `ZNet.Awake` Postfix → `BeginNetworkSession` now calls existing `Shutdown` before
   assigning the new session. `Plugin.OnDestroy` already calls `Shutdown`.
   The identical cleanup sequence was duplicated since `38e356e`.
   This makes future request/ticket state cleanup a single edit, adding one local
   method call without another abstraction or file to navigate.
   Null/same-session guards, cancellation order and exception propagation remain.
   The intermediate null session assignment occurs after cleanup, immediately
   before the new assignment, with no intervening callback or asynchronous work.
   Regression risk is accidentally clearing active travel or canceling a committed
   reservation. A new focused fixture checks 21 lifecycle outcomes, including
   repeated start/shutdown, uncommitted-only cancellation and an injected failure.
   It passed against the original methods before the refactor and again afterward.

2. **`b653348` — simplify positive-fare rendering.**
   `UpdateTravelBadges` rejects nonpositive fares before calling private
   `UpdateTravelBadge`; `CreateButton` calls private `CreateTravelInfoDisplay` only
   for positive fares after the cooldown branch. Removed the redundant inner
   positive-fare conditions in those two renderers.
   These are ordinary internal C# controllers, not Unity message receivers;
   no repository Harmony/reflection/delegate entry to these private methods was found.
   Caller guards, missing-icon fallback, affordability colors, text activation,
   widths/anchors and object lifetimes remain identical for supported calls.
   The benefit is fewer impossible states to reason about; speed was not measured.
   Regression risk is a future caller violating the positive-fare precondition,
   now documented next to each renderer. Call-site/diff review and Debug/static
   checks passed. Existing fixtures do not execute these Unity rendering methods;
   no artificial UI fixture duplicating their implementation was added.

Each change was edited, validated, independently reviewed and committed before the
next. Either implementation commit can be reverted independently. This report is
a separate documentation-only commit.

## Preserved contracts and deferred work

- Harmony targets, overloads, order, return suppression, `__state`, finalizers and
  exceptions were not changed. Map selection still restores no-map state in a
  finally block, and cursor handling retains the existing mouse/large-map guards.
- Server ownership and requester account rights remain distinct. Ready-peer,
  transport/account, range, source/destination access and duplicate-ticket checks
  are not interchangeable. `ForgetPeer` deliberately retains a commit-requested
  ticket for durable save retry; session teardown deliberately clears it.
- Cooldown reservation revalidation, disk flush before acknowledgement, rollback
  on save failure, late grant cancellation, duplicate commit handling and physical/
  CurrencyPocket partial refunds retain their policies. No claim of crash-proof
  item conservation follows from the isolated tests.
- Config keys/defaults/live behavior, saved formats, RPC names/field order,
  public helpers, prefab identities, translation overrides and optional API/version
  checks are unchanged by these commits. Steam/PlayFab identity namespaces and
  Steam-only remote Clan/admin paths are preserved.
- Performance candidates retained: tickets snapshot `Values.ToArray()` while
  nonempty to permit removal/callback processing; favorites scan/sort state every
  0.2 seconds and rebuild only when that state changes (cooldowns display rounded
  minutes); map hints format text per frame but apply layout only on change.
  Font/private-member lookup is cached; no new invalidation burden was added.
  Measure these paths before adding pooling or cross-frame caches. No FPS or
  allocation improvement was measured or claimed.
- Separate possible defect: `Plugin.Awake` catches initialization failures by
  shutting down configuration only, although Clan subscription and partial
  Harmony installation may already exist. The asymmetry is confirmed; a lasting
  leak is unverified because BepInEx/Unity destruction timing was not exercised.
  Investigate with startup failure injection before a separate behavioral fix.

## Validation and remaining execution

- Baseline Debug build/deployment: success, zero warnings/errors. Existing **150**
  behavior checks passed. No baseline production failure was found.
- Teardown step: Debug build/deployment and **171** checks passed. Reservation
  storage, game transport and UI in these tests are controlled doubles.
- UI step/final code: Debug build/deployment succeeded, zero warnings/errors.
  Final static checks passed against all four originals above: **1,589** game
  member instructions resolved, **44** explicit Harmony targets, **3** field
  injections, **2** dynamic Hammer targets, **5** private-field contracts,
  **1** wheel anchor, **4** config persistence contracts and **9** live subscriptions.
  They do not install Harmony into a game process or prove all reflection behavior.
- Diff checks and independent read-only reviews passed for both changes.
- Final `bin/Debug/PortalRules.dll` and installed
  `C:/Program Files (x86)/Steam/steamapps/common/Valheim/BepInEx/plugins/PortalRules.dll`
  SHA-256 match: `0572D5B7B2A845C35107C5242AE0AC9D6C8BCEB3574C0EA9DAFFB41278479C10`.

No actual game, local host, remote dedicated client or Crossplay session was run.
Remaining checks on disposable worlds: same-session initialization and reconnect;
pending/committed Invite trips during disconnect and save retry; paid travel and
refund totals with/without CurrencyPocket; positive/zero fare, sufficient/insufficient
balance, icon fallback, cooldown priority, map close/reopen and scene changes.
The previous cursor/placement fixes still need their existing live-game checklist.

Version remains 1.0.8. No Release build/ZIP, push, uploader operation or publication
was performed. The four initial modified files remain uncommitted intentionally;
there are no additional pending changes from this structural work.
