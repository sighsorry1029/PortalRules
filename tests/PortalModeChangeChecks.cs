#nullable enable annotations
using System;
using System.Globalization;

// Production request gate, server authorization and cycle resolver. Transport,
// Clan registry, proximity, catalog persistence and effects are controlled doubles.
// These checks do not execute Harmony, Unity, real RPCs or catalog persistence.
public static class PortalRulesModeChangeChecks
{
    /* ENUMS */
    private const int CycleAccessModeRequest = -1;
    private static int _checks;
    private static ZDO _portal = new();
    private static bool _nearPortal;
    private static int _writes;
    private static PublicPortalAccessMode _writtenMode;
    private static string _writtenClan = "";
    private static int _effects;
    private static int _refreshes;

    private readonly struct ZDOID { }
    private sealed class ZDO
    {
        internal ZDOID m_uid = default;
        internal PortalBuilder Builder = new("builder-account", "Builder");
        internal PublicPortalAccessMode Mode = PublicPortalAccessMode.Tagged;
        internal bool Admin;
        internal bool Valid = true;
        internal bool Registered = true;
        internal bool IsValid() => Valid;
    }
    private readonly struct PortalBuilder
    {
        internal readonly string AccountId;
        internal readonly string Name;
        internal bool IsValid => !string.IsNullOrEmpty(AccountId);
        internal PortalBuilder(string account, string name) { AccountId = account; Name = name; }
    }
    private sealed class ZNet
    {
        internal static ZNet? instance;
        internal bool Server;
        internal bool IsServer() => Server;
    }
    private sealed class ZNetPeer
    {
        internal string AccountId = "editor-account";
        internal bool InBuilderClan = true;
    }
    private sealed class ZDOMan
    {
        internal static ZDOMan? instance;
        internal ZDO GetZDO(ZDOID id) => _portal;
    }
    private readonly struct PortalRulesMessage
    {
        internal readonly string Token;
        internal PortalRulesMessage(string token, params string[] args) { Token = token; }
    }
    private readonly struct PortalClanMembership
    {
        internal string PrimaryClanId => "builder-primary-clan";
        internal string PrimaryClanName => "Builder Clan";
        internal bool HasPrimaryClan => true;
    }
    private static class Player { internal static bool m_debugMode; }
    private static class PortalRulesPlugin
    {
        /* ADMIN_PROPERTIES */
    }
    private static class PublicPortalInteraction
    {
        internal static bool IsAdminPortal(ZDO zdo) => zdo.Admin;
    }
    private static class PublicPortalData
    {
        internal static string LocalAccount = "editor-account";
        internal static bool ServerAssignedIsAdmin;
        internal static PortalBuilder GetBuilder(ZDO zdo) => zdo.Builder;
        internal static bool TryGetLocalAccountId(out string account)
        {
            account = LocalAccount;
            return account.Length > 0;
        }
        internal static bool TryGetPeerAccountId(ZNetPeer peer, out string account)
        {
            account = peer.AccountId;
            return account.Length > 0;
        }
    }
    private static class PublicPortalCatalog
    {
        internal static PublicPortalAccessMode GetEffectiveAccessMode(ZDO zdo) => zdo.Mode;
        internal static bool TryGetAuthoritativeAccess(ZDOID id, out PublicPortalAccessMode mode, out object owner)
        {
            mode = _portal.Mode;
            owner = new object();
            return true;
        }
        internal static bool TryGetAuthoritativeBuilder(ZDOID id, out PortalBuilder builder)
        {
            builder = _portal.Builder;
            return builder.IsValid;
        }
        internal static void ObservePortal(ZDO zdo) { }
        internal static bool SetAuthoritativeAccess(ZDO zdo, PublicPortalAccessMode mode, string clan)
        {
            _writes++;
            _writtenMode = mode;
            _writtenClan = clan;
            return true;
        }
        internal static bool TryGetTemporaryPublicRemainingSeconds(ZDOID id, out int seconds)
        {
            seconds = 0;
            return false;
        }
    }
    private static class ClanPortalAccess
    {
        internal static bool IsServerRegistryAvailable;
        internal static bool LocalInBuilderClan;
        internal static string MembershipBuilder = "";
        internal static string AuthorizedBuilder = "";
        internal static ZNetPeer? AuthorizedPeer;
        internal static int PermissionQueries;
        internal static bool IsRequesterInBuilderPrimaryClan(PortalBuilder builder, ZNetPeer? requesterPeer)
        {
            PermissionQueries++;
            AuthorizedBuilder = builder.AccountId;
            AuthorizedPeer = requesterPeer;
            // Clan.ResolveMemberships returns Unavailable on a remote client.
            return ZNet.instance?.IsServer() == true && IsServerRegistryAvailable &&
                (requesterPeer?.InBuilderClan ?? LocalInBuilderClan);
        }
        internal static bool TryResolveBuilderMembership(PortalBuilder builder, out PortalClanMembership membership)
        {
            MembershipBuilder = builder.AccountId;
            membership = default;
            return IsServerRegistryAvailable;
        }
    }
    private static class PublicPortalServerPolicy
    {
        internal static int ClanLimit;
        internal static int InviteLimit;
        internal static int InviteCount;
        internal static bool InviteQuotaAvailable;
        internal static string InviteQuotaAccount = "";
        internal static bool TryGetClanQuotaState(string clan, out int count, out int limit)
        {
            count = 0;
            limit = ClanLimit;
            return true;
        }
        internal static bool TryGetInviteQuotaState(string account, out int count, out int limit)
        {
            InviteQuotaAccount = account;
            count = InviteCount;
            limit = InviteLimit;
            return InviteQuotaAvailable;
        }
    }
    private static class PublicPortalKinds
    {
        internal static bool IsRegisteredPortal(ZDO zdo) => zdo.Registered;
    }
    private static class PublicPortalModeChangeEffects
    {
        internal static void Broadcast(ZDOID id) { _effects++; }
    }
    private static class PublicPortalTaggedConnections
    {
        internal static void RefreshConnections() { _refreshes++; }
    }
    private static class PortalRulesLocalization
    {
        internal static string AccessModeToken(PublicPortalAccessMode mode) => mode.ToString();
    }
    private static bool IsAdminPortal(ZDO zdo) => PublicPortalInteraction.IsAdminPortal(zdo);
    private static bool TryValidatePortalInteraction(ZDO zdo, ZNetPeer? peer, out PortalRulesMessage message)
    {
        message = new PortalRulesMessage("$sighsorry_portalrules_move_closer_to_portal");
        return _nearPortal;
    }
    private static class PublicPortalAccess
    {
        /* REQUEST_METHOD */
    }

