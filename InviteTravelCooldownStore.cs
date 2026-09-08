using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using YamlDotNet.Serialization;

namespace PortalRules;

internal enum InviteTravelCooldownFailure : byte
{
    None = 0,
    DataUnavailable = 1,
    StorageUnavailable = 2,
    Active = 3,
    AccountCapacityReached = 4,
    PortalCapacityReached = 5,
    FileCapacityReached = 6,
    SaveFailed = 7,
    ReservationConflict = 8
}

internal readonly struct InviteTravelCooldownReservation
{
    internal readonly string ReservationId;
    internal readonly string AccountId;
    internal readonly string SourceFavoriteId;
    internal readonly string DestinationFavoriteId;
    internal readonly bool RecordsDeparture;
    internal readonly bool RecordsArrival;

    internal InviteTravelCooldownReservation(
        string reservationId,
        string accountId,
        string sourceFavoriteId,
        string destinationFavoriteId,
        bool recordsDeparture,
        bool recordsArrival)
    {
        ReservationId = reservationId;
        AccountId = accountId;
        SourceFavoriteId = sourceFavoriteId;
        DestinationFavoriteId = destinationFavoriteId;
        RecordsDeparture = recordsDeparture;
        RecordsArrival = recordsArrival;
    }

    internal bool IsActive => RecordsDeparture || RecordsArrival;
}

/// <summary>
/// Server-only, per-world cooldown history keyed by authenticated SteamID64
/// and the stable FavoriteId of the Invite portal that was used.
/// </summary>
internal static class InviteTravelCooldownStore
{
    private sealed class PendingReservation
    {
        internal readonly InviteTravelCooldownReservation Value;
        internal readonly long ExpiresAtUtc;
        internal readonly long SerializedByteEstimate;

        internal PendingReservation(
            InviteTravelCooldownReservation value,
            long expiresAtUtc,
            long serializedByteEstimate)
        {
            Value = value;
            ExpiresAtUtc = expiresAtUtc;
            SerializedByteEstimate = serializedByteEstimate;
        }
    }

    private sealed class CooldownFile
    {
        [YamlMember(Alias = "format_version")]
        public int FormatVersion { get; set; }

        [YamlMember(Alias = "worlds")]
        public Dictionary<string,
            Dictionary<string,
                Dictionary<string, CooldownEntryYaml>>>? Worlds { get; set; }
    }

    private sealed class CooldownEntryYaml
    {
        [YamlMember(Alias = "departure_last_utc")]
        public long DepartureLastUtc { get; set; }

        [YamlMember(Alias = "arrival_last_utc")]
        public long ArrivalLastUtc { get; set; }
    }

    private readonly struct CooldownEntry
    {
        public readonly long DepartureLastUtc;
        public readonly long ArrivalLastUtc;

        public CooldownEntry(long departureLastUtc, long arrivalLastUtc)
        {
            DepartureLastUtc = Math.Max(0L, departureLastUtc);
            ArrivalLastUtc = Math.Max(0L, arrivalLastUtc);
        }

        public CooldownEntry WithDeparture(long value)
        {
            return new CooldownEntry(value, ArrivalLastUtc);
        }

        public CooldownEntry WithArrival(long value)
        {
            return new CooldownEntry(DepartureLastUtc, value);
        }
    }

    private const int FormatVersion = 1;
    private const int MaximumWorldCount = 1024;
    private const int MaximumAccountsPerWorld = 32768;
    private const int MaximumPortalsPerAccount = PublicPortalData.MaximumPortalCount;
    private const long MaximumFileBytes = 16L * 1024L * 1024L;
    private const int MaximumSerializedPortalMutationBytes = 256;
    private const int MaximumSerializedAccountHeaderBytes = 64;
    private const int MaximumPendingReservations = 4096;
    private const int MaximumReservationIdLength = 128;
    private const long PendingReservationLifetimeSeconds = 180L;
    private const string DirectoryName = "PortalRules";
    private const string FileName = "invite-travel-cooldowns.yml";
    private static readonly TimeSpan SaveDebounce = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SaveRetryDelay = TimeSpan.FromSeconds(10);

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithDuplicateKeyChecking()
        .Build();
    private static readonly Dictionary<string,
        Dictionary<string,
            Dictionary<string, CooldownEntry>>> Worlds =
        new(StringComparer.Ordinal);
    private static readonly Dictionary<string, PendingReservation>
        PendingReservations = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> PendingDirectionOwners =
        new(StringComparer.Ordinal);

    private static string _filePath = "";
    private static string _worldId = "";
    private static bool _serverActive;
    private static bool _saveDirty;
    private static bool _saveFaulted;
    private static DateTime _nextSaveAttemptUtc;
    private static long _serializedByteUpperBound;

