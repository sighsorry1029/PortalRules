using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using UnityEngine;

namespace PortalRules;

[HarmonyPatch]
internal static partial class PublicPortalInteraction
{
    private const int CycleAccessModeRequest = -1;
    private const float AccessModeRequestCooldownSeconds = 1f;
    private const float MaximumPortalInteractionDistance = 8f;
    private static readonly Dictionary<ZRpc, float> LastAccessModeRequestAt = new();

    internal static void Shutdown()
    {
        LastAccessModeRequestAt.Clear();
    }

    internal static bool IsAdminPortal(ZDO zdo)
    {
        if (zdo == null)
        {
            return false;
        }

        if (PublicPortalKinds.IsAdminPortalPrefab(zdo.GetPrefab()) ||
            PublicPortalCatalog.IsAuthoritativeAdminPortal(zdo.m_uid))
        {
            return true;
        }

        return PublicPortalCatalog.TryGetClientEntry(
                   zdo.m_uid,
                   out PublicPortalCatalogEntry entry) &&
               PublicPortalKinds.IsAdminPortalPrefab(entry.PrefabHash);
    }

    internal static string AccessModeLabel(PublicPortalAccessMode mode)
    {
        return PortalRulesLocalization.Translate(
            PortalRulesLocalization.AccessModeToken(mode));
    }

    private static PublicPortalAccessMode NextAccessModeCandidate(
        PublicPortalAccessMode mode,
        bool isAdminPortal)
    {
        if (isAdminPortal)
        {
            return mode switch
            {
                PublicPortalAccessMode.Admin => PublicPortalAccessMode.Public,
                PublicPortalAccessMode.Public => PublicPortalAccessMode.Tagged,
                PublicPortalAccessMode.Tagged => PublicPortalAccessMode.Admin,
                _ => PublicPortalAccessMode.Public
            };
        }

        return mode switch
        {
            PublicPortalAccessMode.Personal => PublicPortalAccessMode.Admin,
            PublicPortalAccessMode.Admin => PublicPortalAccessMode.Public,
            PublicPortalAccessMode.Public => PublicPortalAccessMode.Invite,
            PublicPortalAccessMode.Invite => PublicPortalAccessMode.Clan,
            PublicPortalAccessMode.Clan => PublicPortalAccessMode.Tagged,
            PublicPortalAccessMode.Tagged => PublicPortalAccessMode.Personal,
            _ => PublicPortalAccessMode.Personal
        };
    }

