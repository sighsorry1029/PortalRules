using System;
using System.Collections.Generic;
using UnityEngine;

namespace PortalRules;

internal enum PortalTravelDenialCode : byte
{
    None = 0,
    Denied = 1,
    NotReady = 2,
    PortalsBlocked = 3,
    BossBlocked = 4,
    SourceUnavailable = 5,
    SourceAccessDenied = 6,
    DestinationUnavailable = 7,
    DestinationAccessDenied = 8,
    RequiredGlobalKeyMissing = 9,
    RequiredGlobalKeyInvalid = 10,
    RequiredGlobalKeyUnavailable = 11,
    SourceNotTagged = 12,
    TaggedRequiresConnectedDestination = 13,
    SourceTooFar = 14,
    DestinationNotTagged = 15,
    ConnectionChanged = 16,
    DestinationPositionInvalid = 17,
    InviteCooldownDataUnavailable = 18,
    InviteCooldownStorageUnavailable = 19,
    InviteCooldownActive = 20,
    InviteCooldownAccountCapacityReached = 21,
    InviteCooldownPortalCapacityReached = 22,
    InviteCooldownFileCapacityReached = 23,
    InviteCooldownSaveFailed = 24,
    AuthorizationFailed = 25,
    RequestRateLimited = 26
}

internal readonly struct PortalTravelDenial
{
    internal static readonly PortalTravelDenial None =
        new(PortalTravelDenialCode.None);

    internal readonly PortalTravelDenialCode Code;
    internal readonly string Argument;
    internal readonly long FirstValue;
    internal readonly long SecondValue;

    internal PortalTravelDenial(
        PortalTravelDenialCode code,
        string argument = "",
        long firstValue = 0L,
        long secondValue = 0L)
    {
        Code = code;
        Argument = argument ?? "";
        FirstValue = firstValue;
        SecondValue = secondValue;
    }
}

internal readonly struct PortalTravelAuthorization
{
    internal readonly Vector3 TargetPosition;
    internal readonly Quaternion TargetRotation;
    internal readonly int CoinCost;
    internal readonly int CargoWeightUnits;
    internal readonly string AccountId;
    internal readonly string SourceFavoriteId;
    internal readonly string DestinationFavoriteId;
    internal readonly bool UsesInviteAsSource;
    internal readonly bool UsesInviteAsDestination;
    internal readonly InviteTravelCooldownReservation CooldownReservation;

    internal PortalTravelAuthorization(
        Vector3 targetPosition,
        Quaternion targetRotation,
        int coinCost,
        int cargoWeightUnits,
        string accountId,
        string sourceFavoriteId,
        string destinationFavoriteId,
        bool usesInviteAsSource,
        bool usesInviteAsDestination,
        InviteTravelCooldownReservation cooldownReservation)
    {
        TargetPosition = targetPosition;
        TargetRotation = targetRotation;
        CoinCost = Math.Max(0, coinCost);
        CargoWeightUnits = Math.Max(
            0,
            Math.Min(
                cargoWeightUnits,
                PublicPortalTravelCost.MaximumCargoWeightUnits));
        AccountId = accountId ?? "";
        SourceFavoriteId = sourceFavoriteId ?? "";
        DestinationFavoriteId = destinationFavoriteId ?? "";
        UsesInviteAsSource = usesInviteAsSource;
        UsesInviteAsDestination = usesInviteAsDestination;
        CooldownReservation = cooldownReservation;
    }

    internal bool RequiresTransaction =>
        CoinCost > 0 || CooldownReservation.IsActive;
}

internal static partial class PublicPortalTeleportService
{
    private enum TeleportRequestKind
    {
        MapSelection = 0,
        ConnectedPortal = 1,
        MapOpen = 2
    }

