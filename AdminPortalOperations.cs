using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace PortalRules;

[HarmonyPatch]
internal static class AdminPortalOperations
{
    private const float AdminOperationCooldownSeconds = 0.25f;
    private const int MaximumPortalTagLength = 10;

    private sealed class RequiredGlobalKeyTextReceiver : TextReceiver
    {
        private readonly ZDOID _portalId;
        private readonly string _currentValue;

        internal RequiredGlobalKeyTextReceiver(
            ZDOID portalId,
            string currentValue)
        {
            _portalId = portalId;
            _currentValue = currentValue ?? "";
        }

        public string GetText()
        {
            return _currentValue;
        }

        public void SetText(string text)
        {
            RequestRequiredGlobalKeyChange(_portalId, text);
        }
    }

    private static readonly Dictionary<ZRpc, float> LastAdminOperationAt = new();
    private static readonly int AdminRemoveRayMask = LayerMask.GetMask(
        "Default",
        "static_solid",
        "Default_small",
        "piece",
        "piece_nonsolid",
        "terrain",
        "vehicle");

    internal static void Shutdown()
    {
        LastAdminOperationAt.Clear();
    }

    internal static void RegisterPeer(ZNet znet, ZNetPeer peer)
    {
        if (znet == null || peer?.m_rpc == null || !znet.IsServer())
        {
            return;
        }

        peer.m_rpc.Register<ZDOID, string, bool>(
            PublicPortalData.ChangeAdminPortalTagRpc,
            OnRemoteTagChange);
        peer.m_rpc.Register<ZDOID, string, bool>(
            PublicPortalData.ChangeAdminPortalRequiredGlobalKeyRpc,
            OnRemoteRequiredGlobalKeyChange);
        peer.m_rpc.Register<ZDOID, bool>(
            PublicPortalData.RemoveAdminPortalRpc,
            OnRemoteRemoval);
    }

    internal static void ForgetPeer(ZRpc rpc)
    {
        if (rpc != null)
        {
            LastAdminOperationAt.Remove(rpc);
        }
    }

    private static void RequestTagChange(TeleportWorld portal, string tag)
    {
        ZDO? zdo = PublicPortalKinds.GetPortalZdo(portal);
        if (zdo == null)
        {
            ShowOperationMessage(new PortalRulesMessage(
                "$sighsorry_portalrules_admin_portal_unavailable"));
            return;
        }

        if (ZNet.instance == null)
        {
            ShowOperationMessage(new PortalRulesMessage(
                "$sighsorry_portalrules_server_unavailable"));
            return;
        }

        if (ZNet.instance.IsServer())
        {
            TryApplyTagChange(
                zdo.m_uid,
                tag,
                requesterPeer: null,
                requesterIsAdmin: PortalRulesPlugin.IsAdmin,
                debugEnabled: Player.m_debugMode,
                out PortalRulesMessage message);
            ShowOperationMessage(message);
            return;
        }

        ZRpc? serverRpc = ZNet.instance.GetServerRPC();
        if (serverRpc == null)
        {
            ShowOperationMessage(new PortalRulesMessage(
                "$sighsorry_portalrules_server_connection_unavailable"));
            return;
        }

        serverRpc.Invoke(
            PublicPortalData.ChangeAdminPortalTagRpc,
            zdo.m_uid,
            tag ?? "",
            Player.m_debugMode);
    }

    private static void RequestRemoval(ZDO zdo)
    {
        if (ZNet.instance == null)
        {
            ShowOperationMessage(new PortalRulesMessage(
                "$sighsorry_portalrules_server_unavailable"));
            return;
        }

        if (ZNet.instance.IsServer())
        {
            TryApplyRemoval(
                zdo.m_uid,
                requesterPeer: null,
                requesterIsAdmin: PortalRulesPlugin.IsAdmin,
                debugEnabled: Player.m_debugMode,
                out PortalRulesMessage message);
            ShowOperationMessage(message);
            return;
        }

        ZRpc? serverRpc = ZNet.instance.GetServerRPC();
        if (serverRpc == null)
        {
            ShowOperationMessage(new PortalRulesMessage(
                "$sighsorry_portalrules_server_connection_unavailable"));
            return;
        }

        serverRpc.Invoke(
            PublicPortalData.RemoveAdminPortalRpc,
            zdo.m_uid,
            Player.m_debugMode);
    }

