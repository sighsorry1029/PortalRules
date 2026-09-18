using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;

namespace PortalRules;

internal static partial class PublicPortalTeleportService
{
    private sealed class PendingCommit
    {
        internal readonly string Ticket;
        internal readonly float ExpiresAt;
        internal float NextSendAt;

        internal PendingCommit(string ticket, float now)
        {
            Ticket = ticket;
            ExpiresAt = now + ClientCommitRetryLifetimeSeconds;
            NextSendAt = now;
        }
    }

    private sealed class ServerTravelTicket
    {
        internal readonly string Ticket;
        internal readonly long RequestId;
        internal readonly ZRpc? PeerRpc;
        internal readonly long PeerUid;
        internal readonly string AccountId;
        internal readonly ZDOID SourceId;
        internal readonly ZDOID DestinationId;
        internal readonly string SourceFavoriteId;
        internal readonly string DestinationFavoriteId;
        internal readonly bool UsesInviteAsSource;
        internal readonly bool UsesInviteAsDestination;
        internal readonly int CoinCost;
        internal readonly InviteTravelCooldownReservation CooldownReservation;
        internal float ExpiresAt;
        internal float NextCommitAttemptAt;
        internal bool CommitRequested;
        internal bool Committed;

        internal ServerTravelTicket(
            string ticket,
            long requestId,
            ZRpc? peerRpc,
            long peerUid,
            string accountId,
            ZDOID sourceId,
            ZDOID destinationId,
            PortalTravelAuthorization authorization,
            float now)
        {
            Ticket = ticket;
            RequestId = requestId;
            PeerRpc = peerRpc;
            PeerUid = peerUid;
            AccountId = accountId;
            SourceId = sourceId;
            DestinationId = destinationId;
            SourceFavoriteId = authorization.SourceFavoriteId;
            DestinationFavoriteId = authorization.DestinationFavoriteId;
            UsesInviteAsSource = authorization.UsesInviteAsSource;
            UsesInviteAsDestination = authorization.UsesInviteAsDestination;
            CoinCost = authorization.CoinCost;
            CooldownReservation = authorization.CooldownReservation;
            ExpiresAt = now + ServerTicketLifetimeSeconds;
            NextCommitAttemptAt = now;
        }
    }

    private const string TravelCommitRpc =
        "sighsorry.PortalRules.TravelCommit.v4";
    private const string TravelCommitAckRpc =
        "sighsorry.PortalRules.TravelCommitAck.v4";
    private const string TravelCancelRpc =
        "sighsorry.PortalRules.TravelCancel.v4";
    private const float ControlRequestCooldownSeconds = 0.1f;
    private const float ClientCommitRetrySeconds = 1f;
    private const float ClientCommitRetryLifetimeSeconds = 30f;
    private const float ServerTicketLifetimeSeconds = 30f;
    private const float ServerCommitLifetimeSeconds = 120f;
    private const float CompletedTicketRetentionSeconds = 60f;
    private const float ServerCommitRetrySeconds = 1f;
    private const int TicketRandomByteCount = 24;
    private const int EncodedTicketLength = 32;
    private const int MaximumControlPackageBytes = 128;
    private const int MaximumServerTickets = 4096;
    private const int MaximumPendingTicketsPerPeer = 4;

    private static readonly Dictionary<ZRpc, float> LastControlRequestAt = new();
    private static readonly Dictionary<string, ServerTravelTicket> ServerTickets =
        new(StringComparer.Ordinal);
    private static PendingCommit? _pendingCommit;

    private static void BeginPendingCommit(string ticket)
    {
        if (!IsValidTicket(ticket))
        {
            return;
        }

        _pendingCommit = new PendingCommit(
            ticket,
            Time.realtimeSinceStartup);
        TickClientCommit(Time.realtimeSinceStartup);
    }