    private readonly struct TeleportGrantPayload
    {
        internal readonly long RequestId;
        internal readonly TeleportRequestKind Kind;
        internal readonly bool Success;
        internal readonly ZDOID TargetPortalId;
        internal readonly Vector3 TargetPosition;
        internal readonly Quaternion TargetRotation;
        internal readonly int CoinCost;
        internal readonly int CargoWeightUnits;
        internal readonly string Ticket;
        internal readonly PortalTravelDenial Denial;

        internal TeleportGrantPayload(
            long requestId,
            TeleportRequestKind kind,
            bool success,
            ZDOID targetPortalId,
            Vector3 targetPosition,
            Quaternion targetRotation,
            int coinCost,
            int cargoWeightUnits,
            string ticket,
            PortalTravelDenial denial)
        {
            RequestId = requestId;
            Kind = kind;
            Success = success;
            TargetPortalId = targetPortalId;
            TargetPosition = targetPosition;
            TargetRotation = targetRotation;
            CoinCost = coinCost;
            CargoWeightUnits = cargoWeightUnits;
            Ticket = ticket ?? "";
            Denial = denial;
        }

        internal void Write(ZPackage package)
        {
            package.Write(RequestId);
            package.Write((int)Kind);
            package.Write(Success);
            package.Write(TargetPortalId);
            package.Write(TargetPosition);
            package.Write(TargetRotation);
            package.Write(CoinCost);
            package.Write(CargoWeightUnits);
            package.Write(Ticket);
            package.Write((int)Denial.Code);
            package.Write(Denial.Argument);
            package.Write(Denial.FirstValue);
            package.Write(Denial.SecondValue);
        }

        internal static TeleportGrantPayload Read(ZPackage package)
        {
            long requestId = package.ReadLong();
            int rawKind = package.ReadInt();
            if (!Enum.IsDefined(typeof(TeleportRequestKind), rawKind))
            {
                throw new InvalidOperationException(
                    $"Unknown portal travel grant kind {rawKind}.");
            }

            TeleportRequestKind kind = (TeleportRequestKind)rawKind;
            bool success = package.ReadBool();
            ZDOID targetPortalId = package.ReadZDOID();
            Vector3 position = package.ReadVector3();
            Quaternion rotation = package.ReadQuaternion();
            int coinCost = package.ReadInt();
            int cargoWeightUnits = package.ReadInt();
            if (cargoWeightUnits < 0 ||
                cargoWeightUnits >
                PublicPortalTravelCost.MaximumCargoWeightUnits)
            {
                throw new InvalidOperationException(
                    "Portal travel grant contained invalid cargo weight.");
            }
            string ticket = package.ReadString();
            if (ticket.Length != 0 && !IsValidTicket(ticket))
            {
                throw new InvalidOperationException(
                    "Portal travel grant contained an invalid ticket.");
            }

            int rawDenialCode = package.ReadInt();
            if (rawDenialCode < byte.MinValue ||
                rawDenialCode > byte.MaxValue ||
                !Enum.IsDefined(
                    typeof(PortalTravelDenialCode),
                    (byte)rawDenialCode))
            {
                throw new InvalidOperationException(
                    $"Unknown portal travel denial code {rawDenialCode}.");
            }

            PortalTravelDenial denial = new(
                (PortalTravelDenialCode)(byte)rawDenialCode,
                package.ReadString(),
                package.ReadLong(),
                package.ReadLong());
            return new TeleportGrantPayload(
                requestId,
                kind,
                success,
                targetPortalId,
                position,
                rotation,
                coinCost,
                cargoWeightUnits,
                ticket,
                denial);
        }
    }

    private sealed class PendingRequest
    {
        public readonly long RequestId;
        public readonly ZDOID SourceId;
        public readonly ZDOID TargetId;
        public readonly TeleportRequestKind Kind;
        public readonly bool SourceAllowsAllItems;
        public readonly int CargoWeightUnits;
        public readonly float ExitDistance;
        public readonly float StartedAt;
        public readonly Action? CompletionAction;

        public PendingRequest(
            long requestId,
            ZDOID sourceId,
            ZDOID targetId,
            TeleportRequestKind kind,
            bool sourceAllowsAllItems,
            int cargoWeightUnits,
            float exitDistance,
            float startedAt,
            Action? completionAction)
        {
            RequestId = requestId;
            SourceId = sourceId;
            TargetId = targetId;
            Kind = kind;
            SourceAllowsAllItems = sourceAllowsAllItems;
            CargoWeightUnits = cargoWeightUnits;
            ExitDistance = exitDistance;
            StartedAt = startedAt;
            CompletionAction = completionAction;
        }
    }

    private const string TeleportRequestRpc =
        "sighsorry.PortalRules.TeleportRequest.v5";
    private const string TeleportGrantRpc =
        "sighsorry.PortalRules.TeleportGrant.v5";
    private const float RequestCooldownSeconds = 0.25f;
    private const float PendingRequestTimeoutSeconds = 5f;
    private const int MaximumRequestPackageBytes = 256;

    private static readonly Dictionary<ZRpc, float> LastRequestAt = new();
    private static readonly Dictionary<ZRpc, long> LastAcceptedRequestId = new();
    private static ZNet? _sessionZNet;
    private static long _nextRequestId;
    private static PendingRequest? _pendingRequest;

    internal static void BeginNetworkSession(ZNet? znet)
    {
        if (znet == null || ReferenceEquals(_sessionZNet, znet))
        {
            return;
        }

        LastRequestAt.Clear();
        LastAcceptedRequestId.Clear();
        LastControlRequestAt.Clear();
        ClearServerTickets();
        CancelPending();
        _pendingCommit = null;
        _sessionZNet = znet;
    }

    internal static void Shutdown()
    {
        LastRequestAt.Clear();
        LastAcceptedRequestId.Clear();
        LastControlRequestAt.Clear();
        ClearServerTickets();
        CancelPending();
        _pendingCommit = null;
        _sessionZNet = null;
    }

