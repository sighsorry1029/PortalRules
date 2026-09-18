# Crossplay portal placement

The account portal limit is effectively disabled on PlayFab without changing the saved
configuration. All four consumers (local placement, server acceptance, quota state,
and catalog limits) use `PublicPortalConfig.IsAccountPortalLimitEnabled`.

## Identity boundary

Reviewed original Windows x64 Valheim 1.0.14 client build 25364265 and dedicated
build 25364309, using the existing global snapshots:

- `C:/Users/blizz/.codex/references/valheim/snapshots/client-b25364265-windows-x64-20260917T122143Z`
- `C:/Users/blizz/.codex/references/valheim/snapshots/dedicated-server-b25364309-windows-x64-20260917T122143Z-depot-restored`

Sources are under `derived/ilspy-9.1.0.7988-r1/assemblies/assembly_valheim/csharp`;
original assemblies are under each snapshot's `original/*_Data/Managed`.

`ZPlayFabSocket(PlayFabPlayer)` assigns public readonly `m_remotePlayerId` from
`remotePlayer.EntityKey.Id`. `OnDataMessageReceived` matches the sender entity to
that remote ID. In contrast, `GetHostName()` returns a platform identity assigned
from an incoming package, so it is not promoted to authenticated Steam ownership.
Local identity uses public `PlayFabManager.IsLoggedIn` and `Entity.Id` (the login
result). These accesses use original DLLs; no publicizer or new private access is used.
The game-provided PlayFab assembly is a compile reference, not a bundled dependency.

`PlayFab_<entity>` is a separate canonical account namespace. The existing checks
for ready peer, character ZDO, network owner, creator playerID, and registry collisions
remain in place. Account normalization is also used by Builder sanitization, editing,
Invite quotas/cooldowns and travel tickets. Steam-only Clan lookup and remote admin
checks remain unchanged; no client-reported platform ID gains those permissions.

## Persistence and backend switching

`player-identities.yml` retains the Steam registry. Crossplay uses
`player-identities-playfab.yml` with the same YAML schema and atomic save lifecycle,
so switching transport cannot overwrite a character's old Steam association or
create a false Steam/PlayFab identity collision. Existing portal ownership is not
transferred between the two namespaces. Steam-only override keys are unchanged;
Crossplay Invite limits use the global default.

Cooldown YAML retains its schema and accepts either canonical account namespace;
the per-account serialized-size allowance includes the longer PlayFab IDs. Existing
Steam cooldowns remain separate. An older mod binary cannot read PlayFab cooldown
entries; do not downgrade a used Crossplay world without restoring its matching
pre-update data backup. Server and clients should use the same patched DLL.

## Validation and remaining checks

Debug build and deployment, existing 89 behavior checks, 45 new isolated identity,
policy and YAML round-trip checks, and original 1.0.14 client/dedicated metadata/IL
checks passed. Tests use controlled transport boundaries, not a live Party session.
Generated outputs, vendor library internals and unrelated game systems were not
changed or comprehensively reviewed.

Actual game validation remains: Crossplay local host and remote dedicated client
placement with saved limit On and Off; editing and Personal access isolation between
two accounts; world save/restart/reconnect; Invite limits and cooldown persistence;
paid travel commit/refund behavior on disconnect; and switching back to Steamworks
with the original settings and ownership intact. Also check the two-line central
message on genuine Crossplay identity failure. No live-game pass is claimed.
