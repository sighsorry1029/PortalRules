using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace PortalRules;

// Damage runs on the object's network owner, which need not be its Builder or
// the server. Only the server resolves Builder/Clan trust; clients query it on
// demand. Only UpdateWear's unsupported-collapse decision is gated; support
// values, support propagation and the other environmental damage stay intact.
internal static class PublicPortalProtection
{
    private const string PresenceRequestRpc = "sighsorry.PortalRules.ProtectionPresence.v1";
    private const string PresenceResultRpc = "sighsorry.PortalRules.ProtectionPresenceResult.v2";
    private const string RemoveRequestRpc = "sighsorry.PortalRules.ProtectedRemove.v1";
    private const string RemoveApprovedRpc = "sighsorry.PortalRules.ProtectedRemoveApproved.v1";
    private const string InvalidateRpc = "sighsorry.PortalRules.ProtectionInvalidate.v1";
    internal const float PresenceRadius = 40f;
    internal const float PresenceGraceSeconds = 10f;
    private const float RefreshSeconds = 1f;
    private const float ResponseLifetimeSeconds = 3f;
    private const float IdleLifetimeSeconds = 30f;
    private const int MaximumTrackedPortals = 1024;
    private static readonly MethodInfo RemoveRpcMethod = AccessTools.Method(
        typeof(WearNTear), "RPC_Remove", new[] { typeof(long), typeof(bool) });
    private static readonly MethodInfo HaveSupportMethod = AccessTools.Method(
        typeof(WearNTear), "HaveSupport", Type.EmptyTypes);

    private sealed class PresenceState
    {
        internal float LastUsed;
        internal float NextCheck;
        internal float LastTrusted = float.NegativeInfinity;
        internal float ValidUntil;
        internal float RequestedAt;
        internal long RequestId;
        internal bool Attended;
        internal bool Nearby;
    }

    private static readonly Dictionary<ZDOID, PresenceState> Presence = new();
    // A grant expires before another request for the same portal can be approved.
    private static readonly Dictionary<ZDOID, double> RemovalGrants = new();
    private static readonly Dictionary<ZRpc, float> LastRemoveRequests = new();
    private static readonly List<ZDOID> ExpiredIds = new();
    private static WearNTear? _authorizedRemoval;
    private static Player? _hammerActor;
    private static long _requestSequence;
    private static float _nextPrune;
    private static Func<Vector3, bool>? _insideActiveWard;
    private static bool _wardQueryFailureLogged;

    private static bool Enabled => PublicPortalConfig.EnablePortalProtection != null &&
                                   PublicPortalConfig.EnablePortalProtection.Value.IsOn();