    internal static void RegisterPeer(ZNet znet, ZNetPeer peer)
    {
        if (znet == null || peer?.m_rpc == null)
        {
            return;
        }

        if (znet.IsServer())
        {
            peer.m_rpc.Register<ZPackage>(TeleportRequestRpc, OnTeleportRequest);
            peer.m_rpc.Register<ZPackage>(TravelCommitRpc, OnTravelCommit);
            peer.m_rpc.Register<ZPackage>(TravelCancelRpc, OnTravelCancel);
        }
        else
        {
            peer.m_rpc.Register<ZPackage>(TeleportGrantRpc, OnTeleportGrant);
            peer.m_rpc.Register<ZPackage>(TravelCommitAckRpc, OnTravelCommitAck);
        }
    }

    internal static void ForgetPeer(ZRpc? rpc)
    {
        if (rpc == null)
        {
            return;
        }

        LastRequestAt.Remove(rpc);
        LastAcceptedRequestId.Remove(rpc);
        LastControlRequestAt.Remove(rpc);
        CancelServerTicketsForPeer(rpc);
        if (ZNet.instance != null &&
            !ZNet.instance.IsServer() &&
            ReferenceEquals(rpc, ZNet.instance.GetServerRPC()))
        {
            CancelPending();
            _pendingCommit = null;
        }
    }

    public static void CancelPending()
    {
        _pendingRequest = null;
    }

    internal static void Tick()
    {
        float now = Time.realtimeSinceStartup;
        if (ZNet.instance != null && ZNet.instance.IsServer())
        {
            TickServerTickets(now);
        }
        else
        {
            TickClientCommit(now);
        }

        if (_pendingRequest != null &&
            now - _pendingRequest.StartedAt >= PendingRequestTimeoutSeconds)
        {
            _pendingRequest = null;
        }
    }

