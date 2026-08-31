using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace PortalRules;

internal readonly struct PublicPortalCatalogEntry
{
    public readonly ZDOID Id;
    public readonly string FavoriteId;
    public readonly int PrefabHash;
    public readonly bool AllowsAllItems;
    public readonly Vector3 Position;
    public readonly Quaternion Rotation;
    public readonly string Tag;
    public readonly PublicPortalAccessMode AccessMode;
    public readonly PortalOwner Owner;
    public readonly int MyPortalOrdinal;
    public readonly int MyPortalLimit;
    public readonly int ModeOrdinal;
    public readonly int ModeCurrent;
    public readonly int ModeLimit;
    public readonly long InviteDepartureCooldownUntilUtc;
    public readonly long InviteArrivalCooldownUntilUtc;

    public PublicPortalCatalogEntry(
        ZDOID id,
        string favoriteId,
        int prefabHash,
        bool allowsAllItems,
        Vector3 position,
        Quaternion rotation,
        string tag,
        PublicPortalAccessMode accessMode,
        PortalOwner owner,
        int myPortalOrdinal = 0,
        int myPortalLimit = 0,
        int modeOrdinal = 0,
        int modeCurrent = 0,
        int modeLimit = 0,
        long inviteDepartureCooldownUntilUtc = 0L,
        long inviteArrivalCooldownUntilUtc = 0L)
    {
        Id = id;
        FavoriteId = PublicPortalData.TryNormalizeFavoriteId(
            favoriteId,
            out string normalizedFavoriteId)
            ? normalizedFavoriteId
            : "";
        PrefabHash = prefabHash;
        AllowsAllItems = allowsAllItems;
        Position = position;
        Rotation = rotation;
        Tag = tag ?? "";
        AccessMode = accessMode;
        Owner = owner;
        MyPortalOrdinal = Math.Max(0, myPortalOrdinal);
        MyPortalLimit = Math.Max(-1, myPortalLimit);
        ModeOrdinal = Math.Max(0, modeOrdinal);
        ModeCurrent = Math.Max(0, modeCurrent);
        ModeLimit = Math.Max(-1, modeLimit);
        InviteDepartureCooldownUntilUtc = Math.Max(
            0L,
            inviteDepartureCooldownUntilUtc);
        InviteArrivalCooldownUntilUtc = Math.Max(
            0L,
            inviteArrivalCooldownUntilUtc);
    }

    public bool HasSameContent(PublicPortalCatalogEntry other)
    {
        return Id == other.Id &&
               string.Equals(FavoriteId, other.FavoriteId, StringComparison.Ordinal) &&
               PrefabHash == other.PrefabHash &&
               AllowsAllItems == other.AllowsAllItems &&
               Position == other.Position &&
               Rotation == other.Rotation &&
               string.Equals(Tag, other.Tag, StringComparison.Ordinal) &&
               AccessMode == other.AccessMode &&
               string.Equals(Owner.Id, other.Owner.Id, StringComparison.Ordinal) &&
               string.Equals(Owner.Name, other.Owner.Name, StringComparison.Ordinal) &&
               MyPortalOrdinal == other.MyPortalOrdinal &&
               MyPortalLimit == other.MyPortalLimit &&
               ModeOrdinal == other.ModeOrdinal &&
               ModeCurrent == other.ModeCurrent &&
               ModeLimit == other.ModeLimit &&
               InviteDepartureCooldownUntilUtc ==
               other.InviteDepartureCooldownUntilUtc &&
               InviteArrivalCooldownUntilUtc ==
               other.InviteArrivalCooldownUntilUtc;
    }

    public PublicPortalCatalogEntry WithDisplayMetadata(
        int myPortalOrdinal,
        int myPortalLimit,
        int modeOrdinal,
        int modeCurrent,
        int modeLimit,
        long inviteDepartureCooldownUntilUtc = 0L,
        long inviteArrivalCooldownUntilUtc = 0L)
    {
        return new PublicPortalCatalogEntry(
            Id,
            FavoriteId,
            PrefabHash,
            AllowsAllItems,
            Position,
            Rotation,
            Tag,
            AccessMode,
            Owner,
            myPortalOrdinal,
            myPortalLimit,
            modeOrdinal,
            modeCurrent,
            modeLimit,
            inviteDepartureCooldownUntilUtc,
            inviteArrivalCooldownUntilUtc);
    }
}

internal readonly struct LocalPortalPlacementState
{
    public readonly PortalBuilder PreviousBuilder;
    public readonly HashSet<ZDOID>? PreviousPendingPortalIds;

    public LocalPortalPlacementState(
        PortalBuilder previousBuilder,
        HashSet<ZDOID>? previousPendingPortalIds)
    {
        PreviousBuilder = previousBuilder;
        PreviousPendingPortalIds = previousPendingPortalIds;
    }
}

internal static partial class PublicPortalCatalog
{
    private readonly struct ServerPortalAuthority
    {
        public readonly PublicPortalAccessMode AccessMode;
        public readonly PortalOwner Owner;
        public readonly string AuthorizedClanId;
        public readonly PortalBuilder Builder;
        public readonly int PrefabHash;
        public readonly string FavoriteId;
        public readonly string RequiredGlobalKey;
        public readonly long PublicExpiresAtUtcSeconds;
        public readonly bool RequiredGlobalKeyIsValid;

        public ServerPortalAuthority(
            PublicPortalAccessMode accessMode,
            PortalOwner owner,
            string authorizedClanId,
            PortalBuilder builder,
            int prefabHash,
            string favoriteId,
            string requiredGlobalKey,
            long publicExpiresAtUtcSeconds)
        {
            AccessMode = accessMode;
            Owner = owner;
            AuthorizedClanId = authorizedClanId ?? "";
            Builder = builder;
            PrefabHash = prefabHash;
            FavoriteId = favoriteId ?? "";
            RequiredGlobalKeyIsValid =
                PublicPortalData.TryNormalizeRequiredGlobalKey(
                    requiredGlobalKey,
                    out string normalizedRequiredGlobalKey,
                    out _);
            RequiredGlobalKey = RequiredGlobalKeyIsValid
                ? normalizedRequiredGlobalKey
                : (requiredGlobalKey ?? "").Trim();
            PublicExpiresAtUtcSeconds =
                accessMode == PublicPortalAccessMode.Public &&
                builder.IsValid &&
                !PublicPortalKinds.IsAdminPortalPrefab(prefabHash)
                    ? PublicPortalData.NormalizePublicExpiresAtUtcSeconds(
                        publicExpiresAtUtcSeconds)
                    : 0L;
        }
    }

    private readonly struct AdminPortalTagAuthority
    {
        public readonly string Tag;

        public AdminPortalTagAuthority(string tag)
        {
            Tag = tag ?? "";
        }
    }

    private sealed class RecipientContext
    {
        public readonly string OwnerId;
        public readonly bool IsAdmin;
        public readonly PortalClanMembership ClanMembership;
        public readonly ZNetPeer? Peer;
        public readonly Dictionary<string, RequiredGlobalKeyQueryResult>
            RequiredGlobalKeyResults = new(StringComparer.Ordinal);

        public RecipientContext(
            string ownerId,
            bool isAdmin,
            PortalClanMembership clanMembership,
            ZNetPeer? peer)
        {
            OwnerId = ownerId ?? "";
            IsAdmin = isAdmin;
            ClanMembership = clanMembership;
            Peer = peer;
        }
    }

    internal sealed class PortalSyncContext
    {
        public readonly ZNetPeer? Peer;
        public readonly HashSet<ZDOID> CreatedIds = new();

        public PortalSyncContext(ZNetPeer? peer)
        {
            Peer = peer;
        }
    }

    private const int MaximumCatalogEntries = PublicPortalData.MaximumPortalCount;
    private const int MaximumTagLength = 256;
    private const int MaximumOwnerIdLength = 128;
    private const int MaximumOwnerNameLength = 128;
    private const int MaximumClanIdLength = 128;
    private const float MaximumAdminPortalPlacementDistance = 15f;
    private const float MaximumPortalCoordinateMagnitude = 1000000f;
    private const double MinimumQuaternionSqrMagnitude = 0.25d;
    private const double MaximumQuaternionSqrMagnitude = 4d;
    private const float ServerRefreshSeconds = 5f;

    private static readonly List<PublicPortalCatalogEntry> ServerEntries = new();
    private static readonly List<PublicPortalCatalogEntry> ClientEntries = new();
    private static readonly Dictionary<ZDOID, PublicPortalCatalogEntry> ClientIndex = new();
    private static readonly Dictionary<ZDOID, ServerPortalAuthority> ServerAuthority = new();
    private static readonly Dictionary<string, ZDOID> ServerPortalsByFavoriteId =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<ZDOID, AdminPortalTagAuthority> AdminPortalTags = new();
    private static readonly Dictionary<string, HashSet<ZDOID>> ServerPortalsByBuilder =
        new(StringComparer.Ordinal);
    private static readonly HashSet<ZDOID> InitialWorldPortalIds = new();
    private static readonly HashSet<ZDOID> PendingRemovalIds = new();

    private static Game? _registeredGame;
    private static Game? _serverLoopGame;
    private static ZNet? _sessionZNet;
    private static int _serverRevision;
    private static int _clientRevision = -1;
    private static float _nextIdentityReconcileAt = -100f;
    private static bool _authorityBackfilled;
    private static bool _hasSnapshot;
    private static bool _worldLoaded;
    private static bool _serverCatalogDirty;
    private static bool _serverDirtyFlushScheduled;
    private static bool _localRecipientViewInitialized;
    private static string _localRecipientOwnerId = "";
    private static bool _localRecipientIsAdmin;
    private static ulong _localRecipientViewFingerprint;
    private static long _sessionGeneration;
    private static long _nextBuildSequence;
    [ThreadStatic]
    private static PortalSyncContext? _activePortalSync;
    [ThreadStatic]
    private static PortalBuilder _activeLocalPlacementBuilder;
    [ThreadStatic]
    private static HashSet<ZDOID>? _pendingLocalPlacementPortalIds;

    public static event Action? Updated;

    public static IReadOnlyList<PublicPortalCatalogEntry> Entries => ClientEntries;
    public static bool HasSnapshot => _hasSnapshot;
    internal static bool IsServerReady =>
        _worldLoaded &&
        _authorityBackfilled &&
        _hasSnapshot &&
        ZNet.instance != null &&
        ZNet.instance.IsServer();

    public static void Register(Game game)
    {
        if (game == null)
        {
            return;
        }

        if (!ReferenceEquals(_sessionZNet, ZNet.instance))
        {
            BeginNetworkSession(ZNet.instance);
        }

        if (ReferenceEquals(_registeredGame, game))
        {
            return;
        }

        _registeredGame = game;

        if (ZNet.instance != null && ZNet.instance.IsServer())
        {
            RefreshAndBroadcast();
            _serverLoopGame = game;
            game.StartCoroutine(ServerRefreshLoop(game));
        }
        else
        {
            RequestRefresh(force: true);
        }
    }

    public static void BeginNetworkSession(ZNet? znet)
    {
        if (znet == null || ReferenceEquals(_sessionZNet, znet))
        {
            return;
        }

        InviteTravelCooldownStore.EndServerSession();
        AdminPortalBiomeDefaults.EndServerSession();
        PortalAccountStore.EndServerSession();
        ResetSessionState();
        _registeredGame = null;
        _sessionZNet = znet;
        if (znet.IsServer())
        {
            PortalAccountStore.BeginServerSession();
            AdminPortalBiomeDefaults.BeginServerSession();
        }
    }

    public static void NotifyServerWorldLoaded(ZNet znet)
    {
        if (!ReferenceEquals(_sessionZNet, znet))
        {
            BeginNetworkSession(znet);
        }

        _worldLoaded = true;
        if (znet.IsServer())
        {
            InviteTravelCooldownStore.BeginServerSession();
        }
        if (_registeredGame != null && znet.IsServer())
        {
            RefreshAndBroadcast();
        }
    }

    public static void Shutdown()
    {
        InviteTravelCooldownStore.EndServerSession();
        AdminPortalBiomeDefaults.EndServerSession();
        PortalAccountStore.EndServerSession();
        ResetSessionState();
        _registeredGame = null;
        _sessionZNet = null;
    }

    public static void ObservePortal(ZDO zdo)
    {
        if (zdo == null ||
            ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            ServerAuthority.ContainsKey(zdo.m_uid) ||
            PendingRemovalIds.Contains(zdo.m_uid) ||
            !zdo.IsValid())
        {
            return;
        }

        if (!PublicPortalKinds.IsHandledPortal(zdo) &&
            !PublicPortalServerPolicy.IsCountedPortalPrefab(zdo.GetPrefab()))
        {
            return;
        }

        bool remoteIngress = _activePortalSync != null;
        if (!_authorityBackfilled)
        {
            if (remoteIngress)
            {
                PublicPortalServerPolicy.RejectUnverifiedPortal(
                    zdo,
                    _activePortalSync?.Peer);
            }

            return;
        }

        if (PublicPortalKinds.IsAdminPortalPrefab(zdo.GetPrefab()))
        {
            ZNetPeer? sourcePeer = _activePortalSync?.Peer;
            if (remoteIngress &&
                !IsAuthenticatedRemoteAdminPortalCreation(zdo, sourcePeer))
            {
                PublicPortalServerPolicy.RejectUnverifiedPortal(zdo, sourcePeer);
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    $"Rejected admin portal creation {zdo.m_uid} from a non-admin or unverified peer.");
                return;
            }

            if (!CanInitializeAdminPortalAuthority(zdo))
            {
                if (remoteIngress)
                {
                    PublicPortalServerPolicy.RejectUnverifiedPortal(zdo, sourcePeer);
                    PortalRulesPlugin.PortalRulesLogger.LogWarning(
                        $"Rejected admin portal creation {zdo.m_uid} while biome GlobalKey defaults were unavailable.");
                }
                else
                {
                    InitialWorldPortalIds.Add(zdo.m_uid);
                }

                // Server-created location and blueprint portals are revisited by
                // the catalog scan once the first valid defaults snapshot exists.
                return;
            }

            SetAndPersistServerAuthority(
                zdo,
                CreateAdminPortalAuthority(
                    zdo,
                    requestedMode: PublicPortalAccessMode.Public,
                    favoriteId: CreateServerFavoriteId(),
                    requiredGlobalKey: ResolveInitialAdminRequiredGlobalKey(zdo)),
                forceSend: true);
            MarkServerCatalogDirty(zdo);
            return;
        }

        if (!remoteIngress && _pendingLocalPlacementPortalIds != null)
        {
            _pendingLocalPlacementPortalIds.Add(zdo.m_uid);
            return;
        }