    internal static void BeginServerSession()
    {
        ClearPendingReservations();
        if (!EndServerSession())
        {
            PortalRulesPlugin.PortalRulesLogger.LogError(
                "Invite cooldown storage retained unsaved data from the previous session; " +
                "the new session will remain unavailable until that data can be saved.");
            return;
        }

        if (ZNet.instance == null || !ZNet.instance.IsServer() || ZNet.World == null)
        {
            return;
        }

        long worldUid = ZNet.World.m_uid;
        if (worldUid == 0L)
        {
            PortalRulesPlugin.PortalRulesLogger.LogError(
                "Invite cooldown storage is unavailable because the world UID is invalid.");
            return;
        }

        _worldId = worldUid.ToString(CultureInfo.InvariantCulture);
        string directory = Path.Combine(Paths.ConfigPath, DirectoryName);
        _filePath = Path.Combine(directory, FileName);
        try
        {
            Directory.CreateDirectory(directory);
            Load();
            bool saveRequired = Prune(PublicPortalData.GetUtcNowSeconds());
            if (!Worlds.ContainsKey(_worldId))
            {
                if (Worlds.Count >= MaximumWorldCount)
                {
                    throw new InvalidDataException(
                        $"{FileName} cannot track more than {MaximumWorldCount} worlds");
                }

                Worlds.Add(
                    _worldId,
                    new Dictionary<string, Dictionary<string, CooldownEntry>>(
                        StringComparer.Ordinal));
                saveRequired = true;
            }

            if (saveRequired)
            {
                Save();
            }

            _saveDirty = false;
            _saveFaulted = false;
            _nextSaveAttemptUtc = DateTime.UtcNow + SaveDebounce;
            _serverActive = true;
        }
        catch (Exception ex)
        {
            Worlds.Clear();
            _worldId = "";
            _filePath = "";
            _serverActive = false;
            _saveDirty = false;
            _saveFaulted = false;
            _nextSaveAttemptUtc = DateTime.MinValue;
            _serializedByteUpperBound = 0L;
            PortalRulesPlugin.PortalRulesLogger.LogError(
                $"Failed to initialize Invite cooldown storage: {ex.Message}");
        }
    }

    internal static bool EndServerSession()
    {
        ClearPendingReservations();
        bool flushed = Flush();
        _serverActive = false;
        if (!flushed && _saveDirty)
        {
            PortalRulesPlugin.PortalRulesLogger.LogError(
                "Invite cooldown storage could not complete its final save; " +
                "unsaved data is being retained in memory.");
            return false;
        }

        _saveDirty = false;
        _saveFaulted = false;
        _nextSaveAttemptUtc = DateTime.MinValue;
        _serializedByteUpperBound = 0L;
        _worldId = "";
        _filePath = "";
        Worlds.Clear();
        return true;
    }

    internal static void Tick()
    {
        PrunePendingReservations(PublicPortalData.GetUtcNowSeconds());
        if (!_saveDirty ||
            DateTime.UtcNow < _nextSaveAttemptUtc)
        {
            return;
        }

        bool restartCurrentServerSession =
            !_serverActive && !string.IsNullOrEmpty(_filePath);
        if (!TryFlushPending(out _) ||
            !restartCurrentServerSession ||
            ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            ZNet.World == null)
        {
            return;
        }

        BeginServerSession();
    }

    internal static bool Flush()
    {
        if (!_saveDirty)
        {
            return true;
        }

        if (string.IsNullOrEmpty(_filePath))
        {
            return false;
        }

        return TryFlushPending(out _);
    }

    internal static void GetServerDeadlines(
        string steamId,
        string favoriteId,
        out long departureUntilUtc,
        out long arrivalUntilUtc)
    {
        departureUntilUtc = 0L;
        arrivalUntilUtc = 0L;
        if (!_serverActive ||
            !TryNormalizeKeys(steamId, favoriteId, out string accountId, out string portalId) ||
            !TryGetEntry(accountId, portalId, out CooldownEntry entry))
        {
            return;
        }

        departureUntilUtc = CalculateDeadline(
            entry.DepartureLastUtc,
            PublicPortalConfig.InviteDepartureCooldownHours.Value);
        arrivalUntilUtc = CalculateDeadline(
            entry.ArrivalLastUtc,
            PublicPortalConfig.InviteArrivalCooldownHours.Value);
    }

    internal static void RemovePortal(string favoriteId)
    {
        if (!_serverActive ||
            !PublicPortalData.TryNormalizeFavoriteId(
                favoriteId,
                out string portalId) ||
            !TryGetCurrentWorldAccounts(
                out Dictionary<string, Dictionary<string, CooldownEntry>> accounts))
        {
            return;
        }

        foreach (PendingReservation pending in PendingReservations.Values
                     .Where(candidate =>
                         candidate.Value.RecordsDeparture &&
                         string.Equals(
                             candidate.Value.SourceFavoriteId,
                             portalId,
                             StringComparison.Ordinal) ||
                         candidate.Value.RecordsArrival &&
                         string.Equals(
                             candidate.Value.DestinationFavoriteId,
                             portalId,
                             StringComparison.Ordinal))
                     .ToArray())
        {
            RemovePendingReservation(pending);
        }

        bool changed = false;
        foreach (Dictionary<string, CooldownEntry> portals in accounts.Values)
        {
            changed |= portals.Remove(portalId);
        }

        if (!changed)
        {
            return;
        }

        foreach (string accountId in accounts
                     .Where(pair => pair.Value.Count == 0)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            accounts.Remove(accountId);
        }

        _saveDirty = true;
    }

