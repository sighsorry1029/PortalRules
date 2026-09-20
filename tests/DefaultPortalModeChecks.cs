#nullable enable annotations
using System;

// Actual initial-mode resolver and enum definitions; Clan and quota are controlled boundaries.
public static class PortalRulesDefaultModeChecks
{
    /* ENUMS */
    private const int MaximumClanIdLength = 128;
    private static int _checks;
    private sealed class Setting<T> { public T Value = default!; }
    private static class PublicPortalConfig
    {
        internal static readonly Setting<PublicPortalDefaultMode> DefaultPortalMode = new();
    }
    private readonly struct PortalBuilder
    {
        internal readonly bool IsValid;
        internal PortalBuilder(bool valid) { IsValid = valid; }
    }
    private readonly struct PortalClanMembership
    {
        internal readonly string PrimaryClanId;
        internal bool HasPrimaryClan => !string.IsNullOrEmpty(PrimaryClanId);
        internal PortalClanMembership(string id) { PrimaryClanId = id; }
    }
    private static class ClanPortalAccess
    {
        internal static bool IsServerRegistryAvailable;
        internal static bool Resolves;
        internal static string PrimaryClan = "";
        internal static int Calls;
        internal static bool TryResolveBuilderMembership(PortalBuilder builder, out PortalClanMembership membership)
        {
            Calls++;
            membership = new PortalClanMembership(PrimaryClan);
            return Resolves;
        }
    }
    private static class PublicPortalServerPolicy
    {
        internal static bool Ready;
        internal static int Count;
        internal static int Limit;
        internal static int Calls;
        internal static string QueriedClan = "";
        internal static bool TryGetClanQuotaState(string clan, out int current, out int limit)
        {
            Calls++;
            QueriedClan = clan;
            current = Count;
            limit = Limit;
            return Ready;
        }
    }

    /* PRODUCTION_METHODS */

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _checks++;
    }
    private static void Reset()
    {
        PublicPortalConfig.DefaultPortalMode.Value = PublicPortalDefaultMode.Clan;
        ClanPortalAccess.IsServerRegistryAvailable = true;
        ClanPortalAccess.Resolves = true;
        ClanPortalAccess.PrimaryClan = " clan-A ";
        ClanPortalAccess.Calls = 0;
        PublicPortalServerPolicy.Ready = true;
        PublicPortalServerPolicy.Count = 0;
        PublicPortalServerPolicy.Limit = 1;
        PublicPortalServerPolicy.Calls = 0;
        PublicPortalServerPolicy.QueriedClan = "";
    }
    private static void ExpectPersonal(string reason, bool validBuilder = true)
    {
        Check(ResolveNewPlayerPortalMode(new PortalBuilder(validBuilder), out string clan) ==
              PublicPortalAccessMode.Personal && clan == "", reason);
    }

    public static string Run()
    {
        Check(string.Join(",", Enum.GetNames(typeof(PublicPortalDefaultMode))) == "Personal,Clan,Public",
            "Only the three placement modes are exposed");
        foreach (PublicPortalDefaultMode mode in new[] { PublicPortalDefaultMode.Personal, PublicPortalDefaultMode.Public })
        {
            Reset();
            PublicPortalConfig.DefaultPortalMode.Value = mode;
            PublicPortalAccessMode result = ResolveNewPlayerPortalMode(new PortalBuilder(true), out string clan);
            Check(result == (mode == PublicPortalDefaultMode.Personal ? PublicPortalAccessMode.Personal : PublicPortalAccessMode.Public)
                  && clan == "", "Explicit Personal/Public choice without Clan binding");
            Check(ClanPortalAccess.Calls == 0 && PublicPortalServerPolicy.Calls == 0,
                "Personal/Public do not depend on optional Clan availability");
            ExpectPersonal("Unverified builder cannot receive Public or Clan access", validBuilder: false);
        }

        Reset();
        PublicPortalConfig.DefaultPortalMode.Value = (PublicPortalDefaultMode)99;
        ExpectPersonal("Unknown config enum fails to Personal");
        Reset();
        ClanPortalAccess.IsServerRegistryAvailable = false;
        ExpectPersonal("Missing Clan API falls back");
        Check(ClanPortalAccess.Calls == 0, "Unavailable registry is not invoked");
        Reset();
        ClanPortalAccess.Resolves = false;
        ExpectPersonal("Failed membership resolution falls back");
        foreach (string id in new[] { "", "   ", new string('x', 129) })
        {
            Reset();
            ClanPortalAccess.PrimaryClan = id;
            ExpectPersonal("Absent primary/guest-only or invalid Clan ID falls back");
            Check(PublicPortalServerPolicy.Calls == 0, "Invalid Clan ID is not passed to quota lookup");
        }
        Reset();
        PublicPortalServerPolicy.Ready = false;
        ExpectPersonal("Unavailable quota state falls back");
        foreach (int limit in new[] { 0, 1, 3 })
        {
            Reset();
            PublicPortalServerPolicy.Limit = limit;
            PublicPortalServerPolicy.Count = limit;
            ExpectPersonal("Disabled/full Clan quota falls back");
        }
        foreach (int limit in new[] { -1, 1, 5 })
        {
            Reset();
            PublicPortalServerPolicy.Limit = limit;
            Check(ResolveNewPlayerPortalMode(new PortalBuilder(true), out string clan) == PublicPortalAccessMode.Clan &&
                  clan == "clan-A" && PublicPortalServerPolicy.QueriedClan == clan,
                "Unlimited/available quota binds the sanitized primary Clan");
        }
        Reset();
        PublicPortalAccessMode first = ResolveNewPlayerPortalMode(new PortalBuilder(true), out _);
        PublicPortalServerPolicy.Count = 1; // Boundary now reports the accepted first placement.
        ExpectPersonal("The next placement observes the updated quota");
        PublicPortalConfig.DefaultPortalMode.Value = PublicPortalDefaultMode.Public;
        Check(ResolveNewPlayerPortalMode(new PortalBuilder(true), out string noClan) == PublicPortalAccessMode.Public &&
              noClan == "" && first == PublicPortalAccessMode.Clan,
            "Live default is read for the next placement without modifying prior results");
        return $"{_checks} default portal mode checks passed (production policy; Clan/quota boundaries are test doubles).";
    }
}