    private static void TickClientCommit(float now)
    {
        PendingCommit? pending = _pendingCommit;
        if (pending == null)
        {
            return;
        }

        if (now >= pending.ExpiresAt)
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                "The server did not acknowledge the portal fare/travel commit before its retry window expired.");
            _pendingCommit = null;
            return;
        }

        if (now < pending.NextSendAt)
        {
            return;
        }

        ZRpc? serverRpc = ZNet.instance?.GetServerRPC();
        if (serverRpc == null || !serverRpc.IsConnected())
        {
            pending.NextSendAt = now + ClientCommitRetrySeconds;
            return;
        }

        ZPackage package = new();
        package.Write(pending.Ticket);
        serverRpc.Invoke(TravelCommitRpc, package);
        pending.NextSendAt = now + ClientCommitRetrySeconds;
    }

    private static void SendTravelCancel(string ticket)
    {
        if (!IsValidTicket(ticket))
        {
            return;
        }

        ZRpc? serverRpc = ZNet.instance?.GetServerRPC();
        if (serverRpc == null || !serverRpc.IsConnected())
        {
            return;
        }

        ZPackage package = new();
        package.Write(ticket);
        serverRpc.Invoke(TravelCancelRpc, package);
    }

    private static void OnTravelCommit(ZRpc rpc, ZPackage package)
    {
        if (ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            IsControlRequestRateLimited(rpc) ||
            package == null ||
            package.GetArray().Length > MaximumControlPackageBytes ||
            !TryReadTicket(package, out string ticket))
        {
            return;
        }

        if (!ServerTickets.TryGetValue(ticket, out ServerTravelTicket serverTicket))
        {
            SendTravelCommitAck(rpc, ticket, committed: false);
            return;
        }

        if (!TryAuthenticateTicket(rpc, serverTicket, out ZNetPeer peer))
        {
            return;
        }

        if (serverTicket.Committed)
        {
            SendTravelCommitAck(rpc, ticket, committed: true);
            return;
        }

        float now = Time.realtimeSinceStartup;
        if (!serverTicket.CommitRequested && now >= serverTicket.ExpiresAt)
        {
            CancelServerTicket(serverTicket);
            SendTravelCommitAck(rpc, ticket, committed: false);
            return;
        }

        if (!serverTicket.CommitRequested)
        {
            serverTicket.CommitRequested = true;
            serverTicket.ExpiresAt = now + ServerCommitLifetimeSeconds;
        }

        TryCommitServerTicket(serverTicket, peer);
    }

    private static void OnTravelCancel(ZRpc rpc, ZPackage package)
    {
        if (ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            IsControlRequestRateLimited(rpc) ||
            package == null ||
            package.GetArray().Length > MaximumControlPackageBytes ||
            !TryReadTicket(package, out string ticket) ||
            !ServerTickets.TryGetValue(ticket, out ServerTravelTicket serverTicket) ||
            !TryAuthenticateTicket(rpc, serverTicket, out _))
        {
            return;
        }

        if (serverTicket.Committed)
        {
            SendTravelCommitAck(rpc, ticket, committed: true);
            return;
        }

        // Once a commit is received, the teleport and client-side fare charge
        // have already started. A later cancel must not erase that commit.
        if (!serverTicket.CommitRequested)
        {
            CancelServerTicket(serverTicket);
        }
    }

    private static void OnTravelCommitAck(ZRpc rpc, ZPackage package)
    {
        if (ZNet.instance == null ||
            ZNet.instance.IsServer() ||
            !ReferenceEquals(rpc, ZNet.instance.GetServerRPC()))
        {
            return;
        }

        try
        {
            string ticket = package.ReadString();
            bool committed = package.ReadBool();
            if (!IsValidTicket(ticket) ||
                _pendingCommit == null ||
                !string.Equals(
                    _pendingCommit.Ticket,
                    ticket,
                    StringComparison.Ordinal))
            {
                return;
            }

            _pendingCommit = null;
            if (!committed)
            {
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    "The server rejected the portal fare/travel commit after the teleport had started.");
            }
        }
        catch (Exception ex)
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                $"Ignored malformed portal travel commit acknowledgment: {ex.Message}");
        }
    }

    private static void SendTravelCommitAck(
        ZRpc rpc,
        string ticket,
        bool committed)
    {
        if (rpc == null || !rpc.IsConnected() || !IsValidTicket(ticket))
        {
            return;
        }

        ZPackage package = new();
        package.Write(ticket);
        package.Write(committed);
        rpc.Invoke(TravelCommitAckRpc, package);
    }

    private static bool TryCreateServerTicket(
        string ticket,
        long requestId,
        ZRpc? peerRpc,
        long peerUid,
        ZDOID sourceId,
        ZDOID destinationId,
        PortalTravelAuthorization authorization,
        out ServerTravelTicket? serverTicket)
    {
        serverTicket = null;
        float now = Time.realtimeSinceStartup;
        PruneServerTickets(now);
        if (!IsValidTicket(ticket) ||
            sourceId.IsNone() ||
            destinationId.IsNone() ||
            sourceId == destinationId ||
            authorization.CoinCost < 0 ||
            !PublicPortalData.TryNormalizeAccountId(
                authorization.AccountId,
                out string accountId) ||
            !PublicPortalData.TryNormalizeFavoriteId(
                authorization.SourceFavoriteId,
                out string sourceFavoriteId) ||
            !PublicPortalData.TryNormalizeFavoriteId(
                authorization.DestinationFavoriteId,
                out string destinationFavoriteId) ||
            ServerTickets.ContainsKey(ticket) ||
            ServerTickets.Count >= MaximumServerTickets)
        {
            return false;
        }

        if (authorization.CooldownReservation.IsActive &&
            (!string.Equals(
                 authorization.CooldownReservation.ReservationId,
                 ticket,
                 StringComparison.Ordinal) ||
             !string.Equals(
                 authorization.CooldownReservation.AccountId,
                 accountId,
                 StringComparison.Ordinal) ||
             authorization.CooldownReservation.RecordsDeparture &&
             (!authorization.UsesInviteAsSource ||
              !string.Equals(
                  authorization.CooldownReservation.SourceFavoriteId,
                  sourceFavoriteId,
                  StringComparison.Ordinal)) ||
             authorization.CooldownReservation.RecordsArrival &&
             (!authorization.UsesInviteAsDestination ||
              !string.Equals(
                  authorization.CooldownReservation.DestinationFavoriteId,
                  destinationFavoriteId,
                  StringComparison.Ordinal))))
        {
            return false;
        }

        if (peerRpc != null)
        {
            ZNetPeer? peer = PublicPortalData.FindPeer(ZNet.instance, peerRpc);
            if (peer == null ||
                peer.m_uid != peerUid ||
                !peer.IsReady() ||
                !PublicPortalData.TryGetPeerAccountId(peer, out string steamId) ||
                !string.Equals(steamId, accountId, StringComparison.Ordinal))
            {
                return false;
            }
        }

        int pendingForIdentity = ServerTickets.Values.Count(candidate =>
            !candidate.Committed &&
            (peerRpc != null
                ? ReferenceEquals(candidate.PeerRpc, peerRpc)
                : candidate.PeerRpc == null &&
                  string.Equals(
                      candidate.AccountId,
                      accountId,
                      StringComparison.Ordinal)));
        if (pendingForIdentity >= MaximumPendingTicketsPerPeer)
        {
            return false;
        }

        PortalTravelAuthorization canonicalAuthorization =
            new(
                authorization.TargetPosition,
                authorization.TargetRotation,
                authorization.CoinCost,
                authorization.CargoWeightUnits,
                accountId,
                sourceFavoriteId,
                destinationFavoriteId,
                authorization.UsesInviteAsSource,
                authorization.UsesInviteAsDestination,
                authorization.CooldownReservation);
        serverTicket = new ServerTravelTicket(
            ticket,
            requestId,
            peerRpc,
            peerUid,
            accountId,
            sourceId,
            destinationId,
            canonicalAuthorization,
            now);
        ServerTickets.Add(ticket, serverTicket);
        return true;
    }

    private static bool TryCreateRequiredServerTicket(
        string ticket,
        long requestId,
        ZRpc? peerRpc,
        long peerUid,
        ZDOID sourceId,
        ZDOID destinationId,
        PortalTravelAuthorization authorization,
        out ServerTravelTicket? serverTicket)
    {
        if (!authorization.RequiresTransaction)
        {
            serverTicket = null;
            return true;
        }

        return TryCreateServerTicket(
            ticket,
            requestId,
            peerRpc,
            peerUid,
            sourceId,
            destinationId,
            authorization,
            out serverTicket);
    }

    private static void FinalizeLocalServerTicket(
        ServerTravelTicket? serverTicket,
        bool teleportStarted)
    {
        if (serverTicket == null)
        {
            return;
        }

        if (!teleportStarted)
        {
            CancelServerTicket(serverTicket);
            return;
        }

        serverTicket.CommitRequested = true;
        serverTicket.ExpiresAt = Time.realtimeSinceStartup +
                                 ServerCommitLifetimeSeconds;
        TryCommitServerTicket(serverTicket, peer: null);
    }

    private static bool TryAuthenticateTicket(
        ZRpc rpc,
        ServerTravelTicket ticket,
        out ZNetPeer peer)
    {
        peer = null!;
        if (ticket.PeerRpc == null ||
            !ReferenceEquals(ticket.PeerRpc, rpc) ||
            ZNet.instance == null)
        {
            return false;
        }

        ZNetPeer? authenticatedPeer = PublicPortalData.FindPeer(ZNet.instance, rpc);
        if (authenticatedPeer == null ||
            authenticatedPeer.m_uid != ticket.PeerUid ||
            !authenticatedPeer.IsReady() ||
            !PublicPortalData.TryGetPeerAccountId(
                authenticatedPeer,
                out string steamId) ||
            !string.Equals(
                steamId,
                ticket.AccountId,
                StringComparison.Ordinal))
        {
            return false;
        }

        peer = authenticatedPeer;
        return true;
    }

    private static bool TryCommitServerTicket(
        ServerTravelTicket ticket,
        ZNetPeer? peer)
    {
        if (ticket.Committed)
        {
            if (ticket.PeerRpc != null)
            {
                SendTravelCommitAck(
                    ticket.PeerRpc,
                    ticket.Ticket,
                    committed: true);
            }

            return true;
        }

        float now = Time.realtimeSinceStartup;
        if (!ticket.CommitRequested || now < ticket.NextCommitAttemptAt)
        {
            return false;
        }

        ticket.NextCommitAttemptAt = now + ServerCommitRetrySeconds;
        if (ticket.CooldownReservation.IsActive &&
            !InviteTravelCooldownStore.TryCommitReservation(
                ticket.CooldownReservation,
                out InviteTravelCooldownFailure failure))
        {
            if (failure is InviteTravelCooldownFailure.StorageUnavailable or
                InviteTravelCooldownFailure.SaveFailed)
            {
                return false;
            }

            PortalRulesPlugin.PortalRulesLogger.LogError(
                $"Portal travel commit {ticket.RequestId} could not persist its Invite cooldown: {failure}.");
            ZRpc? rejectedRpc = ticket.PeerRpc;
            string rejectedTicket = ticket.Ticket;
            CancelServerTicket(ticket);
            if (rejectedRpc != null)
            {
                SendTravelCommitAck(
                    rejectedRpc,
                    rejectedTicket,
                    committed: false);
            }

            return false;
        }

        ticket.Committed = true;
        ticket.ExpiresAt = now + CompletedTicketRetentionSeconds;
        if (ticket.CooldownReservation.IsActive)
        {
            if (peer != null)
            {
                PublicPortalCatalog.RefreshCommittedInviteCooldownView(peer);
            }
            else if (ticket.PeerRpc == null)
            {
                PublicPortalCatalog.RefreshCommittedInviteCooldownView(
                    peer: null);
            }
        }

        PortalRulesPlugin.PortalRulesLogger.LogDebug(
            $"Committed portal travel {ticket.RequestId} for SteamID {ticket.AccountId}; " +
            $"server fare {ticket.CoinCost} Coins, " +
            $"source {ticket.SourceId}/{ticket.SourceFavoriteId} " +
            $"(Invite={ticket.UsesInviteAsSource}), destination " +
            $"{ticket.DestinationId}/{ticket.DestinationFavoriteId} " +
            $"(Invite={ticket.UsesInviteAsDestination}).");
        if (ticket.PeerRpc != null)
        {
            SendTravelCommitAck(
                ticket.PeerRpc,
                ticket.Ticket,
                committed: true);
        }
        else
        {
            // Local-host travel has no lossy RPC acknowledgment to replay.
            ServerTickets.Remove(ticket.Ticket);
        }

        return true;
    }

    private static void TickServerTickets(float now)
    {
        if (ServerTickets.Count == 0)
        {
            return;
        }

        foreach (ServerTravelTicket ticket in ServerTickets.Values.ToArray())
        {
            if (ticket.Committed)
            {
                if (now >= ticket.ExpiresAt)
                {
                    ServerTickets.Remove(ticket.Ticket);
                }

                continue;
            }

            if (now >= ticket.ExpiresAt)
            {
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    ticket.CommitRequested
                        ? $"Portal travel commit {ticket.RequestId} expired before its Invite cooldown could be persisted."
                        : $"Uncommitted portal travel reservation {ticket.RequestId} expired.");
                ZRpc? rpc = ticket.PeerRpc;
                string token = ticket.Ticket;
                bool commitRequested = ticket.CommitRequested;
                CancelServerTicket(ticket);
                if (commitRequested && rpc != null)
                {
                    SendTravelCommitAck(rpc, token, committed: false);
                }

                continue;
            }

            if (!ticket.CommitRequested)
            {
                continue;
            }

            ZNetPeer? peer = null;
            if (ticket.PeerRpc != null)
            {
                TryAuthenticateTicket(ticket.PeerRpc, ticket, out peer);
            }

            TryCommitServerTicket(ticket, peer);
        }
    }

    private static void PruneServerTickets(float now)
    {
        foreach (ServerTravelTicket ticket in ServerTickets.Values
                     .Where(candidate => now >= candidate.ExpiresAt)
                     .ToArray())
        {
            CancelServerTicket(ticket);
        }
    }

    private static void CancelServerTicketsForPeer(ZRpc rpc)
    {
        foreach (ServerTravelTicket ticket in ServerTickets.Values
                     .Where(candidate => ReferenceEquals(candidate.PeerRpc, rpc))
                     .ToArray())
        {
            if (ticket.CommitRequested && !ticket.Committed)
            {
                // The commit was authenticated before disconnect. Keep retrying
                // durable cooldown storage, but there is no peer left to ACK.
                continue;
            }

            CancelServerTicket(ticket);
        }
    }

    private static void ClearServerTickets()
    {
        foreach (ServerTravelTicket ticket in ServerTickets.Values.ToArray())
        {
            CancelServerTicket(ticket);
        }

        ServerTickets.Clear();
    }

    private static void CancelServerTicket(ServerTravelTicket ticket)
    {
        if (!ServerTickets.Remove(ticket.Ticket) || ticket.Committed)
        {
            return;
        }

        InviteTravelCooldownStore.CancelReservation(ticket.Ticket);
    }

    private static bool TryReadTicket(ZPackage package, out string ticket)
    {
        ticket = "";
        try
        {
            ticket = package.ReadString();
            return IsValidTicket(ticket);
        }
        catch
        {
            ticket = "";
            return false;
        }
    }

    private static bool IsControlRequestRateLimited(ZRpc rpc)
    {
        float now = Time.realtimeSinceStartup;
        if (LastControlRequestAt.TryGetValue(rpc, out float lastAt) &&
            now - lastAt < ControlRequestCooldownSeconds)
        {
            return true;
        }

        LastControlRequestAt[rpc] = now;
        return false;
    }

    private static bool IsValidTicket(string ticket)
    {
        if (ticket == null || ticket.Length != EncodedTicketLength)
        {
            return false;
        }

        try
        {
            return Convert.FromBase64String(ticket).Length == TicketRandomByteCount;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string CreateTicket()
    {
        byte[] bytes = new byte[TicketRandomByteCount];
        using RandomNumberGenerator random = RandomNumberGenerator.Create();
        for (int attempt = 0; attempt < 4; attempt++)
        {
            random.GetBytes(bytes);
            string ticket = Convert.ToBase64String(bytes);
            if (!ServerTickets.ContainsKey(ticket))
            {
                return ticket;
            }
        }

        throw new InvalidOperationException(
            "Failed to allocate a unique portal travel ticket.");
    }
}