    internal static bool TryReserve(
        string reservationId,
        string steamId,
        bool usesInviteAsSource,
        string sourceFavoriteId,
        bool usesInviteAsDestination,
        string destinationFavoriteId,
        out InviteTravelCooldownReservation reservation,
        out InviteTravelCooldownFailure failure,
        out long departureRemaining,
        out long arrivalRemaining)
    {
        reservation = default;
        failure = InviteTravelCooldownFailure.None;
        departureRemaining = 0L;
        arrivalRemaining = 0L;
        double departureHours = PublicPortalConfig.InviteDepartureCooldownHours.Value;
        double arrivalHours = PublicPortalConfig.InviteArrivalCooldownHours.Value;
        bool enforceDeparture = usesInviteAsSource && departureHours > 0d;
        bool enforceArrival = usesInviteAsDestination && arrivalHours > 0d;
        if (!enforceDeparture && !enforceArrival)
        {
            return true;
        }

        string canonicalSourceFavoriteId = "";
        string canonicalDestinationFavoriteId = "";
        if (string.IsNullOrWhiteSpace(reservationId) ||
            reservationId.Length > MaximumReservationIdLength ||
            !_serverActive ||
            !PublicPortalData.TryNormalizeSteamId64(steamId, out string accountId) ||
            (enforceDeparture && !PublicPortalData.TryNormalizeFavoriteId(
                 sourceFavoriteId,
                 out canonicalSourceFavoriteId)) ||
            (enforceArrival && !PublicPortalData.TryNormalizeFavoriteId(
                 destinationFavoriteId,
                 out canonicalDestinationFavoriteId)))
        {
            failure = InviteTravelCooldownFailure.DataUnavailable;
            return false;
        }

        if (_saveFaulted &&
            (DateTime.UtcNow < _nextSaveAttemptUtc ||
             !TryFlushPending(out _)))
        {
            failure = InviteTravelCooldownFailure.StorageUnavailable;
            return false;
        }

        long nowUtc = PublicPortalData.GetUtcNowSeconds();
        PrunePendingReservations(nowUtc);
        if (Prune(nowUtc))
        {
            _saveDirty = true;
            if (!TryFlushPending(out _))
            {
                failure = InviteTravelCooldownFailure.StorageUnavailable;
                return false;
            }
        }

        CooldownEntry sourcePrevious = default;
        CooldownEntry destinationPrevious = default;
        if (enforceDeparture)
        {
            TryGetEntry(accountId, canonicalSourceFavoriteId, out sourcePrevious);
        }

        if (enforceArrival)
        {
            TryGetEntry(accountId, canonicalDestinationFavoriteId, out destinationPrevious);
        }
        departureRemaining = enforceDeparture
            ? GetRemainingSeconds(
                CalculateDeadline(sourcePrevious.DepartureLastUtc, departureHours),
                nowUtc)
            : 0L;
        arrivalRemaining = enforceArrival
            ? GetRemainingSeconds(
                CalculateDeadline(destinationPrevious.ArrivalLastUtc, arrivalHours),
                nowUtc)
            : 0L;
        if (departureRemaining > 0L || arrivalRemaining > 0L)
        {
            failure = InviteTravelCooldownFailure.Active;
            return false;
        }

        if (PendingReservations.Count >= MaximumPendingReservations)
        {
            failure = InviteTravelCooldownFailure.StorageUnavailable;
            return false;
        }

        string departureKey = enforceDeparture
            ? CreateDirectionKey(accountId, canonicalSourceFavoriteId, departure: true)
            : "";
        string arrivalKey = enforceArrival
            ? CreateDirectionKey(accountId, canonicalDestinationFavoriteId, departure: false)
            : "";
        if ((departureKey.Length > 0 &&
             PendingDirectionOwners.ContainsKey(departureKey)) ||
            (arrivalKey.Length > 0 &&
             PendingDirectionOwners.ContainsKey(arrivalKey)))
        {
            failure = InviteTravelCooldownFailure.ReservationConflict;
            return false;
        }

        if (!TryGetCurrentWorldAccounts(
                out Dictionary<string, Dictionary<string, CooldownEntry>> accounts))
        {
            failure = InviteTravelCooldownFailure.DataUnavailable;
            return false;
        }

        bool accountExisted = accounts.TryGetValue(
            accountId,
            out Dictionary<string, CooldownEntry> portalEntries);
        if (!accountExisted)
        {
            HashSet<string> pendingNewAccounts = new(StringComparer.Ordinal);
            foreach (PendingReservation pending in PendingReservations.Values)
            {
                if (!accounts.ContainsKey(pending.Value.AccountId))
                {
                    pendingNewAccounts.Add(pending.Value.AccountId);
                }
            }

            if (!pendingNewAccounts.Contains(accountId) &&
                accounts.Count > MaximumAccountsPerWorld -
                pendingNewAccounts.Count - 1)
            {
                failure = InviteTravelCooldownFailure.AccountCapacityReached;
                return false;
            }

            portalEntries = new Dictionary<string, CooldownEntry>(StringComparer.Ordinal);
        }

        HashSet<string> reservedPortalIds = new(StringComparer.Ordinal);
        foreach (PendingReservation pending in PendingReservations.Values)
        {
            InviteTravelCooldownReservation value = pending.Value;
            if (!string.Equals(value.AccountId, accountId, StringComparison.Ordinal))
            {
                continue;
            }

            if (value.RecordsDeparture)
            {
                reservedPortalIds.Add(value.SourceFavoriteId);
            }

            if (value.RecordsArrival)
            {
                reservedPortalIds.Add(value.DestinationFavoriteId);
            }
        }

        HashSet<string> newPortalIds = new(StringComparer.Ordinal);
        HashSet<string> affectedPortalIds = new(StringComparer.Ordinal);
        if (enforceDeparture &&
            !portalEntries.ContainsKey(canonicalSourceFavoriteId) &&
            !reservedPortalIds.Contains(canonicalSourceFavoriteId))
        {
            newPortalIds.Add(canonicalSourceFavoriteId);
        }

        if (enforceDeparture)
        {
            affectedPortalIds.Add(canonicalSourceFavoriteId);
        }

        if (enforceArrival &&
            !portalEntries.ContainsKey(canonicalDestinationFavoriteId) &&
            !reservedPortalIds.Contains(canonicalDestinationFavoriteId))
        {
            newPortalIds.Add(canonicalDestinationFavoriteId);
        }

        if (enforceArrival)
        {
            affectedPortalIds.Add(canonicalDestinationFavoriteId);
        }

        int reservedNewPortalCount = reservedPortalIds.Count(
            favoriteId => !portalEntries.ContainsKey(favoriteId));
        if (portalEntries.Count > MaximumPortalsPerAccount -
            reservedNewPortalCount - newPortalIds.Count)
        {
            failure = InviteTravelCooldownFailure.PortalCapacityReached;
            return false;
        }

        long estimateIncrease =
            (long)affectedPortalIds.Count * MaximumSerializedPortalMutationBytes +
            (accountExisted ? 0L : MaximumSerializedAccountHeaderBytes);
        long pendingSerializedBytes = PendingReservations.Values.Sum(
            pending => pending.SerializedByteEstimate);
        if (_serializedByteUpperBound >
            MaximumFileBytes - pendingSerializedBytes - estimateIncrease)
        {
            if (!_saveDirty || !TryFlushPending(out _))
            {
                failure = InviteTravelCooldownFailure.FileCapacityReached;
                return false;
            }

            if (_serializedByteUpperBound >
                MaximumFileBytes - pendingSerializedBytes - estimateIncrease)
            {
                failure = InviteTravelCooldownFailure.FileCapacityReached;
                return false;
            }
        }

        reservation = new InviteTravelCooldownReservation(
            reservationId,
            accountId,
            canonicalSourceFavoriteId,
            canonicalDestinationFavoriteId,
            enforceDeparture,
            enforceArrival);
        PendingReservations.Add(
            reservationId,
            new PendingReservation(
                reservation,
                Math.Min(
                    PublicPortalData.MaximumUtcSeconds,
                    nowUtc + PendingReservationLifetimeSeconds),
                estimateIncrease));
        if (departureKey.Length > 0)
        {
            PendingDirectionOwners.Add(departureKey, reservationId);
        }

        if (arrivalKey.Length > 0)
        {
            PendingDirectionOwners.Add(arrivalKey, reservationId);
        }

        return true;
    }

