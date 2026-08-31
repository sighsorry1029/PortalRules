using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Bootstrap;
using UnityEngine;

namespace PortalRules;

internal static class PublicPortalServerPolicy
{
    private sealed class PendingRemoval
    {
        public readonly ZDOID PortalId;
        public int Attempts;
        public float NextAttemptAt;

        public PendingRemoval(ZDOID portalId)
        {
            PortalId = portalId;
        }
    }

    private readonly struct QuotaState
    {
        public readonly int EffectiveLimit;
        public readonly int CurrentCount;
        public readonly int EffectiveInviteLimit;
        public readonly int CurrentInviteCount;

        public QuotaState(
            int effectiveLimit,
            int currentCount,
            int effectiveInviteLimit,
            int currentInviteCount)
        {
            EffectiveLimit = effectiveLimit;
            CurrentCount = currentCount;
            EffectiveInviteLimit = effectiveInviteLimit;
            CurrentInviteCount = currentInviteCount;
        }

        public bool Matches(QuotaState other)
        {
            return EffectiveLimit == other.EffectiveLimit &&
                   CurrentCount == other.CurrentCount &&
                   EffectiveInviteLimit == other.EffectiveInviteLimit &&
                   CurrentInviteCount == other.CurrentInviteCount;
        }
    }

    private const string PolicyMessageRpc = "sighsorry.PortalRules.PolicyMessage.v2";
    private const string QuotaStateRpc = "sighsorry.PortalRules.QuotaState.v2";
    private const string InfinityHammerPluginGuid = "infinity_hammer";
    private const byte QuotaStateFormatVersion = 2;
    private const int MaximumRemovalAttemptsBeforeWarning = 5;
    private const int MaximumQueuedRemovalsPerFrame = 10;

    private static readonly Queue<PendingRemoval> PendingRemovals = new();
    private static readonly Dictionary<ZDOID, PendingRemoval> DeferredRemovals = new();
    private static readonly HashSet<int> CountedPortalHashes = new();
    private static readonly Dictionary<ZRpc, QuotaState> LastSentQuotaStates = new();

    private static Game? _registeredGame;
    private static ZNet? _sessionZNet;
    private static string _cachedCountedPrefabSetting = "";
    private static long _sessionGeneration;
    private static bool _hasClientQuotaState;
    private static int _clientEffectiveLimit = -1;
    private static int _clientCurrentCount;
    private static MethodInfo? _infinityHammerNoCreatorGetter;
    private static MethodInfo? _infinityHammerSelectionGetter;
    private static Type? _infinityHammerObjectSelectionType;

    internal static void BeginNetworkSession(ZNet? znet)
    {
        if (znet == null || ReferenceEquals(_sessionZNet, znet))
        {
            return;
        }

        ResetSessionState();
        _sessionZNet = znet;
    }

    internal static void Register(Game game)
    {
        if (game == null ||
            ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            ReferenceEquals(_registeredGame, game))
        {
            return;
        }

        _registeredGame = game;
        _sessionZNet = ZNet.instance;
        long generation = _sessionGeneration;
        game.StartCoroutine(ServerRemovalLoop(game, _sessionZNet, generation));
    }

    internal static void Shutdown()
    {
        ResetSessionState();
        _sessionZNet = null;
    }

    internal static void RegisterPeer(ZNet znet, ZNetPeer peer)
    {
        if (znet == null || peer?.m_rpc == null || znet.IsServer())
        {
            return;
        }

        ResetClientQuotaState();
        peer.m_rpc.Register<ZPackage>(PolicyMessageRpc, OnPolicyMessage);
        peer.m_rpc.Register<ZPackage>(QuotaStateRpc, OnQuotaState);
    }

    internal static void ForgetPeer(ZRpc? rpc)
    {
        if (rpc == null)
        {
            return;
        }

        LastSentQuotaStates.Remove(rpc);
        if (ZNet.instance != null &&
            !ZNet.instance.IsServer() &&
            ReferenceEquals(rpc, ZNet.instance.GetServerRPC()))
        {
            ResetClientQuotaState();
        }
    }