    internal static void OpenRequiredGlobalKeyInput(TeleportWorld portal)
    {
        ZDO? zdo = PublicPortalKinds.GetPortalZdo(portal);
        if (zdo == null)
        {
            ShowOperationMessage(new PortalRulesMessage(
                "$sighsorry_portalrules_admin_portal_unavailable"));
            return;
        }

        TextInput? textInput = TextInput.instance;
        if (textInput == null)
        {
            ShowOperationMessage(new PortalRulesMessage(
                "$sighsorry_portalrules_text_input_unavailable"));
            return;
        }

        textInput.RequestText(
            new RequiredGlobalKeyTextReceiver(
                zdo.m_uid,
                PublicPortalCatalog.GetEffectiveRequiredGlobalKey(zdo)),
            new PortalRulesMessage(
                "$sighsorry_portalrules_required_global_key_input_title")
                .Localize(),
            PublicPortalData.MaximumRequiredGlobalKeyLength);
    }

    private static void RequestRequiredGlobalKeyChange(
        ZDOID portalId,
        string requiredGlobalKey)
    {
        if (ZNet.instance == null || ZDOMan.instance == null)
        {
            ShowOperationMessage(new PortalRulesMessage(
                "$sighsorry_portalrules_server_unavailable"));
            return;
        }

        if (ZNet.instance.IsServer())
        {
            TryApplyRequiredGlobalKeyChange(
                portalId,
                requiredGlobalKey,
                requesterPeer: null,
                requesterIsAdmin: PortalRulesPlugin.IsAdmin,
                debugEnabled: Player.m_debugMode,
                out PortalRulesMessage message);
            ShowOperationMessage(message);
            return;
        }

        ZRpc? serverRpc = ZNet.instance.GetServerRPC();
        if (serverRpc == null)
        {
            ShowOperationMessage(new PortalRulesMessage(
                "$sighsorry_portalrules_server_connection_unavailable"));
            return;
        }

        serverRpc.Invoke(
            PublicPortalData.ChangeAdminPortalRequiredGlobalKeyRpc,
            portalId,
            requiredGlobalKey ?? "",
            Player.m_debugMode);
    }

    private static void OnRemoteTagChange(
        ZRpc rpc,
        ZDOID portalId,
        string tag,
        bool debugEnabled)
    {
        if (!TryResolveRemoteAdminOperation(
                rpc,
                out ZNetPeer? peer,
                out bool requesterIsAdmin,
                out PortalRulesMessage rejection))
        {
            if (peer != null)
            {
                PublicPortalServerPolicy.SendPolicyMessage(peer, rejection);
            }

            return;
        }

        bool success = TryApplyTagChange(
            portalId,
            tag,
            peer,
            requesterIsAdmin,
            debugEnabled,
            out PortalRulesMessage message);
        LogRejectedOperation(success, peer!, portalId, "tag change", message);
        PublicPortalServerPolicy.SendPolicyMessage(peer, message);
    }

    private static void OnRemoteRemoval(
        ZRpc rpc,
        ZDOID portalId,
        bool debugEnabled)
    {
        if (!TryResolveRemoteAdminOperation(
                rpc,
                out ZNetPeer? peer,
                out bool requesterIsAdmin,
                out PortalRulesMessage rejection))
        {
            if (peer != null)
            {
                PublicPortalServerPolicy.SendPolicyMessage(peer, rejection);
            }

            return;
        }

        bool success = TryApplyRemoval(
            portalId,
            peer,
            requesterIsAdmin,
            debugEnabled,
            out PortalRulesMessage message);
        LogRejectedOperation(success, peer!, portalId, "removal", message);
        PublicPortalServerPolicy.SendPolicyMessage(peer, message);
    }

