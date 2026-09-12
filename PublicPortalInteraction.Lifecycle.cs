using System;
using HarmonyLib;
using UnityEngine;

namespace PortalRules;

internal static partial class PublicPortalInteraction
{
    [HarmonyPatch(typeof(Game), "Start")]
    private static class RegisterRpcPatch
    {
        private static void Postfix(Game __instance)
        {
            LastAccessModeRequestAt.Clear();
            PublicPortalMapController.Instance.End();
            PublicPortalCatalog.Register(__instance);
            PublicPortalServerPolicy.Register(__instance);
        }
    }

    [HarmonyPatch(typeof(ZNet), "OnNewConnection")]
    private static class RegisterPeerRpcPatch
    {
        private static void Postfix(ZNet __instance, ZNetPeer peer)
        {
            PublicPortalCatalog.RegisterPeer(__instance, peer);
            PublicPortalServerPolicy.RegisterPeer(__instance, peer);
            PublicPortalTeleportService.RegisterPeer(__instance, peer);
            PublicPortalModeChangeEffects.RegisterPeer(__instance, peer);
            AdminPortalOperations.RegisterPeer(__instance, peer);
            if (__instance.IsServer())
            {
                peer.m_rpc.Register<ZDOID, int, bool>(
                    PublicPortalData.ChangeAccessModeRpc,
                    OnRemoteAccessModeChange);
            }
            else
            {
                peer.m_rpc.Register<ZPackage>(
                    PublicPortalData.ChangeAccessModeResultRpc,
                    OnAccessModeChangeResult);
            }
        }
    }

    [HarmonyPatch(typeof(ZNet), "Awake")]
    private static class BeginNetworkSessionPatch
    {
        private static void Postfix(ZNet __instance)
        {
            PublicPortalCatalog.BeginNetworkSession(__instance);
            PublicPortalServerPolicy.BeginNetworkSession(__instance);
            PublicPortalTeleportService.BeginNetworkSession(__instance);
        }
    }

    [HarmonyPatch(typeof(ZNet), "RPC_PeerInfo")]
    private static class RegisterPeerIdentityPatch
    {
        private static void Postfix(ZNet __instance, ZRpc rpc)
        {
            RefreshPeerIdentity(__instance, rpc);
        }
    }

    [HarmonyPatch(typeof(ZNet), "RPC_CharacterID")]
    private static class RegisterPeerCharacterIdentityPatch
    {
        private static void Postfix(ZNet __instance, ZRpc rpc)
        {
            RefreshPeerIdentity(__instance, rpc);
        }
    }

    private static void RefreshPeerIdentity(ZNet znet, ZRpc rpc)
    {
        if (znet == null || !znet.IsServer())
        {
            return;
        }

        ZNetPeer? peer = PublicPortalData.FindPeer(znet, rpc);
        PublicPortalServerPolicy.SendQuotaState(peer, force: true);
    }

    [HarmonyPatch(typeof(ZNet), nameof(ZNet.Disconnect))]
    private static class CleanupPeerStatePatch
    {
        private static void Prefix(ZNetPeer peer)
        {
            if (peer?.m_rpc == null)
            {
                return;
            }

            LastAccessModeRequestAt.Remove(peer.m_rpc);
            PublicPortalCatalog.ForgetPeer(peer.m_rpc);
            PublicPortalServerPolicy.ForgetPeer(peer.m_rpc);
            PublicPortalTeleportService.ForgetPeer(peer.m_rpc);
            AdminPortalOperations.ForgetPeer(peer.m_rpc);
        }
    }