    internal static bool TryCommitReservation(
        InviteTravelCooldownReservation reservation,
        out InviteTravelCooldownFailure failure)
    {
        failure = InviteTravelCooldownFailure.None;
        if (!reservation.IsActive)
        {
            return true;
        }

        long nowUtc = PublicPortalData.GetUtcNowSeconds();
        if (!PendingReservations.TryGetValue(
                reservation.ReservationId,
                out PendingReservation pending) ||
            pending.ExpiresAtUtc < nowUtc ||
            !ReservationMatches(reservation, pending.Value))
        {
            CancelReservation(reservation.ReservationId);
            failure = InviteTravelCooldownFailure.DataUnavailable;
            return false;
        }

        if (!_serverActive)
        {
            failure = InviteTravelCooldownFailure.DataUnavailable;
            return false;
        }

        if (_saveFaulted &&
            (DateTime.UtcNow < _nextSaveAttemptUtc ||
             !TryFlushPending(out _)))
        {
            failure = InviteTravelCooldownFailure.StorageUnavailable;
            return false;
        }

        if (!TryGetCurrentWorldAccounts(
                out Dictionary<string, Dictionary<string, CooldownEntry>> accounts))
        {
            failure = InviteTravelCooldownFailure.DataUnavailable;
            return false;
        }

        bool accountExisted = accounts.TryGetValue(
            reservation.AccountId,
            out Dictionary<string, CooldownEntry> portalEntries);
        if (!accountExisted)
        {
            if (accounts.Count >= MaximumAccountsPerWorld)
            {
                failure = InviteTravelCooldownFailure.AccountCapacityReached;
                return false;
            }

            portalEntries = new Dictionary<string, CooldownEntry>(StringComparer.Ordinal);
        }

        CooldownEntry sourcePrevious = default;
        CooldownEntry destinationPrevious = default;
        bool sourceExisted = reservation.RecordsDeparture &&
                             portalEntries.TryGetValue(
                                 reservation.SourceFavoriteId,
                                 out sourcePrevious);
        bool destinationExisted = reservation.RecordsArrival &&
                                  portalEntries.TryGetValue(
                                      reservation.DestinationFavoriteId,
                                      out destinationPrevious);
        int newPortalCount = 0;
        if (reservation.RecordsDeparture && !sourceExisted)
        {
            newPortalCount++;
        }

        if (reservation.RecordsArrival &&
            !destinationExisted &&
            (!reservation.RecordsDeparture ||
             !string.Equals(
                 reservation.SourceFavoriteId,
                 reservation.DestinationFavoriteId,
                 StringComparison.Ordinal)))
        {
            newPortalCount++;
        }

        if (portalEntries.Count > MaximumPortalsPerAccount - newPortalCount)
        {
            failure = InviteTravelCooldownFailure.PortalCapacityReached;
            return false;
        }

        if (_serializedByteUpperBound >
            MaximumFileBytes - pending.SerializedByteEstimate)
        {
            failure = InviteTravelCooldownFailure.FileCapacityReached;
            return false;
        }

        if (!accountExisted)
        {
            accounts.Add(reservation.AccountId, portalEntries);
        }

        bool wasSaveDirty = _saveDirty;
        long previousSerializedByteUpperBound = _serializedByteUpperBound;
        if (reservation.RecordsDeparture)
        {
            portalEntries[reservation.SourceFavoriteId] =
                sourcePrevious.WithDeparture(nowUtc);
        }

        if (reservation.RecordsArrival)
        {
            CooldownEntry currentDestination =
                reservation.RecordsDeparture &&
                string.Equals(
                    reservation.SourceFavoriteId,
                    reservation.DestinationFavoriteId,
                    StringComparison.Ordinal) &&
                portalEntries.TryGetValue(
                    reservation.DestinationFavoriteId,
                    out CooldownEntry combined)
                    ? combined
                    : destinationPrevious;
            portalEntries[reservation.DestinationFavoriteId] =
                currentDestination.WithArrival(nowUtc);
        }

        _serializedByteUpperBound += pending.SerializedByteEstimate;
        _saveDirty = true;
        // A travel CommitAck is only sent after this mutation reaches disk.
        // Invite travel is intentionally rarer than ordinary portal use, so a
        // forced flush here is preferable to acknowledging a debounce-window
        // mutation that could still fail later.
        if (TryFlushPending(out _))
        {
            RemovePendingReservation(pending);
            return true;
        }

        RestoreEntry(
            portalEntries,
            reservation.SourceFavoriteId,
            sourceExisted,
            sourcePrevious);
        RestoreEntry(
            portalEntries,
            reservation.DestinationFavoriteId,
            destinationExisted,
            destinationPrevious);
        if (portalEntries.Count == 0)
        {
            accounts.Remove(reservation.AccountId);
        }

        _saveDirty = wasSaveDirty || _saveFaulted;
        _serializedByteUpperBound = previousSerializedByteUpperBound;
        failure = InviteTravelCooldownFailure.SaveFailed;
        return false;
    }

