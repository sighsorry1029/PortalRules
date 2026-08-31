using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Jotunn.Configs;
using Jotunn.Managers;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PortalRules;

[HarmonyPatch]
internal static class AdminPortalPrefabManager
{
    private static readonly PortalCloneSpec[] PortalCloneSpecs =
    {
        new(
            PublicPortalKinds.WoodPortalPrefabName,
            PublicPortalKinds.AdminWoodPortalPrefabName,
            "$sighsorry_portalrules_admin_wood_portal_name"),
        new(
            PublicPortalKinds.StonePortalPrefabName,
            PublicPortalKinds.AdminStonePortalPrefabName,
            "$sighsorry_portalrules_admin_stone_portal_name")
    };

    private static bool _registered;
    private static Player? _lastBuildMenuPlayer;
    private static bool _lastBuildMenuAccess;
    private static bool _buildMenuAccessInitialized;
    private static readonly MethodInfo? UpdateAvailablePiecesMethod =
        AccessTools.DeclaredMethod(typeof(Player), "UpdateAvailablePiecesList");

    internal static void Initialize()
    {
        PrefabManager.OnVanillaPrefabsAvailable += RegisterAdminPortals;
        PieceManager.OnPiecesRegistered += RegisterAdminPortalPieces;
    }

    internal static void Shutdown()
    {
        PrefabManager.OnVanillaPrefabsAvailable -= RegisterAdminPortals;
        PieceManager.OnPiecesRegistered -= RegisterAdminPortalPieces;
        _lastBuildMenuPlayer = null;
        _lastBuildMenuAccess = false;
        _buildMenuAccessInitialized = false;
    }

