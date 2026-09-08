using UnityEngine;

namespace PortalRules;

/// <summary>
/// Owns the stable prefab identity and low-level component/ZDO checks shared by
/// portal subsystems. Access policy and catalog-derived classification stay in
/// their respective higher-level services.
/// </summary>
internal static class PublicPortalKinds
{
    internal const string WoodPortalPrefabName = "portal_wood";
    internal const string StonePortalPrefabName = "portal_stone";
    internal const string AdminWoodPortalPrefabName = "admin_portal_wood";
    internal const string AdminStonePortalPrefabName = "admin_portal_stone";

    private static readonly int AdminWoodPortalHash =
        AdminWoodPortalPrefabName.GetStableHashCode();
    private static readonly int AdminStonePortalHash =
        AdminStonePortalPrefabName.GetStableHashCode();

    internal static bool IsAdminPortalPrefab(int prefabHash)
    {
        return prefabHash == AdminWoodPortalHash ||
               prefabHash == AdminStonePortalHash;
    }

    internal static bool PrefabAllowsAllItems(int prefabHash)
    {
        if (IsAdminPortalPrefab(prefabHash))
        {
            return true;
        }

        GameObject? prefab = ZNetScene.instance != null
            ? ZNetScene.instance.GetPrefab(prefabHash)
            : null;
        TeleportWorld? portal = prefab != null
            ? prefab.GetComponent<TeleportWorld>()
            : null;
        return portal != null && portal.m_allowAllItems;
    }

    internal static ZDO? GetPortalZdo(TeleportWorld portal)
    {
        if (portal == null)
        {
            return null;
        }

        ZNetView nview = portal.GetComponent<ZNetView>();
        return nview != null && nview.IsValid() ? nview.GetZDO() : null;
    }
}