    private static void OnRemoteRequiredGlobalKeyChange(
        ZRpc rpc,
        ZDOID portalId,
        string requiredGlobalKey,
        bool debugEnabled)
    {
        if (!TryResolveRemoteAdminOperation(
                rpc,
                out ZNetPeer? peer,
                out bool requesterIsAdmin,
                out PortalRulesMessage rejection))
        {
            if (peer != null)
            {
                PublicPortalServerPolicy.SendPolicyMessage(peer, rejection);
            }

            return;
        }

        bool success = TryApplyRequiredGlobalKeyChange(
            portalId,
            requiredGlobalKey,
            peer,
            requesterIsAdmin,
            debugEnabled,
            out PortalRulesMessage message);
        LogRejectedOperation(
            success,
            peer!,
            portalId,
            "Required GlobalKey change",
            message);
        PublicPortalServerPolicy.SendPolicyMessage(peer, message);
    }

    private static bool TryResolveRemoteAdminOperation(
        ZRpc rpc,
        out ZNetPeer? peer,
        out bool requesterIsAdmin,
        out PortalRulesMessage rejection)
    {
        peer = null;
        requesterIsAdmin = false;
        rejection = new PortalRulesMessage(
            "$sighsorry_portalrules_admin_request_unauthenticated");
        ZNet? znet = ZNet.instance;
        if (znet == null || !znet.IsServer())
        {
            return false;
        }

        peer = PublicPortalData.FindPeer(znet, rpc);
        if (peer == null || !peer.IsReady())
        {
            return false;
        }

        float now = Time.realtimeSinceStartup;
        if (LastAdminOperationAt.TryGetValue(rpc, out float lastRequestAt) &&
            now - lastRequestAt < AdminOperationCooldownSeconds)
        {
            rejection = new PortalRulesMessage(
                "$sighsorry_portalrules_admin_change_rate_limited");
            return false;
        }

        LastAdminOperationAt[rpc] = now;
        requesterIsAdmin = PublicPortalData.IsPeerAdmin(znet, peer);
        return true;
    }

    private static bool TryApplyTagChange(
        ZDOID portalId,
        string tag,
        ZNetPeer? requesterPeer,
        bool requesterIsAdmin,
        bool debugEnabled,
        out PortalRulesMessage message)
    {
        if (!TryGetAuthorizedAdminPortal(
                portalId,
                requesterPeer,
                requesterIsAdmin,
                debugEnabled,
                out ZDO zdo,
                out message))
        {
            return false;
        }

        tag ??= "";
        if (tag.Length > MaximumPortalTagLength)
        {
            message = new PortalRulesMessage(
                "$sighsorry_portalrules_admin_tag_length_limit",
                MaximumPortalTagLength.ToString(CultureInfo.InvariantCulture));
            return false;
        }

        if (!PublicPortalCatalog.SetAuthoritativeAdminPortalTag(
                zdo,
                tag))
        {
            message = new PortalRulesMessage(
                "$sighsorry_portalrules_admin_tag_update_failed");
            return false;
        }

        message = new PortalRulesMessage(
            "$sighsorry_portalrules_admin_tag_updated");
        return true;
    }

    private static bool TryApplyRemoval(
        ZDOID portalId,
        ZNetPeer? requesterPeer,
        bool requesterIsAdmin,
        bool debugEnabled,
        out PortalRulesMessage message)
    {
        if (!TryGetAuthorizedAdminPortal(
                portalId,
                requesterPeer,
                requesterIsAdmin,
                debugEnabled,
                out ZDO zdo,
                out message))
        {
            return false;
        }

        if (!PublicPortalServerPolicy.QueueAdminPortalRemoval(zdo))
        {
            message = new PortalRulesMessage(
                "$sighsorry_portalrules_admin_portal_removal_unavailable");
            return false;
        }

        message = new PortalRulesMessage(
            "$sighsorry_portalrules_admin_portal_dismantling_queued");
        return true;
    }