    internal static void InitializeStuWard()
    {
        _insideActiveWard = null;
        _wardQueryFailureLogged = false;
        if (!Chainloader.PluginInfos.TryGetValue(PortalRulesPlugin.StuWardSoftDependencyGuid, out var plugin)) return;
        try
        {
            MethodInfo? query = plugin.Instance?.GetType().Assembly.GetType("STUWard.WardAccessApi")?.GetMethod(
                "IsInsideActiveWard", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Vector3) }, null);
            if (query == null || query.ReturnType != typeof(bool))
            {
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    "STUWard coverage API is unavailable; PortalRules portal protection remains active inside wards. Update STUWard to enable delegation.");
                return;
            }
            _insideActiveWard = (Func<Vector3, bool>)Delegate.CreateDelegate(typeof(Func<Vector3, bool>), query);
        }
        catch (Exception exception)
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning($"STUWard coverage integration could not initialize; keeping portal protection: {exception.Message}");
        }
    }

    private static bool IsInsideActiveStuWard(ZDO portal)
    {
        if (_insideActiveWard == null) return false;
        try
        {
            // Cache only the delegate, never coverage: ward enable/radius/destruction
            // changes are read through STUWard's own current spatial index.
            return _insideActiveWard(portal.GetPosition());
        }
        catch (Exception exception)
        {
            if (!_wardQueryFailureLogged)
            {
                _wardQueryFailureLogged = true;
                PortalRulesPlugin.PortalRulesLogger.LogWarning($"STUWard coverage query failed; keeping portal protection: {exception.Message}");
            }
            return false;
        }
    }

    internal static void RegisterPeer(ZNet znet, ZNetPeer peer)
    {
        if (znet == null || peer?.m_rpc == null) return;
        if (znet.IsServer())
        {
            peer.m_rpc.Register<ZDOID, long>(PresenceRequestRpc, OnPresenceRequest);
            peer.m_rpc.Register<ZDOID, bool>(RemoveRequestRpc, OnRemoveRequest);
        }
        else
        {
            peer.m_rpc.Register<ZDOID, long, int>(PresenceResultRpc, OnPresenceResult);
            peer.m_rpc.Register<ZDOID, long, double>(RemoveApprovedRpc, OnRemoveApproved);
            peer.m_rpc.Register(InvalidateRpc, OnInvalidate);
        }
    }

    internal static void Shutdown()
    {
        Presence.Clear();
        RemovalGrants.Clear();
        LastRemoveRequests.Clear();
        ExpiredIds.Clear();
        _authorizedRemoval = null;
        _hammerActor = null;
        _nextPrune = 0f;
        // Do not reuse request IDs: a delayed reply must not match a new request.
    }

    internal static void ForgetPortal(ZDOID id)
    {
        Presence.Remove(id);
        // Keep grants until expiry even if a destruction callback arrives first.
    }

    internal static void ForgetPeer(ZRpc rpc)
    {
        LastRemoveRequests.Remove(rpc);
        InvalidatePresence();
    }

    internal static void InvalidatePresence()
    {
        Presence.Clear();
        if (ZNet.instance == null || !ZNet.instance.IsServer()) return;
        foreach (ZNetPeer peer in ZNet.instance.GetPeers())
        {
            if (peer.IsReady()) peer.m_rpc.Invoke(InvalidateRpc);
        }
    }

    private static void OnInvalidate(ZRpc rpc)
    {
        if (IsServerRpc(rpc)) Presence.Clear();
    }

    internal static void Tick()
    {
        float now = Time.realtimeSinceStartup;
        if (now < _nextPrune) return;
        _nextPrune = now + 5f;
        if (ZNet.instance == null)
        {
            Shutdown();
            _nextPrune = now + 5f;
            return;
        }
        ExpiredIds.Clear();
        foreach (var entry in Presence)
        {
            if (now - entry.Value.LastUsed > IdleLifetimeSeconds) ExpiredIds.Add(entry.Key);
        }
        foreach (ZDOID id in ExpiredIds) Presence.Remove(id);
        ExpiredIds.Clear();
        double worldTime = ZNet.instance.GetTimeSeconds();
        foreach (var entry in RemovalGrants)
        {
            if (worldTime > entry.Value + 1d) ExpiredIds.Add(entry.Key);
        }
        foreach (ZDOID id in ExpiredIds) RemovalGrants.Remove(id);
        ExpiredIds.Clear();
    }

    private static bool IsServerRpc(ZRpc rpc) => ZNet.instance != null &&
        !ZNet.instance.IsServer() && ReferenceEquals(rpc, ZNet.instance.GetServerRPC());

    private static bool IsPlayerPortal(ZDO? zdo)
    {
        return zdo != null && zdo.IsValid() && Game.instance != null &&
               Game.instance.PortalPrefabHash.Contains(zdo.GetPrefab()) &&
               !PublicPortalKinds.IsAdminPortalPrefab(zdo.GetPrefab()) &&
               (PublicPortalData.GetBuilder(zdo).IsValid || zdo.GetLong(ZDOVars.s_creator) != 0L);
    }

    private static bool TryGetServerPortal(ZDOID id, out ZDO zdo, out PortalBuilder builder)
    {
        zdo = ZDOMan.instance?.GetZDO(id)!;
        builder = default;
        return ZNet.instance != null && ZNet.instance.IsServer() &&
               PublicPortalCatalog.IsServerReady && IsPlayerPortal(zdo) &&
               PublicPortalKinds.IsRegisteredPortal(zdo) &&
               PublicPortalCatalog.TryGetAuthoritativeBuilder(id, out builder) && builder.IsValid;
    }

    private static bool IsTrusted(PortalBuilder builder, ZNetPeer? peer)
    {
        bool resolved = peer != null
            ? PublicPortalData.TryGetPeerAccountId(peer, out string account)
            : PublicPortalData.TryGetLocalAccountId(out account);
        return resolved && (string.Equals(account, builder.AccountId, StringComparison.Ordinal) ||
                            ClanPortalAccess.IsRequesterInBuilderPrimaryClan(builder, peer));
    }

    private static bool HasLivingTrustedPlayerNearby(ZDO portal, PortalBuilder builder)
    {
        Vector3 position = portal.GetPosition();
        float rangeSquared = PresenceRadius * PresenceRadius;
        Player? local = Player.m_localPlayer;
        if (local != null && !local.IsDead() &&
            (local.transform.position - position).sqrMagnitude <= rangeSquared && IsTrusted(builder, null))
        {
            return true;
        }
        foreach (ZNetPeer peer in ZNet.instance.GetPeers())
        {
            if (!PublicPortalData.TryGetAuthenticatedPeerCharacterPosition(peer, out Vector3 playerPosition) ||
                (playerPosition - position).sqrMagnitude > rangeSquared ||
                ZDOMan.instance.GetZDO(peer.m_characterID)?.GetBool(ZDOVars.s_dead) != false)
            {
                continue;
            }
            // Distance comes first: no Clan reflection for distant peers.
            if (IsTrusted(builder, peer)) return true;
        }
        return false;
    }

    private static PresenceState? GetPresenceState(ZDOID id, float now)
    {
        if (!Presence.TryGetValue(id, out PresenceState state))
        {
            if (Presence.Count >= MaximumTrackedPortals) return null;
            state = new PresenceState();
            Presence.Add(id, state);
        }
        state.LastUsed = now;
        return state;
    }

    private static int GetServerPresenceFlags(ZDO portal, PortalBuilder builder)
    {
        float now = Time.realtimeSinceStartup;
        PresenceState? state = GetPresenceState(portal.m_uid, now);
        if (state == null) return 0;
        if (now >= state.NextCheck)
        {
            state.NextCheck = now + RefreshSeconds;
            state.Nearby = HasLivingTrustedPlayerNearby(portal, builder);
            if (state.Nearby) state.LastTrusted = now;
        }
        return (now - state.LastTrusted <= PresenceGraceSeconds ? 1 : 0) | (state.Nearby ? 2 : 0);
    }

    private static bool IsAttended(ZDO portal, bool includeDepartureGrace)
    {
        ZNet? znet = ZNet.instance;
        if (znet == null) return false;
        if (znet.IsServer())
        {
            return TryGetServerPortal(portal.m_uid, out ZDO authoritative, out PortalBuilder builder) &&
                   (GetServerPresenceFlags(authoritative, builder) & (includeDepartureGrace ? 1 : 2)) != 0;
        }
        float now = Time.realtimeSinceStartup;
        PresenceState? state = GetPresenceState(portal.m_uid, now);
        if (state == null) return false;
        if (now >= state.NextCheck && znet.GetServerRPC() is ZRpc rpc)
        {
            state.NextCheck = now + RefreshSeconds;
            state.RequestedAt = now;
            state.RequestId = ++_requestSequence;
            rpc.Invoke(PresenceRequestRpc, portal.m_uid, state.RequestId);
        }
        // An unknown/expired server decision cannot make a portal vulnerable.
        return (includeDepartureGrace ? state.Attended : state.Nearby) && now < state.ValidUntil;
    }

    private static void OnPresenceRequest(ZRpc rpc, ZDOID id, long requestId)
    {
        if (!Enabled || !TryGetServerPortal(id, out ZDO portal, out PortalBuilder builder)) return;
        ZNetPeer? peer = PublicPortalData.FindPeer(ZNet.instance, rpc);
        if (peer == null || !peer.IsReady() || portal.GetOwner() != peer.m_uid) return;
        // Bit 0 includes the combat departure grace; bit 1 is current presence.
        rpc.Invoke(PresenceResultRpc, id, requestId, GetServerPresenceFlags(portal, builder));
    }

    private static void OnPresenceResult(ZRpc rpc, ZDOID id, long requestId, int flags)
    {
        if (!Enabled || !IsServerRpc(rpc) || !Presence.TryGetValue(id, out PresenceState state) ||
            state.RequestId != requestId || flags < 0 || flags > 3 || flags == 2) return;
        ZDO? portal = ZDOMan.instance?.GetZDO(id);
        float now = Time.realtimeSinceStartup;
        if (!IsPlayerPortal(portal) || portal!.GetOwner() != ZDOMan.GetSessionID() ||
            now - state.RequestedAt >= ResponseLifetimeSeconds) return;
        state.Attended = (flags & 1) != 0;
        state.Nearby = (flags & 2) != 0;
        // Bound age from request time rather than extending a delayed reply.
        state.ValidUntil = state.RequestedAt + ResponseLifetimeSeconds;
    }

    private static bool ShouldSkipSupportDamage(bool hasSupport, WearNTear wear)
    {
        if (hasSupport || !Enabled) return hasSupport;
        ZNetView? view = wear.GetComponent<ZNetView>();
        if (view == null || !view.IsValid() || !view.IsOwner() || !IsPlayerPortal(view.GetZDO()))
            return hasSupport;
        if (IsInsideActiveStuWard(view.GetZDO())) return hasSupport;
        // Unlike attacks, collapse has no departure grace. Unknown server state
        // protects until a fresh reply; the normal short presence cache still applies.
        return !IsAttended(view.GetZDO(), includeDepartureGrace: false);
    }

    [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.UpdateWear), typeof(float))]
    private static class SupportProtectionPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            int index = -1;
            for (int i = 0; i < code.Count; i++)
            {
                if (!code[i].Calls(HaveSupportMethod)) continue;
                if (index != -1 || i + 3 >= code.Count ||
                    (code[i + 1].opcode != OpCodes.Brtrue && code[i + 1].opcode != OpCodes.Brtrue_S) ||
                    code[i + 2].opcode != OpCodes.Ldc_R4 || !Equals(code[i + 2].operand, 100f))
                {
                    PortalRulesPlugin.PortalRulesLogger.LogWarning(
                        "Portal support protection is unavailable: unexpected UpdateWear collapse branch. Keeping normal support wear.");
                    return code;
                }
                index = i;
            }
            if (index < 0)
            {
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    "Portal support protection is unavailable: UpdateWear collapse branch not found. Keeping normal support wear.");
                return code;
            }
            // Keep the actual HaveSupport call and all its labels. Other calls to
            // HaveSupport (notably inside UpdateSupport) remain completely unchanged.
            code.InsertRange(index + 1, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(PublicPortalProtection),
                    nameof(ShouldSkipSupportDamage), new[] { typeof(bool), typeof(WearNTear) }))
            });
            return code;
        }
    }

    private enum AttackSource { Environment, PlayerOrTamed, Hostile, Unresolved }

    private static AttackSource ClassifyAttack(HitData hit)
    {
        Character? attacker = hit.GetAttacker();
        if (attacker != null)
        {
            if (attacker is Player || attacker.IsTamed()) return AttackSource.PlayerOrTamed;
            return attacker.GetComponent<MonsterAI>() != null ? AttackSource.Hostile : AttackSource.Unresolved;
        }
        if (hit.HaveAttacker())
        {
            ZDO? attackerZdo = ZDOMan.instance?.GetZDO(hit.m_attacker);
            GameObject? prefab = attackerZdo != null ? ZNetScene.instance?.GetPrefab(attackerZdo.GetPrefab()) : null;
            if (prefab != null)
            {
                if (prefab.GetComponent<Player>() != null || attackerZdo!.GetBool(ZDOVars.s_tamed))
                    return AttackSource.PlayerOrTamed;
                if (prefab.GetComponent<MonsterAI>() != null) return AttackSource.Hostile;
            }
            return AttackSource.Unresolved;
        }
        // Missing character instances are not evidence of environmental damage.
        if (hit.m_hitType == HitData.HitType.PlayerHit) return AttackSource.PlayerOrTamed;
        if (hit.m_hitType == HitData.HitType.EnemyHit) return AttackSource.Unresolved;
        return AttackSource.Environment;
    }

    private static void RequestRemoval(ZDO portal)
    {
        ZNet? znet = ZNet.instance;
        if (znet == null) return;
        if (znet.IsServer())
        {
            TryApproveRemoval(portal.m_uid, null, Player.m_debugMode, out PortalRulesMessage message);
            ShowMessage(message);
        }
        else if (znet.GetServerRPC() is ZRpc rpc)
        {
            rpc.Invoke(RemoveRequestRpc, portal.m_uid, Player.m_debugMode);
        }
    }

    private static void OnRemoveRequest(ZRpc rpc, ZDOID id, bool debugEnabled)
    {
        if (!Enabled || ZNet.instance == null || !ZNet.instance.IsServer()) return;
        ZNetPeer? peer = PublicPortalData.FindPeer(ZNet.instance, rpc);
        if (peer == null || !peer.IsReady()) return;
        float now = Time.realtimeSinceStartup;
        if (LastRemoveRequests.TryGetValue(rpc, out float last) && now - last < 0.25f) return;
        LastRemoveRequests[rpc] = now;
        TryApproveRemoval(id, peer, debugEnabled, out PortalRulesMessage message);
        PublicPortalServerPolicy.SendPolicyMessage(peer, message);
    }

    private static bool TryApproveRemoval(ZDOID id, ZNetPeer? requester, bool debugEnabled,
        out PortalRulesMessage message)
    {
        message = new PortalRulesMessage("$sighsorry_portalrules_protected_removal_retry");
        if (!Enabled || !TryGetServerPortal(id, out ZDO portal, out PortalBuilder builder) ||
            !PublicPortalInteraction.TryValidatePortalInteraction(portal, requester, out message)) return false;
        bool admin = debugEnabled && (requester != null
            ? PublicPortalData.IsPeerAdmin(ZNet.instance, requester)
            : PortalRulesPlugin.IsAdmin);
        if (!admin && !IsTrusted(builder, requester))
        {
            message = new PortalRulesMessage("$sighsorry_portalrules_protected_removal_denied");
            return false;
        }
        message = new PortalRulesMessage("$sighsorry_portalrules_protected_removal_retry");
        double now = ZNet.instance.GetTimeSeconds();
        if (RemovalGrants.TryGetValue(id, out double previous) && now <= previous + 1d) return false;
        if (RemovalGrants.Count >= MaximumTrackedPortals) return false;
        long actorId = requester?.m_uid ?? ZDOMan.GetSessionID();
        long ownerId = portal.GetOwner();
        double expires = now + ResponseLifetimeSeconds;
        if (ownerId == ZDOMan.GetSessionID())
        {
            RemovalGrants[id] = expires;
            if (!ExecuteApprovedRemoval(id, actorId)) return false;
        }
        else
        {
            ZNetPeer? owner = null;
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer.IsReady() && peer.m_uid == ownerId) { owner = peer; break; }
            }
            if (owner == null) return false;
            RemovalGrants[id] = expires;
            owner.m_rpc.Invoke(RemoveApprovedRpc, id, actorId, expires);
        }
        message = PortalRulesMessage.Empty;
        return true;
    }

    private static void OnRemoveApproved(ZRpc rpc, ZDOID id, long actorId, double expires)
    {
        if (!Enabled || !IsServerRpc(rpc) || double.IsNaN(expires) || double.IsInfinity(expires)) return;
        double now = ZNet.instance.GetTimeSeconds();
        if (now > expires || expires - now > ResponseLifetimeSeconds + 1d ||
            RemovalGrants.ContainsKey(id) || RemovalGrants.Count >= MaximumTrackedPortals) return;
        RemovalGrants[id] = expires;
        ExecuteApprovedRemoval(id, actorId);
    }

    private static bool ExecuteApprovedRemoval(ZDOID id, long actorId)
    {
        GameObject? instance = ZNetScene.instance?.FindInstance(id);
        ZNetView? view = instance != null ? instance.GetComponent<ZNetView>() : null;
        WearNTear? wear = instance != null ? instance.GetComponent<WearNTear>() : null;
        if (wear == null || view == null || !view.IsValid() || !view.IsOwner() ||
            !IsPlayerPortal(view.GetZDO()) || view.GetZDO().GetFloat(ZDOVars.s_health, wear.m_health) <= 0f) return false;
        WearNTear? previous = _authorizedRemoval;
        try
        {
            _authorizedRemoval = wear;
            // Preserve the authenticated hammer user's sender for other Harmony
            // protections (e.g. StuWard), rather than impersonating the object owner.
            RemoveRpcMethod.Invoke(wear, new object[] { actorId, false });
        }
        finally { _authorizedRemoval = previous; }
        return true;
    }

    internal static void RemoveForServerPolicy(WearNTear wear)
    {
        // Only called by the existing server-owned installation/admin cleanup.
        // A remote caller's blockDrop=true is never an authorization token.
        WearNTear? previous = _authorizedRemoval;
        try
        {
            _authorizedRemoval = wear;
            wear.Remove(blockDrop: true);
        }
        finally { _authorizedRemoval = previous; }
    }

    private static void ShowMessage(PortalRulesMessage message)
    {
        if (!message.IsEmpty && Player.m_localPlayer != null)
            Player.m_localPlayer.Message(MessageHud.MessageType.Center, message.Localize());
    }

    [HarmonyPatch(typeof(Player), "RemovePiece")]
    private static class HammerContextPatch
    {
        private static void Prefix(Player __instance, out Player? __state)
        {
            __state = _hammerActor;
            _hammerActor = __instance == Player.m_localPlayer ? __instance : null;
        }
        private static Exception? Finalizer(Exception? __exception, Player? __state)
        {
            _hammerActor = __state;
            return __exception;
        }
    }

    [HarmonyPatch(typeof(WearNTear), nameof(WearNTear.Remove), typeof(bool))]
    private static class RemoveRequestPatch
    {
        private static bool Prefix(WearNTear __instance, ZNetView ___m_nview)
        {
            if (!Enabled || ReferenceEquals(_authorizedRemoval, __instance) || ___m_nview == null ||
                !___m_nview.IsValid() || !IsPlayerPortal(___m_nview.GetZDO()) ||
                IsInsideActiveStuWard(___m_nview.GetZDO())) return true;
            // Only vanilla's completed hammer validation can initiate a request.
            if (_hammerActor != null) RequestRemoval(___m_nview.GetZDO());
            return false;
        }
    }

    [HarmonyPatch(typeof(WearNTear), "RPC_Remove", typeof(long), typeof(bool))]
    private static class RemoveAuthorityPatch
    {
        private static bool Prefix(WearNTear __instance, ZNetView ___m_nview)
        {
            return !Enabled || ReferenceEquals(_authorizedRemoval, __instance) || ___m_nview == null ||
                   !___m_nview.IsValid() || !IsPlayerPortal(___m_nview.GetZDO()) ||
                   IsInsideActiveStuWard(___m_nview.GetZDO());
        }
    }

    [HarmonyPatch(typeof(WearNTear), "RPC_Damage", typeof(long), typeof(HitData))]
    private static class AttackProtectionPatch
    {
        private static bool Prefix(HitData hit, ZNetView ___m_nview)
        {
            if (!Enabled || ___m_nview == null || !___m_nview.IsValid() || !___m_nview.IsOwner() ||
                !IsPlayerPortal(___m_nview.GetZDO())) return true;
            if (IsInsideActiveStuWard(___m_nview.GetZDO())) return true;
            AttackSource source = ClassifyAttack(hit);
            return source == AttackSource.Environment ||
                   (source == AttackSource.Hostile && IsAttended(___m_nview.GetZDO(), includeDepartureGrace: true));
        }
    }
}