    private static void RequestAccessModeChange(ZDO zdo)
    {
        if (ZNet.instance == null)
        {
            Player.m_localPlayer?.Message(
                MessageHud.MessageType.Center,
                PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_access_change_server_unavailable"));
            return;
        }

        if (ZNet.instance.IsServer())
        {
            bool success = TryApplyAccessModeChange(
                zdo.m_uid,
                CycleAccessModeRequest,
                requesterPeer: null,
                requesterIsAdmin: PortalRulesPlugin.IsAdmin,
                requesterDebugEnabled: Player.m_debugMode,
                out PublicPortalAccessMode appliedMode,
                out PortalRulesMessage message);
            ShowAccessModeResult(success, appliedMode, message);
            return;
        }

        ZRpc? serverRpc = ZNet.instance.GetServerRPC();
        if (serverRpc == null)
        {
            Player.m_localPlayer?.Message(
                MessageHud.MessageType.Center,
                PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_access_change_connection_unavailable"));
            return;
        }

        serverRpc.Invoke(
            PublicPortalData.ChangeAccessModeRpc,
            zdo.m_uid,
            CycleAccessModeRequest,
            Player.m_debugMode);
    }

    private static void OnRemoteAccessModeChange(
        ZRpc rpc,
        ZDOID portalId,
        int accessMode,
        bool debugEnabled)
    {
        ZNet? znet = ZNet.instance;
        if (znet == null || !znet.IsServer())
        {
            return;
        }

        ZNetPeer? peer = PublicPortalData.FindPeer(znet, rpc);
        if (peer == null || !peer.IsReady() || peer.m_characterID.IsNone())
        {
            SendAccessModeResult(
                rpc,
                portalId,
                success: false,
                PublicPortalAccessMode.Personal,
                new PortalRulesMessage(
                    "$sighsorry_portalrules_access_change_unauthenticated"));
            return;
        }

        if (!PublicPortalData.TryGetPeerOwner(peer, out PortalOwner requester))
        {
            SendAccessModeResult(
                rpc,
                portalId,
                success: false,
                PublicPortalAccessMode.Personal,
                new PortalRulesMessage(
                    "$sighsorry_portalrules_platform_identity_unverified"));
            return;
        }

        // Authenticate the active character before consuming the user-facing
        // access-mode cooldown. A transient dedicated-server scene miss must
        // report its real cause instead of turning an immediate retry into a
        // misleading rate-limit failure.
        if (!PublicPortalData.TryGetAuthenticatedPeerCharacterPosition(
                peer,
                out _))
        {
            SendAccessModeResult(
                rpc,
                portalId,
                success: false,
                PublicPortalAccessMode.Personal,
                new PortalRulesMessage(
                    "$sighsorry_portalrules_active_character_unverified"));
            return;
        }

        float now = Time.realtimeSinceStartup;
        if (LastAccessModeRequestAt.TryGetValue(rpc, out float lastRequestAt) &&
            now - lastRequestAt < AccessModeRequestCooldownSeconds)
        {
            SendAccessModeResult(
                rpc,
                portalId,
                success: false,
                PublicPortalAccessMode.Personal,
                new PortalRulesMessage(
                    "$sighsorry_portalrules_access_change_rate_limited"));
            return;
        }

        LastAccessModeRequestAt[rpc] = now;
        bool requesterIsAdmin = PublicPortalData.IsPeerAdmin(znet, peer);
        bool success = TryApplyAccessModeChange(
            portalId,
            accessMode,
            peer,
            requesterIsAdmin,
            debugEnabled,
            out PublicPortalAccessMode appliedMode,
            out PortalRulesMessage message);

        if (!success)
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                $"Rejected portal access request from {requester.Id} for {portalId}: {message.Token}");
        }

        SendAccessModeResult(rpc, portalId, success, appliedMode, message);
    }