    private static bool TryApplyRequiredGlobalKeyChange(
        ZDOID portalId,
        string requiredGlobalKey,
        ZNetPeer? requesterPeer,
        bool requesterIsAdmin,
        bool debugEnabled,
        out PortalRulesMessage message)
    {
        if (!TryGetAuthorizedAdminPortal(
                portalId,
                requesterPeer,
                requesterIsAdmin,
                debugEnabled,
                out ZDO zdo,
                out message))
        {
            return false;
        }

        if (!TryNormalizeRequiredGlobalKeyMessage(
                requiredGlobalKey,
                out string normalizedRequiredGlobalKey,
                out message))
        {
            return false;
        }

        if (normalizedRequiredGlobalKey.Length != 0 &&
            RequiredGlobalKeyAccess.YouAreNotWorthyInstalled)
        {
            RequiredGlobalKeyQueryResult queryResult = requesterPeer != null
                ? RequiredGlobalKeyAccess.QueryPeer(
                    requesterPeer,
                    normalizedRequiredGlobalKey)
                : RequiredGlobalKeyAccess.QueryLocal(
                    normalizedRequiredGlobalKey);
            if (queryResult == RequiredGlobalKeyQueryResult.Invalid)
            {
                message = new PortalRulesMessage(
                    "$sighsorry_portalrules_ynw_required_global_key_rejected");
                return false;
            }

            if (queryResult == RequiredGlobalKeyQueryResult.Unavailable)
            {
                message = new PortalRulesMessage(
                    "$sighsorry_portalrules_ynw_key_api_unavailable");
                return false;
            }
        }

        if (!PublicPortalCatalog.SetAuthoritativeAdminPortalRequiredGlobalKey(
                zdo,
                normalizedRequiredGlobalKey))
        {
            message = new PortalRulesMessage(
                "$sighsorry_portalrules_admin_required_global_key_update_failed");
            return false;
        }

        message = normalizedRequiredGlobalKey.Length == 0
            ? new PortalRulesMessage(
                "$sighsorry_portalrules_admin_required_global_key_cleared")
            : new PortalRulesMessage(
                "$sighsorry_portalrules_admin_required_global_key_updated");
        return true;
    }

    private static bool TryNormalizeRequiredGlobalKeyMessage(
        string? requiredGlobalKey,
        out string normalizedRequiredGlobalKey,
        out PortalRulesMessage message)
    {
        if (PublicPortalData.TryNormalizeRequiredGlobalKey(
                requiredGlobalKey,
                out normalizedRequiredGlobalKey,
                out RequiredGlobalKeyValidationFailure failure))
        {
            message = PortalRulesMessage.Empty;
            return true;
        }

        switch (failure)
        {
            case RequiredGlobalKeyValidationFailure.TooLong:
                message = new PortalRulesMessage(
                    "$sighsorry_portalrules_required_global_key_length_limit",
                    PublicPortalData.MaximumRequiredGlobalKeyLength.ToString(
                        CultureInfo.InvariantCulture));
                break;
            case RequiredGlobalKeyValidationFailure.ControlCharacters:
                message = new PortalRulesMessage(
                    "$sighsorry_portalrules_required_global_key_control_characters");
                break;
            case RequiredGlobalKeyValidationFailure.NumericKey:
                message = new PortalRulesMessage(
                    "$sighsorry_portalrules_required_global_key_numeric_invalid");
                break;
            case RequiredGlobalKeyValidationFailure.ReservedKey:
                message = new PortalRulesMessage(
                    "$sighsorry_portalrules_required_global_key_not_allowed");
                break;
            default:
                message = new PortalRulesMessage(
                    "$sighsorry_portalrules_admin_required_global_key_update_failed");
                break;
        }

        return false;
    }

    private static bool TryGetAuthorizedAdminPortal(
        ZDOID portalId,
        ZNetPeer? requesterPeer,
        bool requesterIsAdmin,
        bool debugEnabled,
        out ZDO zdo,
        out PortalRulesMessage message)
    {
        zdo = null!;
        if (ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            ZDOMan.instance == null)
        {
            message = new PortalRulesMessage(
                "$sighsorry_portalrules_server_not_ready");
            return false;
        }

        if (!requesterIsAdmin || !debugEnabled)
        {
            message = new PortalRulesMessage(
                "$sighsorry_portalrules_admin_portal_controls_require_admin_debug");
            return false;
        }

        ZDO? portal = ZDOMan.instance.GetZDO(portalId);
        if (portal == null ||
            !portal.IsValid() ||
            !PublicPortalInteraction.IsAdminPortal(portal))
        {
            message = new PortalRulesMessage(
                "$sighsorry_portalrules_requested_admin_portal_unavailable");
            return false;
        }

        if (!PublicPortalInteraction.TryValidatePortalInteraction(
                portal,
                requesterPeer,
                out message))
        {
            return false;
        }

        bool hasIdentity = requesterPeer != null
            ? PublicPortalData.TryGetPeerOwner(requesterPeer, out _)
            : PublicPortalData.LocalOwner().IsValid;
        if (!hasIdentity)
        {
            message = new PortalRulesMessage(
                "$sighsorry_portalrules_platform_identity_unverified");
            return false;
        }

        zdo = portal;
        return true;
    }

