using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;

namespace PortalRules;

internal static partial class PublicPortalCatalog
{
    private readonly struct SentViewState
    {
        public readonly int Revision;
        public readonly string RecipientOwnerId;
        public readonly bool IsAdmin;
        public readonly ulong RecipientViewFingerprint;
        public readonly long ViewToken;
        public readonly bool Acknowledged;
        public readonly float LastSentAt;

        public SentViewState(
            int revision,
            string recipientOwnerId,
            bool isAdmin,
            ulong recipientViewFingerprint,
            long viewToken,
            bool acknowledged,
            float lastSentAt)
        {
            Revision = revision;
            RecipientOwnerId = recipientOwnerId;
            IsAdmin = isAdmin;
            RecipientViewFingerprint = recipientViewFingerprint;
            ViewToken = viewToken;
            Acknowledged = acknowledged;
            LastSentAt = lastSentAt;
        }

        public bool MatchesView(
            int revision,
            string recipientOwnerId,
            bool isAdmin,
            ulong recipientViewFingerprint)
        {
            return MatchesRecipient(revision, recipientOwnerId, isAdmin) &&
                   RecipientViewFingerprint == recipientViewFingerprint;
        }

        public bool MatchesRecipient(
            int revision,
            string recipientOwnerId,
            bool isAdmin)
        {
            return Revision == revision &&
                   IsAdmin == isAdmin &&
                   string.Equals(
                       RecipientOwnerId,
                       recipientOwnerId,
                       StringComparison.Ordinal);
        }

        public SentViewState WithAcknowledgement()
        {
            return new SentViewState(
                Revision,
                RecipientOwnerId,
                IsAdmin,
                RecipientViewFingerprint,
                ViewToken,
                acknowledged: true,
                lastSentAt: LastSentAt);
        }
    }

    private sealed class PendingSnapshot
    {
        public readonly int Revision;
        public readonly long ViewToken;
        public readonly int ChunkCount;
        public readonly int TotalCount;
        public readonly string RecipientOwnerId;
        public readonly bool RecipientIsAdmin;
        public readonly long ServerUtcAtSend;
        public readonly long FirstChunkReceivedTimestamp;
        public readonly List<PublicPortalCatalogEntry> Entries;
        public readonly HashSet<string> FavoriteIds = new(StringComparer.Ordinal);
        public int NextChunk;

        public PendingSnapshot(
            int revision,
            long viewToken,
            int chunkCount,
            int totalCount,
            string recipientOwnerId,
            bool recipientIsAdmin,
            long serverUtcAtSend)
        {
            Revision = revision;
            ViewToken = viewToken;
            ChunkCount = chunkCount;
            TotalCount = totalCount;
            RecipientOwnerId = recipientOwnerId;
            RecipientIsAdmin = recipientIsAdmin;
            ServerUtcAtSend = serverUtcAtSend;
            FirstChunkReceivedTimestamp = Stopwatch.GetTimestamp();
            Entries = new List<PublicPortalCatalogEntry>(totalCount);
        }
    }

    private const byte FormatVersion = 7;
    private const int MaximumEntriesPerChunk = 96;
    private const int MaximumChunkCount =
        (MaximumCatalogEntries + MaximumEntriesPerChunk - 1) /
        MaximumEntriesPerChunk;
    private const int MaximumFavoriteIdLength = 32;
    private const float ClientRequestCooldownSeconds = 0.5f;
    private const float ServerRequestCooldownSeconds = 30f;
    private const float SnapshotRetrySeconds = 30f;
    private const string RequestCatalogRpc =
        "sighsorry.PortalRules.CatalogRequest.v7";
    private const string ReceiveCatalogRpc =
        "sighsorry.PortalRules.CatalogSnapshot.v7";

    private static readonly Dictionary<ZRpc, float> LastServerRequestAt = new();
    private static readonly Dictionary<ZRpc, SentViewState> LastSentViews = new();
    private static long _clientViewToken;
    private static long _nextViewToken;
    private static float _lastClientRequestAt = -100f;
    private static long _clientServerUtcAtSync;
    private static long _clientServerClockTimestamp;
    private static PendingSnapshot? _pendingSnapshot;

