using System;
using UnityEngine;

namespace PortalRules;

internal static class PublicPortalModeChangeEffects
{
    private const string PlayModeChangeEffectRpc =
        "sighsorry.PortalRules.PlayModeChangeEffect.v1";

    public static void RegisterPeer(ZNet znet, ZNetPeer peer)
    {
        if (znet == null ||
            peer?.m_rpc == null ||
            znet.IsServer())
        {
            return;
        }

        peer.m_rpc.Register<ZDOID>(
            PlayModeChangeEffectRpc,
            OnPlayModeChangeEffect);
    }

    public static void Broadcast(ZDOID portalId)
    {
        ZNet? znet = ZNet.instance;
        if (znet == null || !znet.IsServer())
        {
            return;
        }

        Play(portalId);
        foreach (ZNetPeer peer in znet.GetPeers())
        {
            if (peer.IsReady() &&
                peer.m_rpc != null &&
                peer.m_rpc.IsConnected())
            {
                peer.m_rpc.Invoke(
                    PlayModeChangeEffectRpc,
                    portalId);
            }
        }
    }

    private static void OnPlayModeChangeEffect(ZRpc rpc, ZDOID portalId)
    {
        if (ZNet.instance == null ||
            ZNet.instance.IsServer() ||
            !ReferenceEquals(rpc, ZNet.instance.GetServerRPC()))
        {
            return;
        }

        Play(portalId);
    }

    private static void Play(ZDOID portalId)
    {
        GameObject? instance = ZNetScene.instance != null ? ZNetScene.instance.FindInstance(portalId) : null;
        TeleportWorld? portal = instance != null ? instance.GetComponent<TeleportWorld>() : null;
        if (portal == null || portal.m_connected == null)
        {
            return;
        }

        portal.m_connected.Create(portal.transform.position, portal.transform.rotation);
    }
}
