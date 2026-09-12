using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace PortalRules;

[HarmonyPatch]
internal static class PublicPortalMap
{
    private static bool IsLocalPlayer(Collider collider)
    {
        Player player = collider.GetComponent<Player>();
        return player != null && Player.m_localPlayer == player;
    }

    private static bool IsMapSelectionPortal(TeleportWorld portal)
    {
        if (portal == null ||
            PublicPortalConfig.EnablePortalMap.Value.IsOff())
        {
            return false;
        }

        ZDO? zdo = PublicPortalKinds.GetPortalZdo(portal);
        return zdo != null &&
               PublicPortalCatalog.GetEffectiveAccessMode(zdo) != PublicPortalAccessMode.Tagged;
    }

    private static float ClampPortalMapMouseWheel(float value, float minimum, float maximum)
    {
        float clamped = Mathf.Clamp(value, minimum, maximum);
        if (!PublicPortalMapController.Instance.IsSelecting)
        {
            return clamped;
        }

        int multiplier = Mathf.Clamp(
            PublicPortalConfig.PortalMapWheelZoomMultiplier.Value,
            1,
            10);
        return clamped * multiplier;
    }

    private static bool HasWhirlingTarget(TeleportWorld portal)
    {
        if (IsMapSelectionPortal(portal))
        {
            return true;
        }

        ZDO? sourceZdo = PublicPortalKinds.GetPortalZdo(portal);
        ZDOID targetId = sourceZdo?.GetConnectionZDOID(
            ZDOExtraData.ConnectionType.Portal) ?? ZDOID.None;
        if (targetId.IsNone() || ZDOMan.instance == null)
        {
            return false;
        }

        if (ZDOMan.instance.GetZDO(targetId) != null)
        {
            return true;
        }

        ZDOMan.instance.RequestZDO(targetId);
        return false;
    }

    [HarmonyPatch(typeof(Minimap), "UpdateMap")]
    private static class PortalMapWheelZoomPatch
    {
        private const int MouseWheelClampSearchWindow = 12;

        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo getMouseScrollWheel = AccessTools.DeclaredMethod(
                typeof(ZInput),
                nameof(ZInput.GetMouseScrollWheel));
            MethodInfo clampFloat = AccessTools.DeclaredMethod(
                typeof(Mathf),
                nameof(Mathf.Clamp),
                new[] { typeof(float), typeof(float), typeof(float) });
            MethodInfo clampPortalMapMouseWheel = AccessTools.DeclaredMethod(
                typeof(PublicPortalMap),
                nameof(ClampPortalMapMouseWheel));

            int clampSearchRemaining = 0;
            bool foundMinimumClamp = false;
            bool foundMaximumClamp = false;
            bool replaced = false;
            foreach (CodeInstruction instruction in instructions)
            {
                if (!replaced && instruction.Calls(getMouseScrollWheel))
                {
                    clampSearchRemaining = MouseWheelClampSearchWindow;
                    foundMinimumClamp = false;
                    foundMaximumClamp = false;
                }
                else if (!replaced && clampSearchRemaining > 0)
                {
                    if (instruction.opcode == OpCodes.Ldc_R4 &&
                        instruction.operand is float constant)
                    {
                        foundMinimumClamp |= constant == -0.05f;
                        foundMaximumClamp |= constant == 0.05f;
                    }
                    else if (foundMinimumClamp &&
                             foundMaximumClamp &&
                             instruction.Calls(clampFloat))
                    {
                        instruction.operand = clampPortalMapMouseWheel;
                        replaced = true;
                    }

                    clampSearchRemaining--;
                }

                yield return instruction;
            }

            if (!replaced)
            {
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    "Could not patch the portal-map mouse-wheel zoom rate; using Valheim's default rate.");
            }
        }
    }

    [HarmonyPatch(typeof(TeleportWorldTrigger), "OnTriggerEnter")]
    private static class PortalTriggerPatch
    {
        private static bool Prefix(TeleportWorldTrigger __instance, Collider colliderIn)
        {
            TeleportWorld sourcePortal = __instance.GetComponentInParent<TeleportWorld>();
            if (!IsLocalPlayer(colliderIn))
            {
                return true;
            }

            if (sourcePortal == null)
            {
                return true;
            }

            if (IsMapSelectionPortal(sourcePortal))
            {
                PublicPortalMapController.Instance.Begin(
                    sourcePortal,
                    __instance.GetComponent<Collider>(),
                    colliderIn);
                return false;
            }

            // Every managed connected trip is server-authorized, including
            // free trips. Never fall back to Valheim's client-only path when
            // portal authority data is temporarily unavailable.
            PublicPortalTeleportService.TryBeginConnectedTeleport(sourcePortal);
            return false;
        }
    }

    [HarmonyPatch(typeof(TeleportWorld), "HaveTarget")]
    private static class HaveTargetPatch
    {
        private static bool Prefix(TeleportWorld __instance, ref bool __result)
        {
            if (!IsMapSelectionPortal(__instance))
            {
                return true;
            }

            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(TeleportWorld), "TargetFound")]
    private static class TargetFoundPatch
    {
        private static bool Prefix(TeleportWorld __instance, ref bool __result)
        {
            if (!IsMapSelectionPortal(__instance))
            {
                return true;
            }

            __result = true;
            return false;
        }
    }

    [HarmonyPatch(typeof(TeleportWorld), "UpdatePortal")]
    private static class PayableCargoWhirlingEffectPatch
    {
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(TeleportWorld __instance)
        {
            if (!PublicPortalTravelCost.IsEnabled ||
                __instance == null ||
                __instance.m_allowAllItems ||
                __instance.m_target_found == null)
            {
                return;
            }

            Transform proximityRoot = __instance.m_proximityRoot != null
                ? __instance.m_proximityRoot
                : __instance.transform;
            float activationRange = Mathf.Max(
                0f,
                __instance.m_activationRange);
            Player? nearbyPlayer = Player.GetClosestPlayer(
                proximityRoot.position,
                activationRange);
            if (nearbyPlayer != null &&
                PublicPortalTravelCost.TryGetCargoWeightUnits(
                    nearbyPlayer,
                    sourceAllowsAllItems: false,
                    out int cargoWeightUnits) &&
                cargoWeightUnits > 0 &&
                HasWhirlingTarget(__instance))
            {
                __instance.m_target_found.SetActive(true);
            }
        }
    }

    [HarmonyPatch(typeof(Minimap), nameof(Minimap.SetMapMode))]
    private static class CloseSelectionOnMapClosePatch
    {
        private static void Postfix(Minimap.MapMode mode)
        {
            PublicPortalMapController.Instance.OnMapModeChanged(mode);
        }
    }

    [HarmonyPatch(typeof(Minimap), nameof(Minimap.OnMapLeftClick))]
    private static class PortalMapLeftClickPatch
    {
        private static bool Prefix(Minimap __instance)
        {
            if (!PublicPortalMapController.Instance.IsSelecting)
            {
                return true;
            }

            PublicPortalMapController.Instance.HandleLeftClick(__instance);
            return false;
        }
    }

    // Valheim routes right-click and touch long-press through this operation.
    // Both toggle favorites while selecting; ordinary pin deletion passes through.
    [HarmonyPatch(typeof(Minimap), "RemovePinUnderPointer")]
    private static class PortalMapRightClickPatch
    {
        private static bool Prefix(Minimap __instance)
        {
            if (!PublicPortalMapController.Instance.IsSelecting)
            {
                return true;
            }

            PublicPortalMapController.Instance.HandleRightClick(__instance);
            return false;
        }
    }
}