    private static void WriteCatalogEntry(
        ZPackage package,
        PublicPortalCatalogEntry entry)
    {
        package.Write(entry.Id);
        package.Write(entry.FavoriteId);
        package.Write(entry.PrefabHash);
        package.Write(entry.AllowsAllItems);
        package.Write(entry.Position);
        package.Write(entry.Rotation);
        package.Write(entry.Tag);
        package.Write((int)entry.AccessMode);
        package.Write(entry.Owner.Id);
        package.Write(entry.Owner.Name);
        package.Write(entry.MyPortalOrdinal);
        package.Write(entry.MyPortalLimit);
        package.Write(entry.ModeOrdinal);
        package.Write(entry.ModeCurrent);
        package.Write(entry.ModeLimit);
        package.Write(entry.InviteDepartureCooldownUntilUtc);
        package.Write(entry.InviteArrivalCooldownUntilUtc);
    }

    private static bool TryReadCatalogEntry(
        ZPackage package,
        HashSet<string> favoriteIds,
        out PublicPortalCatalogEntry entry)
    {
        ZDOID id = package.ReadZDOID();
        string rawFavoriteId = package.ReadString();
        int prefabHash = package.ReadInt();
        bool allowsAllItems = package.ReadBool();
        Vector3 position = package.ReadVector3();
        Quaternion rotation = package.ReadQuaternion();
        string tag = package.ReadString();
        int rawMode = package.ReadInt();
        string ownerId = package.ReadString();
        string ownerName = package.ReadString();
        int myPortalOrdinal = package.ReadInt();
        int myPortalLimit = package.ReadInt();
        int modeOrdinal = package.ReadInt();
        int modeCurrent = package.ReadInt();
        int modeLimit = package.ReadInt();
        long inviteDepartureCooldownUntilUtc = package.ReadLong();
        long inviteArrivalCooldownUntilUtc = package.ReadLong();
        string favoriteId = "";
        bool favoriteIdIsValid =
            rawFavoriteId.Length <= MaximumFavoriteIdLength &&
            PublicPortalData.TryNormalizeFavoriteId(
                rawFavoriteId,
                out favoriteId);

        bool isNumberedMode =
            rawMode == (int)PublicPortalAccessMode.Invite ||
            rawMode == (int)PublicPortalAccessMode.Clan;
        bool validMyPortalMetadata =
            myPortalOrdinal >= 0 &&
            myPortalOrdinal <= MaximumCatalogEntries &&
            myPortalLimit >= -1 &&
            myPortalLimit <= PublicPortalData.MaximumPortalLimit &&
            (myPortalOrdinal != 0 || myPortalLimit == 0);
        bool validModeMetadata = isNumberedMode
            ? modeOrdinal > 0 &&
              modeOrdinal <= MaximumCatalogEntries &&
              modeCurrent >= modeOrdinal &&
              modeCurrent <= MaximumCatalogEntries &&
              modeLimit >= -1 &&
              modeLimit <= PublicPortalData.MaximumPortalLimit
            : modeOrdinal == 0 &&
              modeCurrent == 0 &&
              modeLimit == 0;
        bool validCooldownMetadata =
            inviteDepartureCooldownUntilUtc >= 0L &&
            inviteDepartureCooldownUntilUtc <=
            PublicPortalData.MaximumUtcSeconds &&
            inviteArrivalCooldownUntilUtc >= 0L &&
            inviteArrivalCooldownUntilUtc <=
            PublicPortalData.MaximumUtcSeconds &&
            (rawMode == (int)PublicPortalAccessMode.Invite ||
             inviteDepartureCooldownUntilUtc == 0L &&
             inviteArrivalCooldownUntilUtc == 0L);
        if (id.IsNone() ||
            !favoriteIdIsValid ||
            !favoriteIds.Add(favoriteId) ||
            !IsFinite(position) ||
            !IsFinite(rotation) ||
            tag.Length > MaximumTagLength ||
            ownerId.Length > MaximumOwnerIdLength ||
            ownerName.Length > MaximumOwnerNameLength ||
            !Enum.IsDefined(typeof(PublicPortalAccessMode), rawMode) ||
            !validMyPortalMetadata ||
            !validModeMetadata ||
            !validCooldownMetadata)
        {
            entry = default;
            return false;
        }

        entry = new PublicPortalCatalogEntry(
            id,
            favoriteId,
            prefabHash,
            allowsAllItems,
            position,
            rotation,
            tag,
            (PublicPortalAccessMode)rawMode,
            new PortalOwner(ownerId, ownerName),
            myPortalOrdinal,
            myPortalLimit,
            modeOrdinal,
            modeCurrent,
            modeLimit,
            inviteDepartureCooldownUntilUtc,
            inviteArrivalCooldownUntilUtc);
        return true;
    }

