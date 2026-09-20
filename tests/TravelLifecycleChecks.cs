#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Linq;

// Actual session/ticket cleanup methods; the reservation store is a controlled boundary.
public static class PortalRulesTravelLifecycleChecks
{
    internal sealed class ZNet { }
    private sealed class ZRpc { }
    private sealed class ServerTravelTicket
    {
        public string Ticket = "";
        public bool Committed;
    }
    private static class InviteTravelCooldownStore
    {
        internal static readonly List<string> Canceled = new();
        internal static bool FailCancellation;
        internal static void CancelReservation(string ticket)
        {
            if (FailCancellation) throw new InvalidOperationException("Injected cancellation failure");
            Canceled.Add(ticket);
        }
    }
    private static readonly Dictionary<ZRpc, float> LastRequestAt = new();
    private static readonly Dictionary<ZRpc, long> LastAcceptedRequestId = new();
    private static readonly Dictionary<ZRpc, float> LastControlRequestAt = new();
    private static readonly Dictionary<string, ServerTravelTicket> ServerTickets = new();
    private static ZNet? _sessionZNet;
    private static object? _pendingRequest;
    private static object? _pendingCommit;
    private static int _checks;

    /* PRODUCTION_METHODS */

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _checks++;
    }

    private static void SeedOutstandingTravel()
    {
        var peer = new ZRpc();
        LastRequestAt.Add(peer, 1f);
        LastAcceptedRequestId.Add(peer, 7L);
        LastControlRequestAt.Add(peer, 2f);
        ServerTickets.Add("pending", new ServerTravelTicket { Ticket = "pending" });
        ServerTickets.Add("committed", new ServerTravelTicket { Ticket = "committed", Committed = true });
        _pendingRequest = new object();
        _pendingCommit = new object();
        InviteTravelCooldownStore.Canceled.Clear();
    }

    private static void CheckClean()
    {
        Check(LastRequestAt.Count == 0 && LastAcceptedRequestId.Count == 0 &&
              LastControlRequestAt.Count == 0, "New sessions must not inherit peer request history");
        Check(ServerTickets.Count == 0 && _pendingRequest == null && _pendingCommit == null,
            "Old travel requests and tickets must not survive teardown");
        Check(InviteTravelCooldownStore.Canceled.SequenceEqual(new[] { "pending" }),
            "Cancel only uncommitted reservations, exactly once");
    }

    public static string Run()
    {
        var first = new ZNet();
        BeginNetworkSession(first);
        SeedOutstandingTravel();
        object request = _pendingRequest!;
        object commit = _pendingCommit!;
        foreach (ZNet? ignored in new ZNet?[] { null, first })
        {
            BeginNetworkSession(ignored);
            Check(ReferenceEquals(_sessionZNet, first) && ReferenceEquals(_pendingRequest, request) &&
                  ReferenceEquals(_pendingCommit, commit), "Null/repeated initialization preserves active travel");
            Check(ServerTickets.Count == 2 && InviteTravelCooldownStore.Canceled.Count == 0 &&
                  LastRequestAt.Count == 1 && LastAcceptedRequestId.Count == 1 && LastControlRequestAt.Count == 1,
                "Null/repeated initialization cannot release reservations or reset peer history");
        }

        var second = new ZNet();
        BeginNetworkSession(second);
        Check(ReferenceEquals(_sessionZNet, second), "A new session replaces the old identity");
        CheckClean();
        Shutdown();
        Check(_sessionZNet == null, "Shutdown releases the session reference");
        CheckClean();

        BeginNetworkSession(first);
        SeedOutstandingTravel();
        Shutdown();
        Check(_sessionZNet == null, "Direct shutdown releases the session reference");
        CheckClean();
        Shutdown();
        CheckClean();

        BeginNetworkSession(first);
        SeedOutstandingTravel();
        request = _pendingRequest!;
        commit = _pendingCommit!;
        InviteTravelCooldownStore.FailCancellation = true;
        bool failed = false;
        try { BeginNetworkSession(second); }
        catch (InvalidOperationException) { failed = true; }
        finally { InviteTravelCooldownStore.FailCancellation = false; }
        Check(failed && ReferenceEquals(_sessionZNet, first),
            "Failed teardown must not install a new session");
        Check(ReferenceEquals(_pendingRequest, request) && ReferenceEquals(_pendingCommit, commit),
            "Cleanup failure retains the original ordering before pending states are cleared");
        Shutdown();
        return $"{_checks} travel lifecycle checks passed (production cleanup methods; reservation store is a test double).";
    }
}