    internal static void CancelReservation(string reservationId)
    {
        if (string.IsNullOrEmpty(reservationId) ||
            !PendingReservations.TryGetValue(
                reservationId,
                out PendingReservation pending))
        {
            return;
        }

        RemovePendingReservation(pending);
    }

    internal static string FormatRemaining(long seconds)
    {
        seconds = Math.Max(1L, seconds);
        long roundedMinutes = Math.Max(1L, (seconds + 59L) / 60L);
        if (roundedMinutes < 60L)
        {
            return PortalRulesLocalization.Translate(
                "$sighsorry_portalrules_duration_minutes",
                roundedMinutes.ToString(CultureInfo.InvariantCulture));
        }

        long hours = roundedMinutes / 60L;
        long minutes = roundedMinutes % 60L;
        return minutes == 0L
            ? PortalRulesLocalization.Translate(
                "$sighsorry_portalrules_duration_hours",
                hours.ToString(CultureInfo.InvariantCulture))
            : PortalRulesLocalization.Translate(
                "$sighsorry_portalrules_duration_hours_minutes",
                hours.ToString(CultureInfo.InvariantCulture),
                minutes.ToString(CultureInfo.InvariantCulture));
    }

    internal static long GetRemainingSeconds(long deadlineUtc)
    {
        return GetRemainingSeconds(
            deadlineUtc,
            PublicPortalCatalog.GetEstimatedServerUtcNowSeconds());
    }