    /* SERVER_METHODS */

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _checks++;
    }
    private static void Reset(bool server = false)
    {
        _portal = new ZDO();
        _nearPortal = true;
        _writes = _effects = _refreshes = 0;
        _writtenClan = "";
        ZNet.instance = new ZNet { Server = server };
        ZDOMan.instance = new ZDOMan();
        Player.m_debugMode = false;
        PublicPortalData.ServerAssignedIsAdmin = false;
        PublicPortalData.LocalAccount = "editor-account";
        ClanPortalAccess.IsServerRegistryAvailable = true;
        ClanPortalAccess.LocalInBuilderClan = false;
        ClanPortalAccess.MembershipBuilder = ClanPortalAccess.AuthorizedBuilder = "";
        ClanPortalAccess.AuthorizedPeer = null;
        ClanPortalAccess.PermissionQueries = 0;
        PublicPortalServerPolicy.ClanLimit = PublicPortalServerPolicy.InviteLimit = 1;
        PublicPortalServerPolicy.InviteCount = 0;
        PublicPortalServerPolicy.InviteQuotaAvailable = true;
        PublicPortalServerPolicy.InviteQuotaAccount = "";
    }
    private static bool CanRequest() => PublicPortalAccess.CanRequestAccessModeChange(_portal);
    private static bool Apply(ZNetPeer peer, bool admin = false, bool debug = false, int operation = -1) =>
        TryApplyAccessModeChange(_portal.m_uid, operation, peer, admin, debug, out _, out _);
    private static void Denied(ZNetPeer peer, string reason, bool admin = false, bool debug = false, int operation = -1)
    {
        Check(!Apply(peer, admin, debug, operation) && _writes == 0 && _effects == 0 && _refreshes == 0, reason);
    }

    public static string Run()
    {
        Reset();
        Check(CanRequest(), "Remote non-builder must reach the server despite server-only Clan lookup");
        Check(ClanPortalAccess.PermissionQueries == 0, "Remote request gate must not invoke the server-only Clan query");
        ClanPortalAccess.IsServerRegistryAvailable = false;
        Check(CanRequest(), "Local optional-mod availability cannot establish server edit permission");
        _portal.Mode = PublicPortalAccessMode.Invite;
        Check(!CanRequest(), "Non-builder cannot request an Invite transition without admin debug");
        Player.m_debugMode = true;
        Check(!CanRequest(), "Local debug alone does not establish server admin permission");
        PublicPortalData.ServerAssignedIsAdmin = true;
        Player.m_debugMode = false;
        Check(!CanRequest(), "Server-confirmed admin still requires debug to change another account's Invite portal");
        Player.m_debugMode = true;
        Check(CanRequest(), "Server-confirmed admin with debug can request an Invite transition");
        PublicPortalData.LocalAccount = "builder-account";
        Check(CanRequest(), "Builder can request an Invite transition");
        _portal.Builder = new PortalBuilder("", "");
        Check(!CanRequest(), "Unverified builder remains blocked");
        _portal.Admin = true;
        Check(CanRequest(), "Admin prefab remains available to admin debug");
        Player.m_debugMode = false;
        Check(!CanRequest(), "Admin prefab remains blocked without admin debug");
        Check(!PublicPortalAccess.CanRequestAccessModeChange(null!), "Missing portal remains blocked");

        Reset(server: true);
        Check(!CanRequest(), "Host unrelated player remains blocked");
        ClanPortalAccess.LocalInBuilderClan = true;
        Check(CanRequest(), "Host clan member remains allowed");
        ZNet.instance = null;
        Check(!CanRequest(), "No session must not grant remote request fallback");

        Reset();
        var peer = new ZNetPeer();
        Check(CanRequest(), "Dedicated client can send the clan-member request");
        ZNet.instance!.Server = true;
        Check(Apply(peer) && _writtenMode == PublicPortalAccessMode.Personal && _writes == 1,
            "Server authorizes clan-member Tagged to Personal transition");
        Check(ClanPortalAccess.AuthorizedBuilder == "builder-account" && ClanPortalAccess.AuthorizedPeer == peer,
            "Server checks the authenticated requester against the original builder");
        Check(_effects == 1 && _refreshes == 1, "Accepted mode change still emits effects and refreshes Tagged links");

        Reset(server: true);
        _portal.Mode = PublicPortalAccessMode.Personal;
        Check(Apply(peer) && _writtenMode == PublicPortalAccessMode.Clan && _writtenClan == "builder-primary-clan" &&
              ClanPortalAccess.MembershipBuilder == "builder-account", "Clan transition resolves the builder's primary membership");
        Reset(server: true);
        _portal.Mode = PublicPortalAccessMode.Personal;
        PublicPortalServerPolicy.ClanLimit = 0;
        Check(Apply(peer) && _writtenMode == PublicPortalAccessMode.Public, "Full clan quota still skips Clan");
        Reset(server: true);
        _portal.Mode = PublicPortalAccessMode.Public;
        Check(Apply(peer) && _writtenMode == PublicPortalAccessMode.Tagged, "Clan member without admin debug skips Invite candidate");
        Reset(server: true);
        _portal.Mode = PublicPortalAccessMode.Invite;
        Denied(peer, "Server rejects clan-member changes to an Invite portal");
        Denied(peer, "Invite change requires debug even for an administrator", admin: true);
        Denied(peer, "Claiming debug does not let a non-admin change Invite", debug: true);
        peer.AccountId = "builder-account";
        Check(Apply(peer) && _writtenMode == PublicPortalAccessMode.Tagged, "Original builder can leave Invite");
        peer = new ZNetPeer { InBuilderClan = false };
        Reset(server: true);
        _portal.Mode = PublicPortalAccessMode.Invite;
        PublicPortalServerPolicy.InviteLimit = 0;
        Check(Apply(peer, admin: true, debug: true) && _writtenMode == PublicPortalAccessMode.Tagged,
            "Admin debug can leave another account's Invite portal even if Invite is now disabled");
        Check(_writes == 1 && _effects == 1 && _refreshes == 1,
            "Admin Invite change uses the normal catalog and effect path exactly once");
        Reset(server: true);
        _portal.Mode = PublicPortalAccessMode.Public;
        Check(Apply(peer, admin: true, debug: true) && _writtenMode == PublicPortalAccessMode.Invite,
            "Admin debug can enter Invite on another account's portal without Clan membership");
        Check(PublicPortalServerPolicy.InviteQuotaAccount == "builder-account",
            "Admin Invite entry checks the original builder's quota, not the administrator's account");
        foreach (string quotaState in new[] { "disabled", "full", "unavailable", "unlimited" })
        {
            Reset(server: true);
            _portal.Mode = PublicPortalAccessMode.Public;
            PublicPortalServerPolicy.InviteLimit = quotaState == "disabled" ? 0 : quotaState == "unlimited" ? -1 : 1;
            PublicPortalServerPolicy.InviteCount = 1;
            PublicPortalServerPolicy.InviteQuotaAvailable = quotaState != "unavailable";
            Check(Apply(peer, admin: true, debug: true) && _writtenMode ==
                (quotaState == "unlimited" ? PublicPortalAccessMode.Invite : PublicPortalAccessMode.Tagged),
                "Admin debug respects builder Invite quota: " + quotaState);
        }
        foreach (bool admin in new[] { false, true })
        {
            Reset(server: true);
            _portal.Mode = PublicPortalAccessMode.Public;
            var clanPeer = new ZNetPeer();
            Check(Apply(clanPeer, admin: admin, debug: !admin) && _writtenMode == PublicPortalAccessMode.Tagged,
                "Clan editor cannot enter Invite with only admin or only debug");
        }
        Reset(server: true);
        _portal.Mode = PublicPortalAccessMode.Public;
        Check(Apply(new ZNetPeer { AccountId = "builder-account", InBuilderClan = false }) &&
              _writtenMode == PublicPortalAccessMode.Invite, "Builder can still enter Invite without admin or Clan");
        Reset(server: true);
        _portal.Mode = PublicPortalAccessMode.Invite;
        _nearPortal = false;
        Denied(peer, "Admin debug does not bypass portal interaction validation", admin: true, debug: true);
        Reset(server: true);
        _portal.Mode = PublicPortalAccessMode.Invite;
        _portal.Builder = new PortalBuilder("", "");
        Denied(peer, "Admin debug still requires a verified original builder", admin: true, debug: true);
        Reset(server: true);
        _portal.Mode = PublicPortalAccessMode.Invite;
        Player.m_debugMode = true;
        Check(CanRequest(), "Local host with debug can request another account's Invite transition");
        Check(TryApplyAccessModeChange(_portal.m_uid, -1, null, PortalRulesPlugin.IsAdmin,
                  Player.m_debugMode, out var hostMode, out _) && hostMode == PublicPortalAccessMode.Tagged,
            "Local host follows the same admin debug server authorization");
        peer = new ZNetPeer { InBuilderClan = false };
        Reset(server: true);
        Denied(peer, "Server rejects an unrelated requester even if the client offers Shift+E");
        Denied(peer, "Admin without debug still cannot bypass ordinary portal permission", admin: true);
        Denied(peer, "Debug without admin still cannot bypass ordinary portal permission", debug: true);
        Check(Apply(peer, admin: true, debug: true), "Existing admin+debug override remains available");
        Reset(server: true);
        ClanPortalAccess.IsServerRegistryAvailable = false;
        Denied(new ZNetPeer(), "Unavailable server Clan registry fails closed for non-builders");
        peer.AccountId = "builder-account";
        Check(Apply(peer), "Builder access does not require Clan integration");
        peer = new ZNetPeer();
        Reset(server: true);
        _portal.Admin = true;
        Denied(peer, "Admin prefab requires admin debug");
        Denied(peer, "Admin prefab rejects admin without debug", admin: true);
        Check(Apply(peer, admin: true, debug: true) && _writtenMode == PublicPortalAccessMode.Admin,
            "Admin prefab retains its separate cycle");
        Reset(server: true);
        _nearPortal = false;
        Denied(peer, "Rejected interaction validation cannot write the catalog");
        Reset(server: true);
        Denied(peer, "Direct requested modes remain invalid; only cycle is accepted", operation: 0);
        _portal.Builder = new PortalBuilder("", "");
        Denied(peer, "Server requires a verified builder");
        Reset(server: true);
        _portal.Registered = false;
        Denied(peer, "Unregistered portal remains rejected");
        Reset();
        Denied(peer, "Client cannot apply authoritative access locally");
        return $"{_checks} portal mode change checks passed (production request/authorization/cycle; game and persistence boundaries are doubles).";
    }
}