    private static void LogRejectedOperation(
        bool success,
        ZNetPeer peer,
        ZDOID portalId,
        string operation,
        PortalRulesMessage message)
    {
        if (!success && PublicPortalData.TryGetPeerOwner(peer, out PortalOwner requester))
        {
            string messageDetail = message.IsEmpty
                ? "<empty>"
                : message.Arguments.Length == 0
                    ? message.Token
                    : $"{message.Token} [{string.Join(", ", message.Arguments)}]";
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                $"Rejected admin portal {operation} from {requester.Id} for {portalId}: {messageDetail}");
        }
    }

    private static void ShowOperationMessage(PortalRulesMessage message)
    {
        string localizedMessage = message.Localize();
        if (!string.IsNullOrWhiteSpace(localizedMessage))
        {
            Player.m_localPlayer?.Message(
                MessageHud.MessageType.Center,
                localizedMessage);
        }
    }

    [HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.SetText))]
    private static class AdminPortalSetTextPatch
    {
        private static bool Prefix(TeleportWorld __instance, string text)
        {
            ZDO? zdo = PublicPortalKinds.GetPortalZdo(__instance);
            if (zdo == null ||
                !PublicPortalInteraction.IsAdminPortal(zdo))
            {
                return true;
            }

            RequestTagChange(__instance, text);
            return false;
        }
    }

    [HarmonyPatch(
        typeof(TeleportWorld),
        "RPC_SetTag",
        typeof(long),
        typeof(string),
        typeof(string))]
    private static class BlockVanillaAdminPortalTagRpcPatch
    {
        private static bool Prefix(TeleportWorld __instance)
        {
            ZDO? zdo = PublicPortalKinds.GetPortalZdo(__instance);
            return zdo == null ||
                   !PublicPortalInteraction.IsAdminPortal(zdo);
        }
    }

    [HarmonyPatch(typeof(Player), "RemovePiece")]
    private static class AdminPortalRemovalPatch
    {
        private static bool Prefix(Player __instance, ref bool __result)
        {
            if (__instance == null ||
                __instance != Player.m_localPlayer ||
                !TryGetAimedAdminPortal(__instance, out ZDO zdo))
            {
                return true;
            }

            if (!PortalRulesPlugin.HasAdminDebugAccess)
            {
                ShowOperationMessage(new PortalRulesMessage(
                    "$sighsorry_portalrules_admin_portal_dismantling_requires_admin_debug"));
                __result = false;
                return false;
            }

            RequestRemoval(zdo);
            // Suppress vanilla destruction and keep the authenticated admin
            // removal RPC as the sole dismantling path.
            __result = false;
            return false;
        }

        private static bool TryGetAimedAdminPortal(Player player, out ZDO zdo)
        {
            zdo = null!;
            if (GameCamera.instance == null ||
                !Physics.Raycast(
                    GameCamera.instance.transform.position,
                    GameCamera.instance.transform.forward,
                    out RaycastHit hit,
                    50f,
                    AdminRemoveRayMask,
                    QueryTriggerInteraction.Collide) ||
                Vector3.Distance(hit.point, player.GetEyePoint()) >=
                player.m_maxPlaceDistance)
            {
                return false;
            }

            TeleportWorld? portal = hit.collider.GetComponentInParent<TeleportWorld>();
            ZDO? portalZdo = portal != null
                ? PublicPortalKinds.GetPortalZdo(portal)
                : null;
            if (portalZdo == null ||
                !PublicPortalInteraction.IsAdminPortal(portalZdo))
            {
                return false;
            }

            zdo = portalZdo;
            return true;
        }
    }
}