    public static void RegisterPeer(ZNet znet, ZNetPeer peer)
    {
        if (znet == null || peer?.m_rpc == null)
        {
            return;
        }

        if (znet.IsServer())
        {
            peer.m_rpc.Register<int, long>(RequestCatalogRpc, OnCatalogRequest);
        }
        else
        {
            peer.m_rpc.Register<ZPackage>(ReceiveCatalogRpc, OnCatalogReceived);
        }
    }

    public static void ForgetPeer(ZRpc? rpc)
    {
        if (rpc == null)
        {
            return;
        }

        LastServerRequestAt.Remove(rpc);
        LastSentViews.Remove(rpc);
    }

    public static void RequestRefresh(bool force = false)
    {
        if (ZNet.instance == null)
        {
            return;
        }

        if (ZNet.instance.IsServer())
        {
            RefreshAndBroadcast();
            return;
        }

        float now = Time.realtimeSinceStartup;
        if (!force && now - _lastClientRequestAt < ClientRequestCooldownSeconds)
        {
            return;
        }

        ZRpc? serverRpc = ZNet.instance.GetServerRPC();
        if (serverRpc == null)
        {
            return;
        }

        _lastClientRequestAt = now;
        serverRpc.Invoke(RequestCatalogRpc, _clientRevision, _clientViewToken);
    }

    private static void OnCatalogRequest(
        ZRpc rpc,
        int knownRevision,
        long knownViewToken)
    {
        ZNet? znet = ZNet.instance;
        if (znet == null || !znet.IsServer())
        {
            return;
        }

        ZNetPeer? peer = PublicPortalData.FindPeer(znet, rpc);
        if (peer == null || !peer.IsReady())
        {
            PortalRulesPlugin.PortalRulesLogger.LogDebug(
                "Rejected a portal catalog request from an unauthenticated connection.");
            return;
        }

        float now = Time.realtimeSinceStartup;
        if (_hasSnapshot &&
            knownRevision == _serverRevision &&
            LastSentViews.TryGetValue(rpc, out SentViewState lastView) &&
            knownViewToken != 0 &&
            knownViewToken == lastView.ViewToken &&
            PublicPortalData.TryGetPeerOwner(
                peer,
                out PortalOwner authenticatedOwner) &&
            lastView.MatchesRecipient(
                _serverRevision,
                SanitizeOwner(authenticatedOwner).Id,
                PublicPortalData.IsPeerAdmin(znet, peer)))
        {
            LastSentViews[rpc] = lastView.WithAcknowledgement();
            return;
        }

        if (LastServerRequestAt.TryGetValue(rpc, out float lastRequestAt) &&
            now - lastRequestAt < ServerRequestCooldownSeconds)
        {
            return;
        }

        LastServerRequestAt[rpc] = now;
        if (!_hasSnapshot)
        {
            RefreshServerSnapshot();
        }

        RecipientContext recipient = CreateRecipientContext(peer);
        List<PublicPortalCatalogEntry> visibleEntries =
            BuildRecipientEntries(
                recipient,
                out ulong recipientViewFingerprint);
        SendSnapshot(
            rpc,
            peer,
            recipient,
            visibleEntries,
            recipientViewFingerprint);
    }

