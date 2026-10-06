namespace BepInEx
{
    internal static class Paths { internal static string ConfigPath = "unused-test-config"; }
}
namespace PortalRules
{
    internal sealed class World { internal long m_uid = 1; }
    internal sealed class ZNet
    {
        internal static ZNet instance = new();
        internal static World World = new();
        internal bool IsServer() => true;
    }
    internal static class PortalRulesPlugin
    {
        internal sealed class TestLogger
        {
            internal void LogError(string text) { }
            internal void LogWarning(string text) { }
        }
        internal static readonly TestLogger PortalRulesLogger = new();
    }
    internal static class PublicPortalData
    {
        internal const int MaximumPortalCount = 100000;
        internal const long MaximumUtcSeconds = 253402300799L;
        internal static long Now = 1700000000;
        internal static long GetUtcNowSeconds() => Now;
        internal static bool TryNormalizeAccountId(string input, out string value)
        { value = input; return !string.IsNullOrWhiteSpace(input); }
        internal static bool TryNormalizeFavoriteId(string input, out string value)
        { value = input; return !string.IsNullOrWhiteSpace(input); }
    }
    internal enum PublicPortalAccessMode { Personal, Invite }
    internal sealed class PublicPortalCatalogEntry
    {
        internal PublicPortalAccessMode AccessMode;
        internal long InviteArrivalCooldownUntilUtc;
    }
    internal static class PublicPortalCatalog
    {
        internal static long GetEstimatedServerUtcNowSeconds() => PublicPortalData.Now;
    }
    internal static class PortalRulesLocalization
    {
        internal static string Translate(string token, params string[] args) => token;
    }
    internal sealed class Entry { internal float Value = 1; }
    internal static class PublicPortalConfig
    {
        internal static readonly Entry InviteDepartureCooldownHours = new();
        internal static readonly Entry InviteArrivalCooldownHours = new();
    }
    internal static class PortalRulesFileIO
    {
        internal static bool Fail;
        internal static string Persisted = "";
        internal static int Writes;
        internal static void WriteAtomicFile(string path, string content, string backup)
        {
            if (Fail) throw new IOException("Injected disk failure");
            Persisted = content;
            Writes++;
        }
    }
    public static class InviteCollapseChecks
    {
        private static readonly Type Store = typeof(InviteTravelCooldownStore);
        private static int _checks;
        private static void Check(bool success, string description)
        {
            _checks++;
            if (!success) throw new Exception(description);
        }
        private static FieldInfo Field(string name) => Store.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;
        private static void Set(string name, object value) => Field(name).SetValue(null, value);
        private static IDictionary Dictionary(string name) => (IDictionary)Field(name).GetValue(null)!;
        private static void Reset()
        {
            PortalRulesFileIO.Fail = false; PortalRulesFileIO.Persisted = ""; PortalRulesFileIO.Writes = 0;
            PublicPortalData.Now = 1700000000;
            PublicPortalConfig.InviteDepartureCooldownHours.Value = 1;
            PublicPortalConfig.InviteArrivalCooldownHours.Value = 1;
            Dictionary("PendingReservations").Clear(); Dictionary("PendingDirectionOwners").Clear();
            IDictionary worlds = Dictionary("Worlds"); worlds.Clear();
            worlds.Add("1", Activator.CreateInstance(worlds.GetType().GetGenericArguments()[1]));
            Set("_worldId", "1"); Set("_serverActive", true); Set("_filePath", "in-memory-test");
            Set("_saveDirty", false); Set("_saveFaulted", false);
            Set("_nextSaveAttemptUtc", DateTime.MinValue); Set("_serializedByteUpperBound", 0L);
        }
        private static bool Reserve(string token, bool source, string sourceId, bool dest, string destId,
            out InviteTravelCooldownReservation reservation, out InviteTravelCooldownFailure failure) =>
            InviteTravelCooldownStore.TryReserve(token, "account", source, sourceId, dest, destId,
                out reservation, out failure, out _, out _);
        private static InviteTravelCooldownReservation ReserveBoth()
        {
            Check(Reserve("ticket", true, "source", true, "dest", out var r, out _), "Initial reservation accepted");
            return r;
        }
        private static bool Commit(InviteTravelCooldownReservation r) => InviteTravelCooldownStore.TryCommitReservation(r, out _);
        private static (long Departure, long Arrival) Deadlines(string portal)
        {
            InviteTravelCooldownStore.GetServerDeadlines("account", portal, out long d, out long a);
            return (d, a);
        }
        private static void RemovedEndChecks()
        {
            Reset(); var r = ReserveBoth();
            InviteTravelCooldownStore.RemovePortal("dest");
            Check(!Reserve("second", true, "source", false, "", out _, out var failure) &&
                  failure == InviteTravelCooldownFailure.ReservationConflict, "Surviving source remains locked until commit");
            Check(Commit(r), "Destroyed destination does not cancel in-flight commit");
            Check(Deadlines("source").Departure == PublicPortalData.Now + 3600 && Deadlines("dest") == (0L, 0L),
                "Record surviving source only");
            Check(!PortalRulesFileIO.Persisted.Contains("\"dest\":"), "Deleted endpoint never reappears on disk");
            Check(Dictionary("PendingReservations").Count == 0 && Dictionary("PendingDirectionOwners").Count == 0,
                "Commit clears both pending direction locks");

            Reset(); r = ReserveBoth(); InviteTravelCooldownStore.RemovePortal("source");
            Check(!Reserve("second", false, "", true, "dest", out _, out failure) &&
                  failure == InviteTravelCooldownFailure.ReservationConflict, "Surviving destination remains locked");
            Check(Commit(r) && Deadlines("dest").Arrival == PublicPortalData.Now + 3600 && Deadlines("source") == (0L, 0L),
                "Destroyed source still records surviving arrival");

            Reset(); r = ReserveBoth();
            InviteTravelCooldownStore.RemovePortal("source"); InviteTravelCooldownStore.RemovePortal("dest");
            Check(Commit(r), "Both endpoints gone commits without new cooldown rows");
            Check(((IDictionary)Dictionary("Worlds")["1"]!).Count == 0 && PortalRulesFileIO.Writes == 0,
                "No empty account or unnecessary save for absent history");
            Check(Dictionary("PendingDirectionOwners").Count == 0, "Both-deleted commit releases locks");

            Reset(); Check(Reserve("single", false, "", true, "dest", out r, out _), "Single Invite endpoint reserve");
            InviteTravelCooldownStore.RemovePortal("dest");
            Check(Commit(r) && Dictionary("PendingReservations").Count == 0, "Deleted only Invite endpoint commits");
            Reset(); Check(Reserve("same", true, "same", true, "same", out r, out _), "Same endpoint reserve");
            InviteTravelCooldownStore.RemovePortal("same");
            Check(Commit(r) && Dictionary("PendingDirectionOwners").Count == 0, "Same endpoint removes both directions");
        }
        private static void RetryChecks()
        {
            Reset(); var r = ReserveBoth(); InviteTravelCooldownStore.RemovePortal("dest");
            InviteTravelCooldownStore.RemovePortal("dest"); // repeat network/catalog notification
            PortalRulesFileIO.Fail = true;
            Check(!Commit(r) && Dictionary("PendingReservations").Count == 1 && Dictionary("PendingDirectionOwners").Count == 2,
                "Write failure retains reservation and direction locks");
            Check(Deadlines("source") == (0L, 0L) && Deadlines("dest") == (0L, 0L), "Failed commit rolls back only attempted writes");
            PortalRulesFileIO.Fail = false; Set("_nextSaveAttemptUtc", DateTime.MinValue);
            Check(Commit(r) && Deadlines("source").Departure > 0 && Deadlines("dest") == (0L, 0L),
                "Retry keeps removal flags and writes surviving endpoint only");

            Reset();
            // Existing arrival/departure history is deleted before a new opposite-direction commit.
            Check(Reserve("seed", true, "dest", true, "source", out var seed, out _) && Commit(seed), "Seed opposite directions");
            r = ReserveBoth(); InviteTravelCooldownStore.RemovePortal("source"); InviteTravelCooldownStore.RemovePortal("dest");
            PortalRulesFileIO.Fail = true;
            Check(!Commit(r) && Dictionary("PendingReservations").Count == 1, "Both-deleted commit waits for durable history deletion");
            PortalRulesFileIO.Fail = false; Set("_nextSaveAttemptUtc", DateTime.MinValue);
            Check(Commit(r) && !PortalRulesFileIO.Persisted.Contains("\"source\":") && !PortalRulesFileIO.Persisted.Contains("\"dest\":"),
                "Retry durably deletes history without resurrecting endpoints");
        }
        private static void CleanupChecks()
        {
            Reset(); var r = ReserveBoth(); InviteTravelCooldownStore.RemovePortal("dest");
            InviteTravelCooldownStore.CancelReservation(r.ReservationId);
            Check(Dictionary("PendingReservations").Count == 0 && Dictionary("PendingDirectionOwners").Count == 0,
                "Cancellation after destruction releases all locks");
            Reset(); r = ReserveBoth(); InviteTravelCooldownStore.RemovePortal("dest"); PublicPortalData.Now += 181;
            InviteTravelCooldownStore.Tick();
            Check(Dictionary("PendingReservations").Count == 0 && Dictionary("PendingDirectionOwners").Count == 0,
                "Expired reservation after destruction releases locks");
            Check(!Commit(r), "Expired ticket cannot be revived by destruction flags");
            Reset(); r = ReserveBoth(); InviteTravelCooldownStore.RemovePortal("source"); InviteTravelCooldownStore.EndServerSession();
            Check(Dictionary("PendingReservations").Count == 0 && Dictionary("PendingDirectionOwners").Count == 0,
                "Session shutdown releases destruction-marked reservations");
            Reset(); r = ReserveBoth(); InviteTravelCooldownStore.RemovePortal("dest");
            var forged = new InviteTravelCooldownReservation(r.ReservationId, "other-account", "source", "dest", true, true);
            Check(!Commit(forged) && Deadlines("source") == (0L, 0L), "Destruction does not relax ticket/account matching");
        }
        public static void Run()
        {
            RemovedEndChecks(); RetryChecks(); CleanupChecks();
            Console.WriteLine($"{_checks} Invite collapse checks passed (complete production store; in-memory atomic writer, no game/network execution).");
        }
    }
}