    internal static void Tick()
    {
        Player? player = Player.m_localPlayer;
        if (!_registered || player == null)
        {
            _lastBuildMenuPlayer = null;
            _buildMenuAccessInitialized = false;
            return;
        }

        bool hasAccess = PortalRulesPlugin.HasAdminDebugAccess;
        if (_buildMenuAccessInitialized &&
            ReferenceEquals(_lastBuildMenuPlayer, player) &&
            _lastBuildMenuAccess == hasAccess)
        {
            return;
        }

        _lastBuildMenuPlayer = player;
        _lastBuildMenuAccess = hasAccess;
        _buildMenuAccessInitialized = true;
        if (player.InPlaceMode())
        {
            // Refresh both the available list and placement ghost immediately if
            // the server-admin snapshot or debug mode changes while holding a tool.
            if (UpdateAvailablePiecesMethod == null)
            {
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    "Could not refresh the Hammer piece list after admin build access changed.");
                return;
            }

            try
            {
                UpdateAvailablePiecesMethod.Invoke(player, Array.Empty<object>());
            }
            catch (Exception ex)
            {
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    $"Could not refresh the Hammer piece list after admin build access changed: " +
                    $"{ex.GetBaseException().Message}");
            }
        }
    }

    private static void RegisterAdminPortals()
    {
        if (_registered)
        {
            PrefabManager.OnVanillaPrefabsAvailable -= RegisterAdminPortals;
            return;
        }

        int registeredCount = 0;
        foreach (PortalCloneSpec spec in PortalCloneSpecs)
        {
            GameObject? prefab = PrefabManager.Instance.GetPrefab(spec.TargetPrefabName);
            if (prefab != null)
            {
                registeredCount++;
                continue;
            }

            Piece? sourcePiece = PrefabManager.Instance
                .GetPrefab(spec.SourcePrefabName)
                ?.GetComponent<Piece>();
            prefab = PrefabManager.Instance.CreateClonedPrefab(
                spec.TargetPrefabName,
                spec.SourcePrefabName);
            if (prefab == null)
            {
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    $"Could not clone '{spec.SourcePrefabName}' into '{spec.TargetPrefabName}'.");
                continue;
            }

            PrepareAdminPortal(prefab, sourcePiece, spec.DisplayName);
            PrefabManager.Instance.AddPrefab(prefab);
            registeredCount++;
        }

        if (registeredCount == PortalCloneSpecs.Length)
        {
            _registered = true;
            PrefabManager.OnVanillaPrefabsAvailable -= RegisterAdminPortals;
            PortalRulesPlugin.PortalRulesLogger.LogInfo(
                $"Registered admin portal prefabs: {PublicPortalKinds.AdminWoodPortalPrefabName}, {PublicPortalKinds.AdminStonePortalPrefabName}.");
        }
    }

    private static void RegisterAdminPortalPieces()
    {
        int registeredCount = 0;
        foreach (PortalCloneSpec spec in PortalCloneSpecs)
        {
            GameObject? prefab = PrefabManager.Instance.GetPrefab(spec.TargetPrefabName);
            if (prefab == null)
            {
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    $"Could not find admin portal '{spec.TargetPrefabName}' for Hammer registration.");
                continue;
            }

            try
            {
                PieceManager.Instance.RegisterPieceInPieceTable(
                    prefab,
                    PieceTables.Hammer,
                    PieceCategories.Misc);
                registeredCount++;
            }
            catch (Exception ex)
            {
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    $"Could not add admin portal '{spec.TargetPrefabName}' to the Hammer piece table: {ex.Message}");
            }
        }

        if (registeredCount == PortalCloneSpecs.Length)
        {
            PortalRulesPlugin.PortalRulesLogger.LogInfo(
                "Registered admin portal pieces in the Hammer Misc category.");
        }
    }

    private static void PrepareAdminPortal(
        GameObject prefab,
        Piece? sourcePiece,
        string displayName)
    {
        PrepareAdminPiece(prefab, sourcePiece, displayName);
        RemoveRootComponent<WearNTear>(prefab);
        // Retain the source visual hierarchy and solid colliders. Valheim uses a
        // non-trigger collider's ClosestPoint to position the placement ghost;
        // removing every solid collider sends the ghost and placed piece away
        // from the aimed point.

        TeleportWorld teleportWorld = prefab.GetComponent<TeleportWorld>();
        if (teleportWorld != null)
        {
            teleportWorld.enabled = true;
            teleportWorld.m_allowAllItems = true;
            teleportWorld.m_proximityRoot = teleportWorld.m_proximityRoot != null
                ? teleportWorld.m_proximityRoot
                : prefab.transform;
            EnsureTargetFoundEffect(prefab, teleportWorld);
            EnsureModelRenderer(prefab, teleportWorld);
        }

        ZNetView zNetView = prefab.GetComponent<ZNetView>();
        if (zNetView != null)
        {
            zNetView.m_persistent = true;
            zNetView.m_distant = false;
            zNetView.m_type = ZDO.ObjectType.Solid;
        }

        EnsureInteractionTrigger(prefab);
    }

    private static void PrepareAdminPiece(
        GameObject prefab,
        Piece? sourcePiece,
        string displayName)
    {
        Piece? adminPiece = prefab.GetComponent<Piece>();
        if (adminPiece == null)
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                $"Admin portal '{prefab.name}' has no Piece metadata; its icon cannot be displayed.");
            return;
        }

        if (sourcePiece?.m_icon != null)
        {
            adminPiece.m_icon = sourcePiece.m_icon;
        }
        else
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                $"Admin portal '{prefab.name}' could not reuse its source portal icon.");
        }

        // Keep Piece only as discoverable icon/placement metadata. Admin portal
        // authority owns creation, removal, creator state, and resource policy.
        // A distinct recipe key prevents the zero-resource clone from teaching
        // the player the original portal recipe that supplied its visuals.
        adminPiece.m_name = displayName;
        adminPiece.m_description =
            "$sighsorry_portalrules_admin_portal_description";
        adminPiece.m_category = Piece.PieceCategory.Misc;
        // Keep the registered pieces out of Valheim's normal recipe discovery.
        // The availability patch below inserts them directly for admin+debug.
        adminPiece.m_enabled = false;
        adminPiece.m_canBeRemoved = false;
        adminPiece.m_craftingStation = null;
        adminPiece.m_resources = Array.Empty<Piece.Requirement>();
        adminPiece.m_destroyedLootPrefab = null;
        adminPiece.m_comfort = 0;
        adminPiece.m_comfortGroup = Piece.ComfortGroup.None;
        adminPiece.m_comfortObject = null;
        adminPiece.m_harvest = false;
        adminPiece.m_primaryTarget = false;
        adminPiece.m_randomTarget = false;
        adminPiece.m_targetNonPlayerBuilt = false;
    }

    private static bool IsAdminPortalPiece(Piece piece)
    {
        if (piece == null)
        {
            return false;
        }

        string prefabName = Utils.GetPrefabName(piece.gameObject);
        return prefabName == PublicPortalKinds.AdminWoodPortalPrefabName ||
               prefabName == PublicPortalKinds.AdminStonePortalPrefabName;
    }

    private static void EnsureInteractionTrigger(GameObject prefab)
    {
        if (prefab.transform.Find("ADMIN_INTERACT") != null)
        {
            return;
        }

        Collider teleportCollider = FindTeleportCollider(prefab);
        Transform sourceTransform = teleportCollider != null ? teleportCollider.transform : prefab.transform;
        GameObject interaction = new("ADMIN_INTERACT");
        interaction.layer = LayerOrFallback("piece_nonsolid", prefab.layer);
        interaction.transform.SetParent(prefab.transform, false);
        interaction.transform.localPosition = sourceTransform.localPosition;
        interaction.transform.localRotation = sourceTransform.localRotation;
        interaction.transform.localScale = sourceTransform.localScale;

        BoxCollider interactionCollider = interaction.AddComponent<BoxCollider>();
        interactionCollider.isTrigger = true;
        if (teleportCollider is BoxCollider teleportBox)
        {
            interactionCollider.center = teleportBox.center;
            interactionCollider.size = teleportBox.size;
        }
        else
        {
            interactionCollider.center = new Vector3(0f, 1.5f, 0f);
            interactionCollider.size = new Vector3(2f, 3f, 0.5f);
        }
    }

    private static Collider FindTeleportCollider(GameObject prefab)
    {
        foreach (Collider collider in prefab.GetComponentsInChildren<Collider>(true))
        {
            if (collider.GetComponent<TeleportWorldTrigger>() != null)
            {
                return collider;
            }
        }

        return null!;
    }

    private static void DisablePlacedPortalSolidColliders(GameObject portal)
    {
        foreach (Collider collider in portal.GetComponentsInChildren<Collider>(true))
        {
            if (!collider.isTrigger)
            {
                collider.enabled = false;
            }
        }
    }

    private static void HidePlacedPortalMeshes(GameObject portal)
    {
        TeleportWorld? teleportWorld = portal.GetComponent<TeleportWorld>();
        Transform? targetFoundEffect = teleportWorld?.m_target_found?.transform;

        foreach (MeshRenderer renderer in portal.GetComponentsInChildren<MeshRenderer>(true))
        {
            if (targetFoundEffect == null || !renderer.transform.IsChildOf(targetFoundEffect))
            {
                renderer.enabled = false;
            }
        }

        foreach (SkinnedMeshRenderer renderer in portal.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (targetFoundEffect == null || !renderer.transform.IsChildOf(targetFoundEffect))
            {
                renderer.enabled = false;
            }
        }
    }

    private static void EnsureTargetFoundEffect(GameObject prefab, TeleportWorld teleportWorld)
    {
        if (teleportWorld.m_target_found != null)
        {
            return;
        }

        GameObject dummy = new("ADMIN_TARGET_FOUND_DUMMY");
        dummy.transform.SetParent(prefab.transform, false);
        teleportWorld.m_target_found = dummy.AddComponent<EffectFade>();
    }

    private static void EnsureModelRenderer(GameObject prefab, TeleportWorld teleportWorld)
    {
        if (teleportWorld.m_model != null)
        {
            return;
        }

        MeshRenderer meshRenderer = prefab.GetComponentInChildren<MeshRenderer>(true);
        if (meshRenderer == null)
        {
            GameObject dummy = GameObject.CreatePrimitive(PrimitiveType.Cube);
            dummy.name = "ADMIN_MODEL_DUMMY";
            dummy.transform.SetParent(prefab.transform, false);
            dummy.transform.localScale = Vector3.one * 0.1f;
            RemoveRootComponent<Collider>(dummy);
            meshRenderer = dummy.GetComponent<MeshRenderer>();
        }

        teleportWorld.m_model = meshRenderer;
    }

    private static int LayerOrFallback(string layerName, int fallback)
    {
        int layer = LayerMask.NameToLayer(layerName);
        return layer >= 0 ? layer : fallback;
    }

    private static void RemoveRootComponent<T>(GameObject gameObject) where T : Component
    {
        T component = gameObject.GetComponent<T>();
        if (component != null)
        {
            Object.DestroyImmediate(component);
        }
    }

    internal static void RegisterPortalHashes(Game game)
    {
        foreach (PortalCloneSpec spec in PortalCloneSpecs)
        {
            int hash = spec.TargetPrefabName.GetStableHashCode();
            if (!game.PortalPrefabHash.Contains(hash))
            {
                game.PortalPrefabHash.Add(hash);
            }
        }
    }

    [HarmonyPatch(typeof(Game), "Awake")]
    private static class GameAwakePatch
    {
        private static void Postfix(Game __instance)
        {
            RegisterPortalHashes(__instance);
        }
    }

    [HarmonyPatch(typeof(TeleportWorld), "UpdatePortal")]
    private static class ShowAdminPortalWhirlingEffectNearbyPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(TeleportWorld __instance)
        {
            ZDO? zdo = PublicPortalKinds.GetPortalZdo(__instance);
            if (zdo == null ||
                !PublicPortalKinds.IsAdminPortalPrefab(zdo.GetPrefab()))
            {
                return;
            }

            EffectFade? effect = __instance.m_target_found;
            if (effect != null)
            {
                Player? player = Player.m_localPlayer;
                Transform proximityRoot = __instance.m_proximityRoot != null
                    ? __instance.m_proximityRoot
                    : __instance.transform;
                float activationRange = Mathf.Max(0f, __instance.m_activationRange);
                bool isNearby = player != null &&
                                Player.GetClosestPlayer(
                                    proximityRoot.position,
                                    activationRange) != null;
                effect.SetActive(isNearby);
            }
        }
    }

    [HarmonyPatch(typeof(Piece), "Awake")]
    private static class ClearAdminPortalPieceCreatorPatch
    {
        private static void Postfix(
            Piece __instance,
            ref long ___m_creator)
        {
            if (IsAdminPortalPiece(__instance))
            {
                ___m_creator = 0L;
                if (!ZNetView.m_forceDisableInit)
                {
                    // Placement ghosts need the source solid collider for
                    // Player.UpdatePlacementGhost's ClosestPoint calculation.
                    // Placed instances retain only their interaction/teleport
                    // triggers and nearby-player portal marker effect.
                    // The source frame is hidden completely and has no
                    // physical collision.
                    DisablePlacedPortalSolidColliders(__instance.gameObject);
                    HidePlacedPortalMeshes(__instance.gameObject);
                }
            }
        }
    }

    [HarmonyPatch(typeof(Piece), nameof(Piece.SetCreator))]
    private static class BlockAdminPortalPieceCreatorPatch
    {
        private static bool Prefix(Piece __instance)
        {
            return !IsAdminPortalPiece(__instance);
        }
    }

    [HarmonyPatch(typeof(PieceTable), nameof(PieceTable.UpdateAvailable))]
    private static class AdminPortalBuildMenuVisibilityPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(
            PieceTable __instance,
            Player player,
            List<List<Piece>> ___m_availablePieces)
        {
            foreach (List<Piece> availablePieces in ___m_availablePieces)
            {
                availablePieces.RemoveAll(IsAdminPortalPiece);
            }

            if (player != Player.m_localPlayer ||
                !PortalRulesPlugin.HasAdminDebugAccess)
            {
                return;
            }

            int miscIndex = (int)Piece.PieceCategory.Misc;
            if (miscIndex < 0 || miscIndex >= ___m_availablePieces.Count)
            {
                return;
            }

            List<Piece> miscPieces = ___m_availablePieces[miscIndex];
            foreach (GameObject prefab in __instance.m_pieces)
            {
                Piece? piece = prefab != null ? prefab.GetComponent<Piece>() : null;
                if (piece != null &&
                    IsAdminPortalPiece(piece) &&
                    !miscPieces.Contains(piece))
                {
                    miscPieces.Add(piece);
                }
            }
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.TryPlacePiece))]
    private static class AdminPortalPlacementAccessPatch
    {
        private static bool Prefix(
            Player __instance,
            Piece piece,
            ref bool __result)
        {
            if (!IsAdminPortalPiece(piece))
            {
                return true;
            }

            if (__instance == Player.m_localPlayer &&
                PortalRulesPlugin.HasAdminDebugAccess)
            {
                return true;
            }

            __instance?.Message(
                MessageHud.MessageType.Center,
                new PortalRulesMessage(
                    "$sighsorry_portalrules_admin_portal_placement_requires_admin_debug")
                    .Localize());
            __result = false;
            return false;
        }
    }

    private readonly struct PortalCloneSpec
    {
        internal readonly string SourcePrefabName;
        internal readonly string TargetPrefabName;
        internal readonly string DisplayName;

        internal PortalCloneSpec(
            string sourcePrefabName,
            string targetPrefabName,
            string displayName)
        {
            SourcePrefabName = sourcePrefabName;
            TargetPrefabName = targetPrefabName;
            DisplayName = displayName;
        }
    }
}
