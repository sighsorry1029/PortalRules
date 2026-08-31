namespace PortalRules;

internal static class PublicPortalAccess
{
    public static bool CanUsePortal(ZDO zdo)
    {
        if (zdo == null)
        {
            return false;
        }

        if (PublicPortalCatalog.TryCanLocalServerUserUsePortal(
                zdo.m_uid,
                out bool serverCanUse))
        {
            return serverCanUse;
        }

        if (ZNet.instance != null && !ZNet.instance.IsServer())
        {
            // A dedicated server sends a recipient-filtered catalog. Once the
            // first snapshot exists, absence is an authoritative denial rather
            // than permission to fall back to public ZDO metadata.
            return PublicPortalCatalog.HasSnapshot &&
                   PublicPortalCatalog.TryGetClientEntry(
                       zdo.m_uid,
                       out PublicPortalCatalogEntry visibleEntry) &&
                   CanUsePortal(visibleEntry);
        }

        if (!CanUsePortal(
                PublicPortalCatalog.GetEffectiveAccessMode(zdo),
                PublicPortalCatalog.GetEffectiveOwner(zdo)))
        {
            return false;
        }

        return RequiredGlobalKeyAccess.IsPresent(
            RequiredGlobalKeyAccess.QueryLocal(
                PublicPortalCatalog.GetEffectiveRequiredGlobalKey(zdo)));
    }

    public static bool CanUsePortal(PublicPortalCatalogEntry portal)
    {
        if (PublicPortalCatalog.TryCanLocalServerUserUsePortal(
                portal.Id,
                out bool serverCanUse))
        {
            return serverCanUse;
        }

        if (portal.AccessMode == PublicPortalAccessMode.Clan &&
            ZNet.instance != null &&
            !ZNet.instance.IsServer())
        {
            // A dedicated server only includes Clan entries in the recipient's
            // authenticated, clan-filtered snapshot. The server rechecks again
            // before issuing the teleport grant.
            return true;
        }

        return CanUsePortal(portal.AccessMode, portal.Owner);
    }

    private static bool CanUsePortal(PublicPortalAccessMode mode, PortalOwner owner)
    {
        if (mode is PublicPortalAccessMode.Public or
            PublicPortalAccessMode.Invite or
            PublicPortalAccessMode.Tagged)
        {
            return true;
        }

        if (PublicPortalData.IsLocalOwner(owner))
        {
            return true;
        }

        return mode switch
        {
            PublicPortalAccessMode.Admin => PortalRulesPlugin.IsAdmin,
            // Clan access is granted only by the server authority or by an
            // authenticated, recipient-filtered catalog entry.
            PublicPortalAccessMode.Clan => false,
            _ => false
        };
    }

    public static bool CanEditPortal(ZDO zdo)
    {
        if (zdo == null)
        {
            return false;
        }

        if (PublicPortalInteraction.IsAdminPortal(zdo))
        {
            return PortalRulesPlugin.HasAdminDebugAccess;
        }

        PortalBuilder builder = PublicPortalData.GetBuilder(zdo);
        if (!builder.IsValid)
        {
            return false;
        }

        bool requesterIsBuilder =
            PublicPortalData.TryGetLocalSteamId64(out string steamId) &&
            string.Equals(
                steamId,
                builder.AccountId,
                System.StringComparison.Ordinal);
        if (requesterIsBuilder)
        {
            return true;
        }

        if (PublicPortalCatalog.GetEffectiveAccessMode(zdo) ==
            PublicPortalAccessMode.Invite)
        {
            return false;
        }

        return PortalRulesPlugin.HasAdminDebugAccess ||
               ClanPortalAccess.IsRequesterInBuilderPrimaryClan(
                   builder,
                   requesterPeer: null);
    }
}