    internal static bool TryGetInviteArrivalCooldownRemaining(
        PublicPortalCatalogEntry portal,
        out long remainingSeconds)
    {
        remainingSeconds = 0L;
        if (portal.AccessMode != PublicPortalAccessMode.Invite ||
            portal.InviteArrivalCooldownUntilUtc <= 0L)
        {
            return false;
        }

        remainingSeconds = GetRemainingSeconds(
            portal.InviteArrivalCooldownUntilUtc);
        return remainingSeconds > 0L;
    }

    private static bool TryNormalizeKeys(
        string steamId,
        string favoriteId,
        out string accountId,
        out string portalId)
    {
        accountId = "";
        portalId = "";
        return PublicPortalData.TryNormalizeSteamId64(steamId, out accountId) &&
               PublicPortalData.TryNormalizeFavoriteId(favoriteId, out portalId);
    }

    private static string CreateDirectionKey(
        string accountId,
        string favoriteId,
        bool departure)
    {
        return accountId + (departure ? ":D:" : ":A:") + favoriteId;
    }

    private static bool ReservationMatches(
        InviteTravelCooldownReservation first,
        InviteTravelCooldownReservation second)
    {
        return string.Equals(
                   first.ReservationId,
                   second.ReservationId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   first.AccountId,
                   second.AccountId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   first.SourceFavoriteId,
                   second.SourceFavoriteId,
                   StringComparison.Ordinal) &&
               string.Equals(
                   first.DestinationFavoriteId,
                   second.DestinationFavoriteId,
                   StringComparison.Ordinal) &&
               first.RecordsDeparture == second.RecordsDeparture &&
               first.RecordsArrival == second.RecordsArrival;
    }

    private static void PrunePendingReservations(long nowUtc)
    {
        if (PendingReservations.Count == 0)
        {
            return;
        }

        List<PendingReservation>? expired = null;
        foreach (PendingReservation pending in PendingReservations.Values)
        {
            if (pending.ExpiresAtUtc < nowUtc)
            {
                (expired ??= new List<PendingReservation>()).Add(pending);
            }
        }

        if (expired == null)
        {
            return;
        }

        foreach (PendingReservation pending in expired)
        {
            RemovePendingReservation(pending);
        }
    }

    private static void RemovePendingReservation(PendingReservation pending)
    {
        InviteTravelCooldownReservation value = pending.Value;
        PendingReservations.Remove(value.ReservationId);
        if (value.RecordsDeparture)
        {
            PendingDirectionOwners.Remove(
                CreateDirectionKey(
                    value.AccountId,
                    value.SourceFavoriteId,
                    departure: true));
        }

        if (value.RecordsArrival)
        {
            PendingDirectionOwners.Remove(
                CreateDirectionKey(
                    value.AccountId,
                    value.DestinationFavoriteId,
                    departure: false));
        }
    }

    private static void ClearPendingReservations()
    {
        PendingReservations.Clear();
        PendingDirectionOwners.Clear();
    }

    private static bool TryGetEntry(
        string accountId,
        string portalId,
        out CooldownEntry entry)
    {
        entry = default;
        return TryGetCurrentWorldAccounts(
                   out Dictionary<string, Dictionary<string, CooldownEntry>> accounts) &&
               accounts.TryGetValue(accountId, out Dictionary<string, CooldownEntry> portals) &&
               portals.TryGetValue(portalId, out entry);
    }

    private static bool TryGetCurrentWorldAccounts(
        out Dictionary<string, Dictionary<string, CooldownEntry>> accounts)
    {
        accounts = null!;
        return !string.IsNullOrEmpty(_worldId) &&
               Worlds.TryGetValue(_worldId, out accounts);
    }

    private static void RestoreEntry(
        Dictionary<string, CooldownEntry> entries,
        string portalId,
        bool existed,
        CooldownEntry previous)
    {
        if (string.IsNullOrEmpty(portalId))
        {
            return;
        }

        if (existed)
        {
            entries[portalId] = previous;
        }
        else
        {
            entries.Remove(portalId);
        }
    }

    private static long CalculateDeadline(long lastUtc, double hours)
    {
        if (lastUtc <= 0L || hours <= 0d || double.IsNaN(hours) || double.IsInfinity(hours))
        {
            return 0L;
        }

        long duration = (long)Math.Ceiling(Math.Min(hours, 8760d) * 3600d);
        return lastUtc > PublicPortalData.MaximumUtcSeconds - duration
            ? PublicPortalData.MaximumUtcSeconds
            : lastUtc + duration;
    }

    private static long GetRemainingSeconds(long deadlineUtc, long nowUtc)
    {
        return deadlineUtc > nowUtc ? deadlineUtc - nowUtc : 0L;
    }