    public static void TeleportTo(
        PublicPortalCatalogEntry target,
        ZDOID sourcePortalId,
        bool sourceAllowsAllItems,
        Action endSelection)
    {
        Player? player = Player.m_localPlayer;
        if (player == null)
        {
            return;
        }

        if (!PublicPortalAccess.CanUsePortal(target))
        {
            player.Message(
                MessageHud.MessageType.Center,
                PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_portal_denied"));
            return;
        }

        BeginTeleportRequest(
            TeleportRequestKind.MapSelection,
            sourcePortalId,
            target.Id,
            sourceAllowsAllItems,
            exitDistance: 2f,
            endSelection);
    }

    internal static void AuthorizeMapOpen(
        ZDOID sourcePortalId,
        bool sourceAllowsAllItems,
        Action openMap)
    {
        if (sourcePortalId.IsNone() || openMap == null)
        {
            return;
        }

        BeginTeleportRequest(
            TeleportRequestKind.MapOpen,
            sourcePortalId,
            ZDOID.None,
            sourceAllowsAllItems,
            exitDistance: 0f,
            openMap);
    }

    internal static bool TryBeginConnectedTeleport(TeleportWorld sourcePortal)
    {
        if (sourcePortal == null)
        {
            return false;
        }

        ZDO? sourceZdo = PublicPortalKinds.GetPortalZdo(sourcePortal);
        if (sourceZdo == null)
        {
            Player.m_localPlayer?.Message(
                MessageHud.MessageType.Center,
                PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_portal_data_not_ready"));
            return true;
        }

        ZDOID targetPortalId = sourceZdo.GetConnectionZDOID(
            ZDOExtraData.ConnectionType.Portal);
        if (targetPortalId.IsNone())
        {
            return true;
        }

        float exitDistance = sourcePortal.m_exitDistance;
        if (!IsFinite(exitDistance) || exitDistance < 0f)
        {
            exitDistance = 1f;
        }

        BeginTeleportRequest(
            TeleportRequestKind.ConnectedPortal,
            sourceZdo.m_uid,
            targetPortalId,
            sourcePortal.m_allowAllItems,
            exitDistance,
            endSelection: null);
        return true;
    }

    private static void BeginTeleportRequest(
        TeleportRequestKind kind,
        ZDOID sourcePortalId,
        ZDOID targetPortalId,
        bool sourceAllowsAllItems,
        float exitDistance,
        Action? endSelection)
    {
        Player? player = Player.m_localPlayer;
        if (player == null)
        {
            return;
        }

        if (!PublicPortalTravelCost.TryGetLocalCargoWeightUnits(
                sourceAllowsAllItems,
                out int cargoWeightUnits,
                forceRefresh: true))
        {
            player.Message(MessageHud.MessageType.Center, "$msg_noteleport");
            return;
        }

        if (_pendingCommit != null)
        {
            player.Message(
                MessageHud.MessageType.Center,
                PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_waiting_for_server"));
            return;
        }

        if (PublicPortalCatalog.TryGetClientEntry(
                sourcePortalId,
                out PublicPortalCatalogEntry sourceEntry) &&
            PublicPortalCatalog.TryGetClientEntry(
                targetPortalId,
                out PublicPortalCatalogEntry targetEntry))
        {
            int expectedCoinCost = PublicPortalTravelCost.CalculateCost(
                sourceEntry.AllowsAllItems,
                sourceEntry.Position,
                targetEntry.Position,
                cargoWeightUnits);
            if (expectedCoinCost > 0 &&
                !PortalCoinWallet.HasLocalCoins(expectedCoinCost))
            {
                player.Message(
                    MessageHud.MessageType.Center,
                    PortalRulesLocalization.Translate(
                        "$sighsorry_portalrules_need_coins",
                        expectedCoinCost.ToString()));
                return;
            }
        }

        ZNet? znet = ZNet.instance;
        if (znet == null)
        {
            player.Message(
                MessageHud.MessageType.Center,
                PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_portal_not_ready"));
            return;
        }

        if (znet.IsServer())
        {
            string ticket = kind == TeleportRequestKind.MapOpen
                ? ""
                : CreateTicket();
            bool success = TryAuthorizeRequest(
                kind,
                peer: null,
                sourcePortalId,
                targetPortalId,
                ticket,
                cargoWeightUnits,
                out PortalTravelAuthorization authorization,
                out PortalTravelDenial denial);

            if (!success)
            {
                ShowTravelDenial(player, denial);
                PublicPortalCatalog.RequestRefresh(force: true);
                return;
            }

            if (kind == TeleportRequestKind.MapOpen)
            {
                endSelection?.Invoke();
                return;
            }

            if (!TryCreateRequiredServerTicket(
                    ticket,
                    0L,
                    null,
                    0L,
                    sourcePortalId,
                    targetPortalId,
                    authorization,
                    out ServerTravelTicket? serverTicket))
            {
                InviteTravelCooldownStore.CancelReservation(ticket);
                ShowTravelDenial(
                    player,
                    new PortalTravelDenial(
                        PortalTravelDenialCode.AuthorizationFailed));
                return;
            }

            bool teleportStarted = CompleteTeleport(
                authorization.TargetPosition,
                authorization.TargetRotation,
                exitDistance,
                authorization.CoinCost,
                authorization.CargoWeightUnits,
                kind,
                sourceAllowsAllItems,
                endSelection);
            FinalizeLocalServerTicket(serverTicket, teleportStarted);
            return;
        }

        ZRpc? serverRpc = znet.GetServerRPC();
        if (serverRpc == null || !serverRpc.IsConnected())
        {
            player.Message(
                MessageHud.MessageType.Center,
                PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_server_not_ready"));
            return;
        }

        if (_pendingRequest != null &&
            Time.realtimeSinceStartup - _pendingRequest.StartedAt <
            PendingRequestTimeoutSeconds)
        {
            player.Message(
                MessageHud.MessageType.Center,
                PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_waiting_for_server"));
            return;
        }

        CancelPending();
        long requestId = NextRequestId();
        _pendingRequest = new PendingRequest(
            requestId,
            sourcePortalId,
            targetPortalId,
            kind,
            sourceAllowsAllItems,
            cargoWeightUnits,
            exitDistance,
            Time.realtimeSinceStartup,
            endSelection);

        ZPackage request = new();
        request.Write(requestId);
        request.Write((int)kind);
        request.Write(sourcePortalId);
        request.Write(targetPortalId);
        request.Write(cargoWeightUnits);
        serverRpc.Invoke(TeleportRequestRpc, request);
    }

    private static bool TryAuthorizeRequest(
        TeleportRequestKind kind,
        ZNetPeer? peer,
        ZDOID sourcePortalId,
        ZDOID targetPortalId,
        string ticket,
        int cargoWeightUnits,
        out PortalTravelAuthorization authorization,
        out PortalTravelDenial denial)
    {
        authorization = default;
        if (kind == TeleportRequestKind.MapOpen)
        {
            if (!targetPortalId.IsNone())
            {
                denial = new PortalTravelDenial(
                    PortalTravelDenialCode.Denied);
                return false;
            }

            return peer == null
                ? PublicPortalCatalog.TryAuthorizeLocalMapOpen(
                    sourcePortalId,
                    out denial)
                : PublicPortalCatalog.TryAuthorizeMapOpen(
                    peer,
                    sourcePortalId,
                    out denial);
        }

        if (kind == TeleportRequestKind.ConnectedPortal)
        {
            return peer == null
                ? PublicPortalCatalog.TryAuthorizeLocalConnectedTeleport(
                    sourcePortalId,
                    targetPortalId,
                    ticket,
                    cargoWeightUnits,
                    out authorization,
                    out denial)
                : PublicPortalCatalog.TryAuthorizeConnectedTeleport(
                    peer,
                    sourcePortalId,
                    targetPortalId,
                    ticket,
                    cargoWeightUnits,
                    out authorization,
                    out denial);
        }

        return peer == null
            ? PublicPortalCatalog.TryAuthorizeLocalTeleport(
            sourcePortalId,
            targetPortalId,
            ticket,
            cargoWeightUnits,
            out authorization,
                out denial)
            : PublicPortalCatalog.TryAuthorizeTeleport(
                peer,
            sourcePortalId,
            targetPortalId,
            ticket,
            cargoWeightUnits,
            out authorization,
                out denial);
    }

    private static void OnTeleportRequest(ZRpc rpc, ZPackage package)
    {
        ZNet? znet = ZNet.instance;
        if (znet == null ||
            !znet.IsServer() ||
            package == null ||
            package.GetArray().Length > MaximumRequestPackageBytes)
        {
            return;
        }

        ZNetPeer? peer = PublicPortalData.FindPeer(znet, rpc);
        if (peer == null || !peer.IsReady())
        {
            return;
        }

        float now = Time.realtimeSinceStartup;
        bool requestRateLimited =
            LastRequestAt.TryGetValue(rpc, out float lastRequestAt) &&
            now - lastRequestAt < RequestCooldownSeconds;
        if (!requestRateLimited)
        {
            LastRequestAt[rpc] = now;
        }

        long requestId;
        TeleportRequestKind kind;
        ZDOID sourcePortalId;
        ZDOID targetPortalId;
        int cargoWeightUnits;
        try
        {
            requestId = package.ReadLong();
            int rawKind = package.ReadInt();
            if (!Enum.IsDefined(typeof(TeleportRequestKind), rawKind))
            {
                throw new InvalidOperationException(
                    $"Unknown portal travel request kind {rawKind}.");
            }

            kind = (TeleportRequestKind)rawKind;
            sourcePortalId = package.ReadZDOID();
            targetPortalId = package.ReadZDOID();
            cargoWeightUnits = package.ReadInt();
            if (requestId <= 0L ||
                cargoWeightUnits < 0 ||
                cargoWeightUnits >
                PublicPortalTravelCost.MaximumCargoWeightUnits)
            {
                throw new InvalidOperationException(
                    "Portal travel request ID or cargo weight is invalid.");
            }
        }
        catch (Exception ex)
        {
            if (!requestRateLimited)
            {
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    $"Rejected malformed portal travel request: {ex.Message}");
            }

            return;
        }

        bool replayedRequest =
            LastAcceptedRequestId.TryGetValue(rpc, out long lastRequestId) &&
            requestId <= lastRequestId;
        bool success = false;
        PortalTravelAuthorization authorization = default;
        string ticket = "";
        PortalTravelDenial denial = new(PortalTravelDenialCode.Denied);
        if (requestRateLimited || replayedRequest)
        {
            SendTeleportGrant(
                rpc,
                requestId,
                kind,
                false,
                targetPortalId,
                cargoWeightUnits,
                authorization,
                "",
                new PortalTravelDenial(
                    PortalTravelDenialCode.RequestRateLimited));
            return;
        }

        LastAcceptedRequestId[rpc] = requestId;
        if (kind != TeleportRequestKind.MapOpen)
        {
            ticket = CreateTicket();
        }

        try
        {
            success = TryAuthorizeRequest(
                kind,
                peer,
                sourcePortalId,
                targetPortalId,
                ticket,
                cargoWeightUnits,
                out authorization,
                out denial);
        }
        catch (Exception ex)
        {
            denial = new PortalTravelDenial(
                PortalTravelDenialCode.AuthorizationFailed);
            PortalRulesPlugin.PortalRulesLogger.LogError(
                $"Portal travel authorization failed for peer {peer.m_uid}: {ex.Message}");
        }

        if (success &&
            !TryCreateRequiredServerTicket(
                ticket,
                requestId,
                rpc,
                peer.m_uid,
                sourcePortalId,
                targetPortalId,
                authorization,
                out _))
        {
            success = false;
            denial = new PortalTravelDenial(
                PortalTravelDenialCode.AuthorizationFailed);
        }

        NormalizeGrantResult(
            ref success,
            ref authorization,
            ref ticket);

        SendTeleportGrant(
            rpc,
            requestId,
            kind,
            success,
            targetPortalId,
            cargoWeightUnits,
            authorization,
            ticket,
            denial);
    }

    private static void NormalizeGrantResult(
        ref bool success,
        ref PortalTravelAuthorization authorization,
        ref string ticket)
    {
        if (!success)
        {
            InviteTravelCooldownStore.CancelReservation(ticket);
            authorization = default;
            ticket = "";
            return;
        }

        if (!authorization.RequiresTransaction)
        {
            ticket = "";
        }
    }

    private static void SendTeleportGrant(
        ZRpc rpc,
        long requestId,
        TeleportRequestKind kind,
        bool success,
        ZDOID targetPortalId,
        int cargoWeightUnits,
        PortalTravelAuthorization authorization,
        string ticket,
        PortalTravelDenial denial)
    {
        TeleportGrantPayload payload = new(
            requestId,
            kind,
            success,
            targetPortalId,
            authorization.TargetPosition,
            authorization.TargetRotation,
            authorization.CoinCost,
            cargoWeightUnits,
            ticket,
            denial);
        ZPackage response = new();
        payload.Write(response);
        rpc.Invoke(TeleportGrantRpc, response);
    }

    private static void OnTeleportGrant(ZRpc rpc, ZPackage package)
    {
        if (ZNet.instance == null ||
            ZNet.instance.IsServer() ||
            !ReferenceEquals(rpc, ZNet.instance.GetServerRPC()))
        {
            return;
        }

        try
        {
            TeleportGrantPayload payload =
                TeleportGrantPayload.Read(package);
            HandleTeleportGrant(payload);
        }
        catch (Exception ex)
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                $"Ignored malformed portal travel grant: {ex.Message}");
            if (_pendingRequest != null)
            {
                CancelPending();
                Player.m_localPlayer?.Message(
                    MessageHud.MessageType.Center,
                    PortalRulesLocalization.Translate(
                        "$sighsorry_portalrules_portal_authorization_failed"));
                PublicPortalCatalog.RequestRefresh(force: true);
            }
        }
    }

    private static void HandleTeleportGrant(TeleportGrantPayload payload)
    {
        PendingRequest? pendingRequest = _pendingRequest;
        if (payload.RequestId == 0L ||
            pendingRequest == null ||
            payload.RequestId != pendingRequest.RequestId ||
            payload.TargetPortalId != pendingRequest.TargetId ||
            payload.Kind != pendingRequest.Kind)
        {
            // A valid late grant can otherwise hold an Invite direction until
            // the server-side ticket expires. Do not cancel the ticket that is
            // already being committed after an accepted duplicate grant.
            if (payload.Ticket.Length > 0 &&
                (_pendingCommit == null ||
                 !string.Equals(
                     _pendingCommit.Ticket,
                     payload.Ticket,
                     StringComparison.Ordinal)))
            {
                SendTravelCancel(payload.Ticket);
            }

            return;
        }

        if (Time.realtimeSinceStartup - pendingRequest.StartedAt >=
            PendingRequestTimeoutSeconds)
        {
            CancelPending();
            SendTravelCancel(payload.Ticket);
            Player.m_localPlayer?.Message(
                MessageHud.MessageType.Center,
                PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_approval_expired"));
            PublicPortalCatalog.RequestRefresh(force: true);
            return;
        }

        bool sourceAllowsAllItems = pendingRequest.SourceAllowsAllItems;
        int requestedCargoWeightUnits = pendingRequest.CargoWeightUnits;
        ZDOID sourcePortalId = pendingRequest.SourceId;
        float exitDistance = pendingRequest.ExitDistance;
        Action? completionAction = pendingRequest.CompletionAction;
        CancelPending();
        if (!payload.Success || payload.CoinCost < 0 ||
            payload.CargoWeightUnits != requestedCargoWeightUnits ||
            payload.Kind == TeleportRequestKind.MapOpen && payload.Ticket.Length != 0 ||
            payload.Kind != TeleportRequestKind.MapOpen &&
            payload.CoinCost > 0 && payload.Ticket.Length == 0)
        {
            SendTravelCancel(payload.Ticket);
            ShowTravelDenial(Player.m_localPlayer, payload.Denial);
            PublicPortalCatalog.RequestRefresh(force: true);
            return;
        }

        if (payload.Kind == TeleportRequestKind.MapOpen)
        {
            completionAction?.Invoke();
            return;
        }

        if (!CanCompleteGrantedTeleport(
                sourcePortalId,
                out string localMessage))
        {
            Player.m_localPlayer?.Message(
                MessageHud.MessageType.Center,
                localMessage);
            SendTravelCancel(payload.Ticket);
            PublicPortalCatalog.RequestRefresh(force: true);
            return;
        }

        bool teleportStarted = CompleteTeleport(
            payload.TargetPosition,
            payload.TargetRotation,
            exitDistance,
            payload.CoinCost,
            payload.CargoWeightUnits,
            payload.Kind,
            sourceAllowsAllItems,
            completionAction);
        if (!teleportStarted)
        {
            SendTravelCancel(payload.Ticket);
            return;
        }

        if (payload.Ticket.Length > 0)
        {
            BeginPendingCommit(payload.Ticket);
        }
    }





    private static void ShowTravelDenial(
        Player? player,
        PortalTravelDenial denial)
    {
        if (player == null)
        {
            return;
        }

        if (denial.Code == PortalTravelDenialCode.RequiredGlobalKeyMissing &&
            PublicPortalData.TryNormalizeRequiredGlobalKey(
                denial.Argument,
                out string requiredGlobalKey,
                out _) &&
            requiredGlobalKey.Length > 0)
        {
            if (RequiredGlobalKeyAccess.YouAreNotWorthyInstalled)
            {
                if (RequiredGlobalKeyAccess.TryShowLocalMissingRequirement(
                        requiredGlobalKey))
                {
                    return;
                }

                player.Message(
                    MessageHud.MessageType.Center,
                    PortalRulesLocalization.Translate(
                        "$sighsorry_portalrules_required_global_key_unavailable"));
                return;
            }

            player.Message(
                MessageHud.MessageType.Center,
                PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_portal_denied") +
                "\n" +
                PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_missing_global_key",
                    requiredGlobalKey.RemoveRichTextTags()));
            return;
        }

        player.Message(
            MessageHud.MessageType.Center,
            GetTravelDenialMessage(denial));
    }

    private static string GetTravelDenialMessage(PortalTravelDenial denial)
    {
        string token = denial.Code switch
        {
            PortalTravelDenialCode.NotReady =>
                "$sighsorry_portalrules_portal_not_ready",
            PortalTravelDenialCode.PortalsBlocked => "$msg_blocked",
            PortalTravelDenialCode.BossBlocked => "$msg_blockedbyboss",
            PortalTravelDenialCode.SourceUnavailable =>
                "$sighsorry_portalrules_source_portal_unavailable",
            PortalTravelDenialCode.SourceAccessDenied =>
                "$sighsorry_portalrules_source_portal_denied",
            PortalTravelDenialCode.DestinationUnavailable =>
                "$sighsorry_portalrules_destination_portal_unavailable",
            PortalTravelDenialCode.DestinationAccessDenied =>
                "$sighsorry_portalrules_destination_portal_denied",
            PortalTravelDenialCode.RequiredGlobalKeyMissing or
                PortalTravelDenialCode.RequiredGlobalKeyInvalid =>
                "$sighsorry_portalrules_required_global_key_invalid",
            PortalTravelDenialCode.RequiredGlobalKeyUnavailable =>
                "$sighsorry_portalrules_required_global_key_unavailable",
            PortalTravelDenialCode.SourceNotTagged =>
                "$sighsorry_portalrules_source_not_tagged",
            PortalTravelDenialCode.TaggedRequiresConnectedDestination =>
                "$sighsorry_portalrules_tagged_uses_connection",
            PortalTravelDenialCode.SourceTooFar =>
                "$sighsorry_portalrules_move_closer_to_source",
            PortalTravelDenialCode.DestinationNotTagged =>
                "$sighsorry_portalrules_destination_not_tagged",
            PortalTravelDenialCode.ConnectionChanged =>
                "$sighsorry_portalrules_connection_changed",
            PortalTravelDenialCode.DestinationPositionInvalid =>
                "$sighsorry_portalrules_destination_position_invalid",
            PortalTravelDenialCode.InviteCooldownDataUnavailable =>
                "$sighsorry_portalrules_invite_cooldown_data_unavailable",
            PortalTravelDenialCode.InviteCooldownStorageUnavailable =>
                "$sighsorry_portalrules_invite_cooldown_storage_unavailable",
            PortalTravelDenialCode.InviteCooldownAccountCapacityReached =>
                "$sighsorry_portalrules_invite_cooldown_account_capacity",
            PortalTravelDenialCode.InviteCooldownPortalCapacityReached =>
                "$sighsorry_portalrules_invite_cooldown_portal_capacity",
            PortalTravelDenialCode.InviteCooldownFileCapacityReached =>
                "$sighsorry_portalrules_invite_cooldown_file_capacity",
            PortalTravelDenialCode.InviteCooldownSaveFailed =>
                "$sighsorry_portalrules_invite_cooldown_save_failed",
            PortalTravelDenialCode.AuthorizationFailed =>
                "$sighsorry_portalrules_portal_authorization_failed",
            PortalTravelDenialCode.RequestRateLimited =>
                "$sighsorry_portalrules_portal_request_rate_limited",
            _ => "$sighsorry_portalrules_portal_denied"
        };

        if (denial.Code != PortalTravelDenialCode.InviteCooldownActive)
        {
            return PortalRulesLocalization.Translate(token);
        }

        string departure = denial.FirstValue > 0L
            ? InviteTravelCooldownStore.FormatRemaining(denial.FirstValue)
            : "";
        string arrival = denial.SecondValue > 0L
            ? InviteTravelCooldownStore.FormatRemaining(denial.SecondValue)
            : "";
        if (departure.Length > 0 && arrival.Length > 0)
        {
            return PortalRulesLocalization.Translate(
                "$sighsorry_portalrules_invite_departure_arrival_cooldown",
                departure,
                arrival);
        }

        return departure.Length > 0
            ? PortalRulesLocalization.Translate(
                "$sighsorry_portalrules_invite_departure_cooldown",
                departure)
            : PortalRulesLocalization.Translate(
                "$sighsorry_portalrules_invite_arrival_cooldown",
                arrival.Length > 0 ? arrival : "?");
    }

    private static bool CompleteTeleport(
        Vector3 targetPosition,
        Quaternion targetRotation,
        float exitDistance,
        int coinCost,
        int authorizedCargoWeightUnits,
        TeleportRequestKind kind,
        bool sourceAllowsAllItems,
        Action? endSelection)
    {
        Player? player = Player.m_localPlayer;
        if (player == null ||
            !PublicPortalTravelCost.TryGetLocalCargoWeightUnits(
                sourceAllowsAllItems,
                out int currentCargoWeightUnits,
                forceRefresh: true))
        {
            player?.Message(MessageHud.MessageType.Center, "$msg_noteleport");
            return false;
        }

        if (currentCargoWeightUnits != authorizedCargoWeightUnits)
        {
            player.Message(
                MessageHud.MessageType.Center,
                PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_cargo_changed"));
            return false;
        }

        if (coinCost < 0 || !PortalCoinWallet.HasLocalCoins(coinCost))
        {
            player.Message(
                MessageHud.MessageType.Center,
                PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_need_coins",
                    Math.Max(0, coinCost).ToString()));
            return false;
        }

        if (!PortalCoinWallet.TryConsumeLocalCoins(
                coinCost,
                out PortalCoinPaymentReceipt fareReceipt))
        {
            player.Message(
                MessageHud.MessageType.Center,
                PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_fare_reservation_failed"));
            return false;
        }

        Vector3 position =
            targetPosition +
            targetRotation * Vector3.forward * exitDistance +
            Vector3.up;
        bool teleportStarted;
        try
        {
            teleportStarted = player.TeleportTo(
                position,
                targetRotation,
                distantTeleport: true);
        }
        catch (Exception ex)
        {
            RefundReservedFare(fareReceipt);
            PortalRulesPlugin.PortalRulesLogger.LogError(
                $"Portal travel failed while starting the teleport: {ex}");
            player.Message(
                MessageHud.MessageType.Center,
                PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_teleport_start_failed"));
            return false;
        }

        if (!teleportStarted)
        {
            RefundReservedFare(fareReceipt);
            player.Message(
                MessageHud.MessageType.Center,
                PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_teleport_start_failed"));
            return false;
        }

        try
        {
            endSelection?.Invoke();
        }
        catch (Exception ex)
        {
            PortalRulesPlugin.PortalRulesLogger.LogError(
                $"Portal travel started, but its selection cleanup failed: {ex}");
        }

        if (kind == TeleportRequestKind.MapSelection)
        {
            try
            {
                Minimap.instance?.SetMapMode(Minimap.MapMode.Small);
            }
            catch (Exception ex)
            {
                PortalRulesPlugin.PortalRulesLogger.LogError(
                    $"Portal travel started, but closing the map failed: {ex}");
            }
        }

        try
        {
            Game.instance?.IncrementPlayerStat(PlayerStatType.PortalsUsed);
        }
        catch (Exception ex)
        {
            PortalRulesPlugin.PortalRulesLogger.LogError(
                $"Portal travel started, but updating the portal-use statistic failed: {ex}");
        }

        return true;
    }

    private static void RefundReservedFare(
        in PortalCoinPaymentReceipt receipt)
    {
        if (!PortalCoinWallet.TryRefundLocalCoins(receipt))
        {
            PortalRulesPlugin.PortalRulesLogger.LogError(
                $"Portal travel did not start and restoring the reserved {receipt.TotalCoins} Coins failed.");
        }
    }

    private static bool CanCompleteGrantedTeleport(
        ZDOID sourcePortalId,
        out string message)
    {
        Player? player = Player.m_localPlayer;
        if (player == null ||
            sourcePortalId.IsNone() ||
            ZDOMan.instance == null)
        {
            message = PortalRulesLocalization.Translate(
                "$sighsorry_portalrules_portal_no_longer_ready");
            return false;
        }

        if (ZoneSystem.instance == null)
        {
            message = PortalRulesLocalization.Translate(
                "$sighsorry_portalrules_portal_no_longer_ready");
            return false;
        }

        if (ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoPortals))
        {
            message = "$msg_blocked";
            return false;
        }

        if (ZoneSystem.instance.GetGlobalKey(GlobalKeys.NoBossPortals) &&
            (RandEventSystem.instance?.GetBossEvent() != null ||
             ZoneSystem.instance.GetGlobalKey(
                 GlobalKeys.activeBosses,
                 out float activeBosses) &&
             activeBosses > 0f))
        {
            message = "$msg_blockedbyboss";
            return false;
        }

        ZDO? sourcePortal = ZDOMan.instance.GetZDO(sourcePortalId);
        Vector3 playerPosition = player.transform.position;
        Vector3 sourcePosition =
            sourcePortal != null
                ? sourcePortal.GetPosition()
                : Vector3.positiveInfinity;
        if (sourcePortal == null ||
            !sourcePortal.IsValid() ||
            !IsFinite(playerPosition) ||
            !IsFinite(sourcePosition) ||
            Vector3.Distance(
                playerPosition,
                sourcePosition) >
            PublicPortalData.MaximumTeleportSourceDistance)
        {
            message = PortalRulesLocalization.Translate(
                "$sighsorry_portalrules_move_closer_to_source_retry");
            return false;
        }

        message = "";
        return true;
    }

    private static bool IsFinite(Vector3 value)
    {
        return IsFinite(value.x) &&
               IsFinite(value.y) &&
               IsFinite(value.z);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    internal static bool CanTeleportWithItems(bool sourceAllowsAllItems)
    {
        return PublicPortalTravelCost.TryGetLocalCargoWeightUnits(
            sourceAllowsAllItems,
            out _);
    }

    private static long NextRequestId()
    {
        unchecked
        {
            _nextRequestId++;
            if (_nextRequestId == 0L)
            {
                _nextRequestId++;
            }

            return _nextRequestId;
        }
    }
}