    internal static void SendQuotaState(ZNetPeer? peer, bool force = false)
    {
        if (ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            !PublicPortalCatalog.IsServerReady ||
            peer?.m_rpc == null ||
            !peer.IsReady() ||
            !peer.m_rpc.IsConnected() ||
            !PublicPortalCatalog.TryEnsurePeerIdentity(peer, out string steamId))
        {
            return;
        }

        QuotaState state = BuildQuotaState(steamId);
        if (!force &&
            LastSentQuotaStates.TryGetValue(peer.m_rpc, out QuotaState previous) &&
            previous.Matches(state))
        {
            return;
        }

        ZPackage package = new();
        package.Write(QuotaStateFormatVersion);
        package.Write(state.EffectiveLimit);
        package.Write(state.CurrentCount);
        package.Write(state.EffectiveInviteLimit);
        package.Write(state.CurrentInviteCount);
        peer.m_rpc.Invoke(QuotaStateRpc, package);
        LastSentQuotaStates[peer.m_rpc] = state;
    }

    internal static void BroadcastQuotaStates()
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer())
        {
            return;
        }

        foreach (ZNetPeer peer in ZNet.instance.GetPeers())
        {
            SendQuotaState(peer);
        }

        RefreshLocalServerQuotaState();
    }

    internal static void NotifyQuotaConfigurationChanged()
    {
        RefreshCountedPortalHashes();
        if (ZNet.instance != null && ZNet.instance.IsServer())
        {
            PublicPortalCatalog.RefreshCountedPortalConfiguration();
        }
    }

    private static QuotaState BuildQuotaState(string steamId)
    {
        int effectiveLimit = PublicPortalConfig.EnableAccountPortalLimit.Value.IsOff()
            ? -1
            : PortalAccountStore.GetEffectivePortalLimit(
                steamId,
                PublicPortalConfig.MaxPortalsPerAccount.Value);
        int currentCount = Math.Min(
            PublicPortalData.MaximumPortalCount,
            PublicPortalCatalog.GetBuilderPortalCount(steamId));
        int effectiveInviteLimit = PortalAccountStore.GetEffectiveInvitePortalLimit(
            steamId,
            PublicPortalConfig.MaxInvitePortalsPerAccount.Value);
        int currentInviteCount = Math.Min(
            PublicPortalData.MaximumPortalCount,
            PublicPortalCatalog.GetBuilderInvitePortalCount(steamId));
        return new QuotaState(
            effectiveLimit,
            currentCount,
            effectiveInviteLimit,
            currentInviteCount);
    }

    internal static void RefreshLocalServerQuotaState()
    {
        if (ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            !PublicPortalCatalog.IsServerReady ||
            !PublicPortalCatalog.TryEnsureLocalIdentity(out string steamId))
        {
            return;
        }

        QuotaState state = BuildQuotaState(steamId);
        _clientEffectiveLimit = state.EffectiveLimit;
        _clientCurrentCount = state.CurrentCount;
        _hasClientQuotaState = true;
    }

    internal static bool TryGetInviteQuotaState(
        string steamId,
        out int currentCount,
        out int effectiveLimit)
    {
        currentCount = 0;
        effectiveLimit = 0;
        if (ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            !PublicPortalCatalog.IsServerReady ||
            !PublicPortalData.TryNormalizeSteamId64(steamId, out string canonicalSteamId))
        {
            return false;
        }

        QuotaState state = BuildQuotaState(canonicalSteamId);
        currentCount = state.CurrentInviteCount;
        effectiveLimit = state.EffectiveInviteLimit;
        return true;
    }

    internal static bool TryGetClanQuotaState(
        string clanId,
        out int currentCount,
        out int effectiveLimit)
    {
        currentCount = 0;
        effectiveLimit = Math.Max(
            -1,
            Math.Min(
                PublicPortalData.MaximumPortalLimit,
                PublicPortalConfig.MaxClanPortalsPerClan.Value));
        string normalizedClanId = (clanId ?? "").Trim();
        if (ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            !PublicPortalCatalog.IsServerReady ||
            normalizedClanId.Length == 0)
        {
            return false;
        }

        currentCount = Math.Min(
            PublicPortalData.MaximumPortalCount,
            PublicPortalCatalog.GetClanPortalCount(normalizedClanId));
        return true;
    }

    internal static bool IsCountedPortalPrefab(int prefabHash)
    {
        RefreshCountedPortalHashes();
        return prefabHash != 0 &&
               !PublicPortalKinds.IsAdminPortalPrefab(prefabHash) &&
               CountedPortalHashes.Contains(prefabHash);
    }

    internal static bool TryAcceptNewPortal(
        ZDO zdo,
        PortalBuilder builder,
        ZNetPeer? sourcePeer)
    {
        if (zdo == null ||
            !IsCountedPortalPrefab(zdo.GetPrefab()) ||
            PublicPortalConfig.EnableAccountPortalLimit.Value.IsOff())
        {
            return true;
        }

        if (!PublicPortalData.TryNormalizeSteamId64(
                builder.AccountId,
                out string steamId))
        {
            RejectUnverifiedPortal(zdo, sourcePeer);
            return false;
        }

        int limit = PortalAccountStore.GetEffectivePortalLimit(
            steamId,
            PublicPortalConfig.MaxPortalsPerAccount.Value);
        if (limit < 0)
        {
            return true;
        }

        int currentCount = PublicPortalCatalog.GetBuilderPortalCount(steamId);
        if (currentCount < limit)
        {
            return true;
        }

        if (PublicPortalCatalog.MarkPendingRemoval(zdo.m_uid))
        {
            QueueRemoval(zdo.m_uid);
        }

        SendPolicyMessage(
            sourcePeer,
            new PortalRulesMessage(
                "$sighsorry_portalrules_portal_limit_reached",
                currentCount.ToString(CultureInfo.InvariantCulture),
                limit.ToString(CultureInfo.InvariantCulture)));
        SendQuotaState(sourcePeer, force: true);
        PortalRulesPlugin.PortalRulesLogger.LogWarning(
            $"Rejected portal {zdo.m_uid} from {steamId}: " +
            $"account already has {currentCount}/{limit} counted portals.");
        return false;
    }

    internal static void RejectUnverifiedPortal(ZDO zdo, ZNetPeer? sourcePeer)
    {
        RejectPortal(
            zdo,
            sourcePeer,
            new PortalRulesMessage(
                "$sighsorry_portalrules_portal_placement_account_unverified"));
    }

    internal static void RejectPortal(
        ZDO zdo,
        ZNetPeer? sourcePeer,
        PortalRulesMessage message)
    {
        if (zdo == null)
        {
            return;
        }

        if (PublicPortalCatalog.MarkPendingRemoval(zdo.m_uid))
        {
            QueueRemoval(zdo.m_uid);
        }

        SendPolicyMessage(sourcePeer, message);
        PortalRulesPlugin.PortalRulesLogger.LogWarning(
            $"Rejected portal creation {zdo.m_uid} ({message.Token}).");
    }

    internal static bool QueueAdminPortalRemoval(ZDO zdo)
    {
        if (zdo == null ||
            ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            !zdo.IsValid() ||
            !PublicPortalInteraction.IsAdminPortal(zdo) ||
            !PublicPortalCatalog.MarkPendingRemoval(zdo.m_uid))
        {
            return false;
        }

        QueueRemoval(zdo.m_uid);
        PublicPortalCatalog.RefreshAndBroadcast();
        PublicPortalTaggedConnections.RefreshConnections();
        return true;
    }

    internal static bool ShouldBlockLocalPlacement(Piece piece, out string message)
    {
        message = "";
        if (piece == null ||
            PublicPortalConfig.EnableAccountPortalLimit.Value.IsOff() ||
            !IsCountedPortalPrefab(Utils.GetPrefabName(piece.gameObject).GetStableHashCode()))
        {
            return false;
        }

        if (ZNet.instance != null &&
            ZNet.instance.IsServer() &&
            IsInfinityHammerNoCreatorActive())
        {
            return false;
        }

        string syncMessage = PortalRulesLocalization.Translate(
            "$sighsorry_portalrules_portal_limit_syncing");
        int limit;
        int currentCount;
        if (ZNet.instance != null && ZNet.instance.IsServer())
        {
            if (!PublicPortalCatalog.IsServerReady ||
                !PublicPortalCatalog.TryEnsureLocalIdentity(out string steamId))
            {
                message = syncMessage;
                return true;
            }

            limit = PortalAccountStore.GetEffectivePortalLimit(
                steamId,
                PublicPortalConfig.MaxPortalsPerAccount.Value);
            currentCount = PublicPortalCatalog.GetBuilderPortalCount(steamId);
        }
        else
        {
            if (!_hasClientQuotaState)
            {
                message = syncMessage;
                return true;
            }

            limit = _clientEffectiveLimit;
            currentCount = _clientCurrentCount;
        }

        if (limit < 0)
        {
            return false;
        }

        if (currentCount < limit)
        {
            return false;
        }

        message = PortalRulesLocalization.Translate(
            "$sighsorry_portalrules_portal_limit_placement_blocked",
            limit.ToString(CultureInfo.InvariantCulture));
        return true;
    }

    private static bool IsInfinityHammerNoCreatorActive()
    {
        try
        {
            if (!TryBindInfinityHammerCompat())
            {
                return false;
            }

            if (_infinityHammerNoCreatorGetter?.Invoke(null, null) is not true)
            {
                return false;
            }

            object? selection = _infinityHammerSelectionGetter?.Invoke(null, null);
            return selection != null &&
                   _infinityHammerObjectSelectionType!.IsInstanceOfType(selection);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryBindInfinityHammerCompat()
    {
        if (_infinityHammerNoCreatorGetter != null &&
            _infinityHammerSelectionGetter != null &&
            _infinityHammerObjectSelectionType != null)
        {
            return true;
        }

        if (!Chainloader.PluginInfos.TryGetValue(
                InfinityHammerPluginGuid,
                out var pluginInfo))
        {
            return false;
        }

        Assembly? assembly = pluginInfo.Instance?.GetType().Assembly;
        Type? configurationType = assembly?.GetType(
            "InfinityHammer.Configuration",
            throwOnError: false,
            ignoreCase: false);
        Type? selectionType = assembly?.GetType(
            "InfinityHammer.Selection",
            throwOnError: false,
            ignoreCase: false);
        Type? objectSelectionType = assembly?.GetType(
            "InfinityHammer.ObjectSelection",
            throwOnError: false,
            ignoreCase: false);
        MethodInfo? noCreatorGetter = configurationType?
            .GetProperty(
                "NoCreator",
                BindingFlags.Public | BindingFlags.Static)?
            .GetGetMethod(nonPublic: false);
        MethodInfo? selectionGetter = selectionType?.GetMethod(
            "Get",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            Type.EmptyTypes,
            modifiers: null);
        if (noCreatorGetter?.ReturnType != typeof(bool) ||
            selectionGetter == null ||
            objectSelectionType == null)
        {
            return false;
        }

        _infinityHammerNoCreatorGetter = noCreatorGetter;
        _infinityHammerSelectionGetter = selectionGetter;
        _infinityHammerObjectSelectionType = objectSelectionType;
        return true;
    }

    private static IEnumerator ServerRemovalLoop(
        Game game,
        ZNet sessionZNet,
        long generation)
    {
        while (IsActiveSession(game, sessionZNet, generation))
        {
            PromoteDeferredRemovals();
            if (PendingRemovals.Count > 0)
            {
                ProcessPendingRemovals(MaximumQueuedRemovalsPerFrame);
                yield return null;
                continue;
            }

            yield return new WaitForSeconds(1f);
        }
    }

    private static void QueueRemoval(ZDOID portalId)
    {
        if (portalId.IsNone())
        {
            return;
        }

        PendingRemovals.Enqueue(new PendingRemoval(portalId));
    }

    private static void ProcessPendingRemovals(int maximumCount)
    {
        int attemptsThisPass = Math.Min(
            Math.Max(0, maximumCount),
            PendingRemovals.Count);
        for (int processed = 0; processed < attemptsThisPass; processed++)
        {
            PendingRemoval removal = PendingRemovals.Dequeue();

            try
            {
                DestroyPortal(removal);
            }
            catch (Exception ex)
            {
                removal.Attempts++;
                removal.NextAttemptAt =
                    Time.realtimeSinceStartup +
                    Math.Min(
                        60f,
                        (float)Math.Pow(2d, Math.Min(removal.Attempts, 6)));
                DeferredRemovals[removal.PortalId] = removal;

                if (removal.Attempts == 1)
                {
                    PortalRulesPlugin.PortalRulesLogger.LogError(
                        $"Failed to remove portal {removal.PortalId}: {ex.Message}");
                }
                else if (removal.Attempts == MaximumRemovalAttemptsBeforeWarning)
                {
                    PortalRulesPlugin.PortalRulesLogger.LogWarning(
                        $"Portal {removal.PortalId} removal has failed repeatedly; " +
                        $"the server will retry with backoff. Last error: {ex.Message}");
                }
            }
        }
    }

    private static void PromoteDeferredRemovals()
    {
        if (DeferredRemovals.Count == 0)
        {
            return;
        }

        float now = Time.realtimeSinceStartup;
        List<PendingRemoval> ready = new();
        foreach (PendingRemoval removal in DeferredRemovals.Values)
        {
            if (removal.NextAttemptAt <= now)
            {
                ready.Add(removal);
            }
        }

        foreach (PendingRemoval removal in ready)
        {
            DeferredRemovals.Remove(removal.PortalId);
            PendingRemovals.Enqueue(removal);
        }
    }

    private static void DestroyPortal(PendingRemoval removal)
    {
        ZDOMan? zdoMan = ZDOMan.instance;
        if (zdoMan == null)
        {
            throw new InvalidOperationException("ZDO manager is unavailable.");
        }

        ZDO? zdo = zdoMan.GetZDO(removal.PortalId);
        if (zdo == null || !zdo.IsValid())
        {
            PublicPortalCatalog.NotifyPortalDestroyed(removal.PortalId);
            return;
        }

        zdo.SetOwner(ZDOMan.GetSessionID());
        ZNetScene? znetScene = ZNetScene.instance;
        GameObject? instance = znetScene?.FindInstance(removal.PortalId);
        if (instance != null)
        {
            try
            {
                WearNTear? wearNTear = instance.GetComponent<WearNTear>();
                ZNetView? instanceView = instance.GetComponent<ZNetView>();
                bool hasValidView = instanceView != null && instanceView.IsValid();
                if (wearNTear != null && hasValidView)
                {
                    wearNTear.Remove(blockDrop: true);
                }
                else
                {
                    znetScene!.Destroy(instance);
                    if (!hasValidView)
                    {
                        zdoMan.DestroyZDO(zdo);
                    }
                }
            }
            catch (Exception ex)
            {
                PortalRulesPlugin.PortalRulesLogger.LogError(
                    $"Portal instance removal failed for {removal.PortalId}; " +
                    $"falling back to ZDO removal: {ex.Message}");
                zdoMan.DestroyZDO(zdo);
            }

            return;
        }

        zdoMan.DestroyZDO(zdo);
    }

    internal static void SendPolicyMessage(
        ZNetPeer? peer,
        PortalRulesMessage message)
    {
        if (message.IsEmpty)
        {
            return;
        }

        if (peer != null)
        {
            if (peer.m_rpc != null && peer.m_rpc.IsConnected())
            {
                ZPackage package = new();
                message.Write(package);
                peer.m_rpc.Invoke(PolicyMessageRpc, package);
            }

            return;
        }

        Player.m_localPlayer?.Message(
            MessageHud.MessageType.Center,
            message.Localize());
    }

    private static void OnPolicyMessage(ZRpc rpc, ZPackage package)
    {
        if (ZNet.instance == null ||
            ZNet.instance.IsServer() ||
            !ReferenceEquals(rpc, ZNet.instance.GetServerRPC()))
        {
            return;
        }

        try
        {
            if (!PortalRulesMessage.TryRead(package, out PortalRulesMessage message))
            {
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    "Ignored a malformed PortalRules policy message from the server.");
                return;
            }

            Player.m_localPlayer?.Message(
                MessageHud.MessageType.Center,
                message.Localize());
        }
        catch (Exception ex)
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                $"Failed to read a PortalRules policy message: {ex.Message}");
        }
    }

    private static void OnQuotaState(ZRpc rpc, ZPackage package)
    {
        if (ZNet.instance == null ||
            ZNet.instance.IsServer() ||
            !ReferenceEquals(rpc, ZNet.instance.GetServerRPC()))
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                "Ignored portal quota state from a non-server connection.");
            return;
        }

        try
        {
            byte version = package.ReadByte();
            int effectiveLimit = package.ReadInt();
            int currentCount = package.ReadInt();
            int effectiveInviteLimit = package.ReadInt();
            int currentInviteCount = package.ReadInt();
            if (version != QuotaStateFormatVersion ||
                effectiveLimit < -1 ||
                effectiveLimit > PublicPortalData.MaximumPortalLimit ||
                currentCount < 0 ||
                currentCount > PublicPortalData.MaximumPortalCount ||
                effectiveInviteLimit < -1 ||
                effectiveInviteLimit > PublicPortalData.MaximumPortalLimit ||
                currentInviteCount < 0 ||
                currentInviteCount > PublicPortalData.MaximumPortalCount)
            {
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    "Ignored malformed portal quota state from the server.");
                return;
            }

            _clientEffectiveLimit = effectiveLimit;
            _clientCurrentCount = currentCount;
            _hasClientQuotaState = true;
        }
        catch (Exception ex)
        {
            PortalRulesPlugin.PortalRulesLogger.LogError(
                $"Failed to read portal quota state: {ex.Message}");
        }
    }

    private static void RefreshCountedPortalHashes()
    {
        string setting = PublicPortalConfig.CountedPortalPrefabs.Value ?? "";
        if (string.Equals(
                setting,
                _cachedCountedPrefabSetting,
                StringComparison.Ordinal))
        {
            return;
        }

        _cachedCountedPrefabSetting = setting;
        CountedPortalHashes.Clear();
        foreach (string prefabName in setting.Split(','))
        {
            string trimmedName = prefabName.Trim();
            if (!string.IsNullOrWhiteSpace(trimmedName))
            {
                CountedPortalHashes.Add(trimmedName.GetStableHashCode());
            }
        }
    }

    private static bool IsActiveSession(
        Game game,
        ZNet sessionZNet,
        long generation)
    {
        return generation == _sessionGeneration &&
               ReferenceEquals(_registeredGame, game) &&
               ReferenceEquals(_sessionZNet, sessionZNet) &&
               ReferenceEquals(ZNet.instance, sessionZNet) &&
               sessionZNet.IsServer();
    }

    private static void ResetSessionState()
    {
        _sessionGeneration++;
        _registeredGame = null;
        PendingRemovals.Clear();
        DeferredRemovals.Clear();
        LastSentQuotaStates.Clear();
        ResetClientQuotaState();
    }

    private static void ResetClientQuotaState()
    {
        _hasClientQuotaState = false;
        _clientEffectiveLimit = -1;
        _clientCurrentCount = 0;
    }
}
