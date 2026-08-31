using System;
using HarmonyLib;
using UnityEngine;

namespace PortalRules;

[HarmonyPatch]
internal static class PublicPortalPinTypeRegistrar
{
    private static AccessTools.FieldRef<Minimap, bool[]>? _visibleIconTypes;
    private static bool _visibleIconTypesBindingAttempted;
    private static bool _visibleIconTypesWarningLogged;
    private static Minimap.PinType _portalPinType = Minimap.PinType.Icon3;
    private static Minimap? _registeredMinimap;
    private static Sprite? _portalSprite;

    public static Minimap.PinType PortalPinType => _portalPinType;

    public static void EnsureRegistered(Minimap minimap)
    {
        if (minimap == null || ReferenceEquals(_registeredMinimap, minimap))
        {
            return;
        }

        if (!TryGetVisibleIconTypes(
                out AccessTools.FieldRef<Minimap, bool[]> visibleIconTypes))
        {
            return;
        }

        Sprite? portalSprite = GetPortalSprite();
        if (portalSprite == null || minimap.m_icons == null)
        {
            return;
        }

        try
        {
            ref bool[] currentVisibleIconTypes = ref visibleIconTypes(minimap);
            if (currentVisibleIconTypes == null)
            {
                return;
            }

            int customPinType = currentVisibleIconTypes.Length;
            bool[] expandedVisibleIconTypes = new bool[customPinType + 1];
            Array.Copy(
                currentVisibleIconTypes,
                expandedVisibleIconTypes,
                customPinType);
            expandedVisibleIconTypes[customPinType] = true;

            _portalPinType = (Minimap.PinType)customPinType;
            currentVisibleIconTypes = expandedVisibleIconTypes;
            minimap.m_icons.Add(new Minimap.SpriteData
            {
                m_name = _portalPinType,
                m_icon = portalSprite
            });

            _registeredMinimap = minimap;
        }
        catch (Exception ex)
        {
            _visibleIconTypes = null;
            LogVisibleIconTypesBindingFailure(ex.Message);
        }
    }

    private static bool TryGetVisibleIconTypes(
        out AccessTools.FieldRef<Minimap, bool[]> visibleIconTypes)
    {
        if (!_visibleIconTypesBindingAttempted)
        {
            _visibleIconTypesBindingAttempted = true;
            try
            {
                _visibleIconTypes =
                    AccessTools.FieldRefAccess<Minimap, bool[]>(
                        "m_visibleIconTypes");
            }
            catch (Exception ex)
            {
                LogVisibleIconTypesBindingFailure(ex.Message);
            }
        }

        if (_visibleIconTypes != null)
        {
            visibleIconTypes = _visibleIconTypes;
            return true;
        }

        LogVisibleIconTypesBindingFailure(
            "Minimap.m_visibleIconTypes was not found.");
        visibleIconTypes = null!;
        return false;
    }

    private static void LogVisibleIconTypesBindingFailure(string reason)
    {
        if (_visibleIconTypesWarningLogged)
        {
            return;
        }

        _visibleIconTypesWarningLogged = true;
        PortalRulesPlugin.PortalRulesLogger.LogWarning(
            "The PortalRules map pin type could not be registered. " + reason);
    }

    private static Sprite? GetPortalSprite()
    {
        if (_portalSprite != null)
        {
            return _portalSprite;
        }

        _portalSprite = FindSprite("teleport_6") ?? FindSprite("teleport_7") ?? FindSprite("mapicon_portal");
        if (_portalSprite != null)
        {
            return _portalSprite;
        }

        Texture2D? texture = FindTexture("teleport_6") ?? FindTexture("teleport_7");
        if (texture == null)
        {
            return null;
        }

        _portalSprite = Sprite.Create(
            texture,
            new Rect(0f, 0f, texture.width, texture.height),
            new Vector2(0.5f, 0.5f),
            100f);
        _portalSprite.name = "PortalRules Portal Icon";
        return _portalSprite;
    }

    private static Sprite? FindSprite(string name)
    {
        foreach (Sprite sprite in Resources.FindObjectsOfTypeAll<Sprite>())
        {
            if (sprite != null && sprite.name == name)
            {
                return sprite;
            }
        }

        return null;
    }

    private static Texture2D? FindTexture(string name)
    {
        foreach (Texture2D texture in Resources.FindObjectsOfTypeAll<Texture2D>())
        {
            if (texture != null && texture.name == name)
            {
                return texture;
            }
        }

        return null;
    }

    [HarmonyPatch(typeof(Minimap), "Start")]
    private static class MinimapStartPatch
    {
        private static void Postfix(Minimap __instance)
        {
            EnsureRegistered(__instance);
        }
    }
}