        ServerPortalAuthority authority;
        if (InitialWorldPortalIds.Contains(zdo.m_uid))
        {
            authority = ReadInitialAuthority(zdo);
        }
        else
        {
            bool builderResolved = TryResolveActiveBuilder(
                zdo,
                out PortalBuilder builder);
            long creatorPlayerId = zdo.GetLong(ZDOVars.s_creator, 0L);
            if (!builderResolved &&
                !remoteIngress &&
                creatorPlayerId != 0L)
            {
                builderResolved = TryCreateRegistryBuilder(zdo, out builder);
            }

            if (!builderResolved)
            {
                if (remoteIngress || creatorPlayerId != 0L)
                {
                    ZNetPeer? peer = _activePortalSync?.Peer;
                    PublicPortalServerPolicy.RejectUnverifiedPortal(zdo, peer);
                    return;
                }

                authority = CreateAuthorityForNewPortal(zdo);
            }
            else
            {
                PortalOwner owner = new(builder.AccountId, builder.Name);
                PortalBuilder sanitizedBuilder = SanitizeBuilder(builder);
                if (!PublicPortalServerPolicy.TryAcceptNewPortal(
                        zdo,
                        sanitizedBuilder,
                        _activePortalSync?.Peer))
                {
                    return;
                }

                authority = new ServerPortalAuthority(
                    PublicPortalAccessMode.Personal,
                    SanitizeOwner(owner),
                    "",
                    sanitizedBuilder,
                    zdo.GetPrefab(),
                    CreateServerFavoriteId(),
                    "",
                    CreateTemporaryPublicExpirationUtcSeconds(
                        PublicPortalAccessMode.Personal,
                        sanitizedBuilder,
                        zdo.GetPrefab()));
            }
        }

        if (TryNormalizeTemporaryPublicAuthority(
                authority,
                PublicPortalData.GetUtcNowSeconds(),
                out ServerPortalAuthority normalizedAuthority,
                out _))
        {
            authority = normalizedAuthority;
        }

