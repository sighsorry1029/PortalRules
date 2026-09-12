using System;
using System.Collections.Generic;
using HarmonyLib;

namespace PortalRules;

[HarmonyPatch]
internal static class PublicPortalTaggedConnections
{
    private delegate void SetConnectionDelegate(
        Game game,
        ZDO portal,
        ZDOID connection,
        bool forceImmediateConnection);

    private static SetConnectionDelegate? _setConnection;
    private static bool _setConnectionBindingAttempted;
    private static bool _setConnectionWarningLogged;

    internal static void RefreshConnections()
    {
        if (ZNet.instance != null &&
            ZNet.instance.IsServer() &&
            Game.instance != null)
        {
            Game.instance.ConnectPortals();
        }
    }

    private static bool IsTaggedConnectable(ZDO zdo)
    {
        return zdo != null &&
               PublicPortalCatalog.GetEffectiveAccessMode(zdo) == PublicPortalAccessMode.Tagged;
    }

    private static bool AreConnectionScopesCompatible(ZDO source, ZDO target)
    {
        bool sourceIsTagged = IsTaggedConnectable(source);
        bool targetIsTagged = IsTaggedConnectable(target);
        if (!sourceIsTagged || !targetIsTagged)
        {
            return sourceIsTagged == targetIsTagged;
        }

        bool sourceIsAdminPortal =
            PublicPortalKinds.IsAdminPortalPrefab(source.GetPrefab());
        bool targetIsAdminPortal =
            PublicPortalKinds.IsAdminPortalPrefab(target.GetPrefab());
        if (sourceIsAdminPortal || targetIsAdminPortal)
        {
            // Creatorless Admin portals form a public system pool, separate from
            // player-owned or Clan-bound Tagged connections.
            return sourceIsAdminPortal && targetIsAdminPortal;
        }

        string sourceClanId =
            PublicPortalCatalog.GetEffectiveAuthorizedClanId(source);
        string targetClanId =
            PublicPortalCatalog.GetEffectiveAuthorizedClanId(target);
        if (sourceClanId.Length != 0 || targetClanId.Length != 0)
        {
            return sourceClanId.Length != 0 &&
                   string.Equals(
                       sourceClanId,
                       targetClanId,
                       System.StringComparison.Ordinal);
        }

        PortalOwner sourceOwner = PublicPortalCatalog.GetEffectiveOwner(source);
        PortalOwner targetOwner = PublicPortalCatalog.GetEffectiveOwner(target);
        return sourceOwner.IsValid &&
               targetOwner.IsValid &&
               string.Equals(
                   sourceOwner.Id,
                   targetOwner.Id,
                   System.StringComparison.Ordinal);
    }

    [HarmonyPatch(typeof(Game), "ClearCurrentlyConnectingPortals")]
    private static class ClearCrossModeConnectionsPatch
    {
        private static void Postfix(Game __instance)
        {
            if (ZDOMan.instance == null)
            {
                return;
            }

            foreach (ZDO portal in ZDOMan.instance.GetPortalList())
            {
                ZDOID connection = portal.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);
                if (connection == ZDOID.None)
                {
                    continue;
                }

                ZDO target = ZDOMan.instance.GetZDO(connection);
                if (target != null &&
                    AreConnectionScopesCompatible(portal, target))
                {
                    continue;
                }

                if (!TryClearConnection(__instance, portal))
                {
                    return;
                }
            }
        }
    }

    [HarmonyPatch(typeof(Game), "FindRandomUnconnectedPortal")]
    private static class FindCompatiblePortalPatch
    {
        private static void Prefix(ref List<ZDO> portals, ZDO skip)
        {
            List<ZDO> candidates = new();
            foreach (ZDO portal in portals)
            {
                if (!AreConnectionScopesCompatible(skip, portal))
                {
                    continue;
                }

                candidates.Add(portal);
            }

            portals = candidates;
        }
    }

    private static bool TryGetSetConnection(
        out SetConnectionDelegate setConnection)
    {
        if (!_setConnectionBindingAttempted)
        {
            _setConnectionBindingAttempted = true;
            try
            {
                System.Reflection.MethodInfo? method =
                    AccessTools.DeclaredMethod(
                        typeof(Game),
                        "SetConnection",
                        new[] { typeof(ZDO), typeof(ZDOID), typeof(bool) });
                if (method != null)
                {
                    _setConnection =
                        AccessTools.MethodDelegate<SetConnectionDelegate>(
                            method,
                            instance: null,
                            virtualCall: false);
                }
            }
            catch (Exception ex)
            {
                LogSetConnectionBindingFailure(ex.Message);
            }
        }

        if (_setConnection != null)
        {
            setConnection = _setConnection;
            return true;
        }

        LogSetConnectionBindingFailure(
            "Game.SetConnection(ZDO, ZDOID, bool) was not found.");
        setConnection = null!;
        return false;
    }

    private static bool TryClearConnection(Game game, ZDO portal)
    {
        if (!TryGetSetConnection(out SetConnectionDelegate setConnection))
        {
            return false;
        }

        try
        {
            setConnection(
                game,
                portal,
                ZDOID.None,
                forceImmediateConnection: false);
            return true;
        }
        catch (Exception ex)
        {
            _setConnection = null;
            LogSetConnectionBindingFailure(ex.Message);
            return false;
        }
    }

    private static void LogSetConnectionBindingFailure(string reason)
    {
        if (_setConnectionWarningLogged)
        {
            return;
        }

        _setConnectionWarningLogged = true;
        PortalRulesPlugin.PortalRulesLogger.LogWarning(
            "Tagged portal connection cleanup is unavailable; " +
            $"candidate filtering will remain active. {reason}");
    }
}