    [HarmonyPatch(typeof(ZNet), "ServerLoadWorld")]
    private static class ServerWorldLoadedPatch
    {
        private static void Postfix(ZNet __instance)
        {
            if (__instance.IsServer() && !ZNet.m_loadError)
            {
                PublicPortalCatalog.NotifyServerWorldLoaded(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(TeleportWorld), "Awake")]
    private static class ObservePortalPatch
    {
        private static void Postfix(TeleportWorld __instance)
        {
            if (__instance == null ||
                ZNet.instance == null ||
                !ZNet.instance.IsServer())
            {
                return;
            }

            ZDO? zdo = PublicPortalKinds.GetPortalZdo(__instance);
            if (zdo != null)
            {
                PublicPortalCatalog.ObservePortal(zdo);
            }
        }
    }

    [HarmonyPatch(typeof(ZDOMan), "AddIfPortal", typeof(ZDO), typeof(int))]
    private static class ObserveAddedPortalPatch
    {
        private static void Postfix(ZDOMan __instance, ZDO zdo, int prefabHash)
        {
            // CreateNewZDO calls this before inserting/initializing its ZDO.
            // Observe that local path in TeleportWorld.Awake; RPC_ZDOData calls
            // here again after Deserialize, while the authenticated context lives.
            if (ZNet.instance != null && ZNet.instance.IsServer() &&
                Game.instance != null && Game.instance.PortalPrefabHash.Contains(prefabHash) &&
                zdo.GetPrefab() == prefabHash &&
                ReferenceEquals(__instance.GetZDO(zdo.m_uid), zdo) &&
                PublicPortalKinds.IsRegisteredPortal(zdo))
            {
                PublicPortalCatalog.ObservePortal(zdo);
            }
        }
    }

    [HarmonyPatch(typeof(ZDOMan), "RPC_ZDOData")]
    private static class BindPortalCreationToTransportPatch
    {
        private static void Prefix(
            ZRpc rpc,
            out PublicPortalCatalog.PortalSyncContext? __state)
        {
            __state = PublicPortalCatalog.BeginPortalSync(rpc);
        }

        private static Exception? Finalizer(
            Exception? __exception,
            PublicPortalCatalog.PortalSyncContext? __state)
        {
            PublicPortalCatalog.RestorePortalSync(__state);
            return __exception;
        }
    }

    [HarmonyPatch(
        typeof(ZDOMan),
        "CreateNewZDO",
        typeof(ZDOID),
        typeof(Vector3),
        typeof(int))]
    private static class ObserveRemoteCreatedZdoPatch
    {
        private static void Postfix(ZDO __result)
        {
            PublicPortalCatalog.ObserveRemoteZdoCreated(__result);
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.TryPlacePiece))]
    private static class PreviewPortalLimitPatch
    {
        private static bool Prefix(
            Player __instance,
            Piece piece,
            ref bool __result)
        {
            if (__instance == null ||
                __instance != Player.m_localPlayer ||
                !PublicPortalServerPolicy.ShouldBlockLocalPlacement(piece, out string message))
            {
                return true;
            }

            __instance.Message(MessageHud.MessageType.Center, message);
            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(Player), nameof(Player.PlacePiece))]
    private static class BindLocalPortalPlacementPatch
    {
        private static void Prefix(Player __instance, out LocalPortalPlacementState __state)
        {
            __state = PublicPortalCatalog.BeginLocalPlacement(__instance);
        }

        private static Exception? Finalizer(
            Exception? __exception,
            LocalPortalPlacementState __state)
        {
            try
            {
                PublicPortalCatalog.CompleteLocalPlacement(__state);
            }
            catch (Exception ex)
            {
                PortalRulesPlugin.PortalRulesLogger.LogError(
                    $"Failed to finalize local portal placement authority: {ex.Message}");
            }

            return __exception;
        }
    }

    [HarmonyPatch(typeof(ZDOMan), "HandleDestroyedZDO")]
    private static class ObserveDestroyedPortalPatch
    {
        private static void Prefix(ZDOID uid)
        {
            if (ZNet.instance != null && ZNet.instance.IsServer())
            {
                PublicPortalCatalog.NotifyPortalDestroyed(uid);
            }
        }
    }

    [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.PrepareSave))]
    private static class ReconcileBeforeWorldSavePatch
    {
        private static void Prefix()
        {
            PublicPortalCatalog.ReconcileAuthorityToZdos();
            PortalAccountStore.FlushPlayerIdentities();
            InviteTravelCooldownStore.Flush();
        }
    }
}