        SetAndPersistServerAuthority(zdo, authority, forceSend: true);
        MarkServerCatalogDirty(zdo);
    }

    public static PortalSyncContext? BeginPortalSync(ZRpc rpc)
    {
        if (ZNet.instance != null &&
            ZNet.instance.IsServer() &&
            _worldLoaded &&
            !_authorityBackfilled)
        {
            RefreshAndBroadcast();
        }

        PortalSyncContext? previousContext = _activePortalSync;
        ZNetPeer? peer =
            ZNet.instance != null && ZNet.instance.IsServer()
                ? PublicPortalData.FindPeer(ZNet.instance, rpc)
                : null;
        _activePortalSync = new PortalSyncContext(peer);
        return previousContext;
    }

    public static void RestorePortalSync(PortalSyncContext? previousContext)
    {
        _activePortalSync = previousContext;
    }

    public static void ObserveRemoteZdoCreated(ZDO zdo)
    {
        if (zdo != null && _activePortalSync != null)
        {
            _activePortalSync.CreatedIds.Add(zdo.m_uid);
        }
    }

    public static LocalPortalPlacementState BeginLocalPlacement(Player player)
    {
        if (ZNet.instance != null &&
            ZNet.instance.IsServer() &&
            _worldLoaded &&
            !_authorityBackfilled)
        {
            RefreshAndBroadcast();
        }

        LocalPortalPlacementState state = new(
            _activeLocalPlacementBuilder,
            _pendingLocalPlacementPortalIds);
        _activeLocalPlacementBuilder =
            ZNet.instance != null &&
            ZNet.instance.IsServer() &&
            player != null &&
            player == Player.m_localPlayer &&
            TryEnsureLocalIdentity(out string steamId)
                ? SanitizeBuilder(new PortalBuilder(
                    steamId,
                    ((Character)player).GetHoverName(),
                    player.GetPlayerID(),
                    0L))
                : new PortalBuilder("", "");
        _pendingLocalPlacementPortalIds = new HashSet<ZDOID>();
        return state;
    }

    public static void CompleteLocalPlacement(LocalPortalPlacementState state)
    {
        HashSet<ZDOID>? completedPortalIds = _pendingLocalPlacementPortalIds;
        _pendingLocalPlacementPortalIds = null;
        try
        {
            if (ZNet.instance != null &&
                ZNet.instance.IsServer() &&
                ZDOMan.instance != null &&
                completedPortalIds != null)
            {
                foreach (ZDOID portalId in completedPortalIds.ToArray())
                {
                    ZDO? portal = ZDOMan.instance.GetZDO(portalId);
                    if (portal != null && portal.IsValid())
                    {
                        ObservePortal(portal);
                    }
                }
            }
        }
        finally
        {
            _activeLocalPlacementBuilder = state.PreviousBuilder;
            _pendingLocalPlacementPortalIds = state.PreviousPendingPortalIds;
        }
    }

    internal static void TickIdentityRegistry()
    {
        if (ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            !PortalAccountStore.IsActive ||
            Time.realtimeSinceStartup < _nextIdentityReconcileAt)
        {
            return;
        }

        _nextIdentityReconcileAt = Time.realtimeSinceStartup + 1f;
        TryEnsureLocalIdentity(out _);
        PublicPortalServerPolicy.RefreshLocalServerQuotaState();
    }

    internal static bool TryEnsurePeerIdentity(ZNetPeer? peer, out string steamId)
    {
        steamId = "";
        if (ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            !PublicPortalData.TryGetPeerSteamId64(peer, out steamId) ||
            !PublicPortalData.TryGetAuthenticatedPeerPlayerId(peer, out long playerId) ||
            !PortalAccountStore.TryRememberIdentity(playerId, steamId, out bool added))
        {
            steamId = "";
            return false;
        }

        if (added)
        {
            BackfillBuilderForCreator(playerId, steamId, peer?.m_playerName ?? "");
        }

        return true;
    }

    internal static bool TryEnsureLocalIdentity(out string steamId)
    {
        steamId = "";
        Player? player = Player.m_localPlayer;
        if (ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            player == null ||
            !PublicPortalData.TryGetLocalSteamId64(out steamId))
        {
            steamId = "";
            return false;
        }

        long playerId = player.GetPlayerID();
        if (!PortalAccountStore.TryRememberIdentity(
                playerId,
                steamId,
                out bool added))
        {
            steamId = "";
            return false;
        }

        if (added)
        {
            BackfillBuilderForCreator(
                playerId,
                steamId,
                ((Character)player).GetHoverName());
        }

        return true;
    }

    public static bool TryGetAuthoritativeAccess(
        ZDOID portalId,
        out PublicPortalAccessMode accessMode,
        out PortalOwner owner)
    {
        if (!PendingRemovalIds.Contains(portalId) &&
            ServerAuthority.TryGetValue(portalId, out ServerPortalAuthority authority))
        {
            bool expired = IsTemporaryPublicAccessExpired(
                authority,
                PublicPortalData.GetUtcNowSeconds());
            accessMode = expired
                ? PublicPortalAccessMode.Personal
                : authority.AccessMode;
            owner = expired
                ? new PortalOwner(
                    authority.Builder.AccountId,
                    authority.Builder.Name)
                : authority.Owner;
            return true;
        }

        accessMode = PublicPortalAccessMode.Personal;
        owner = new PortalOwner("", "");
        return false;
    }

    internal static bool IsAuthoritativeAdminPortal(ZDOID portalId)
    {
        return !PendingRemovalIds.Contains(portalId) &&
               ServerAuthority.TryGetValue(portalId, out ServerPortalAuthority authority) &&
               PublicPortalKinds.IsAdminPortalPrefab(authority.PrefabHash);
    }

    internal static bool TryGetTemporaryPublicRemainingSeconds(
        ZDOID portalId,
        out int remainingSeconds)
    {
        remainingSeconds = 0;
        if (!ServerAuthority.TryGetValue(
                portalId,
                out ServerPortalAuthority authority) ||
            authority.AccessMode != PublicPortalAccessMode.Public ||
            authority.PublicExpiresAtUtcSeconds == 0L)
        {
            return false;
        }

        long remaining =
            authority.PublicExpiresAtUtcSeconds - PublicPortalData.GetUtcNowSeconds();
        if (remaining <= 0L)
        {
            return false;
        }

        remainingSeconds = (int)Math.Min(int.MaxValue, remaining);
        return true;
    }

    internal static bool TryAuthorizeTeleport(
        ZNetPeer? peer,
        ZDOID sourcePortalId,
        ZDOID targetPortalId,
        string reservationId,
        out PortalTravelAuthorization authorization,
        out PortalTravelDenial denial)
    {
        authorization = default;
        denial = new PortalTravelDenial(PortalTravelDenialCode.Denied);
        if (!TryCreateRemoteAuthorizationContext(
                peer,
                out RecipientContext recipient,
                out Vector3 characterPosition))
        {
            return false;
        }

        return TryAuthorizeTeleport(
            recipient,
            characterPosition,
            sourcePortalId,
            targetPortalId,
            requireConnectedPortal: false,
            reservationId,
            out authorization,
            out denial);
    }

    internal static bool TryAuthorizeLocalTeleport(
        ZDOID sourcePortalId,
        ZDOID targetPortalId,
        string reservationId,
        out PortalTravelAuthorization authorization,
        out PortalTravelDenial denial)
    {
        CreateLocalAuthorizationContext(
            out RecipientContext recipient,
            out Vector3 playerPosition);
        return TryAuthorizeTeleport(
            recipient,
            playerPosition,
            sourcePortalId,
            targetPortalId,
            requireConnectedPortal: false,
            reservationId,
            out authorization,
            out denial);
    }

    internal static bool TryAuthorizeConnectedTeleport(
        ZNetPeer? peer,
        ZDOID sourcePortalId,
        ZDOID targetPortalId,
        string reservationId,
        out PortalTravelAuthorization authorization,
        out PortalTravelDenial denial)
    {
        authorization = default;
        denial = new PortalTravelDenial(PortalTravelDenialCode.Denied);
        if (!TryCreateRemoteAuthorizationContext(
                peer,
                out RecipientContext recipient,
                out Vector3 characterPosition))
        {
            return false;
        }

        return TryAuthorizeTeleport(
            recipient,
            characterPosition,
            sourcePortalId,
            targetPortalId,
            requireConnectedPortal: true,
            reservationId,
            out authorization,
            out denial);
    }

    internal static bool TryAuthorizeLocalConnectedTeleport(
        ZDOID sourcePortalId,
        ZDOID targetPortalId,
        string reservationId,
        out PortalTravelAuthorization authorization,
        out PortalTravelDenial denial)
    {
        CreateLocalAuthorizationContext(
            out RecipientContext recipient,
            out Vector3 playerPosition);
        return TryAuthorizeTeleport(
            recipient,
            playerPosition,
            sourcePortalId,
            targetPortalId,
            requireConnectedPortal: true,
            reservationId,
            out authorization,
            out denial);
    }

    internal static bool TryAuthorizeMapOpen(
        ZNetPeer? peer,
        ZDOID sourcePortalId,
        out PortalTravelDenial denial)
    {
        denial = new PortalTravelDenial(PortalTravelDenialCode.Denied);
        if (PublicPortalConfig.EnablePortalMap.Value.IsOff() ||
            !TryCreateRemoteAuthorizationContext(
                peer,
                out RecipientContext recipient,
                out Vector3 characterPosition))
        {
            return false;
        }

        return TryAuthorizePortalSource(
            recipient,
            characterPosition,
            sourcePortalId,
            requireConnectedPortal: false,
            out _,
            out _,
            out denial);
    }

    internal static bool TryAuthorizeLocalMapOpen(
        ZDOID sourcePortalId,
        out PortalTravelDenial denial)
    {
        denial = new PortalTravelDenial(PortalTravelDenialCode.Denied);
        if (PublicPortalConfig.EnablePortalMap.Value.IsOff())
        {
            return false;
        }

        CreateLocalAuthorizationContext(
            out RecipientContext recipient,
            out Vector3 playerPosition);
        return TryAuthorizePortalSource(
            recipient,
            playerPosition,
            sourcePortalId,
            requireConnectedPortal: false,
            out _,
            out _,
            out denial);
    }

    public static bool SetAuthoritativeAccess(
        ZDO zdo,
        PublicPortalAccessMode accessMode,
        string authorizedClanId)
    {
        if (zdo == null ||
            ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            PendingRemovalIds.Contains(zdo.m_uid) ||
            !ServerAuthority.TryGetValue(zdo.m_uid, out ServerPortalAuthority currentAuthority))
        {
            return false;
        }

        if (TryNormalizeTemporaryPublicAuthority(
                currentAuthority,
                PublicPortalData.GetUtcNowSeconds(),
                out ServerPortalAuthority normalizedCurrentAuthority,
                out _))
        {
            currentAuthority = normalizedCurrentAuthority;
        }

        string sanitizedClanId = SanitizeClanId(authorizedClanId);
        bool isAdminPortal =
            PublicPortalKinds.IsAdminPortalPrefab(currentAuthority.PrefabHash);
        if (isAdminPortal && !IsAllowedAdminPortalMode(accessMode))
        {
            return false;
        }

        PortalBuilder updatedBuilder = isAdminPortal
            ? new PortalBuilder("", "")
            : currentAuthority.Builder;
        if (!isAdminPortal && !updatedBuilder.IsValid)
        {
            return false;
        }

        PortalOwner sanitizedOwner = isAdminPortal
            ? new PortalOwner("", "")
            : new PortalOwner(updatedBuilder.AccountId, updatedBuilder.Name);
        if (isAdminPortal)
        {
            sanitizedClanId = "";
        }

        if (!isAdminPortal &&
            accessMode == PublicPortalAccessMode.Invite &&
            currentAuthority.AccessMode != PublicPortalAccessMode.Invite)
        {
            int inviteLimit = PortalAccountStore.GetEffectiveInvitePortalLimit(
                updatedBuilder.AccountId,
                PublicPortalConfig.MaxInvitePortalsPerAccount.Value);
            if (inviteLimit >= 0 &&
                GetBuilderInvitePortalCount(updatedBuilder.AccountId) >= inviteLimit)
            {
                return false;
            }
        }

        if (!isAdminPortal &&
            accessMode is PublicPortalAccessMode.Clan or
                PublicPortalAccessMode.Tagged)
        {
            bool hasPrimaryClan =
                ClanPortalAccess.TryResolveBuilderMembership(
                    updatedBuilder,
                    out PortalClanMembership membership) &&
                membership.HasPrimaryClan;
            if (accessMode == PublicPortalAccessMode.Clan &&
                !hasPrimaryClan)
            {
                return false;
            }

            sanitizedClanId = hasPrimaryClan
                ? SanitizeClanId(membership.PrimaryClanId)
                : "";
            if (accessMode == PublicPortalAccessMode.Clan &&
                string.IsNullOrWhiteSpace(sanitizedClanId))
            {
                return false;
            }

            if (accessMode == PublicPortalAccessMode.Clan &&
                (currentAuthority.AccessMode != PublicPortalAccessMode.Clan ||
                !string.Equals(
                    currentAuthority.AuthorizedClanId,
                    sanitizedClanId,
                    StringComparison.Ordinal)))
            {
                int clanLimit = Math.Max(
                    -1,
                    Math.Min(
                        PublicPortalData.MaximumPortalLimit,
                        PublicPortalConfig.MaxClanPortalsPerClan.Value));
                if (clanLimit >= 0 &&
                    GetClanPortalCount(sanitizedClanId) >= clanLimit)
                {
                    return false;
                }
            }
        }

        long publicExpiresAtUtcSeconds =
            accessMode == PublicPortalAccessMode.Public &&
            currentAuthority.AccessMode == PublicPortalAccessMode.Public
                ? currentAuthority.PublicExpiresAtUtcSeconds
                : CreateTemporaryPublicExpirationUtcSeconds(
                    accessMode,
                    updatedBuilder,
                    currentAuthority.PrefabHash);

        ServerPortalAuthority updatedAuthority = new(
            accessMode,
            sanitizedOwner,
            accessMode is PublicPortalAccessMode.Clan or
                PublicPortalAccessMode.Tagged
                ? sanitizedClanId
                : "",
            updatedBuilder,
            currentAuthority.PrefabHash,
            currentAuthority.FavoriteId,
            currentAuthority.RequiredGlobalKey,
            publicExpiresAtUtcSeconds);
        SetAndPersistServerAuthority(zdo, updatedAuthority, forceSend: true);
        RefreshAndBroadcast();
        return true;
    }

    internal static bool SetAuthoritativeAdminPortalTag(
        ZDO zdo,
        string tag)
    {
        if (zdo == null ||
            ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            PendingRemovalIds.Contains(zdo.m_uid) ||
            !ServerAuthority.TryGetValue(zdo.m_uid, out ServerPortalAuthority authority) ||
            !PublicPortalKinds.IsAdminPortalPrefab(authority.PrefabHash))
        {
            return false;
        }

        AdminPortalTags[zdo.m_uid] = new AdminPortalTagAuthority(
            Truncate(tag, 10));
        zdo.UpdateConnection(ZDOExtraData.ConnectionType.Portal, ZDOID.None);
        WriteAuthorityToZdo(zdo, authority, forceSend: true);
        RefreshAndBroadcast();
        PublicPortalTaggedConnections.RefreshConnections();
        return true;
    }

    internal static bool SetAuthoritativeAdminPortalRequiredGlobalKey(
        ZDO zdo,
        string requiredGlobalKey)
    {
        if (zdo == null ||
            ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            PendingRemovalIds.Contains(zdo.m_uid) ||
            !ServerAuthority.TryGetValue(
                zdo.m_uid,
                out ServerPortalAuthority currentAuthority) ||
            !PublicPortalKinds.IsAdminPortalPrefab(
                currentAuthority.PrefabHash) ||
            !PublicPortalData.TryNormalizeRequiredGlobalKey(
                requiredGlobalKey,
                out string normalizedRequiredGlobalKey,
                out _))
        {
            return false;
        }

        ServerPortalAuthority updatedAuthority = new(
            currentAuthority.AccessMode,
            currentAuthority.Owner,
            currentAuthority.AuthorizedClanId,
            currentAuthority.Builder,
            currentAuthority.PrefabHash,
            currentAuthority.FavoriteId,
            normalizedRequiredGlobalKey,
            currentAuthority.PublicExpiresAtUtcSeconds);
        SetAndPersistServerAuthority(zdo, updatedAuthority, forceSend: true);
        RefreshAndBroadcast();
        return true;
    }

    internal static int GetBuilderPortalCount(string builderAccountId)
    {
        if (!PublicPortalData.TryNormalizeSteamId64(
                builderAccountId,
                out string normalizedBuilderId) ||
            !ServerPortalsByBuilder.TryGetValue(normalizedBuilderId, out HashSet<ZDOID> portalIds))
        {
            return 0;
        }

        int count = 0;
        foreach (ZDOID portalId in portalIds)
        {
            if (PendingRemovalIds.Contains(portalId) ||
                !ServerAuthority.TryGetValue(portalId, out ServerPortalAuthority authority) ||
                !PublicPortalServerPolicy.IsCountedPortalPrefab(authority.PrefabHash))
            {
                continue;
            }

            count++;
        }

        return count;
    }

    internal static int GetBuilderInvitePortalCount(string builderAccountId)
    {
        if (!PublicPortalData.TryNormalizeSteamId64(
                builderAccountId,
                out string normalizedBuilderId) ||
            !ServerPortalsByBuilder.TryGetValue(
                normalizedBuilderId,
                out HashSet<ZDOID> portalIds))
        {
            return 0;
        }

        int count = 0;
        foreach (ZDOID portalId in portalIds)
        {
            if (PendingRemovalIds.Contains(portalId) ||
                !ServerAuthority.TryGetValue(
                    portalId,
                    out ServerPortalAuthority authority) ||
                !authority.Builder.IsValid ||
                authority.AccessMode != PublicPortalAccessMode.Invite ||
                PublicPortalKinds.IsAdminPortalPrefab(
                    authority.PrefabHash))
            {
                continue;
            }

            count++;
        }

        return count;
    }

    internal static int GetClanPortalCount(string clanId)
    {
        string normalizedClanId = SanitizeClanId(clanId);
        if (string.IsNullOrEmpty(normalizedClanId))
        {
            return 0;
        }

        int count = 0;
        foreach (KeyValuePair<ZDOID, ServerPortalAuthority> pair in ServerAuthority)
        {
            ServerPortalAuthority authority = pair.Value;
            if (PendingRemovalIds.Contains(pair.Key) ||
                !authority.Builder.IsValid ||
                authority.AccessMode != PublicPortalAccessMode.Clan ||
                PublicPortalKinds.IsAdminPortalPrefab(
                    authority.PrefabHash) ||
                !string.Equals(
                    authority.AuthorizedClanId,
                    normalizedClanId,
                    StringComparison.Ordinal))
            {
                continue;
            }

            count++;
        }

        return count;
    }

    internal static bool TryGetAuthoritativeBuilder(
        ZDOID portalId,
        out PortalBuilder builder)
    {
        if (!PendingRemovalIds.Contains(portalId) &&
            ServerAuthority.TryGetValue(
                portalId,
                out ServerPortalAuthority authority))
        {
            builder = authority.Builder;
            return true;
        }

        builder = new PortalBuilder("", "");
        return false;
    }

    internal static bool MarkPendingRemoval(ZDOID portalId)
    {
        if (portalId.IsNone() || !PendingRemovalIds.Add(portalId))
        {
            return false;
        }

        MarkServerCatalogDirty();
        return true;
    }

    internal static void NotifyPortalDestroyed(ZDOID portalId)
    {
        RemoveServerAuthority(portalId);
        PendingRemovalIds.Remove(portalId);
        InitialWorldPortalIds.Remove(portalId);
        MarkServerCatalogDirty();
    }

    internal static bool TryGetClientEntry(
        ZDOID portalId,
        out PublicPortalCatalogEntry entry)
    {
        return ClientIndex.TryGetValue(portalId, out entry);
    }

    internal static bool TryCanLocalServerUserUsePortal(
        ZDOID portalId,
        out bool canUse)
    {
        canUse = false;
        if (ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            PendingRemovalIds.Contains(portalId) ||
            !ServerAuthority.TryGetValue(
                portalId,
                out ServerPortalAuthority authority))
        {
            return false;
        }

        PortalOwner recipient = PublicPortalData.LocalOwner();
        RecipientContext recipientContext = CreateRecipientContext(
            recipient,
            PortalRulesPlugin.IsAdmin,
            ResolveLocalMembership());
        canUse = CanRecipientUseAuthority(
            authority,
            recipientContext);
        return true;
    }

    public static PublicPortalAccessMode GetEffectiveAccessMode(ZDO zdo)
    {
        if (zdo != null)
        {
            if (PendingRemovalIds.Contains(zdo.m_uid))
            {
                return PublicPortalAccessMode.Personal;
            }

            if (ServerAuthority.TryGetValue(zdo.m_uid, out ServerPortalAuthority authority))
            {
                return IsTemporaryPublicAccessExpired(
                    authority,
                    PublicPortalData.GetUtcNowSeconds())
                    ? PublicPortalAccessMode.Personal
                    : authority.AccessMode;
            }

            if (ClientIndex.TryGetValue(zdo.m_uid, out PublicPortalCatalogEntry entry))
            {
                return entry.AccessMode;
            }
        }

        return zdo != null ? PublicPortalData.GetAccessMode(zdo) : PublicPortalAccessMode.Personal;
    }

    public static PortalOwner GetEffectiveOwner(ZDO zdo)
    {
        if (zdo != null)
        {
            if (PendingRemovalIds.Contains(zdo.m_uid))
            {
                return new PortalOwner("", "");
            }

            if (ServerAuthority.TryGetValue(zdo.m_uid, out ServerPortalAuthority authority))
            {
                return IsTemporaryPublicAccessExpired(
                    authority,
                    PublicPortalData.GetUtcNowSeconds())
                    ? new PortalOwner(
                        authority.Builder.AccountId,
                        authority.Builder.Name)
                    : authority.Owner;
            }

            if (ClientIndex.TryGetValue(zdo.m_uid, out PublicPortalCatalogEntry entry))
            {
                return entry.Owner;
            }
        }

        return zdo != null ? PublicPortalData.GetOwner(zdo) : new PortalOwner("", "");
    }

    internal static string GetEffectiveAuthorizedClanId(ZDO zdo)
    {
        if (zdo == null || PendingRemovalIds.Contains(zdo.m_uid))
        {
            return "";
        }

        return ServerAuthority.TryGetValue(
            zdo.m_uid,
            out ServerPortalAuthority authority)
            ? authority.AuthorizedClanId
            : SanitizeClanId(PublicPortalData.GetAuthorizedClanId(zdo));
    }

    internal static string GetEffectiveRequiredGlobalKey(ZDO zdo)
    {
        if (zdo == null || PendingRemovalIds.Contains(zdo.m_uid))
        {
            return "";
        }

        return ServerAuthority.TryGetValue(
            zdo.m_uid,
            out ServerPortalAuthority authority)
            ? authority.RequiredGlobalKey
            : PublicPortalData.GetRequiredGlobalKey(zdo);
    }

    public static void RefreshAndBroadcast()
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer())
        {
            return;
        }

        // An immediate refresh satisfies any queued dirty request. If new
        // changes arrive while the refresh runs they will mark it dirty again.
        _serverCatalogDirty = false;
        bool catalogChanged = RefreshServerSnapshot();
        RefreshLocalRecipientView(catalogChanged);
        if (catalogChanged)
        {
            BroadcastSnapshot();
        }
        else
        {
            BroadcastChangedViews();
        }

        PublicPortalServerPolicy.BroadcastQuotaStates();
    }

    internal static void RefreshCountedPortalConfiguration()
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer())
        {
            return;
        }

        if (ZDOMan.instance != null)
        {
            foreach (ZDO portal in ZDOMan.instance.GetPortals())
            {
                if (portal != null && portal.IsValid())
                {
                    InitialWorldPortalIds.Add(portal.m_uid);
                }
            }
        }

        RefreshAndBroadcast();
    }

    internal static void RefreshTemporaryPublicConfiguration()
    {
        if (ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            ZDOMan.instance == null ||
            !_authorityBackfilled)
        {
            return;
        }

        long nowUtcSeconds = PublicPortalData.GetUtcNowSeconds();
        int changed = 0;
        foreach (KeyValuePair<ZDOID, ServerPortalAuthority> pair in
                 ServerAuthority.ToArray())
        {
            if (PendingRemovalIds.Contains(pair.Key))
            {
                continue;
            }

            ZDO? portal = ZDOMan.instance.GetZDO(pair.Key);
            if (portal == null || !portal.IsValid())
            {
                continue;
            }

            long publicExpiresAtUtcSeconds =
                CreateTemporaryPublicExpirationUtcSeconds(
                    pair.Value.AccessMode,
                    pair.Value.Builder,
                    pair.Value.PrefabHash,
                    nowUtcSeconds);
            if (publicExpiresAtUtcSeconds ==
                pair.Value.PublicExpiresAtUtcSeconds)
            {
                continue;
            }

            ServerPortalAuthority updatedAuthority = new(
                pair.Value.AccessMode,
                pair.Value.Owner,
                pair.Value.AuthorizedClanId,
                pair.Value.Builder,
                pair.Value.PrefabHash,
                pair.Value.FavoriteId,
                pair.Value.RequiredGlobalKey,
                publicExpiresAtUtcSeconds);
            SetAndPersistServerAuthority(
                portal,
                updatedAuthority,
                forceSend: true);
            changed++;
        }

        if (changed > 0)
        {
            PortalRulesPlugin.PortalRulesLogger.LogInfo(
                $"Reset temporary Public access for {changed} portal(s) after a duration configuration change.");
        }

        RefreshAndBroadcast();
    }

    internal static void RefreshInviteCooldownConfiguration()
    {
        if (ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            !_hasSnapshot)
        {
            return;
        }

        RefreshLocalClientEntries(forceUpdate: true);
        BroadcastSnapshot();
    }

    private static void MarkServerCatalogDirty(ZDO portal)
    {
        if (!PublicPortalKinds.IsHandledPortal(portal) &&
            !PublicPortalServerPolicy.IsCountedPortalPrefab(portal.GetPrefab()))
        {
            return;
        }

        MarkServerCatalogDirty();
    }

    private static void MarkServerCatalogDirty()
    {
        _serverCatalogDirty = true;
        Game? game = _registeredGame;
        ZNet? sessionZNet = _sessionZNet;
        if (_serverDirtyFlushScheduled ||
            !_worldLoaded ||
            game == null ||
            sessionZNet == null ||
            !ReferenceEquals(ZNet.instance, sessionZNet) ||
            !sessionZNet.IsServer())
        {
            return;
        }

        _serverDirtyFlushScheduled = true;
        game.StartCoroutine(
            FlushDirtyServerCatalogNextFrame(
                game,
                sessionZNet,
                _sessionGeneration));
    }

    private static IEnumerator FlushDirtyServerCatalogNextFrame(
        Game game,
        ZNet sessionZNet,
        long generation)
    {
        yield return null;

        bool sameNetworkSession =
            generation == _sessionGeneration &&
            ReferenceEquals(_sessionZNet, sessionZNet);
        if (!sameNetworkSession)
        {
            yield break;
        }

        // Clear the latch before every same-session exit path. Otherwise a
        // transient world/server state can suppress later dirty scheduling
        // until the periodic scan happens to recover it.
        _serverDirtyFlushScheduled = false;
        if (!ReferenceEquals(_registeredGame, game))
        {
            if (_serverCatalogDirty)
            {
                MarkServerCatalogDirty();
            }

            yield break;
        }

        if (!ReferenceEquals(ZNet.instance, sessionZNet) ||
            !_worldLoaded ||
            !sessionZNet.IsServer())
        {
            yield break;
        }

        if (_serverCatalogDirty)
        {
            RefreshAndBroadcast();
        }
    }

    internal static void RefreshClanViews()
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer() || !_hasSnapshot)
        {
            return;
        }

        RefreshLocalRecipientView(catalogChanged: false);
        BroadcastChangedViews();
    }

    public static void ReconcileAuthorityToZdos()
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null)
        {
            return;
        }

        foreach (KeyValuePair<ZDOID, ServerPortalAuthority> pair in ServerAuthority.ToArray())
        {
            if (PendingRemovalIds.Contains(pair.Key))
            {
                continue;
            }

            ZDO? portal = ZDOMan.instance.GetZDO(pair.Key);
            if (portal != null && portal.IsValid() && !ZdoMatchesAuthority(portal, pair.Value))
            {
                WriteAuthorityToZdo(portal, pair.Value, forceSend: false);
            }
        }
    }

    private static IEnumerator ServerRefreshLoop(Game game)
    {
        yield return new WaitForSeconds(1f);

        while (ReferenceEquals(_serverLoopGame, game) &&
               ReferenceEquals(_registeredGame, game) &&
               ZNet.instance != null &&
               ZNet.instance.IsServer())
        {
            RefreshAndBroadcast();
            yield return new WaitForSeconds(ServerRefreshSeconds);
        }
    }

    private static bool RefreshServerSnapshot()
    {
        if (!_worldLoaded || ZDOMan.instance == null)
        {
            return false;
        }

        PortalAccountStore.BeginServerSession();
        AdminPortalBiomeDefaults.BeginServerSession();
        if (!AdminPortalBiomeDefaults.IsBiomeRegistryReady)
        {
            return false;
        }

        AdminPortalBiomeDefaults.EnsureTemplateReady();
        TryEnsureLocalIdentity(out _);
        if (ZNet.instance != null)
        {
            foreach (ZNetPeer peer in ZNet.instance.GetPeers().ToArray())
            {
                if (peer != null && peer.IsReady())
                {
                    TryEnsurePeerIdentity(peer, out _);
                }
            }
        }

        bool initialBackfill = !_authorityBackfilled;
        long nowUtcSeconds = PublicPortalData.GetUtcNowSeconds();
        int revertedTemporaryPublicPortals = 0;
        int returnedDisabledInvitePortals = 0;
        HashSet<ZDOID> livePortalIds = new();
        List<PublicPortalCatalogEntry> snapshot = new();
        ZDO[] portals = ZDOMan.instance.GetPortals()
            .Where(portal => portal != null)
            .OrderBy(portal => portal.m_uid)
            .ToArray();
        SeedNextBuildSequence(portals);

        foreach (ZDO portal in portals)
        {
            if (portal == null || !portal.IsValid())
            {
                continue;
            }

            livePortalIds.Add(portal.m_uid);
            if (PendingRemovalIds.Contains(portal.m_uid))
            {
                continue;
            }

            if (initialBackfill)
            {
                InitialWorldPortalIds.Add(portal.m_uid);
            }

            if (PublicPortalKinds.IsAdminPortalPrefab(portal.GetPrefab()) &&
                !ServerAuthority.ContainsKey(portal.m_uid) &&
                !CanInitializeAdminPortalAuthority(portal))
            {
                // Do not stamp a missing biome default as an explicit empty key.
                // Keeping the initial-world ID lets the later retry preserve any
                // blueprint/location metadata when configuration becomes valid.
                continue;
            }

            ServerPortalAuthority authority;
            bool authorityChanged = false;
            if (ServerAuthority.TryGetValue(portal.m_uid, out authority))
            {
                if (TryBackfillAuthorityBuilder(portal, authority, out ServerPortalAuthority backfilled))
                {
                    authority = backfilled;
                    authorityChanged = true;
                }
            }
            else
            {
                bool isNewHandledPortal = PublicPortalKinds.IsHandledPortal(portal);
                if (!isNewHandledPortal &&
                    !PublicPortalServerPolicy.IsCountedPortalPrefab(portal.GetPrefab()))
                {
                    continue;
                }

                authority = initialBackfill || InitialWorldPortalIds.Contains(portal.m_uid)
                    ? ReadInitialAuthority(portal)
                    : CreateAuthorityForNewPortal(portal);
                authorityChanged = true;
            }

            if (TryNormalizeTemporaryPublicAuthority(
                    authority,
                    nowUtcSeconds,
                    out ServerPortalAuthority normalizedAuthority,
                    out bool revertedToPersonal))
            {
                authority = normalizedAuthority;
                authorityChanged = true;
                if (revertedToPersonal)
                {
                    revertedTemporaryPublicPortals++;
                }
            }

            if (TryNormalizeDisabledInviteAuthority(
                    authority,
                    out ServerPortalAuthority personalAuthority))
            {
                authority = personalAuthority;
                authorityChanged = true;
                returnedDisabledInvitePortals++;
            }

            if (authorityChanged)
            {
                SetAndPersistServerAuthority(
                    portal,
                    authority,
                    forceSend: true);
            }
            else if (!ZdoMatchesAuthority(portal, authority))
            {
                PortalRulesPlugin.PortalRulesLogger.LogDebug(
                    $"Restoring server-authoritative access data for portal {portal.m_uid}.");
                WriteAuthorityToZdo(portal, authority, forceSend: true);
            }

            if (!PublicPortalKinds.IsHandledPortal(portal))
            {
                continue;
            }

            Vector3 position = portal.GetPosition();
            Quaternion rotation = portal.GetRotation();
            if (!IsSafePortalPosition(position) ||
                !TryNormalizePortalRotation(
                    rotation,
                    out Quaternion normalizedRotation))
            {
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    $"Skipped portal {portal.m_uid} with invalid transform data.");
                continue;
            }

            snapshot.Add(new PublicPortalCatalogEntry(
                portal.m_uid,
                authority.FavoriteId,
                authority.PrefabHash,
                PublicPortalKinds.PrefabAllowsAllItems(authority.PrefabHash),
                position,
                normalizedRotation,
                Truncate(portal.GetString(ZDOVars.s_tag, ""), MaximumTagLength),
                authority.AccessMode,
                authority.Owner));
        }

        if (revertedTemporaryPublicPortals > 0)
        {
            PortalRulesPlugin.PortalRulesLogger.LogInfo(
                $"Returned {revertedTemporaryPublicPortals} expired Public portal(s) to their Builder's Personal access.");
        }

        if (returnedDisabledInvitePortals > 0)
        {
            PortalRulesPlugin.PortalRulesLogger.LogInfo(
                $"Returned {returnedDisabledInvitePortals} Invite portal(s) with an effective zero Invite limit to their Builder's Personal access.");
        }

        foreach (ZDOID staleId in ServerAuthority.Keys
                     .Where(id => !livePortalIds.Contains(id))
                     .ToArray())
        {
            RemoveServerAuthority(staleId);
        }

        foreach (ZDOID staleId in InitialWorldPortalIds
                     .Where(id => !livePortalIds.Contains(id))
                     .ToArray())
        {
            InitialWorldPortalIds.Remove(staleId);
        }

        foreach (ZDOID staleId in PendingRemovalIds
                     .Where(id => !livePortalIds.Contains(id))
                     .ToArray())
        {
            PendingRemovalIds.Remove(staleId);
        }

        ApplyModeDisplayMetadata(snapshot);
        snapshot.Sort((left, right) => left.Id.CompareTo(right.Id));

        if (snapshot.Count > MaximumCatalogEntries)
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                $"Portal catalog exceeded {MaximumCatalogEntries} entries; truncating the snapshot.");
            snapshot.RemoveRange(MaximumCatalogEntries, snapshot.Count - MaximumCatalogEntries);
        }

        _authorityBackfilled = true;
        bool firstSnapshot = !_hasSnapshot;
        _hasSnapshot = true;
        if (!firstSnapshot && SnapshotsMatch(ServerEntries, snapshot))
        {
            return false;
        }

        ServerEntries.Clear();
        ServerEntries.AddRange(snapshot);
        _serverRevision++;
        return true;
    }

    private static ServerPortalAuthority ReadInitialAuthority(ZDO portal)
    {
        string favoriteId = ReadInitialFavoriteId(portal);
        int authorityVersion =
            portal.GetInt(PublicPortalData.AuthorityVersionKey, 0);
        if (PublicPortalKinds.IsAdminPortalPrefab(portal.GetPrefab()))
        {
            return CreateAdminPortalAuthority(
                portal,
                favoriteId: favoriteId,
                requiredGlobalKey: ResolveInitialAdminRequiredGlobalKey(portal));
        }

        int rawMode = portal.GetInt(PublicPortalData.AccessModeKey, -1);
        bool modeIsValid =
            Enum.IsDefined(typeof(PublicPortalAccessMode), rawMode) &&
            (rawMode != (int)PublicPortalAccessMode.Invite ||
             authorityVersion >= PublicPortalData.InviteAccessAuthorityVersion);
        PortalOwner storedOwner = SanitizeOwner(PublicPortalData.GetOwner(portal));
        bool serverStamped = authorityVersion >= 1;
        string storedAuthorizedClanId =
            authorityVersion >= PublicPortalData.ClanAccessAuthorityVersion &&
            storedOwner.IsValid &&
            rawMode is (int)PublicPortalAccessMode.Clan or
                (int)PublicPortalAccessMode.Tagged
                ? SanitizeClanId(PublicPortalData.GetAuthorizedClanId(portal))
                : "";
        int builderAuthorityVersion =
            portal.GetInt(PublicPortalData.BuilderAuthorityVersionKey, 0);
        bool builderStamped =
            builderAuthorityVersion >=
            PublicPortalData.CurrentBuilderAuthorityVersion;
        PortalBuilder storedBuilder = builderStamped
            ? SanitizeBuilder(PublicPortalData.GetBuilder(portal))
            : new PortalBuilder("", "");
        int prefabHash = builderStamped
            ? portal.GetInt(PublicPortalData.AuthorizedPrefabHashKey, portal.GetPrefab())
            : portal.GetPrefab();
        if (prefabHash == 0)
        {
            prefabHash = portal.GetPrefab();
        }
        if (!serverStamped && storedOwner.IsValid)
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                $"Portal {portal.m_uid} has legacy ownership that was not server-authorized; " +
                "a server admin must reclaim it.");
            storedOwner = new PortalOwner("", "");
        }

        if (!storedBuilder.IsValid &&
            TryCreateRegistryBuilder(portal, out PortalBuilder registryBuilder))
        {
            storedBuilder = registryBuilder;
        }

        bool builderRequiredMode =
            rawMode == (int)PublicPortalAccessMode.Invite ||
            rawMode == (int)PublicPortalAccessMode.Clan ||
            rawMode == (int)PublicPortalAccessMode.Tagged ||
            rawMode == (int)PublicPortalAccessMode.Public &&
            (storedOwner.IsValid ||
             portal.GetLong(ZDOVars.s_creator, 0L) != 0L);
        if (modeIsValid && builderRequiredMode && !storedBuilder.IsValid)
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                $"Portal {portal.m_uid} had {((PublicPortalAccessMode)rawMode)} access " +
                "without valid current Builder authority; it was changed to Personal.");
            rawMode = (int)PublicPortalAccessMode.Personal;
            storedAuthorizedClanId = "";
        }

        if (modeIsValid && (serverStamped || storedOwner.IsValid))
        {
            return new ServerPortalAuthority(
                (PublicPortalAccessMode)rawMode,
                storedOwner,
                storedAuthorizedClanId,
                storedBuilder,
                prefabHash,
                favoriteId,
                "",
                authorityVersion >= PublicPortalData.TemporaryPublicAuthorityVersion
                    ? PublicPortalData.GetPublicExpiresAtUtcSeconds(portal)
                    : 0L);
        }

        if (modeIsValid)
        {
            return new ServerPortalAuthority(
                (PublicPortalAccessMode)rawMode,
                new PortalOwner("", ""),
                "",
                storedBuilder,
                prefabHash,
                favoriteId,
                "",
                authorityVersion >= PublicPortalData.TemporaryPublicAuthorityVersion
                    ? PublicPortalData.GetPublicExpiresAtUtcSeconds(portal)
                    : 0L);
        }

        return new ServerPortalAuthority(
            PublicPortalAccessMode.Personal,
            new PortalOwner("", ""),
            "",
            storedBuilder,
            prefabHash,
            favoriteId,
            "",
            0L);
    }

    private static bool TryCreateRegistryBuilder(
        ZDO portal,
        out PortalBuilder builder,
        string fallbackName = "")
    {
        builder = new PortalBuilder("", "");
        if (portal == null ||
            PublicPortalKinds.IsAdminPortalPrefab(portal.GetPrefab()))
        {
            return false;
        }

        long creatorPlayerId = portal.GetLong(ZDOVars.s_creator, 0L);
        if (creatorPlayerId == 0L ||
            !PortalAccountStore.TryResolveSteamId(
                creatorPlayerId,
                out string steamId))
        {
            return false;
        }

        string creatorName = portal.GetString(ZDOVars.s_creatorName, "");
        builder = SanitizeBuilder(new PortalBuilder(
            steamId,
            string.IsNullOrWhiteSpace(creatorName) ? fallbackName : creatorName,
            creatorPlayerId,
            0L));
        return builder.IsValid;
    }

    private static bool TryBackfillAuthorityBuilder(
        ZDO portal,
        ServerPortalAuthority authority,
        out ServerPortalAuthority updatedAuthority,
        string fallbackName = "")
    {
        updatedAuthority = authority;
        if (authority.Builder.IsValid ||
            PublicPortalKinds.IsAdminPortalPrefab(authority.PrefabHash) ||
            !TryCreateRegistryBuilder(portal, out PortalBuilder builder, fallbackName))
        {
            return false;
        }

        updatedAuthority = new ServerPortalAuthority(
            authority.AccessMode,
            authority.Owner,
            authority.AuthorizedClanId,
            builder,
            authority.PrefabHash,
            authority.FavoriteId,
            authority.RequiredGlobalKey,
            authority.PublicExpiresAtUtcSeconds);
        return true;
    }

    private static void BackfillBuilderForCreator(
        long playerId,
        string steamId,
        string fallbackName)
    {
        if (playerId == 0L ||
            ZDOMan.instance == null ||
            !PublicPortalData.TryNormalizeSteamId64(steamId, out string canonicalSteamId))
        {
            return;
        }

        int changed = 0;
        foreach (KeyValuePair<ZDOID, ServerPortalAuthority> pair in
                 ServerAuthority.ToArray())
        {
            if (pair.Value.Builder.IsValid ||
                PublicPortalKinds.IsAdminPortalPrefab(pair.Value.PrefabHash))
            {
                continue;
            }

            ZDO? portal = ZDOMan.instance.GetZDO(pair.Key);
            if (portal == null ||
                !portal.IsValid() ||
                portal.GetLong(ZDOVars.s_creator, 0L) != playerId)
            {
                continue;
            }

            string creatorName = portal.GetString(ZDOVars.s_creatorName, "");
            PortalBuilder builder = SanitizeBuilder(new PortalBuilder(
                canonicalSteamId,
                string.IsNullOrWhiteSpace(creatorName) ? fallbackName : creatorName,
                playerId,
                0L));
            if (!builder.IsValid)
            {
                continue;
            }

            ServerPortalAuthority updatedAuthority = new(
                pair.Value.AccessMode,
                pair.Value.Owner,
                pair.Value.AuthorizedClanId,
                builder,
                pair.Value.PrefabHash,
                pair.Value.FavoriteId,
                pair.Value.RequiredGlobalKey,
                pair.Value.PublicExpiresAtUtcSeconds);
            if (TryNormalizeTemporaryPublicAuthority(
                    updatedAuthority,
                    PublicPortalData.GetUtcNowSeconds(),
                    out ServerPortalAuthority normalizedAuthority,
                    out _))
            {
                updatedAuthority = normalizedAuthority;
            }
            SetAndPersistServerAuthority(portal, updatedAuthority, forceSend: true);
            changed++;
        }

        if (changed > 0)
        {
            PortalRulesPlugin.PortalRulesLogger.LogInfo(
                $"Attributed {changed} existing portal(s) from playerID {playerId} " +
                $"to SteamID64 {canonicalSteamId}.");
            MarkServerCatalogDirty();
        }
    }

    private static ServerPortalAuthority CreateAuthorityForNewPortal(ZDO portal)
    {
        if (PublicPortalKinds.IsAdminPortalPrefab(portal.GetPrefab()))
        {
            return CreateAdminPortalAuthority(
                portal,
                requestedMode: PublicPortalAccessMode.Public,
                favoriteId: CreateServerFavoriteId(),
                requiredGlobalKey: ResolveInitialAdminRequiredGlobalKey(portal));
        }

        if (TryCreateServerLocalCreatorlessBlueprintAuthority(
                portal,
                out ServerPortalAuthority blueprintAuthority))
        {
            return blueprintAuthority;
        }

        return new ServerPortalAuthority(
            PublicPortalAccessMode.Personal,
            new PortalOwner("", ""),
            "",
            new PortalBuilder("", ""),
            portal.GetPrefab(),
            CreateServerFavoriteId(),
            "",
            0L);
    }

    private static bool TryCreateServerLocalCreatorlessBlueprintAuthority(
        ZDO portal,
        out ServerPortalAuthority authority)
    {
        authority = default;
        int prefabHash = portal.GetPrefab();
        // Blueprint systems copy arbitrary ZDO fields. Import an access mode only
        // from a complete current PortalRules stamp on a ZDO created by this
        // server session; identity, quota, expiry, and favorite data never cross.
        if (_activePortalSync != null ||
            ZDOMan.instance == null ||
            PublicPortalKinds.IsAdminPortalPrefab(prefabHash) ||
            portal.GetLong(ZDOVars.s_creator, 0L) != 0L ||
            portal.m_uid.UserID != ZDOMan.GetSessionID() ||
            portal.GetInt(PublicPortalData.AuthorityVersionKey, 0) !=
            PublicPortalData.CurrentAccessAuthorityVersion ||
            portal.GetInt(PublicPortalData.BuilderAuthorityVersionKey, 0) !=
            PublicPortalData.CurrentBuilderAuthorityVersion ||
            portal.GetInt(PublicPortalData.AuthorizedPrefabHashKey, 0) !=
            prefabHash)
        {
            return false;
        }

        int rawMode = portal.GetInt(PublicPortalData.AccessModeKey, -1);
        if (rawMode != (int)PublicPortalAccessMode.Public)
        {
            return false;
        }

        PublicPortalAccessMode mode = (PublicPortalAccessMode)rawMode;
        authority = new ServerPortalAuthority(
            mode,
            new PortalOwner("", ""),
            "",
            new PortalBuilder("", ""),
            prefabHash,
            CreateServerFavoriteId(),
            "",
            0L);
        PortalRulesPlugin.PortalRulesLogger.LogDebug(
            $"Restored {mode} access for server-created creatorless blueprint " +
            $"portal {portal.m_uid}; copied identity and favorite data were discarded.");
        return true;
    }

    private static string ReadInitialFavoriteId(ZDO portal)
    {
        string storedFavoriteId = portal.GetString(PublicPortalData.FavoriteIdKey, "");
        if (PublicPortalData.TryNormalizeFavoriteId(
                storedFavoriteId,
                out string normalizedFavoriteId) &&
            !ServerPortalsByFavoriteId.ContainsKey(normalizedFavoriteId))
        {
            return normalizedFavoriteId;
        }

        if (!string.IsNullOrWhiteSpace(storedFavoriteId))
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                $"Portal {portal.m_uid} had an invalid or duplicate favorite ID; " +
                "the server assigned a new one.");
        }

        return CreateServerFavoriteId();
    }

    private static string CreateServerFavoriteId()
    {
        string favoriteId;
        do
        {
            favoriteId = Guid.NewGuid().ToString("N");
        }
        while (ServerPortalsByFavoriteId.ContainsKey(favoriteId));

        return favoriteId;
    }

    private static bool ZdoMatchesAuthority(ZDO portal, ServerPortalAuthority authority)
    {
        PortalOwner currentOwner = PublicPortalData.GetOwner(portal);
        PortalBuilder currentBuilder = PublicPortalData.GetBuilder(portal);
        bool adminCreatorIsEmpty =
            !PublicPortalKinds.IsAdminPortalPrefab(authority.PrefabHash) ||
            portal.GetLong(ZDOVars.s_creator, 0L) == 0L &&
            string.IsNullOrEmpty(portal.GetString(ZDOVars.s_creatorName, ""));
        bool adminPortalIsServerOwned =
            !PublicPortalKinds.IsAdminPortalPrefab(authority.PrefabHash) ||
            ZNet.instance != null &&
            ZNet.instance.IsServer() &&
            portal.GetOwner() == ZDOMan.GetSessionID();
        bool adminTagMatches =
            !PublicPortalKinds.IsAdminPortalPrefab(authority.PrefabHash) ||
            AdminPortalTags.TryGetValue(portal.m_uid, out AdminPortalTagAuthority adminTag) &&
            string.Equals(
                portal.GetString(ZDOVars.s_tag, ""),
                adminTag.Tag,
                StringComparison.Ordinal) &&
            string.IsNullOrEmpty(portal.GetString(ZDOVars.s_tagauthor, ""));
        bool requiredGlobalKeyMatches = string.Equals(
            portal.GetString(PublicPortalData.RequiredGlobalKeyKey, ""),
            authority.RequiredGlobalKey,
            StringComparison.Ordinal);
        bool publicExpirationMatches =
            PublicPortalData.GetPublicExpiresAtUtcSeconds(portal) ==
            authority.PublicExpiresAtUtcSeconds;
        return adminCreatorIsEmpty &&
               adminPortalIsServerOwned &&
               adminTagMatches &&
               requiredGlobalKeyMatches &&
               publicExpirationMatches &&
               string.Equals(
                   portal.GetString(PublicPortalData.FavoriteIdKey, ""),
                   authority.FavoriteId,
                   StringComparison.Ordinal) &&
               portal.GetInt(PublicPortalData.AuthorityVersionKey, 0) >=
               PublicPortalData.CurrentAccessAuthorityVersion &&
               portal.GetInt(PublicPortalData.BuilderAuthorityVersionKey, 0) >=
               PublicPortalData.CurrentBuilderAuthorityVersion &&
               portal.GetInt(PublicPortalData.AccessModeKey, -1) == (int)authority.AccessMode &&
               string.Equals(currentOwner.Id, authority.Owner.Id, StringComparison.Ordinal) &&
               string.Equals(currentOwner.Name, authority.Owner.Name, StringComparison.Ordinal) &&
               string.Equals(
                   SanitizeClanId(PublicPortalData.GetAuthorizedClanId(portal)),
                   authority.AuthorizedClanId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   currentBuilder.AccountId,
                   authority.Builder.AccountId,
                   StringComparison.Ordinal) &&
               string.Equals(currentBuilder.Name, authority.Builder.Name, StringComparison.Ordinal) &&
               currentBuilder.CharacterPlayerId ==
               authority.Builder.CharacterPlayerId &&
               currentBuilder.BuildSequence == authority.Builder.BuildSequence &&
               portal.GetInt(PublicPortalData.AuthorizedPrefabHashKey, 0) == authority.PrefabHash &&
               portal.GetPrefab() == authority.PrefabHash;
    }

    private static void WriteAuthorityToZdo(
        ZDO portal,
        ServerPortalAuthority authority,
        bool forceSend)
    {
        portal.SetPrefab(authority.PrefabHash);
        PublicPortalData.SetAccessMode(portal, authority.AccessMode, authority.Owner);
        PublicPortalData.SetAuthorizedClanId(portal, authority.AuthorizedClanId);
        PublicPortalData.SetBuilder(portal, authority.Builder, authority.PrefabHash);
        PublicPortalData.SetFavoriteId(portal, authority.FavoriteId);
        PublicPortalData.SetRequiredGlobalKey(
            portal,
            authority.RequiredGlobalKey);
        PublicPortalData.SetPublicExpiresAtUtcSeconds(
            portal,
            authority.PublicExpiresAtUtcSeconds);
        if (PublicPortalKinds.IsAdminPortalPrefab(authority.PrefabHash))
        {
            EnsureAdminPortalTagAuthority(portal);
            AdminPortalTagAuthority adminTag = AdminPortalTags[portal.m_uid];
            portal.Set(ZDOVars.s_creator, 0L);
            portal.Set(ZDOVars.s_creatorName, "");
            portal.Set(ZDOVars.s_tag, adminTag.Tag);
            portal.Set(ZDOVars.s_tagauthor, "");
            if (ZNet.instance != null && ZNet.instance.IsServer())
            {
                portal.SetOwner(ZDOMan.GetSessionID());
            }
        }

        portal.Set(
            PublicPortalData.AuthorityVersionKey,
            PublicPortalData.CurrentAccessAuthorityVersion);
        portal.Set(
            PublicPortalData.BuilderAuthorityVersionKey,
            PublicPortalData.CurrentBuilderAuthorityVersion);
        if (forceSend && ZDOMan.instance != null)
        {
            ZDOMan.instance.ForceSendZDO(portal.m_uid);
        }
    }

    private static void SetAndPersistServerAuthority(
        ZDO portal,
        ServerPortalAuthority authority,
        bool forceSend)
    {
        if (PublicPortalKinds.IsAdminPortalPrefab(portal.GetPrefab()))
        {
            EnsureAdminPortalTagAuthority(portal);
            authority = CreateAdminPortalAuthority(
                portal,
                authority.AccessMode,
                authority.FavoriteId,
                authority.RequiredGlobalKey);
        }

        authority = SetServerAuthority(portal.m_uid, authority);
        WriteAuthorityToZdo(portal, authority, forceSend);
    }

    private static ServerPortalAuthority CreateAdminPortalAuthority(
        ZDO portal,
        PublicPortalAccessMode? requestedMode = null,
        string? favoriteId = null,
        string? requiredGlobalKey = null)
    {
        PublicPortalAccessMode mode = requestedMode ??
                                      (PublicPortalAccessMode)portal.GetInt(
                                          PublicPortalData.AccessModeKey,
                                          (int)PublicPortalAccessMode.Public);
        if (!IsAllowedAdminPortalMode(mode))
        {
            mode = PublicPortalAccessMode.Public;
        }

        return new ServerPortalAuthority(
            mode,
            new PortalOwner("", ""),
            "",
            new PortalBuilder("", ""),
            portal.GetPrefab(),
            PublicPortalData.TryNormalizeFavoriteId(
                favoriteId,
                out string normalizedFavoriteId)
                ? normalizedFavoriteId
                : CreateServerFavoriteId(),
            requiredGlobalKey ?? "",
            0L);
    }

    private static string ResolveInitialAdminRequiredGlobalKey(ZDO portal)
    {
        // The out-value overload distinguishes a missing field from a deliberately
        // stored empty value. Empty therefore remains an explicit override and is
        // also how an Alt+E clear stays stable across scans and restarts.
        if (portal.GetString(
                PublicPortalData.RequiredGlobalKeyKey,
                out string explicitValue))
        {
            if (PublicPortalData.TryNormalizeRequiredGlobalKey(
                    explicitValue,
                    out string normalizedExplicitValue,
                    out RequiredGlobalKeyValidationFailure failure))
            {
                return normalizedExplicitValue;
            }

            PortalRulesPlugin.PortalRulesLogger.LogError(
                $"Admin portal {portal.m_uid} has an invalid explicit Required GlobalKey " +
                $"({failure}); preserving it so access fails closed.");
            return (explicitValue ?? "").Trim();
        }

        return AdminPortalBiomeDefaults.TryResolve(
            portal.GetPosition(),
            out string biomeDefault)
            ? biomeDefault
            : "";
    }

    private static bool CanInitializeAdminPortalAuthority(ZDO portal)
    {
        // An explicitly stored value (including an intentional empty value) is a
        // complete blueprint/location override and does not depend on biome data.
        return portal.GetString(
                   PublicPortalData.RequiredGlobalKeyKey,
                   out _) ||
               AdminPortalBiomeDefaults.HasValidSnapshot;
    }

    private static bool IsAllowedAdminPortalMode(PublicPortalAccessMode mode)
    {
        return mode is PublicPortalAccessMode.Admin or
            PublicPortalAccessMode.Public or
            PublicPortalAccessMode.Tagged;
    }

    private static long CreateTemporaryPublicExpirationUtcSeconds(
        PublicPortalAccessMode accessMode,
        PortalBuilder builder,
        int prefabHash)
    {
        return CreateTemporaryPublicExpirationUtcSeconds(
            accessMode,
            builder,
            prefabHash,
            PublicPortalData.GetUtcNowSeconds());
    }

    private static long CreateTemporaryPublicExpirationUtcSeconds(
        PublicPortalAccessMode accessMode,
        PortalBuilder builder,
        int prefabHash,
        long nowUtcSeconds)
    {
        int durationSeconds = PublicPortalConfig.PublicAccessDurationSeconds.Value;
        if (accessMode != PublicPortalAccessMode.Public ||
            !builder.IsValid ||
            PublicPortalKinds.IsAdminPortalPrefab(prefabHash) ||
            durationSeconds <= 0)
        {
            return 0L;
        }

        return Math.Min(
            PublicPortalData.MaximumUtcSeconds,
            nowUtcSeconds + durationSeconds);
    }

    private static bool IsTemporaryPublicAccessExpired(
        ServerPortalAuthority authority,
        long nowUtcSeconds)
    {
        return PublicPortalConfig.PublicAccessDurationSeconds.Value > 0 &&
               authority.AccessMode == PublicPortalAccessMode.Public &&
               authority.Builder.IsValid &&
               !PublicPortalKinds.IsAdminPortalPrefab(
                   authority.PrefabHash) &&
               (authority.PublicExpiresAtUtcSeconds == 0L ||
                authority.PublicExpiresAtUtcSeconds <= nowUtcSeconds);
    }

    private static bool TryNormalizeTemporaryPublicAuthority(
        ServerPortalAuthority authority,
        long nowUtcSeconds,
        out ServerPortalAuthority updatedAuthority,
        out bool revertedToPersonal)
    {
        updatedAuthority = authority;
        revertedToPersonal = false;
        bool isTemporaryPublicPortal =
            authority.AccessMode == PublicPortalAccessMode.Public &&
            authority.Builder.IsValid &&
            !PublicPortalKinds.IsAdminPortalPrefab(
                authority.PrefabHash);
        if (!isTemporaryPublicPortal ||
            PublicPortalConfig.PublicAccessDurationSeconds.Value <= 0)
        {
            if (authority.PublicExpiresAtUtcSeconds == 0L)
            {
                return false;
            }

            updatedAuthority = new ServerPortalAuthority(
                authority.AccessMode,
                authority.Owner,
                authority.AuthorizedClanId,
                authority.Builder,
                authority.PrefabHash,
                authority.FavoriteId,
                authority.RequiredGlobalKey,
                0L);
            return true;
        }

        if (!IsTemporaryPublicAccessExpired(authority, nowUtcSeconds))
        {
            return false;
        }

        updatedAuthority = CreateBuilderPersonalAuthority(authority);
        revertedToPersonal = true;
        return true;
    }

    private static bool TryNormalizeDisabledInviteAuthority(
        ServerPortalAuthority authority,
        out ServerPortalAuthority updatedAuthority)
    {
        updatedAuthority = authority;
        if (!PortalAccountStore.HasValidOverrideSnapshot ||
            authority.AccessMode != PublicPortalAccessMode.Invite ||
            !authority.Builder.IsValid ||
            PublicPortalKinds.IsAdminPortalPrefab(authority.PrefabHash) ||
            PortalAccountStore.GetEffectiveInvitePortalLimit(
                authority.Builder.AccountId,
                PublicPortalConfig.MaxInvitePortalsPerAccount.Value) != 0)
        {
            return false;
        }

        updatedAuthority = CreateBuilderPersonalAuthority(authority);
        return true;
    }

    private static ServerPortalAuthority CreateBuilderPersonalAuthority(
        ServerPortalAuthority authority)
    {
        return new ServerPortalAuthority(
            PublicPortalAccessMode.Personal,
            new PortalOwner(
                authority.Builder.AccountId,
                authority.Builder.Name),
            "",
            authority.Builder,
            authority.PrefabHash,
            authority.FavoriteId,
            authority.RequiredGlobalKey,
            0L);
    }

    private static void EnsureAdminPortalTagAuthority(ZDO portal)
    {
        if (!AdminPortalTags.ContainsKey(portal.m_uid))
        {
            AdminPortalTags.Add(
                portal.m_uid,
                new AdminPortalTagAuthority(
                    Truncate(portal.GetString(ZDOVars.s_tag, ""), 10)));
        }
    }

    private static RecipientContext CreateRecipientContext(ZNetPeer peer)
    {
        PortalOwner owner = PublicPortalData.TryGetPeerOwner(
            peer,
            out PortalOwner authenticatedOwner)
                ? authenticatedOwner
                : new PortalOwner("", "");
        bool isAdmin =
            ZNet.instance != null &&
            PublicPortalData.IsPeerAdmin(ZNet.instance, peer);
        return CreateRecipientContext(
            owner,
            isAdmin,
            ResolvePeerMembership(peer),
            peer);
    }

    private static RecipientContext CreateRecipientContext(
        PortalOwner owner,
        bool isAdmin,
        PortalClanMembership clanMembership,
        ZNetPeer? peer = null)
    {
        return new RecipientContext(
            SanitizeOwner(owner).Id,
            isAdmin,
            clanMembership,
            peer);
    }

    private static bool TryCreateRemoteAuthorizationContext(
        ZNetPeer? peer,
        out RecipientContext recipient,
        out Vector3 playerPosition)
    {
        recipient = null!;
        playerPosition = Vector3.positiveInfinity;
        if (!PublicPortalData.TryGetPeerOwner(peer, out PortalOwner owner) ||
            !PublicPortalData.TryGetAuthenticatedPeerCharacterPosition(
                peer,
                out playerPosition))
        {
            return false;
        }

        recipient = CreateRecipientContext(
            owner,
            PublicPortalData.IsPeerAdmin(ZNet.instance, peer!),
            ResolvePeerMembership(peer),
            peer);
        return true;
    }

    private static void CreateLocalAuthorizationContext(
        out RecipientContext recipient,
        out Vector3 playerPosition)
    {
        playerPosition = Player.m_localPlayer != null
            ? Player.m_localPlayer.transform.position
            : Vector3.positiveInfinity;
        recipient = CreateRecipientContext(
            PublicPortalData.LocalOwner(),
            PortalRulesPlugin.IsAdmin,
            ResolveLocalMembership());
    }

    private static void RefreshLocalRecipientView(bool catalogChanged)
    {
        if (ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            Player.m_localPlayer == null)
        {
            ResetLocalRecipientViewState();
            return;
        }

        RecipientContext recipient = CreateRecipientContext(
            PublicPortalData.LocalOwner(),
            PortalRulesPlugin.IsAdmin,
            ResolveLocalMembership());
        List<PublicPortalCatalogEntry> recipientEntries =
            BuildRecipientEntries(recipient, out ulong fingerprint);
        bool changed = !_localRecipientViewInitialized ||
                       !string.Equals(
                            _localRecipientOwnerId,
                            recipient.OwnerId,
                            StringComparison.Ordinal) ||
                       _localRecipientIsAdmin != recipient.IsAdmin ||
                       _localRecipientViewFingerprint != fingerprint;

        _localRecipientViewInitialized = true;
        _localRecipientOwnerId = recipient.OwnerId;
        _localRecipientIsAdmin = recipient.IsAdmin;
        _localRecipientViewFingerprint = fingerprint;
        if (catalogChanged || changed)
        {
            ApplyLocalClientEntries(recipientEntries, forceUpdate: true);
        }
    }

    private static void ResetLocalRecipientViewState()
    {
        _localRecipientViewInitialized = false;
        _localRecipientOwnerId = "";
        _localRecipientIsAdmin = false;
        _localRecipientViewFingerprint = 0UL;
    }

    private static PortalClanMembership ResolvePeerMembership(ZNetPeer? peer)
    {
        return ClanPortalAccess.TryResolvePeerMembership(
            peer,
            out PortalClanMembership membership)
                ? membership
                : default;
    }

    private static PortalClanMembership ResolveLocalMembership()
    {
        return ClanPortalAccess.TryResolveLocalMembership(
            out PortalClanMembership membership)
                ? membership
                : default;
    }

    private static bool ShouldSendEntry(
        PublicPortalCatalogEntry entry,
        RecipientContext recipient)
    {
        return ServerAuthority.TryGetValue(
                   entry.Id,
                   out ServerPortalAuthority authority) &&
               CanRecipientUseAuthority(authority, recipient);
    }

    private static void ApplyModeDisplayMetadata(
        List<PublicPortalCatalogEntry> entries)
    {
        Dictionary<ZDOID, (int Ordinal, int Current, int Limit)> metadata = new();
        IEnumerable<IGrouping<string, KeyValuePair<ZDOID, ServerPortalAuthority>>>
            inviteGroups = ServerAuthority
                .Where(pair =>
                    !PendingRemovalIds.Contains(pair.Key) &&
                    pair.Value.AccessMode == PublicPortalAccessMode.Invite &&
                    pair.Value.Builder.IsValid &&
                    !PublicPortalKinds.IsAdminPortalPrefab(
                        pair.Value.PrefabHash))
                .GroupBy(
                    pair => pair.Value.Builder.AccountId,
                    StringComparer.Ordinal);
        foreach (IGrouping<string, KeyValuePair<ZDOID, ServerPortalAuthority>> group in
                 inviteGroups)
        {
            List<KeyValuePair<ZDOID, ServerPortalAuthority>> ordered =
                OrderAuthorityByBuildSequence(group);
            int limit = PortalAccountStore.GetEffectiveInvitePortalLimit(
                group.Key,
                PublicPortalConfig.MaxInvitePortalsPerAccount.Value);
            for (int i = 0; i < ordered.Count; i++)
            {
                metadata[ordered[i].Key] = (i + 1, ordered.Count, limit);
            }
        }

        int clanLimit = Math.Max(
            -1,
            Math.Min(
                PublicPortalData.MaximumPortalLimit,
                PublicPortalConfig.MaxClanPortalsPerClan.Value));
        IEnumerable<IGrouping<string, KeyValuePair<ZDOID, ServerPortalAuthority>>>
            clanGroups = ServerAuthority
                .Where(pair =>
                    !PendingRemovalIds.Contains(pair.Key) &&
                    pair.Value.AccessMode == PublicPortalAccessMode.Clan &&
                    pair.Value.Builder.IsValid &&
                    !string.IsNullOrWhiteSpace(pair.Value.AuthorizedClanId) &&
                    !PublicPortalKinds.IsAdminPortalPrefab(
                        pair.Value.PrefabHash))
                .GroupBy(
                    pair => pair.Value.AuthorizedClanId,
                    StringComparer.Ordinal);
        foreach (IGrouping<string, KeyValuePair<ZDOID, ServerPortalAuthority>> group in
                 clanGroups)
        {
            List<KeyValuePair<ZDOID, ServerPortalAuthority>> ordered =
                OrderAuthorityByBuildSequence(group);
            for (int i = 0; i < ordered.Count; i++)
            {
                metadata[ordered[i].Key] = (i + 1, ordered.Count, clanLimit);
            }
        }

        for (int i = 0; i < entries.Count; i++)
        {
            PublicPortalCatalogEntry entry = entries[i];
            if (!metadata.TryGetValue(
                    entry.Id,
                    out (int Ordinal, int Current, int Limit) display))
            {
                continue;
            }

            entries[i] = entry.WithDisplayMetadata(
                0,
                0,
                display.Ordinal,
                display.Current,
                display.Limit);
        }
    }

    private static List<KeyValuePair<ZDOID, ServerPortalAuthority>>
        OrderAuthorityByBuildSequence(
            IEnumerable<KeyValuePair<ZDOID, ServerPortalAuthority>> authorities)
    {
        return authorities
            .OrderBy(pair => pair.Value.Builder.BuildSequence > 0L
                ? pair.Value.Builder.BuildSequence
                : long.MaxValue)
            .ThenBy(pair => pair.Value.FavoriteId, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key)
            .ToList();
    }

    private static List<PublicPortalCatalogEntry> BuildRecipientEntries(
        RecipientContext recipient)
    {
        Dictionary<ZDOID, int> myPortalOrdinals = new();
        int myPortalLimit = 0;
        if (PublicPortalData.TryNormalizeSteamId64(
                recipient.OwnerId,
                out string recipientAccountId))
        {
            List<KeyValuePair<ZDOID, ServerPortalAuthority>> mine =
                OrderAuthorityByBuildSequence(
                    ServerAuthority.Where(pair =>
                        !PendingRemovalIds.Contains(pair.Key) &&
                        pair.Value.Builder.IsValid &&
                        string.Equals(
                            pair.Value.Builder.AccountId,
                            recipientAccountId,
                            StringComparison.Ordinal) &&
                        PublicPortalServerPolicy.IsCountedPortalPrefab(
                            pair.Value.PrefabHash)));
            for (int i = 0; i < mine.Count; i++)
            {
                myPortalOrdinals[mine[i].Key] = i + 1;
            }

            myPortalLimit = PublicPortalConfig.EnableAccountPortalLimit.Value.IsOff()
                ? -1
                : PortalAccountStore.GetEffectivePortalLimit(
                    recipientAccountId,
                    PublicPortalConfig.MaxPortalsPerAccount.Value);
        }

        List<PublicPortalCatalogEntry> visible = new();
        foreach (PublicPortalCatalogEntry entry in ServerEntries)
        {
            if (!ShouldSendEntry(entry, recipient))
            {
                continue;
            }

            int myOrdinal = myPortalOrdinals.TryGetValue(
                entry.Id,
                out int ordinal)
                    ? ordinal
                    : 0;
            long departureUntilUtc = 0L;
            long arrivalUntilUtc = 0L;
            if (entry.AccessMode == PublicPortalAccessMode.Invite &&
                !string.IsNullOrWhiteSpace(recipient.OwnerId))
            {
                InviteTravelCooldownStore.GetServerDeadlines(
                    recipient.OwnerId,
                    entry.FavoriteId,
                    out departureUntilUtc,
                    out arrivalUntilUtc);
            }

            visible.Add(entry.WithDisplayMetadata(
                myOrdinal,
                myOrdinal > 0 ? myPortalLimit : 0,
                entry.ModeOrdinal,
                entry.ModeCurrent,
                entry.ModeLimit,
                departureUntilUtc,
                arrivalUntilUtc));
        }

        return visible;
    }

    private static List<PublicPortalCatalogEntry> BuildRecipientEntries(
        RecipientContext recipient,
        out ulong fingerprint)
    {
        List<PublicPortalCatalogEntry> visible = BuildRecipientEntries(recipient);
        fingerprint = ComputeRecipientViewFingerprint(visible);
        return visible;
    }

    private static void RefreshLocalClientEntries(bool forceUpdate)
    {
        if (ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            Player.m_localPlayer == null)
        {
            if (ClientEntries.Count > 0)
            {
                ClientEntries.Clear();
                ClientIndex.Clear();
                if (forceUpdate)
                {
                    Updated?.Invoke();
                }
            }

            return;
        }

        RecipientContext recipient = CreateRecipientContext(
            PublicPortalData.LocalOwner(),
            PortalRulesPlugin.IsAdmin,
            ResolveLocalMembership());
        List<PublicPortalCatalogEntry> received =
            BuildRecipientEntries(recipient, out ulong fingerprint);
        _localRecipientViewInitialized = true;
        _localRecipientOwnerId = recipient.OwnerId;
        _localRecipientIsAdmin = recipient.IsAdmin;
        _localRecipientViewFingerprint = fingerprint;
        ApplyLocalClientEntries(received, forceUpdate);
    }

    private static void ApplyLocalClientEntries(
        List<PublicPortalCatalogEntry> received,
        bool forceUpdate)
    {
        bool changed = !SnapshotsMatch(ClientEntries, received);
        _clientRevision = _serverRevision;
        if (!changed && !forceUpdate)
        {
            return;
        }

        ClientEntries.Clear();
        ClientEntries.AddRange(received);
        RebuildClientIndex();
        Updated?.Invoke();
    }

    private static ulong ComputeRecipientViewFingerprint(
        IReadOnlyList<PublicPortalCatalogEntry> entries)
    {
        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong fingerprint = offsetBasis;
        HashFingerprintInt(ref fingerprint, entries.Count, prime);
        foreach (PublicPortalCatalogEntry entry in entries)
        {
            HashFingerprintInt(ref fingerprint, entry.FavoriteId.Length, prime);
            foreach (char character in entry.FavoriteId)
            {
                fingerprint ^= character;
                fingerprint *= prime;
            }

            HashFingerprintInt(ref fingerprint, (int)entry.AccessMode, prime);
            HashFingerprintInt(
                ref fingerprint,
                entry.AllowsAllItems ? 1 : 0,
                prime);
            HashFingerprintInt(ref fingerprint, entry.MyPortalOrdinal, prime);
            HashFingerprintInt(ref fingerprint, entry.MyPortalLimit, prime);
            HashFingerprintInt(ref fingerprint, entry.ModeOrdinal, prime);
            HashFingerprintInt(ref fingerprint, entry.ModeCurrent, prime);
            HashFingerprintInt(ref fingerprint, entry.ModeLimit, prime);
            HashFingerprintLong(
                ref fingerprint,
                entry.InviteDepartureCooldownUntilUtc,
                prime);
            HashFingerprintLong(
                ref fingerprint,
                entry.InviteArrivalCooldownUntilUtc,
                prime);
        }

        return fingerprint;
    }

    private static void HashFingerprintInt(
        ref ulong fingerprint,
        int value,
        ulong prime)
    {
        HashFingerprintLong(ref fingerprint, value, prime);
    }

    private static void HashFingerprintLong(
        ref ulong fingerprint,
        long value,
        ulong prime)
    {
        ulong bits = unchecked((ulong)value);
        for (int i = 0; i < sizeof(long); i++)
        {
            fingerprint ^= (byte)bits;
            fingerprint *= prime;
            bits >>= 8;
        }
    }

    private static void ResetSessionState()
    {
        _sessionGeneration++;
        _serverLoopGame = null;
        _serverRevision = 0;
        _clientRevision = -1;
        _nextBuildSequence = 0L;
        _nextIdentityReconcileAt = -100f;
        _authorityBackfilled = false;
        _hasSnapshot = false;
        _worldLoaded = false;
        _serverCatalogDirty = false;
        _serverDirtyFlushScheduled = false;
        ResetLocalRecipientViewState();
        _activePortalSync = null;
        _activeLocalPlacementBuilder = new PortalBuilder("", "");
        _pendingLocalPlacementPortalIds = null;
        ServerEntries.Clear();
        ClientEntries.Clear();
        ClientIndex.Clear();
        ServerAuthority.Clear();
        ServerPortalsByFavoriteId.Clear();
        AdminPortalTags.Clear();
        ServerPortalsByBuilder.Clear();
        InitialWorldPortalIds.Clear();
        PendingRemovalIds.Clear();
        PublicPortalData.SetServerAssignedOwnerId("");
        PublicPortalData.SetServerAssignedIsAdmin(false);
        ResetCatalogNetworkState();
    }

    private static void RebuildClientIndex()
    {
        ClientIndex.Clear();
        foreach (PublicPortalCatalogEntry entry in ClientEntries)
        {
            ClientIndex[entry.Id] = entry;
        }
    }

    private static bool TryAuthorizeTeleport(
        RecipientContext recipient,
        Vector3 playerPosition,
        ZDOID sourcePortalId,
        ZDOID targetPortalId,
        bool requireConnectedPortal,
        string reservationId,
        out PortalTravelAuthorization authorization,
        out PortalTravelDenial denial)
    {
        authorization = default;
        denial = new PortalTravelDenial(PortalTravelDenialCode.Denied);
        if ((!requireConnectedPortal &&
             PublicPortalConfig.EnablePortalMap.Value.IsOff()) ||
            targetPortalId.IsNone() ||
            sourcePortalId == targetPortalId)
        {
            return false;
        }

        if (!TryAuthorizePortalSource(
                recipient,
                playerPosition,
                sourcePortalId,
                requireConnectedPortal,
                out ZDO sourcePortal,
                out ServerPortalAuthority sourceAuthority,
                out denial))
        {
            return false;
        }

        Vector3 sourcePosition = sourcePortal.GetPosition();
        if (!TryGetUsablePortal(
                targetPortalId,
                recipient,
                source: false,
                out ZDO targetPortal,
                out denial))
        {
            return false;
        }

        if (!ServerAuthority.TryGetValue(
                targetPortalId,
                out ServerPortalAuthority targetAuthority))
        {
            denial = new PortalTravelDenial(
                PortalTravelDenialCode.DestinationUnavailable);
            return false;
        }

        bool targetIsTagged =
            targetAuthority.AccessMode == PublicPortalAccessMode.Tagged;
        if (requireConnectedPortal &&
            PublicPortalConfig.EnablePortalMap.Value.IsOn() &&
            !targetIsTagged)
        {
            denial = new PortalTravelDenial(
                PortalTravelDenialCode.DestinationNotTagged);
            return false;
        }

        if (requireConnectedPortal &&
            (sourcePortal.GetConnectionZDOID(
                 ZDOExtraData.ConnectionType.Portal) != targetPortalId ||
             targetPortal.GetConnectionZDOID(
                 ZDOExtraData.ConnectionType.Portal) != sourcePortalId))
        {
            denial = new PortalTravelDenial(
                PortalTravelDenialCode.ConnectionChanged);
            return false;
        }

        Vector3 targetPosition = targetPortal.GetPosition();
        Quaternion targetRotation = targetPortal.GetRotation();
        if (!IsSafePortalPosition(targetPosition) ||
            !TryNormalizePortalRotation(
                targetRotation,
                out Quaternion normalizedTargetRotation))
        {
            targetPosition = Vector3.zero;
            targetRotation = Quaternion.identity;
            denial = new PortalTravelDenial(
                PortalTravelDenialCode.DestinationPositionInvalid);
            return false;
        }

        targetRotation = normalizedTargetRotation;
        int coinCost = PublicPortalTravelCost.CalculateCost(
            sourceAuthority.PrefabHash,
            sourceAuthority.AccessMode,
            PublicPortalKinds.PrefabAllowsAllItems(sourceAuthority.PrefabHash),
            sourcePosition,
            targetAuthority.PrefabHash,
            targetAuthority.AccessMode,
            targetPosition);
        bool inviteSource =
            sourceAuthority.AccessMode == PublicPortalAccessMode.Invite;
        bool inviteDestination =
            targetAuthority.AccessMode == PublicPortalAccessMode.Invite;
        if (!InviteTravelCooldownStore.TryReserve(
                reservationId,
                recipient.OwnerId,
                inviteSource,
                sourceAuthority.FavoriteId,
                inviteDestination,
                targetAuthority.FavoriteId,
                out InviteTravelCooldownReservation cooldownReservation,
                out InviteTravelCooldownFailure cooldownFailure,
                out long departureRemaining,
                out long arrivalRemaining))
        {
            targetPosition = Vector3.zero;
            targetRotation = Quaternion.identity;
            coinCost = 0;
            denial = CreateInviteCooldownDenial(
                cooldownFailure,
                departureRemaining,
                arrivalRemaining);
            return false;
        }

        authorization = new PortalTravelAuthorization(
            targetPosition,
            targetRotation,
            coinCost,
            recipient.OwnerId,
            sourceAuthority.FavoriteId,
            targetAuthority.FavoriteId,
            inviteSource,
            inviteDestination,
            cooldownReservation);
        denial = PortalTravelDenial.None;
        return true;
    }

    private static bool TryAuthorizePortalSource(
        RecipientContext recipient,
        Vector3 playerPosition,
        ZDOID sourcePortalId,
        bool requireConnectedPortal,
        out ZDO sourcePortal,
        out ServerPortalAuthority sourceAuthority,
        out PortalTravelDenial denial)
    {
        sourcePortal = null!;
        sourceAuthority = default;
        denial = new PortalTravelDenial(PortalTravelDenialCode.Denied);
        if (string.IsNullOrWhiteSpace(recipient.OwnerId) ||
            sourcePortalId.IsNone() ||
            ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            ZDOMan.instance == null)
        {
            return false;
        }

        if (ZoneSystem.instance == null)
        {
            denial = new PortalTravelDenial(PortalTravelDenialCode.NotReady);
            return false;
        }

        if (ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoPortals))
        {
            denial = new PortalTravelDenial(
                PortalTravelDenialCode.PortalsBlocked);
            return false;
        }

        if (ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoBossPortals) &&
            (RandEventSystem.instance?.GetBossEvent() != null ||
             ZoneSystem.instance.GetGlobalKey(
                 GlobalKeys.activeBosses,
                 out float activeBosses) &&
             activeBosses > 0f))
        {
            denial = new PortalTravelDenial(
                PortalTravelDenialCode.BossBlocked);
            return false;
        }

        if (!TryGetUsablePortal(
                sourcePortalId,
                recipient,
                source: true,
                out sourcePortal,
                out denial))
        {
            return false;
        }

        if (!ServerAuthority.TryGetValue(sourcePortalId, out sourceAuthority))
        {
            denial = new PortalTravelDenial(
                PortalTravelDenialCode.SourceUnavailable);
            return false;
        }

        bool sourceIsTagged =
            sourceAuthority.AccessMode == PublicPortalAccessMode.Tagged;
        if (requireConnectedPortal)
        {
            if (PublicPortalConfig.EnablePortalMap.Value.IsOn() &&
                !sourceIsTagged)
            {
                denial = new PortalTravelDenial(
                    PortalTravelDenialCode.SourceNotTagged);
                return false;
            }
        }
        else if (sourceIsTagged)
        {
            denial = new PortalTravelDenial(
                PortalTravelDenialCode.TaggedRequiresConnectedDestination);
            return false;
        }

        Vector3 sourcePosition = sourcePortal.GetPosition();
        if (!IsFinite(playerPosition) ||
            !IsSafePortalPosition(sourcePosition) ||
            Vector3.Distance(playerPosition, sourcePosition) >
            PublicPortalData.MaximumTeleportSourceDistance)
        {
            denial = new PortalTravelDenial(
                PortalTravelDenialCode.SourceTooFar);
            return false;
        }

        denial = PortalTravelDenial.None;
        return true;
    }

    private static PortalTravelDenial CreateInviteCooldownDenial(
        InviteTravelCooldownFailure failure,
        long departureRemaining,
        long arrivalRemaining)
    {
        return failure switch
        {
            InviteTravelCooldownFailure.DataUnavailable =>
                new PortalTravelDenial(
                    PortalTravelDenialCode.InviteCooldownDataUnavailable),
            InviteTravelCooldownFailure.StorageUnavailable =>
                new PortalTravelDenial(
                    PortalTravelDenialCode.InviteCooldownStorageUnavailable),
            InviteTravelCooldownFailure.Active =>
                new PortalTravelDenial(
                    PortalTravelDenialCode.InviteCooldownActive,
                    firstValue: Math.Max(0L, departureRemaining),
                    secondValue: Math.Max(0L, arrivalRemaining)),
            InviteTravelCooldownFailure.AccountCapacityReached =>
                new PortalTravelDenial(
                    PortalTravelDenialCode.InviteCooldownAccountCapacityReached),
            InviteTravelCooldownFailure.PortalCapacityReached =>
                new PortalTravelDenial(
                    PortalTravelDenialCode.InviteCooldownPortalCapacityReached),
            InviteTravelCooldownFailure.FileCapacityReached =>
                new PortalTravelDenial(
                    PortalTravelDenialCode.InviteCooldownFileCapacityReached),
            InviteTravelCooldownFailure.SaveFailed =>
                new PortalTravelDenial(
                    PortalTravelDenialCode.InviteCooldownSaveFailed),
            InviteTravelCooldownFailure.ReservationConflict =>
                new PortalTravelDenial(
                    PortalTravelDenialCode.RequestRateLimited),
            _ => new PortalTravelDenial(
                PortalTravelDenialCode.InviteCooldownDataUnavailable)
        };
    }

    internal static void RefreshCommittedInviteCooldownView(ZNetPeer? peer)
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer())
        {
            return;
        }

        RecipientContext recipient;
        if (peer != null)
        {
            recipient = CreateRecipientContext(peer);
        }
        else
        {
            CreateLocalAuthorizationContext(out recipient, out _);
        }

        RefreshRecipientCooldownView(recipient);
    }

    private static void RefreshRecipientCooldownView(RecipientContext recipient)
    {
        if (recipient.Peer != null)
        {
            if (recipient.Peer.m_rpc != null &&
                recipient.Peer.m_rpc.IsConnected())
            {
                SendSnapshot(recipient.Peer.m_rpc, recipient.Peer);
            }

            return;
        }

        RefreshLocalClientEntries(forceUpdate: true);
    }

    private static bool TryGetUsablePortal(
        ZDOID portalId,
        RecipientContext recipient,
        bool source,
        out ZDO portal,
        out PortalTravelDenial denial)
    {
        portal = null!;
        denial = new PortalTravelDenial(
            source
                ? PortalTravelDenialCode.SourceUnavailable
                : PortalTravelDenialCode.DestinationUnavailable);
        if (PendingRemovalIds.Contains(portalId) ||
            !ServerAuthority.TryGetValue(
                portalId,
                out ServerPortalAuthority authority) ||
            ZDOMan.instance == null)
        {
            return false;
        }

        ZDO? current = ZDOMan.instance.GetZDO(portalId);
        if (current == null || !current.IsValid())
        {
            return false;
        }

        if (!ZdoMatchesAuthority(current, authority))
        {
            WriteAuthorityToZdo(current, authority, forceSend: true);
        }

        if (!PublicPortalKinds.IsHandledPortal(current))
        {
            return false;
        }

        if (!CanRecipientUseBaseAuthority(authority, recipient))
        {
            denial = new PortalTravelDenial(
                source
                    ? PortalTravelDenialCode.SourceAccessDenied
                    : PortalTravelDenialCode.DestinationAccessDenied);
            return false;
        }

        if (!TryCheckRecipientRequiredGlobalKey(
                authority,
                recipient,
                out denial))
        {
            return false;
        }

        portal = current;
        denial = PortalTravelDenial.None;
        return true;
    }

    private static bool CanRecipientUseAuthority(
        ServerPortalAuthority authority,
        RecipientContext recipient)
    {
        return CanRecipientUseBaseAuthority(authority, recipient) &&
               TryCheckRecipientRequiredGlobalKey(
                   authority,
                   recipient,
                   out _);
    }

    private static bool CanRecipientUseBaseAuthority(
        ServerPortalAuthority authority,
        RecipientContext recipient)
    {
        if (IsTemporaryPublicAccessExpired(
                authority,
                PublicPortalData.GetUtcNowSeconds()))
        {
            return authority.Builder.IsValid &&
                   string.Equals(
                       authority.Builder.AccountId,
                       recipient.OwnerId,
                       StringComparison.Ordinal);
        }

        if (authority.Owner.IsValid &&
            string.Equals(
                authority.Owner.Id,
                recipient.OwnerId,
                StringComparison.Ordinal))
        {
            return true;
        }

        return authority.AccessMode switch
        {
            PublicPortalAccessMode.Public => true,
            PublicPortalAccessMode.Invite => true,
            PublicPortalAccessMode.Tagged =>
                PublicPortalKinds.IsAdminPortalPrefab(authority.PrefabHash) ||
                !string.IsNullOrWhiteSpace(authority.AuthorizedClanId) &&
                recipient.ClanMembership.ContainsClan(
                    authority.AuthorizedClanId),
            PublicPortalAccessMode.Admin => recipient.IsAdmin,
            PublicPortalAccessMode.Clan =>
                !string.IsNullOrWhiteSpace(authority.AuthorizedClanId) &&
                recipient.ClanMembership.ContainsClan(
                    authority.AuthorizedClanId),
            _ => false
        };
    }

    private static bool TryCheckRecipientRequiredGlobalKey(
        ServerPortalAuthority authority,
        RecipientContext recipient,
        out PortalTravelDenial denial)
    {
        denial = PortalTravelDenial.None;
        if (string.IsNullOrEmpty(authority.RequiredGlobalKey))
        {
            return true;
        }

        if (!authority.RequiredGlobalKeyIsValid)
        {
            denial = new PortalTravelDenial(
                PortalTravelDenialCode.RequiredGlobalKeyInvalid);
            return false;
        }

        if (!recipient.RequiredGlobalKeyResults.TryGetValue(
                authority.RequiredGlobalKey,
                out RequiredGlobalKeyQueryResult result))
        {
            result = recipient.Peer != null
                ? RequiredGlobalKeyAccess.QueryPeer(
                    recipient.Peer,
                    authority.RequiredGlobalKey)
                : RequiredGlobalKeyAccess.QueryLocal(
                    authority.RequiredGlobalKey);
            recipient.RequiredGlobalKeyResults[authority.RequiredGlobalKey] =
                result;
        }

        if (RequiredGlobalKeyAccess.IsPresent(result))
        {
            return true;
        }

        denial = result switch
        {
            RequiredGlobalKeyQueryResult.PersonalMissing or
                RequiredGlobalKeyQueryResult.SharedMissing =>
                new PortalTravelDenial(
                    PortalTravelDenialCode.RequiredGlobalKeyMissing,
                    authority.RequiredGlobalKey),
            RequiredGlobalKeyQueryResult.Invalid =>
                new PortalTravelDenial(
                    PortalTravelDenialCode.RequiredGlobalKeyInvalid),
            _ => new PortalTravelDenial(
                PortalTravelDenialCode.RequiredGlobalKeyUnavailable)
        };
        return false;
    }

    private static bool TryResolveActiveBuilder(ZDO zdo, out PortalBuilder builder)
    {
        builder = new PortalBuilder("", "");
        if (_activePortalSync != null)
        {
            if (!_activePortalSync.CreatedIds.Contains(zdo.m_uid))
            {
                return false;
            }

            ZNetPeer? peer = _activePortalSync?.Peer;
            if (peer == null ||
                !peer.IsReady() ||
                peer.m_characterID.IsNone() ||
                zdo.m_uid.UserID != peer.m_uid ||
                zdo.GetOwner() != peer.m_uid ||
                ZDOMan.instance == null)
            {
                return false;
            }

            if (!PublicPortalData.TryGetAuthenticatedPeerPlayerId(
                    peer,
                    out long expectedPlayerId) ||
                !PublicPortalData.TryGetPeerSteamId64(peer, out string steamId))
            {
                return false;
            }

            long creatorPlayerId = zdo.GetLong(ZDOVars.s_creator, 0L);
            if (creatorPlayerId == 0L ||
                expectedPlayerId != creatorPlayerId ||
                !PortalAccountStore.TryRememberIdentity(
                    expectedPlayerId,
                    steamId,
                    out bool added))
            {
                return false;
            }

            if (added)
            {
                BackfillBuilderForCreator(
                    expectedPlayerId,
                    steamId,
                    peer.m_playerName);
            }

            builder = SanitizeBuilder(new PortalBuilder(
                steamId,
                peer.m_playerName,
                expectedPlayerId,
                0L));
            return builder.IsValid;
        }

        if (_activeLocalPlacementBuilder.IsValid &&
            Player.m_localPlayer != null)
        {
            long localPlayerId = Player.m_localPlayer.GetPlayerID();
            long creatorPlayerId = zdo.GetLong(ZDOVars.s_creator, 0L);
            if (localPlayerId == 0L ||
                creatorPlayerId == 0L ||
                creatorPlayerId != localPlayerId ||
                !PortalAccountStore.TryResolveSteamId(
                    localPlayerId,
                    out string steamId) ||
                !string.Equals(
                    steamId,
                    _activeLocalPlacementBuilder.AccountId,
                    StringComparison.Ordinal))
            {
                return false;
            }

            builder = SanitizeBuilder(_activeLocalPlacementBuilder);
            return builder.IsValid;
        }

        return false;
    }

    private static bool IsAuthenticatedRemoteAdminPortalCreation(
        ZDO zdo,
        ZNetPeer? peer)
    {
        Vector3 portalPosition = zdo.GetPosition();
        return _activePortalSync != null &&
               _activePortalSync.CreatedIds.Contains(zdo.m_uid) &&
               peer != null &&
               peer.IsReady() &&
               zdo.m_uid.UserID == peer.m_uid &&
               zdo.GetOwner() == peer.m_uid &&
               ZNet.instance != null &&
               PublicPortalData.IsPeerAdmin(ZNet.instance, peer) &&
               PublicPortalData.TryGetPeerOwner(peer, out _) &&
               PublicPortalData.TryGetAuthenticatedPeerCharacterPosition(
                   peer,
                   out Vector3 playerPosition) &&
               IsSafePortalPosition(portalPosition) &&
               TryNormalizePortalRotation(zdo.GetRotation(), out _) &&
               Vector3.Distance(playerPosition, portalPosition) <=
               MaximumAdminPortalPlacementDistance;
    }

    private static ServerPortalAuthority SetServerAuthority(
        ZDOID portalId,
        ServerPortalAuthority authority)
    {
        if (authority.Builder.IsValid)
        {
            PortalBuilder sequencedBuilder = authority.Builder;
            if (sequencedBuilder.BuildSequence <= 0L)
            {
                sequencedBuilder = sequencedBuilder.WithBuildSequence(
                    NextBuildSequence());
            }
            else
            {
                _nextBuildSequence = Math.Max(
                    _nextBuildSequence,
                    sequencedBuilder.BuildSequence);
            }

            if (sequencedBuilder.BuildSequence !=
                authority.Builder.BuildSequence)
            {
                authority = new ServerPortalAuthority(
                    authority.AccessMode,
                    authority.Owner,
                    authority.AuthorizedClanId,
                    sequencedBuilder,
                    authority.PrefabHash,
                    authority.FavoriteId,
                    authority.RequiredGlobalKey,
                    authority.PublicExpiresAtUtcSeconds);
            }
        }

        if (authority.Builder.IsValid &&
            !PublicPortalKinds.IsAdminPortalPrefab(
                authority.PrefabHash) &&
            (!string.Equals(
                 authority.Owner.Id,
                 authority.Builder.AccountId,
                 StringComparison.Ordinal) ||
             !string.Equals(
                 authority.Owner.Name,
                 authority.Builder.Name,
                 StringComparison.Ordinal)))
        {
            authority = new ServerPortalAuthority(
                authority.AccessMode,
                new PortalOwner(
                    authority.Builder.AccountId,
                    authority.Builder.Name),
                authority.AuthorizedClanId,
                authority.Builder,
                authority.PrefabHash,
                authority.FavoriteId,
                authority.RequiredGlobalKey,
                authority.PublicExpiresAtUtcSeconds);
        }

        bool hasValidFavoriteId = PublicPortalData.TryNormalizeFavoriteId(
            authority.FavoriteId,
            out string favoriteId);
        bool favoriteIdBelongsToAnotherPortal =
            hasValidFavoriteId &&
            ServerPortalsByFavoriteId.TryGetValue(
                favoriteId,
                out ZDOID existingPortalId) &&
            existingPortalId != portalId;
        if (!hasValidFavoriteId || favoriteIdBelongsToAnotherPortal)
        {
            favoriteId = CreateServerFavoriteId();
        }

        if (!string.Equals(authority.FavoriteId, favoriteId, StringComparison.Ordinal))
        {
            authority = new ServerPortalAuthority(
                authority.AccessMode,
                authority.Owner,
                authority.AuthorizedClanId,
                authority.Builder,
                authority.PrefabHash,
                favoriteId,
                authority.RequiredGlobalKey,
                authority.PublicExpiresAtUtcSeconds);
        }

        bool hadCurrentAuthority = ServerAuthority.TryGetValue(
            portalId,
            out ServerPortalAuthority currentAuthority);
        if (hadCurrentAuthority &&
            !string.Equals(
                currentAuthority.FavoriteId,
                favoriteId,
                StringComparison.Ordinal) &&
            ServerPortalsByFavoriteId.TryGetValue(
                currentAuthority.FavoriteId,
                out ZDOID indexedPortalId) &&
            indexedPortalId == portalId)
        {
            ServerPortalsByFavoriteId.Remove(currentAuthority.FavoriteId);
        }

        if (hadCurrentAuthority &&
            currentAuthority.Builder.IsValid &&
            !string.Equals(
                currentAuthority.Builder.AccountId,
                authority.Builder.AccountId,
                StringComparison.Ordinal))
        {
            RemoveBuilderPortal(currentAuthority.Builder.AccountId, portalId);
        }

        ServerAuthority[portalId] = authority;
        ServerPortalsByFavoriteId[favoriteId] = portalId;
        if (!authority.Builder.IsValid)
        {
            return authority;
        }

        if (!ServerPortalsByBuilder.TryGetValue(
                authority.Builder.AccountId,
                out HashSet<ZDOID> portalIds))
        {
            portalIds = new HashSet<ZDOID>();
            ServerPortalsByBuilder.Add(authority.Builder.AccountId, portalIds);
        }

        portalIds.Add(portalId);
        return authority;
    }

    private static long NextBuildSequence()
    {
        if (_nextBuildSequence < long.MaxValue)
        {
            _nextBuildSequence++;
            return _nextBuildSequence;
        }

        long candidate = 1L;
        HashSet<long> used = ServerAuthority.Values
            .Where(authority => authority.Builder.BuildSequence > 0L)
            .Select(authority => authority.Builder.BuildSequence)
            .ToHashSet();
        if (ZDOMan.instance != null)
        {
            foreach (ZDO portal in ZDOMan.instance.GetPortals())
            {
                if (portal == null ||
                    portal.GetInt(
                        PublicPortalData.BuilderAuthorityVersionKey,
                        0) < PublicPortalData.CurrentBuilderAuthorityVersion)
                {
                    continue;
                }

                long storedSequence = portal.GetLong(
                    PublicPortalData.PortalBuildSequenceKey,
                    0L);
                if (storedSequence > 0L)
                {
                    used.Add(storedSequence);
                }
            }
        }

        while (candidate < long.MaxValue && used.Contains(candidate))
        {
            candidate++;
        }

        return candidate;
    }

    private static void SeedNextBuildSequence(IEnumerable<ZDO> portals)
    {
        foreach (ServerPortalAuthority authority in ServerAuthority.Values)
        {
            _nextBuildSequence = Math.Max(
                _nextBuildSequence,
                authority.Builder.BuildSequence);
        }

        foreach (ZDO portal in portals)
        {
            if (portal == null ||
                portal.GetInt(
                    PublicPortalData.BuilderAuthorityVersionKey,
                    0) < PublicPortalData.CurrentBuilderAuthorityVersion)
            {
                continue;
            }

            _nextBuildSequence = Math.Max(
                _nextBuildSequence,
                portal.GetLong(PublicPortalData.PortalBuildSequenceKey, 0L));
        }
    }

    private static void RemoveServerAuthority(ZDOID portalId)
    {
        AdminPortalTags.Remove(portalId);
        if (!ServerAuthority.TryGetValue(portalId, out ServerPortalAuthority authority))
        {
            return;
        }

        InviteTravelCooldownStore.RemovePortal(authority.FavoriteId);
        ServerAuthority.Remove(portalId);
        if (ServerPortalsByFavoriteId.TryGetValue(
                authority.FavoriteId,
                out ZDOID indexedPortalId) &&
            indexedPortalId == portalId)
        {
            ServerPortalsByFavoriteId.Remove(authority.FavoriteId);
        }

        if (authority.Builder.IsValid)
        {
            RemoveBuilderPortal(authority.Builder.AccountId, portalId);
        }
    }

    private static void RemoveBuilderPortal(string builderAccountId, ZDOID portalId)
    {
        if (!ServerPortalsByBuilder.TryGetValue(
                builderAccountId,
                out HashSet<ZDOID> portalIds))
        {
            return;
        }

        portalIds.Remove(portalId);
        if (portalIds.Count == 0)
        {
            ServerPortalsByBuilder.Remove(builderAccountId);
        }
    }

    private static PortalBuilder SanitizeBuilder(PortalBuilder builder)
    {
        string accountId = PublicPortalData.TryNormalizeSteamId64(
            builder.AccountId,
            out string canonicalSteamId)
            ? canonicalSteamId
            : "";

        return new PortalBuilder(
            accountId,
            Truncate(builder.Name, MaximumOwnerNameLength),
            builder.CharacterPlayerId,
            builder.BuildSequence);
    }

    private static PortalOwner SanitizeOwner(PortalOwner owner)
    {
        string ownerId = owner.Id ?? "";
        if (ownerId.Length > MaximumOwnerIdLength)
        {
            ownerId = "";
        }

        return new PortalOwner(
            ownerId,
            Truncate(owner.Name, MaximumOwnerNameLength));
    }

    private static string SanitizeClanId(string clanId)
    {
        clanId = (clanId ?? "").Trim();
        return clanId.Length <= MaximumClanIdLength
            ? clanId
            : "";
    }

    private static string Truncate(string value, int maximumLength)
    {
        value ??= "";
        return value.Length <= maximumLength ? value : value.Substring(0, maximumLength);
    }

    private static bool IsFinite(Vector3 value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }

    private static bool IsSafePortalPosition(Vector3 value)
    {
        return IsFinite(value) &&
               Math.Abs(value.x) <= MaximumPortalCoordinateMagnitude &&
               Math.Abs(value.y) <= MaximumPortalCoordinateMagnitude &&
               Math.Abs(value.z) <= MaximumPortalCoordinateMagnitude;
    }

    private static bool IsFinite(Quaternion value)
    {
        return IsFinite(value.x) &&
               IsFinite(value.y) &&
               IsFinite(value.z) &&
               IsFinite(value.w);
    }

    private static bool TryNormalizePortalRotation(
        Quaternion value,
        out Quaternion normalized)
    {
        normalized = Quaternion.identity;
        if (!IsFinite(value))
        {
            return false;
        }

        double sqrMagnitude =
            (double)value.x * value.x +
            (double)value.y * value.y +
            (double)value.z * value.z +
            (double)value.w * value.w;
        if (sqrMagnitude < MinimumQuaternionSqrMagnitude ||
            sqrMagnitude > MaximumQuaternionSqrMagnitude)
        {
            return false;
        }

        float inverseMagnitude = (float)(1d / Math.Sqrt(sqrMagnitude));
        normalized = new Quaternion(
            value.x * inverseMagnitude,
            value.y * inverseMagnitude,
            value.z * inverseMagnitude,
            value.w * inverseMagnitude);
        return IsFinite(normalized);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool SnapshotsMatch(
        IReadOnlyList<PublicPortalCatalogEntry> left,
        IReadOnlyList<PublicPortalCatalogEntry> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            if (!left[i].HasSameContent(right[i]))
            {
                return false;
            }
        }

        return true;
    }
}