    private static void OnCatalogReceived(ZRpc rpc, ZPackage package)
    {
        if (ZNet.instance == null ||
            ZNet.instance.IsServer() ||
            !ReferenceEquals(rpc, ZNet.instance.GetServerRPC()))
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                "Ignored portal catalog data from a non-server connection.");
            return;
        }

        try
        {
            byte version = package.ReadByte();
            if (version != FormatVersion)
            {
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    $"Ignored portal catalog format {version}; expected {FormatVersion}.");
                return;
            }

            int revision = package.ReadInt();
            long viewToken = package.ReadLong();
            int chunkIndex = package.ReadInt();
            int chunkCount = package.ReadInt();
            int totalCount = package.ReadInt();
            string recipientOwnerId = package.ReadString();
            bool recipientIsAdmin = package.ReadBool();
            long serverUtcAtSend = package.ReadLong();
            int entryCount = package.ReadInt();
            int expectedChunkCount = Math.Max(
                1,
                (totalCount + MaximumEntriesPerChunk - 1) /
                MaximumEntriesPerChunk);

            if (revision < 0 ||
                viewToken == 0 ||
                revision < _clientRevision ||
                chunkIndex < 0 ||
                chunkCount < 1 ||
                chunkCount > MaximumChunkCount ||
                chunkIndex >= chunkCount ||
                totalCount < 0 ||
                totalCount > MaximumCatalogEntries ||
                chunkCount != expectedChunkCount ||
                recipientOwnerId.Length > MaximumOwnerIdLength ||
                entryCount < 0 ||
                entryCount > MaximumEntriesPerChunk ||
                entryCount > totalCount ||
                serverUtcAtSend <= 0L ||
                serverUtcAtSend > PublicPortalData.MaximumUtcSeconds)
            {
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    $"Ignored malformed portal catalog chunk {chunkIndex}/{chunkCount} " +
                    $"for revision {revision}.");
                ResetPendingSnapshot();
                return;
            }

            PendingSnapshot? pendingSnapshot = _pendingSnapshot;
            if (chunkIndex == 0)
            {
                pendingSnapshot = new PendingSnapshot(
                    revision,
                    viewToken,
                    chunkCount,
                    totalCount,
                    recipientOwnerId,
                    recipientIsAdmin,
                    serverUtcAtSend);
                _pendingSnapshot = pendingSnapshot;
            }
            else if (pendingSnapshot == null ||
                     revision != pendingSnapshot.Revision ||
                     viewToken != pendingSnapshot.ViewToken ||
                     chunkCount != pendingSnapshot.ChunkCount ||
                     chunkIndex != pendingSnapshot.NextChunk ||
                     totalCount != pendingSnapshot.TotalCount ||
                     !string.Equals(
                         recipientOwnerId,
                         pendingSnapshot.RecipientOwnerId,
                         StringComparison.Ordinal) ||
                     recipientIsAdmin != pendingSnapshot.RecipientIsAdmin ||
                     serverUtcAtSend != pendingSnapshot.ServerUtcAtSend)
            {
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    $"Ignored out-of-sequence portal catalog chunk " +
                    $"{chunkIndex}/{chunkCount}.");
                ResetPendingSnapshot();
                return;
            }

            PendingSnapshot currentSnapshot = pendingSnapshot!;
            List<PublicPortalCatalogEntry> chunkEntries = new(entryCount);
            for (int i = 0; i < entryCount; i++)
            {
                if (!TryReadCatalogEntry(
                        package,
                        currentSnapshot.FavoriteIds,
                        out PublicPortalCatalogEntry entry))
                {
                    PortalRulesPlugin.PortalRulesLogger.LogWarning(
                        $"Ignored malformed portal catalog entry at index {i}.");
                    ResetPendingSnapshot();
                    return;
                }

                chunkEntries.Add(entry);
            }

            currentSnapshot.Entries.AddRange(chunkEntries);
            currentSnapshot.NextChunk++;
            if (currentSnapshot.NextChunk < currentSnapshot.ChunkCount)
            {
                return;
            }

            if (currentSnapshot.Entries.Count != currentSnapshot.TotalCount)
            {
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    $"Ignored incomplete portal catalog revision " +
                    $"{currentSnapshot.Revision}: expected " +
                    $"{currentSnapshot.TotalCount}, received " +
                    $"{currentSnapshot.Entries.Count}.");
                ResetPendingSnapshot();
                return;
            }

            List<PublicPortalCatalogEntry> received = currentSnapshot.Entries;
            int completedRevision = currentSnapshot.Revision;
            long completedViewToken = currentSnapshot.ViewToken;
            string completedOwnerId = currentSnapshot.RecipientOwnerId;
            bool completedIsAdmin = currentSnapshot.RecipientIsAdmin;
            long completedServerUtcAtSend = currentSnapshot.ServerUtcAtSend;
            long completedFirstChunkTimestamp =
                currentSnapshot.FirstChunkReceivedTimestamp;
            ResetPendingSnapshot();
            SynchronizeClientServerClock(
                completedServerUtcAtSend,
                completedFirstChunkTimestamp);

            bool recipientStateChanged =
                !string.Equals(
                    PublicPortalData.ServerAssignedOwnerId,
                    completedOwnerId,
                    StringComparison.Ordinal) ||
                PublicPortalData.ServerAssignedIsAdmin != completedIsAdmin;
            if (_hasSnapshot &&
                completedRevision == _clientRevision &&
                SnapshotsMatch(ClientEntries, received))
            {
                PublicPortalData.SetServerAssignedOwnerId(completedOwnerId);
                PublicPortalData.SetServerAssignedIsAdmin(completedIsAdmin);
                _clientViewToken = completedViewToken;
                if (recipientStateChanged)
                {
                    Updated?.Invoke();
                }

                AcknowledgeSnapshot(completedRevision, completedViewToken);
                return;
            }

            _clientRevision = completedRevision;
            _clientViewToken = completedViewToken;
            _hasSnapshot = true;
            PublicPortalData.SetServerAssignedOwnerId(completedOwnerId);
            PublicPortalData.SetServerAssignedIsAdmin(completedIsAdmin);
            ClientEntries.Clear();
            ClientEntries.AddRange(received);
            RebuildClientIndex();
            Updated?.Invoke();
            AcknowledgeSnapshot(completedRevision, completedViewToken);
        }
        catch (Exception ex)
        {
            ResetPendingSnapshot();
            PortalRulesPlugin.PortalRulesLogger.LogError(
                $"Failed to read portal catalog: {ex.Message}");
        }
    }

    private static void BroadcastSnapshot()
    {
        if (ZNet.instance == null)
        {
            return;
        }

        foreach (ZNetPeer peer in ZNet.instance.GetPeers().ToArray())
        {
            if (peer.IsReady() && peer.m_rpc != null)
            {
                SendSnapshot(peer.m_rpc, peer);
            }
        }
    }

    private static void BroadcastChangedViews()
    {
        if (ZNet.instance == null || !_hasSnapshot)
        {
            return;
        }

        foreach (ZNetPeer peer in ZNet.instance.GetPeers().ToArray())
        {
            if (!peer.IsReady() || peer.m_rpc == null)
            {
                continue;
            }

            RecipientContext recipient = CreateRecipientContext(peer);
            List<PublicPortalCatalogEntry> visibleEntries =
                BuildRecipientEntries(
                    recipient,
                    out ulong recipientViewFingerprint);
            if (!LastSentViews.TryGetValue(
                    peer.m_rpc,
                    out SentViewState lastView) ||
                !lastView.MatchesView(
                    _serverRevision,
                    recipient.OwnerId,
                    recipient.IsAdmin,
                    recipientViewFingerprint) ||
                (!lastView.Acknowledged &&
                 Time.realtimeSinceStartup - lastView.LastSentAt >=
                 SnapshotRetrySeconds))
            {
                SendSnapshot(
                    peer.m_rpc,
                    peer,
                    recipient,
                    visibleEntries,
                    recipientViewFingerprint);
            }
        }
    }

    private static void SendSnapshot(ZRpc rpc, ZNetPeer peer)
    {
        if (!rpc.IsConnected())
        {
            LastServerRequestAt.Remove(rpc);
            LastSentViews.Remove(rpc);
            return;
        }

        RecipientContext recipient = CreateRecipientContext(peer);
        List<PublicPortalCatalogEntry> visibleEntries =
            BuildRecipientEntries(
                recipient,
                out ulong recipientViewFingerprint);
        SendSnapshot(
            rpc,
            peer,
            recipient,
            visibleEntries,
            recipientViewFingerprint);
    }

    private static void SendSnapshot(
        ZRpc rpc,
        ZNetPeer peer,
        RecipientContext recipient,
        List<PublicPortalCatalogEntry> visibleEntries,
        ulong recipientViewFingerprint)
    {
        if (!rpc.IsConnected())
        {
            LastServerRequestAt.Remove(rpc);
            LastSentViews.Remove(rpc);
            return;
        }

        long viewToken =
            LastSentViews.TryGetValue(rpc, out SentViewState previousView) &&
            previousView.MatchesView(
                _serverRevision,
                recipient.OwnerId,
                recipient.IsAdmin,
                recipientViewFingerprint)
                ? previousView.ViewToken
                : NextViewToken();
        int chunkCount = Math.Max(
            1,
            (visibleEntries.Count + MaximumEntriesPerChunk - 1) /
            MaximumEntriesPerChunk);
        long serverUtcAtSend = PublicPortalData.GetUtcNowSeconds();
        for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            int startIndex = chunkIndex * MaximumEntriesPerChunk;
            int entryCount = Math.Min(
                MaximumEntriesPerChunk,
                visibleEntries.Count - startIndex);

            ZPackage package = new();
            package.Write(FormatVersion);
            package.Write(_serverRevision);
            package.Write(viewToken);
            package.Write(chunkIndex);
            package.Write(chunkCount);
            package.Write(visibleEntries.Count);
            package.Write(recipient.OwnerId);
            package.Write(recipient.IsAdmin);
            package.Write(serverUtcAtSend);
            package.Write(entryCount);
            for (int i = 0; i < entryCount; i++)
            {
                WriteCatalogEntry(
                    package,
                    visibleEntries[startIndex + i]);
            }

            rpc.Invoke(ReceiveCatalogRpc, package);
        }

        LastSentViews[rpc] = new SentViewState(
            _serverRevision,
            recipient.OwnerId,
            recipient.IsAdmin,
            recipientViewFingerprint,
            viewToken,
            acknowledged: false,
            lastSentAt: Time.realtimeSinceStartup);
        PublicPortalServerPolicy.SendQuotaState(peer, force: true);
    }

    private static void AcknowledgeSnapshot(int revision, long viewToken)
    {
        if (ZNet.instance == null || ZNet.instance.IsServer())
        {
            return;
        }

        ZRpc? serverRpc = ZNet.instance.GetServerRPC();
        if (serverRpc != null && serverRpc.IsConnected())
        {
            serverRpc.Invoke(RequestCatalogRpc, revision, viewToken);
        }
    }

    private static long NextViewToken()
    {
        unchecked
        {
            _nextViewToken++;
            if (_nextViewToken == 0)
            {
                _nextViewToken++;
            }

            return _nextViewToken;
        }
    }

    private static void ResetCatalogNetworkState()
    {
        _clientViewToken = 0L;
        _nextViewToken = 0L;
        _lastClientRequestAt = -100f;
        LastServerRequestAt.Clear();
        LastSentViews.Clear();
        _clientServerUtcAtSync = 0L;
        _clientServerClockTimestamp = 0L;
        ResetPendingSnapshot();
    }

    private static void ResetPendingSnapshot()
    {
        _pendingSnapshot = null;
    }

    internal static long GetEstimatedServerUtcNowSeconds()
    {
        if (ZNet.instance == null ||
            ZNet.instance.IsServer() ||
            _clientServerUtcAtSync <= 0L ||
            _clientServerClockTimestamp <= 0L)
        {
            return PublicPortalData.GetUtcNowSeconds();
        }

        long elapsedTicks =
            Stopwatch.GetTimestamp() - _clientServerClockTimestamp;
        if (elapsedTicks <= 0L)
        {
            return _clientServerUtcAtSync;
        }

        long elapsedSeconds =
            (long)(elapsedTicks / (double)Stopwatch.Frequency);
        return _clientServerUtcAtSync >
               PublicPortalData.MaximumUtcSeconds - elapsedSeconds
            ? PublicPortalData.MaximumUtcSeconds
            : _clientServerUtcAtSync + elapsedSeconds;
    }

    private static void SynchronizeClientServerClock(
        long serverUtcAtSend,
        long firstChunkReceivedTimestamp)
    {
        _clientServerUtcAtSync = serverUtcAtSend;
        _clientServerClockTimestamp = firstChunkReceivedTimestamp > 0L
            ? firstChunkReceivedTimestamp
            : Stopwatch.GetTimestamp();
    }
}