    private static void OnAccessModeChangeResult(
        ZRpc rpc,
        ZPackage package)
    {
        if (ZNet.instance == null ||
            ZNet.instance.IsServer() ||
            !ReferenceEquals(rpc, ZNet.instance.GetServerRPC()))
        {
            return;
        }

        ZDOID portalId;
        bool success;
        int rawAccessMode;
        PortalRulesMessage message;
        try
        {
            portalId = package.ReadZDOID();
            success = package.ReadBool();
            rawAccessMode = package.ReadInt();
            if (!PortalRulesMessage.TryRead(package, out message))
            {
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    "Rejected malformed portal access result from the server.");
                return;
            }
        }
        catch (Exception exception)
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                $"Rejected malformed portal access result from the server: {exception.Message}");
            return;
        }

        if (portalId.IsNone())
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                "Rejected portal access result without a portal ID.");
            return;
        }

        if (!Enum.IsDefined(typeof(PublicPortalAccessMode), rawAccessMode))
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                $"Rejected portal access result with unknown mode {rawAccessMode}.");
            return;
        }

        PublicPortalAccessMode accessMode =
            (PublicPortalAccessMode)rawAccessMode;

        if (success)
        {
            PublicPortalCatalog.RequestRefresh(force: true);
        }

        ShowAccessModeResult(success, accessMode, message);
    }

    private static bool TryApplyAccessModeChange(
        ZDOID portalId,
        int rawAccessMode,
        ZNetPeer? requesterPeer,
        bool requesterIsAdmin,
        bool requesterDebugEnabled,
        out PublicPortalAccessMode appliedMode,
        out PortalRulesMessage message)
    {
        appliedMode = PublicPortalAccessMode.Personal;
        if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null)
        {
            message = new PortalRulesMessage(
                "$sighsorry_portalrules_server_not_ready");
            return false;
        }

        if (rawAccessMode != CycleAccessModeRequest)
        {
            message = new PortalRulesMessage(
                "$sighsorry_portalrules_access_change_invalid_operation");
            return false;
        }
        ZDO? zdo = ZDOMan.instance.GetZDO(portalId);
        if (zdo == null ||
            !zdo.IsValid() ||
            !PublicPortalKinds.IsRegisteredPortal(zdo))
        {
            message = new PortalRulesMessage(
                "$sighsorry_portalrules_destination_not_managed_portal");
            return false;
        }

        if (!TryValidatePortalInteraction(zdo, requesterPeer, out message))
        {
            return false;
        }

        if (!PublicPortalCatalog.TryGetAuthoritativeAccess(
                portalId,
                out PublicPortalAccessMode currentMode,
                out _))
        {
            PublicPortalCatalog.ObservePortal(zdo);
            if (!PublicPortalCatalog.TryGetAuthoritativeAccess(
                    portalId,
                    out currentMode,
                    out _))
            {
                message = new PortalRulesMessage(
                    "$sighsorry_portalrules_portal_missing_from_catalog");
                return false;
            }
        }

        bool isAdminPortal = IsAdminPortal(zdo);
        if (isAdminPortal && (!requesterIsAdmin || !requesterDebugEnabled))
        {
            message = new PortalRulesMessage(
                "$sighsorry_portalrules_admin_portal_change_requires_admin_debug");
            return false;
        }

        PortalBuilder builder = new("", "");
        if (!isAdminPortal &&
            (!PublicPortalCatalog.TryGetAuthoritativeBuilder(
                 portalId,
                 out builder) ||
             !builder.IsValid))
        {
            message = new PortalRulesMessage(
                "$sighsorry_portalrules_portal_builder_unverified");
            return false;
        }

        appliedMode = currentMode;
        string requesterSteamId;
        bool requesterIdentityResolved = requesterPeer != null
            ? PublicPortalData.TryGetPeerAccountId(
                requesterPeer,
                out requesterSteamId)
            : PublicPortalData.TryGetLocalAccountId(
                out requesterSteamId);
        bool requesterIsBuilder =
            requesterIdentityResolved &&
            builder.IsValid &&
            string.Equals(
                requesterSteamId,
                builder.AccountId,
                StringComparison.Ordinal);

        if (!isAdminPortal &&
            currentMode == PublicPortalAccessMode.Invite &&
            !requesterIsBuilder)
        {
            message = new PortalRulesMessage(
                "$sighsorry_portalrules_invite_access_change_builder_only");
            return false;
        }

        if (!isAdminPortal &&
            !requesterIsBuilder &&
            !(requesterIsAdmin && requesterDebugEnabled) &&
            !ClanPortalAccess.IsRequesterInBuilderPrimaryClan(
                builder,
                requesterPeer))
        {
            message = new PortalRulesMessage(
                "$sighsorry_portalrules_access_cannot_be_changed");
            return false;
        }

        if (!TryResolveNextAccessMode(
                currentMode,
                isAdminPortal,
                requesterIsAdmin,
                requesterIsBuilder,
                builder,
                out PublicPortalAccessMode requestedMode,
                out string authorizedClanId,
                out string authorizedClanName))
        {
            message = new PortalRulesMessage(
                "$sighsorry_portalrules_no_eligible_access_mode");
            return false;
        }

        if (!PublicPortalCatalog.SetAuthoritativeAccess(
                zdo,
                requestedMode,
                authorizedClanId))
        {
            message = new PortalRulesMessage(
                "$sighsorry_portalrules_catalog_update_failed");
            return false;
        }

        appliedMode = requestedMode;
        if (requestedMode != currentMode)
        {
            PublicPortalModeChangeEffects.Broadcast(zdo.m_uid);
            PublicPortalTaggedConnections.RefreshConnections();
        }

        string accessModeToken =
            PortalRulesLocalization.AccessModeToken(requestedMode);
        if (requestedMode == PublicPortalAccessMode.Clan &&
            !string.IsNullOrWhiteSpace(authorizedClanName))
        {
            message = new PortalRulesMessage(
                "$sighsorry_portalrules_access_changed_clan",
                accessModeToken,
                authorizedClanName);
        }
        else if (!isAdminPortal &&
                 requestedMode == PublicPortalAccessMode.Public &&
                 PublicPortalCatalog.TryGetTemporaryPublicRemainingSeconds(
                     zdo.m_uid,
                     out int remainingSeconds))
        {
            message = new PortalRulesMessage(
                "$sighsorry_portalrules_access_changed_temporary",
                accessModeToken,
                remainingSeconds.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            message = new PortalRulesMessage(
                "$sighsorry_portalrules_access_changed",
                accessModeToken);
        }
        return true;
    }

    private static bool TryResolveNextAccessMode(
        PublicPortalAccessMode currentMode,
        bool isAdminPortal,
        bool requesterIsAdmin,
        bool requesterIsBuilder,
        PortalBuilder builder,
        out PublicPortalAccessMode nextMode,
        out string authorizedClanId,
        out string authorizedClanName)
    {
        nextMode = currentMode;
        authorizedClanId = "";
        authorizedClanName = "";

        int candidateCount = isAdminPortal ? 3 : 6;
        for (int i = 0; i < candidateCount; i++)
        {
            authorizedClanId = "";
            authorizedClanName = "";
            PublicPortalAccessMode candidate = NextAccessModeCandidate(
                nextMode,
                isAdminPortal);
            nextMode = candidate;

            if (candidate == PublicPortalAccessMode.Admin &&
                !requesterIsAdmin)
            {
                continue;
            }

            if (candidate == PublicPortalAccessMode.Invite)
            {
                if (!requesterIsBuilder ||
                    !PublicPortalServerPolicy.TryGetInviteQuotaState(
                        builder.AccountId,
                        out int currentCount,
                        out int effectiveLimit) ||
                    (effectiveLimit >= 0 && currentCount >= effectiveLimit))
                {
                    continue;
                }
            }

            if (!isAdminPortal &&
                candidate is PublicPortalAccessMode.Clan or
                    PublicPortalAccessMode.Tagged)
            {
                PortalClanMembership membership = default;
                bool hasPrimaryClan =
                    ClanPortalAccess.IsServerRegistryAvailable &&
                    ClanPortalAccess.TryResolveBuilderMembership(
                        builder,
                        out membership) &&
                    membership.HasPrimaryClan;
                if (candidate == PublicPortalAccessMode.Clan &&
                    (!hasPrimaryClan ||
                     !PublicPortalServerPolicy.TryGetClanQuotaState(
                         membership.PrimaryClanId,
                         out int currentCount,
                         out int effectiveLimit) ||
                     effectiveLimit >= 0 && currentCount >= effectiveLimit))
                {
                    continue;
                }

                if (hasPrimaryClan)
                {
                    authorizedClanId = membership.PrimaryClanId;
                    authorizedClanName = membership.PrimaryClanName;
                }
            }

            return true;
        }

        nextMode = currentMode;
        authorizedClanId = "";
        authorizedClanName = "";
        return false;
    }

    internal static bool TryValidatePortalInteraction(
        ZDO zdo,
        ZNetPeer? requesterPeer,
        out PortalRulesMessage message)
    {
        message = new PortalRulesMessage(
            "$sighsorry_portalrules_move_closer_to_portal");
        if (zdo == null ||
            !zdo.IsValid() ||
            ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            ZDOMan.instance == null ||
            !PublicPortalKinds.IsRegisteredPortal(zdo))
        {
            message = new PortalRulesMessage(
                "$sighsorry_portalrules_requested_portal_unavailable");
            return false;
        }

        Vector3 playerPosition;
        if (requesterPeer != null)
        {
            if (!PublicPortalData.TryGetAuthenticatedPeerCharacterPosition(
                    requesterPeer,
                    out playerPosition))
            {
                message = new PortalRulesMessage(
                    "$sighsorry_portalrules_active_character_unverified");
                return false;
            }
        }
        else
        {
            Player? localPlayer = Player.m_localPlayer;
            if (localPlayer == null)
            {
                message = new PortalRulesMessage(
                    "$sighsorry_portalrules_local_player_unverified");
                return false;
            }

            playerPosition = localPlayer.transform.position;
        }

        Vector3 portalPosition = zdo.GetPosition();
        if (!IsFinite(playerPosition) ||
            !IsFinite(portalPosition) ||
            Vector3.Distance(playerPosition, portalPosition) >
            MaximumPortalInteractionDistance)
        {
            return false;
        }

        message = PortalRulesMessage.Empty;
        return true;
    }

    private static bool IsFinite(Vector3 value)
    {
        return !float.IsNaN(value.x) &&
               !float.IsInfinity(value.x) &&
               !float.IsNaN(value.y) &&
               !float.IsInfinity(value.y) &&
               !float.IsNaN(value.z) &&
               !float.IsInfinity(value.z);
    }

    private static void SendAccessModeResult(
        ZRpc rpc,
        ZDOID portalId,
        bool success,
        PublicPortalAccessMode accessMode,
        PortalRulesMessage message)
    {
        ZPackage package = new();
        package.Write(portalId);
        package.Write(success);
        package.Write((int)accessMode);
        message.Write(package);
        rpc.Invoke(PublicPortalData.ChangeAccessModeResultRpc, package);
    }

    private static void ShowAccessModeResult(
        bool success,
        PublicPortalAccessMode accessMode,
        PortalRulesMessage message)
    {
        string text = message.IsEmpty
            ? success
                ? PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_access_changed",
                    AccessModeLabel(accessMode))
                : PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_access_change_failed")
            : message.Localize();
        Player.m_localPlayer?.Message(MessageHud.MessageType.Center, text);
    }

    [HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.GetHoverText))]
    private static class HoverTextPatch
    {
        private static void Postfix(TeleportWorld __instance, ref string __result)
        {
            if (__instance == null)
            {
                return;
            }

            string allItemsHeader = __instance.m_allowAllItems
                ? "<color=#B8EBB8>" + PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_hover_all_items_teleportable") +
                  "</color>\n"
                : "";
            ZDO? zdo = PublicPortalKinds.GetPortalZdo(__instance);
            if (zdo == null)
            {
                __result = allItemsHeader + __result;
                return;
            }

            PublicPortalAccessMode mode = PublicPortalCatalog.GetEffectiveAccessMode(zdo);
            string connectedLabel = Localization.instance != null
                ? Localization.instance.Localize("$piece_portal_connected")
                : "$piece_portal_connected";
            string unconnectedLabel = Localization.instance != null
                ? Localization.instance.Localize("$piece_portal_unconnected")
                : "$piece_portal_unconnected";
            string connectionStatus = mode == PublicPortalAccessMode.Tagged
                ? HasMutualPortalConnection(zdo) ? connectedLabel : unconnectedLabel
                : "";
            string portalTagHeader = "$piece_portal $piece_portal_tag:\"" +
                                     __instance.GetText().RemoveRichTextTags() + "\"";
            if (Localization.instance != null)
            {
                portalTagHeader = Localization.instance.Localize(portalTagHeader);
            }

            __result = allItemsHeader + SetConnectionStatusAfterPortalTag(
                __result,
                portalTagHeader,
                connectedLabel,
                unconnectedLabel,
                connectionStatus);
            if (PublicPortalAccess.CanEditPortal(zdo))
            {
                string useKey = Localization.instance != null ? Localization.instance.Localize("$KEY_Use") : "$KEY_Use";
                __result += "\n" + PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_hover_change_access_mode",
                    PublicPortalConfig.ToggleAccessKey.Value.ToString(),
                    useKey,
                    AccessModeLabel(mode));
            }

            if (!IsAdminPortal(zdo) &&
                mode == PublicPortalAccessMode.Public &&
                TryGetTemporaryPublicRemainingSeconds(
                    zdo,
                    out int publicRemainingSeconds))
            {
                __result += "\n" + PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_hover_temporary_public_remaining",
                    publicRemainingSeconds.ToString());
            }

            if (mode == PublicPortalAccessMode.Invite)
            {
                string inviteInfo = PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_hover_invite_description");
                string cooldownInfo = "";
                if (PublicPortalCatalog.TryGetClientEntry(
                        zdo.m_uid,
                        out PublicPortalCatalogEntry entry))
                {
                    inviteInfo = PortalRulesLocalization.Translate(
                        "$sighsorry_portalrules_hover_invite_description_with_quota",
                        entry.ModeCurrent.ToString(),
                        FormatPortalLimit(entry.ModeLimit));
                    long cooldownRemainingSeconds =
                        InviteTravelCooldownStore.GetRemainingSeconds(
                            entry.InviteDepartureCooldownUntilUtc);
                    if (cooldownRemainingSeconds > 0L)
                    {
                        cooldownInfo = PortalRulesLocalization.Translate(
                            "$sighsorry_portalrules_invite_departure_cooldown",
                            InviteTravelCooldownStore.FormatRemaining(
                                cooldownRemainingSeconds));
                    }
                }

                if (!string.IsNullOrEmpty(cooldownInfo))
                {
                    __result += $"\n{cooldownInfo}";
                }

                __result += $"\n{inviteInfo}";
            }
            else if (mode == PublicPortalAccessMode.Clan &&
                     PublicPortalCatalog.TryGetClientEntry(
                         zdo.m_uid,
                         out PublicPortalCatalogEntry entry))
            {
                __result += "\n" + PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_hover_clan_quota",
                    entry.ModeCurrent.ToString(),
                    FormatPortalLimit(entry.ModeLimit));
            }

            if (IsAdminPortal(zdo) &&
                PortalRulesPlugin.HasAdminDebugAccess)
            {
                string useKey = Localization.instance != null
                    ? Localization.instance.Localize("$KEY_Use")
                    : "$KEY_Use";
                string requiredGlobalKey =
                    PublicPortalCatalog.GetEffectiveRequiredGlobalKey(zdo);
                string requiredGlobalKeyLabel =
                    string.IsNullOrEmpty(requiredGlobalKey)
                        ? PortalRulesLocalization.Translate(
                            "$sighsorry_portalrules_none")
                        : requiredGlobalKey.RemoveRichTextTags();
                __result += "\n" + PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_hover_required_global_key",
                    PortalRulesLocalization.Translate(
                        "$sighsorry_portalrules_key_alt"),
                    useKey,
                    requiredGlobalKeyLabel);
            }
        }
    }

    private static bool HasMutualPortalConnection(ZDO source)
    {
        if (!source.IsValid() || ZDOMan.instance == null)
        {
            return false;
        }

        ZDOID targetId = source.GetConnectionZDOID(
            ZDOExtraData.ConnectionType.Portal);
        if (targetId.IsNone() || targetId == source.m_uid)
        {
            return false;
        }

        ZDO? target = ZDOMan.instance.GetZDO(targetId);
        return target != null && target.IsValid() &&
               target.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) ==
               source.m_uid;
    }

    private static string SetConnectionStatusAfterPortalTag(
        string hoverText,
        string portalTagHeader,
        string connectedLabel,
        string unconnectedLabel,
        string connectionStatus)
    {
        if (string.IsNullOrEmpty(hoverText) ||
            string.IsNullOrEmpty(portalTagHeader) ||
            !hoverText.StartsWith(portalTagHeader, StringComparison.Ordinal))
        {
            return hoverText;
        }

        // The actual sanitized, localized tag fixes the boundary even when a
        // saved or externally supplied tag contains quotes or line breaks.
        int suffixStart = portalTagHeader.Length;
        string connectedSuffix = " [" + connectedLabel + "]";
        string unconnectedSuffix = " [" + unconnectedLabel + "]";
        int remainingLength = hoverText.Length - suffixStart;
        bool hasConnectedSuffix = remainingLength >= connectedSuffix.Length &&
                                  string.CompareOrdinal(
                                      hoverText, suffixStart,
                                      connectedSuffix, 0,
                                      connectedSuffix.Length) == 0;
        int suffixLength = hasConnectedSuffix
            ? connectedSuffix.Length
            : remainingLength >= unconnectedSuffix.Length &&
              string.CompareOrdinal(
                  hoverText, suffixStart,
                  unconnectedSuffix, 0,
                  unconnectedSuffix.Length) == 0
                ? unconnectedSuffix.Length
                : 0;
        if (suffixLength == 0)
        {
            return hoverText;
        }

        int suffixEnd = suffixStart + suffixLength;
        if (suffixEnd < hoverText.Length &&
            hoverText[suffixEnd] != '\n' &&
            !(hoverText[suffixEnd] == '\r' &&
              suffixEnd + 1 < hoverText.Length &&
              hoverText[suffixEnd + 1] == '\n'))
        {
            return hoverText;
        }

        if (string.Equals(
                connectionStatus,
                hasConnectedSuffix ? connectedLabel : unconnectedLabel,
                StringComparison.Ordinal))
        {
            return hoverText;
        }

        string replacement = string.IsNullOrEmpty(connectionStatus)
            ? ""
            : " [" + connectionStatus + "]";
        return portalTagHeader + replacement + hoverText.Substring(suffixEnd);
    }

    private static bool TryGetTemporaryPublicRemainingSeconds(
        ZDO zdo,
        out int remainingSeconds)
    {
        if (PublicPortalCatalog.TryGetTemporaryPublicRemainingSeconds(
                zdo.m_uid,
                out remainingSeconds))
        {
            return true;
        }

        long expiresAtUtcSeconds =
            PublicPortalData.GetPublicExpiresAtUtcSeconds(zdo);
        long remaining =
            expiresAtUtcSeconds -
            PublicPortalCatalog.GetEstimatedServerUtcNowSeconds();
        if (remaining <= 0L)
        {
            remainingSeconds = 0;
            return false;
        }

        remainingSeconds = (int)Math.Min(int.MaxValue, remaining);
        return true;
    }

    private static string FormatPortalLimit(int limit)
    {
        return limit < 0 ? "∞" : limit.ToString();
    }

    [HarmonyPatch(typeof(TeleportWorld), nameof(TeleportWorld.Interact))]
    private static class ToggleModePatch
    {
        private static bool Prefix(
            TeleportWorld __instance,
            Humanoid human,
            bool hold,
            ref bool __result)
        {
            if (hold ||
                human != Player.m_localPlayer ||
                __instance == null)
            {
                return true;
            }

            ZDO? zdo = PublicPortalKinds.GetPortalZdo(__instance);
            if (zdo == null)
            {
                return true;
            }

            bool toggleAccess = PublicPortalConfig.ToggleAccessKey.Value.IsKeyHeld();
            bool isAdminPortal = IsAdminPortal(zdo);
            if (isAdminPortal && !PortalRulesPlugin.HasAdminDebugAccess)
            {
                Player.m_localPlayer?.Message(
                    MessageHud.MessageType.Center,
                    PortalRulesLocalization.Translate(
                        "$sighsorry_portalrules_admin_portal_controls_require_admin_debug"));
                __result = true;
                return false;
            }

            // TeleportWorld.Interact's `alt` argument is Valheim's AltPlace
            // action (Shift by default), not the physical Alt key requested
            // for this separate admin control.
            bool editRequiredGlobalKey =
                Input.GetKey(KeyCode.LeftAlt) ||
                Input.GetKey(KeyCode.RightAlt);
            if (isAdminPortal && editRequiredGlobalKey)
            {
                AdminPortalOperations.OpenRequiredGlobalKeyInput(__instance);
                __result = true;
                return false;
            }

            if (isAdminPortal && !toggleAccess)
            {
                TextInput.instance?.RequestText(__instance, "$piece_portal_tag", 10);
                __result = true;
                return false;
            }

            if (!toggleAccess)
            {
                return true;
            }

            if (!PublicPortalAccess.CanEditPortal(zdo))
            {
                Player.m_localPlayer?.Message(
                    MessageHud.MessageType.Center,
                    PortalRulesLocalization.Translate(
                        "$sighsorry_portalrules_access_cannot_be_changed"));
                __result = true;
                return false;
            }

            RequestAccessModeChange(zdo);
            __result = true;
            return false;
        }
    }
}