    private static long NormalizeUtcSeconds(long value)
    {
        return value >= 0L && value <= PublicPortalData.MaximumUtcSeconds
            ? value
            : 0L;
    }

    private static void Load()
    {
        Worlds.Clear();
        string backupPath = _filePath + ".bak";
        if (!File.Exists(_filePath))
        {
            if (File.Exists(backupPath))
            {
                LoadFromPath(backupPath);
                Save();
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    $"Recovered {FileName} from its backup because the primary file was missing.");
            }

            return;
        }

        try
        {
            LoadFromPath(_filePath);
            return;
        }
        catch (Exception primaryException)
        {
            Worlds.Clear();
            if (!File.Exists(backupPath))
            {
                throw;
            }

            try
            {
                LoadFromPath(backupPath);
                string invalidPath = _filePath + ".invalid-" +
                                     DateTime.UtcNow.ToString(
                                         "yyyyMMddHHmmssfff",
                                         CultureInfo.InvariantCulture) +
                                     "-" + Guid.NewGuid().ToString("N");
                File.Move(_filePath, invalidPath);
                Save();
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    $"Recovered {FileName} from its backup. The invalid primary file was preserved as " +
                    $"'{Path.GetFileName(invalidPath)}'.");
                return;
            }
            catch (Exception recoveryException)
            {
                Worlds.Clear();
                throw new InvalidDataException(
                    $"Failed to load {FileName} and its backup. Primary error: " +
                    $"{primaryException.Message}. Recovery error: {recoveryException.Message}",
                    recoveryException);
            }
        }
    }

    private static void LoadFromPath(string path)
    {
        Worlds.Clear();
        _serializedByteUpperBound = 0L;
        FileInfo file = new(path);
        if (file.Length > MaximumFileBytes)
        {
            throw new InvalidDataException(
                $"'{Path.GetFileName(path)}' exceeds {MaximumFileBytes} bytes");
        }

        string contents = File.ReadAllText(path);
        CooldownFile? data = Deserializer.Deserialize<CooldownFile>(contents);
        if (data == null || data.FormatVersion != FormatVersion || data.Worlds == null)
        {
            throw new InvalidDataException(
                $"{FileName} must contain format_version {FormatVersion} and worlds");
        }

        if (data.Worlds.Count > MaximumWorldCount)
        {
            throw new InvalidDataException(
                $"{FileName} contains more than {MaximumWorldCount} worlds");
        }

        foreach (KeyValuePair<string,
                     Dictionary<string, Dictionary<string, CooldownEntryYaml>>> world in data.Worlds)
        {
            if (!long.TryParse(
                    world.Key,
                    NumberStyles.AllowLeadingSign,
                    CultureInfo.InvariantCulture,
                    out long worldUid) ||
                worldUid == 0L ||
                !string.Equals(
                    world.Key,
                    worldUid.ToString(CultureInfo.InvariantCulture),
                    StringComparison.Ordinal) ||
                world.Value == null ||
                world.Value.Count > MaximumAccountsPerWorld)
            {
                throw new InvalidDataException(
                    $"{FileName} contains an invalid world entry '{world.Key}'");
            }

            Dictionary<string, Dictionary<string, CooldownEntry>> accounts =
                new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Dictionary<string, CooldownEntryYaml>> account in
                     world.Value)
            {
                if (!PublicPortalData.TryNormalizeSteamId64(
                        account.Key,
                        out string normalizedAccountId) ||
                    !string.Equals(account.Key, normalizedAccountId, StringComparison.Ordinal) ||
                    account.Value == null ||
                    account.Value.Count > MaximumPortalsPerAccount)
                {
                    throw new InvalidDataException(
                        $"{FileName} contains an invalid account entry '{account.Key}'");
                }

                Dictionary<string, CooldownEntry> portals = new(StringComparer.Ordinal);
                foreach (KeyValuePair<string, CooldownEntryYaml> portal in account.Value)
                {
                    if (!PublicPortalData.TryNormalizeFavoriteId(
                            portal.Key,
                            out string normalizedPortalId) ||
                        !string.Equals(
                            portal.Key,
                            normalizedPortalId,
                            StringComparison.Ordinal) ||
                        portal.Value == null ||
                        NormalizeUtcSeconds(portal.Value.DepartureLastUtc) !=
                        portal.Value.DepartureLastUtc ||
                        NormalizeUtcSeconds(portal.Value.ArrivalLastUtc) !=
                        portal.Value.ArrivalLastUtc)
                    {
                        throw new InvalidDataException(
                            $"{FileName} contains an invalid portal entry '{portal.Key}'");
                    }

                    portals.Add(
                        normalizedPortalId,
                        new CooldownEntry(
                            portal.Value.DepartureLastUtc,
                            portal.Value.ArrivalLastUtc));
                }

                accounts.Add(normalizedAccountId, portals);
            }

            Worlds.Add(world.Key, accounts);
        }

        _serializedByteUpperBound = Encoding.UTF8.GetByteCount(contents);
    }

    private static bool Prune(long nowUtc)
    {
        bool changed = false;
        long oldestUsefulUtc = Math.Max(0L, nowUtc - 366L * 24L * 60L * 60L);
        foreach (Dictionary<string, Dictionary<string, CooldownEntry>> accounts in
                 Worlds.Values)
        {
            foreach (Dictionary<string, CooldownEntry> portals in accounts.Values)
            {
                foreach (string portalId in portals
                             .Where(pair =>
                                 pair.Value.DepartureLastUtc < oldestUsefulUtc &&
                                 pair.Value.ArrivalLastUtc < oldestUsefulUtc)
                             .Select(pair => pair.Key)
                             .ToArray())
                {
                    portals.Remove(portalId);
                    changed = true;
                }
            }

            foreach (string accountId in accounts
                         .Where(pair => pair.Value.Count == 0)
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                accounts.Remove(accountId);
                changed = true;
            }
        }

        foreach (string worldId in Worlds
                     .Where(pair =>
                         pair.Value.Count == 0 &&
                         !string.Equals(
                             pair.Key,
                             _worldId,
                             StringComparison.Ordinal))
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            Worlds.Remove(worldId);
            changed = true;
        }

        return changed;
    }

    private static void Save()
    {
        if (string.IsNullOrEmpty(_filePath))
        {
            throw new InvalidOperationException("Invite cooldown path is not initialized");
        }

        if (Worlds.Count > MaximumWorldCount)
        {
            throw new InvalidDataException(
                $"{FileName} cannot store more than {MaximumWorldCount} worlds");
        }

        foreach (KeyValuePair<string,
                     Dictionary<string, Dictionary<string, CooldownEntry>>> world in Worlds)
        {
            if (world.Value.Count > MaximumAccountsPerWorld)
            {
                throw new InvalidDataException(
                    $"{FileName} cannot store more than {MaximumAccountsPerWorld} accounts per world");
            }

            if (world.Value.Values.Any(portals =>
                    portals.Count > MaximumPortalsPerAccount))
            {
                throw new InvalidDataException(
                    $"{FileName} cannot store more than {MaximumPortalsPerAccount} portals per account");
            }
        }

        StringBuilder yaml = new();
        yaml.AppendLine("# AUTO-GENERATED by PortalRules. Do not edit while the server is running.");
        yaml.AppendLine("# Cooldowns are scoped by world, SteamID64, portal FavoriteId, and direction.");
        yaml.AppendLine("format_version: 1");
        if (Worlds.Count == 0)
        {
            yaml.AppendLine("worlds: {}");
        }
        else
        {
            yaml.AppendLine("worlds:");
            foreach (KeyValuePair<string,
                         Dictionary<string, Dictionary<string, CooldownEntry>>> world in
                     Worlds.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                yaml.Append("  \"").Append(world.Key).AppendLine("\":");
                if (world.Value.Count == 0)
                {
                    yaml.AppendLine("    {}");
                    continue;
                }

                foreach (KeyValuePair<string, Dictionary<string, CooldownEntry>> account in
                         world.Value.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    yaml.Append("    \"").Append(account.Key).AppendLine("\":");
                    if (account.Value.Count == 0)
                    {
                        yaml.AppendLine("      {}");
                        continue;
                    }

                    foreach (KeyValuePair<string, CooldownEntry> portal in
                             account.Value.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    {
                        yaml.Append("      \"").Append(portal.Key).AppendLine("\":");
                        yaml.Append("        departure_last_utc: ")
                            .AppendLine(portal.Value.DepartureLastUtc.ToString(CultureInfo.InvariantCulture));
                        yaml.Append("        arrival_last_utc: ")
                            .AppendLine(portal.Value.ArrivalLastUtc.ToString(CultureInfo.InvariantCulture));
                    }
                }
            }
        }

        string contents = yaml.ToString();
        int byteCount = Encoding.UTF8.GetByteCount(contents);
        if (byteCount > MaximumFileBytes)
        {
            throw new InvalidDataException(
                $"{FileName} would exceed {MaximumFileBytes} bytes");
        }

        PortalRulesFileIO.WriteAtomicFile(
            _filePath,
            contents,
            _filePath + ".bak");
        _serializedByteUpperBound = byteCount;
    }

    private static bool TryFlushPending(out string error)
    {
        error = "";
        if (!_saveDirty)
        {
            _saveFaulted = false;
            return true;
        }

        try
        {
            Save();
            _saveDirty = false;
            _saveFaulted = false;
            _nextSaveAttemptUtc = DateTime.UtcNow + SaveDebounce;
            return true;
        }
        catch (Exception ex)
        {
            _saveDirty = true;
            _saveFaulted = true;
            _nextSaveAttemptUtc = DateTime.UtcNow + SaveRetryDelay;
            error = ex.Message;
            PortalRulesPlugin.PortalRulesLogger.LogError(
                $"Failed to save Invite cooldown data: {ex.Message}");
            return false;
        }
    }
}
