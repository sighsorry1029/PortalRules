using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using Jotunn.Managers;
using Splatform;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace PortalRules;

internal sealed class PublicPortalPinController
{
    private const float PortalClickRadius = 96f;
    private const float TravelBadgeRefreshIntervalSeconds = 0.2f;
    private const float PinDecorationRefreshIntervalSeconds = 0.2f;
    private static readonly Color UnaffordableColor =
        new(1f, 0.42f, 0.32f);
    private static readonly Color CooldownIconColor =
        new(0.42f, 0.42f, 0.42f, 0.82f);
    private static readonly Color CooldownCrossColor =
        new(0.92f, 0.18f, 0.12f);
    private static readonly Color FavoriteStarColor =
        new(1f, 0.76f, 0.20f);
    private const string CooldownPinColorOpen = "<color=#FFAD59>";
    private const string CooldownPinLineMarker =
        "\n" + CooldownPinColorOpen;
    private static AccessTools.FieldRef<Minimap, float>? LargeZoom =
        CreateLargeZoomAccessor();

    private readonly Dictionary<Minimap.PinData, PublicPortalCatalogEntry> _activePins = new();
    private sealed class PinDecoration
    {
        public readonly GameObject Root;
        public readonly Text MyPortalText;
        public readonly Text FavoriteStar;
        public readonly Text CooldownCross;
        public Image? Icon;
        public Color OriginalIconColor;

        public PinDecoration(
            GameObject root,
            Text myPortalText,
            Text favoriteStar,
            Text cooldownCross,
            Image? icon)
        {
            Root = root;
            MyPortalText = myPortalText;
            FavoriteStar = favoriteStar;
            CooldownCross = cooldownCross;
            Icon = icon;
            OriginalIconColor = icon != null ? icon.color : Color.white;
        }
    }

    private sealed class TravelBadge
    {
        public readonly GameObject Root;
        public readonly RectTransform RootRect;
        public readonly Image CoinIcon;
        public readonly RectTransform CoinIconRect;
        public readonly Text CostText;
        public readonly RectTransform CostTextRect;

        public TravelBadge(
            GameObject root,
            Image coinIcon,
            Text costText)
        {
            Root = root;
            RootRect = (RectTransform)root.transform;
            CoinIcon = coinIcon;
            CoinIconRect = (RectTransform)coinIcon.transform;
            CostText = costText;
            CostTextRect = (RectTransform)costText.transform;
        }
    }

    private readonly Dictionary<Minimap.PinData, TravelBadge> _travelBadges = new();
    private readonly Dictionary<Minimap.PinData, PinDecoration> _pinDecorations = new();
    private float _nextTravelBadgeRefreshAt = -1f;
    private float _nextPinDecorationRefreshAt = -1f;

    public int Count => _activePins.Count;

    public IEnumerable<PublicPortalCatalogEntry> Portals => _activePins.Values;

    public void Refresh(bool requestServerRefresh = true)
    {
        Clear();

        Minimap minimap = Minimap.instance;
        if (minimap == null)
        {
            if (requestServerRefresh)
            {
                PublicPortalCatalog.RequestRefresh();
            }

            return;
        }

        PublicPortalPinTypeRegistrar.EnsureRegistered(minimap);

        foreach (PublicPortalCatalogEntry portal in PublicPortalCatalog.Entries)
        {
            if (!PublicPortalAccess.CanUsePortal(portal) ||
                ShouldHideAdminTaggedPortal(portal))
            {
                continue;
            }

            string pinName = BuildPinName(portal);

            Minimap.PinData pin = minimap.AddPin(
                portal.Position,
                PublicPortalPinTypeRegistrar.PortalPinType,
                pinName,
                save: false,
                isChecked: false,
                ownerID: 0L,
                author: default(PlatformUserID));

            pin.m_doubleSize = false;
            _activePins[pin] = portal;
        }

        if (requestServerRefresh)
        {
            PublicPortalCatalog.RequestRefresh();
        }
    }

    private static bool ShouldHideAdminTaggedPortal(
        PublicPortalCatalogEntry portal)
    {
        // Keep the entry in the recipient catalog so connected travel and its HUD
        // work for everyone; only the map representation is an admin-debug aid.
        return portal.AccessMode == PublicPortalAccessMode.Tagged &&
               PublicPortalKinds.IsAdminPortalPrefab(portal.PrefabHash) &&
               !PortalRulesPlugin.HasAdminDebugAccess;
    }

    public void Clear()
    {
        foreach (PinDecoration decoration in _pinDecorations.Values)
        {
            RestorePinIcon(decoration);
            if (decoration.Root != null)
            {
                Object.Destroy(decoration.Root);
            }
        }

        _pinDecorations.Clear();
        _nextPinDecorationRefreshAt = -1f;
        foreach (TravelBadge badge in _travelBadges.Values)
        {
            if (badge.Root != null)
            {
                Object.Destroy(badge.Root);
            }
        }

        _travelBadges.Clear();
        _nextTravelBadgeRefreshAt = -1f;
        if (Minimap.instance != null)
        {
            foreach (Minimap.PinData pin in _activePins.Keys.ToArray())
            {
                Minimap.instance.RemovePin(pin);
            }
        }

        _activePins.Clear();
    }

    public void UpdateTravelBadges(
        bool isSelecting,
        PublicPortalCatalogEntry? sourcePortal)
    {
        UpdatePinDecorations();

        if (!isSelecting ||
            !sourcePortal.HasValue ||
            !PublicPortalTravelCost.IsEnabled)
        {
            HideTravelBadges();
            _nextTravelBadgeRefreshAt = -1f;
            return;
        }

        float now = Time.unscaledTime;
        if (_nextTravelBadgeRefreshAt >= 0f &&
            now < _nextTravelBadgeRefreshAt)
        {
            return;
        }

        _nextTravelBadgeRefreshAt =
            now + TravelBadgeRefreshIntervalSeconds;

        int localCoinCount = PortalCoinWallet.GetLocalCoinCount();
        Sprite? coinIcon = PublicPortalTravelCost.GetCoinIcon();
        foreach (KeyValuePair<Minimap.PinData, PublicPortalCatalogEntry> pair in _activePins)
        {
            if (pair.Value.Id == sourcePortal.Value.Id ||
                InviteTravelCooldownStore.TryGetInviteArrivalCooldownRemaining(
                    pair.Value,
                    out _) ||
                pair.Key.m_uiElement == null)
            {
                SetTravelBadgeVisible(pair.Key, visible: false);
                continue;
            }

            int travelCost = PublicPortalTravelCost.CalculateCost(
                sourcePortal.Value,
                pair.Value);
            if (travelCost <= 0)
            {
                SetTravelBadgeVisible(pair.Key, visible: false);
                continue;
            }

            TravelBadge? badge = EnsureTravelBadge(pair.Key);
            if (badge == null)
            {
                continue;
            }

            UpdateTravelBadge(
                badge,
                travelCost,
                localCoinCount,
                coinIcon);
            if (!badge.Root.activeSelf)
            {
                badge.Root.SetActive(true);
            }
        }
    }

    private void UpdatePinDecorations()
    {
        float now = Time.unscaledTime;
        if (_nextPinDecorationRefreshAt >= 0f &&
            now < _nextPinDecorationRefreshAt)
        {
            return;
        }

        _nextPinDecorationRefreshAt =
            now + PinDecorationRefreshIntervalSeconds;
        HashSet<string> favoriteIds = new(
            PublicPortalData.ReadFavorites(),
            StringComparer.Ordinal);
        foreach (KeyValuePair<Minimap.PinData, PublicPortalCatalogEntry> pair in
                 _activePins)
        {
            Minimap.PinData pin = pair.Key;
            PublicPortalCatalogEntry portal = pair.Value;
            if (pin.m_uiElement == null)
            {
                continue;
            }

            PinDecoration decoration = EnsurePinDecoration(pin);
            bool isMyPortal = portal.MyPortalOrdinal > 0;
            decoration.MyPortalText.gameObject.SetActive(isMyPortal);
            if (isMyPortal)
            {
                decoration.MyPortalText.text = PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_my_portal_ordinal",
                    portal.MyPortalOrdinal.ToString(),
                    FormatLimit(portal.MyPortalLimit));
            }

            decoration.FavoriteStar.gameObject.SetActive(
                favoriteIds.Contains(portal.FavoriteId));

            bool inviteArrivalBlocked =
                InviteTravelCooldownStore.TryGetInviteArrivalCooldownRemaining(
                    portal,
                    out long remainingSeconds);
            UpdatePinIcon(decoration, pin.m_iconElement, inviteArrivalBlocked);
            decoration.CooldownCross.gameObject.SetActive(inviteArrivalBlocked);
            UpdateInviteCooldownLine(
                pin,
                inviteArrivalBlocked
                    ? InviteTravelCooldownStore.FormatRemaining(
                        remainingSeconds)
                    : "");
        }
    }

    private PinDecoration EnsurePinDecoration(Minimap.PinData pin)
    {
        if (_pinDecorations.TryGetValue(
                pin,
                out PinDecoration existing) &&
            existing.Root != null)
        {
            if (existing.Root.transform.parent == pin.m_uiElement)
            {
                return existing;
            }

            RestorePinIcon(existing);
            Object.Destroy(existing.Root);
        }

        GameObject root = new(
            "PortalRulesPinDecoration",
            typeof(RectTransform));
        root.transform.SetParent(pin.m_uiElement, false);
        RectTransform rootRect = (RectTransform)root.transform;
        rootRect.anchorMin = new Vector2(0.5f, 0.5f);
        rootRect.anchorMax = new Vector2(0.5f, 0.5f);
        rootRect.pivot = new Vector2(0.5f, 0.5f);
        rootRect.anchoredPosition = Vector2.zero;
        rootRect.sizeDelta = Vector2.zero;

        Text myPortalText = CreateDecorationText(
            root.transform,
            "MyPortal",
            fontSize: 12,
            FontStyle.Bold,
            Color.white,
            new Vector2(0f, 25f),
            new Vector2(190f, 18f));
        Text favoriteStar = CreateDecorationText(
            root.transform,
            "FavoriteStar",
            fontSize: 14,
            FontStyle.Bold,
            FavoriteStarColor,
            Vector2.zero,
            new Vector2(20f, 20f));
        favoriteStar.text = "★";
        Text cooldownCross = CreateDecorationText(
            root.transform,
            "InviteCooldownCross",
            fontSize: 27,
            FontStyle.Bold,
            CooldownCrossColor,
            Vector2.zero,
            new Vector2(34f, 34f));
        cooldownCross.text = "X";

        PinDecoration decoration = new(
            root,
            myPortalText,
            favoriteStar,
            cooldownCross,
            pin.m_iconElement);
        _pinDecorations[pin] = decoration;
        return decoration;
    }

    private static void UpdateInviteCooldownLine(
        Minimap.PinData pin,
        string cooldownText)
    {
        string currentName = pin.m_name ?? "";
        int markerIndex = currentName.LastIndexOf(
            CooldownPinLineMarker,
            StringComparison.Ordinal);
        string baseName = markerIndex >= 0
            ? currentName.Substring(0, markerIndex)
            : currentName;
        string updatedName = string.IsNullOrEmpty(cooldownText)
            ? baseName
            : string.IsNullOrEmpty(baseName)
                ? CooldownPinColorOpen + cooldownText + "</color>"
                : baseName + CooldownPinLineMarker + cooldownText + "</color>";
        if (string.Equals(currentName, updatedName, StringComparison.Ordinal))
        {
            return;
        }

        pin.m_name = updatedName;
        if (pin.m_NamePinData?.PinNameText != null)
        {
            pin.m_NamePinData.PinNameText.text = updatedName;
        }
    }

    private static Text CreateDecorationText(
        Transform parent,
        string name,
        int fontSize,
        FontStyle fontStyle,
        Color color,
        Vector2 anchoredPosition,
        Vector2 size)
    {
        GameObject textObject = new(
            name,
            typeof(RectTransform),
            typeof(Text),
            typeof(Outline));
        textObject.transform.SetParent(parent, false);
        RectTransform rect = (RectTransform)textObject.transform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = size;

        Text text = textObject.GetComponent<Text>();
        text.font = GUIManager.Instance.AveriaSerif;
        text.fontSize = fontSize;
        text.fontStyle = fontStyle;
        text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Truncate;
        text.color = color;
        text.raycastTarget = false;

        Outline outline = textObject.GetComponent<Outline>();
        outline.effectColor = new Color(0f, 0f, 0f, 0.9f);
        outline.effectDistance = new Vector2(1f, -1f);
        outline.useGraphicAlpha = true;
        return text;
    }

    private static void UpdatePinIcon(
        PinDecoration decoration,
        Image? currentIcon,
        bool cooldownBlocked)
    {
        if (decoration.Icon != currentIcon)
        {
            RestorePinIcon(decoration);
            decoration.Icon = currentIcon;
            decoration.OriginalIconColor = currentIcon != null
                ? currentIcon.color
                : Color.white;
        }

        if (decoration.Icon != null)
        {
            decoration.Icon.color = cooldownBlocked
                ? CooldownIconColor
                : decoration.OriginalIconColor;
        }
    }

    private static void RestorePinIcon(PinDecoration decoration)
    {
        if (decoration.Icon != null)
        {
            decoration.Icon.color = decoration.OriginalIconColor;
        }
    }

    private static string BuildPinName(PublicPortalCatalogEntry portal)
    {
        string tag = SanitizePinLine(portal.Tag);
        string modeLine = portal.AccessMode switch
        {
            PublicPortalAccessMode.Invite =>
                BuildModeLine(
                    PublicPortalInteraction.AccessModeLabel(
                        PublicPortalAccessMode.Invite),
                    portal.ModeOrdinal,
                    portal.ModeLimit),
            PublicPortalAccessMode.Clan =>
                BuildModeLine(
                    PublicPortalInteraction.AccessModeLabel(
                        PublicPortalAccessMode.Clan),
                    portal.ModeOrdinal,
                    portal.ModeLimit),
            _ => ""
        };

        if (string.IsNullOrEmpty(tag))
        {
            return modeLine;
        }

        return string.IsNullOrEmpty(modeLine)
            ? tag
            : $"{tag}\n{modeLine}";
    }

    private static string BuildModeLine(
        string label,
        int ordinal,
        int limit)
    {
        return ordinal > 0
            ? PortalRulesLocalization.Translate(
                "$sighsorry_portalrules_portal_mode_ordinal",
                label,
                ordinal.ToString(),
                FormatLimit(limit))
            : label;
    }

    private static string FormatLimit(int limit)
    {
        return limit < 0 ? "∞" : limit.ToString();
    }

    private static string SanitizePinLine(string? value)
    {
        return (value ?? "")
            .RemoveRichTextTags()
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ')
            .Trim();
    }

    private void HideTravelBadges()
    {
        foreach (TravelBadge badge in _travelBadges.Values)
        {
            if (badge.Root != null && badge.Root.activeSelf)
            {
                badge.Root.SetActive(false);
            }
        }
    }

    private void SetTravelBadgeVisible(Minimap.PinData pin, bool visible)
    {
        if (_travelBadges.TryGetValue(pin, out TravelBadge badge) &&
            badge.Root != null &&
            badge.Root.activeSelf != visible)
        {
            badge.Root.SetActive(visible);
        }
    }

    private TravelBadge? EnsureTravelBadge(Minimap.PinData pin)
    {
        if (pin.m_uiElement == null)
        {
            return null;
        }

        if (_travelBadges.TryGetValue(pin, out TravelBadge existing) &&
            existing.Root != null)
        {
            if (existing.Root.transform.parent == pin.m_uiElement)
            {
                return existing;
            }

            Object.Destroy(existing.Root);
        }

        GameObject badge = new("PortalTravelInfo", typeof(RectTransform));
        badge.transform.SetParent(pin.m_uiElement, false);
        RectTransform rect = (RectTransform)badge.transform;
        rect.anchorMin = new Vector2(1f, 0.5f);
        rect.anchorMax = new Vector2(1f, 0.5f);
        rect.pivot = new Vector2(0f, 0.5f);
        rect.anchoredPosition = new Vector2(4f, 0f);
        rect.sizeDelta = new Vector2(0f, 20f);

        GameObject iconObject = new("CoinsIcon", typeof(RectTransform), typeof(Image));
        iconObject.transform.SetParent(badge.transform, false);
        RectTransform iconRect = (RectTransform)iconObject.transform;
        iconRect.anchorMin = new Vector2(0f, 0.5f);
        iconRect.anchorMax = new Vector2(0f, 0.5f);
        iconRect.pivot = new Vector2(0f, 0.5f);
        iconRect.anchoredPosition = Vector2.zero;
        iconRect.sizeDelta = new Vector2(18f, 18f);

        Image icon = iconObject.GetComponent<Image>();
        icon.preserveAspect = true;
        icon.raycastTarget = false;

        GameObject countObject = new("Count", typeof(RectTransform), typeof(Text));
        countObject.transform.SetParent(badge.transform, false);
        RectTransform countRect = (RectTransform)countObject.transform;
        countRect.anchorMin = Vector2.zero;
        countRect.anchorMax = Vector2.one;
        countRect.offsetMin = new Vector2(22f, 0f);
        countRect.offsetMax = Vector2.zero;

        Text count = countObject.GetComponent<Text>();
        count.font = GUIManager.Instance.AveriaSerif;
        count.fontSize = 14;
        count.fontStyle = FontStyle.Bold;
        count.alignment = TextAnchor.MiddleLeft;
        count.horizontalOverflow = HorizontalWrapMode.Overflow;
        count.verticalOverflow = VerticalWrapMode.Truncate;
        count.raycastTarget = false;

        TravelBadge travelBadge = new(badge, icon, count);
        _travelBadges[pin] = travelBadge;
        return travelBadge;
    }

    private static void UpdateTravelBadge(
        TravelBadge badge,
        int travelCost,
        int localCoinCount,
        Sprite? sharedCoinIcon)
    {
        float cursor = 0f;
        bool hasCost = travelCost > 0;
        Sprite? coinIcon = hasCost
            ? badge.CoinIcon.sprite ?? sharedCoinIcon
            : null;
        bool showCoinIcon = coinIcon != null;
        badge.CoinIcon.gameObject.SetActive(showCoinIcon);
        if (showCoinIcon)
        {
            badge.CoinIcon.sprite = coinIcon;
            SetLeftAlignedRect(badge.CoinIconRect, cursor, 18f);
            cursor += 22f;
        }

        badge.CostText.gameObject.SetActive(hasCost);
        if (hasCost)
        {
            badge.CostText.text = showCoinIcon
                ? PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_quantity",
                    travelCost.ToString())
                : PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_coins_quantity",
                    travelCost.ToString());
            badge.CostText.color = localCoinCount >= travelCost
                ? Color.white
                : UnaffordableColor;
            float costWidth = Mathf.Ceil(badge.CostText.preferredWidth) + 2f;
            SetLeftAlignedRect(badge.CostTextRect, cursor, costWidth);
            cursor += costWidth;
        }

        badge.RootRect.sizeDelta = new Vector2(cursor, 20f);
    }

    private static void SetLeftAlignedRect(
        RectTransform rect,
        float x,
        float width)
    {
        rect.anchorMin = new Vector2(0f, 0.5f);
        rect.anchorMax = new Vector2(0f, 0.5f);
        rect.pivot = new Vector2(0f, 0.5f);
        rect.anchoredPosition = new Vector2(x, 0f);
        rect.sizeDelta = new Vector2(width, 18f);
    }

    public PublicPortalCatalogEntry? FindClosest(Vector3 worldPoint)
    {
        PublicPortalCatalogEntry? closest = null;
        float closestDistance = float.MaxValue;
        Minimap? minimap = Minimap.instance;
        float zoomScale = 1f;
        if (minimap != null && LargeZoom != null)
        {
            try
            {
                zoomScale = LargeZoom(minimap) * 2f;
            }
            catch (Exception ex)
            {
                LargeZoom = null;
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    $"Could not read Minimap.m_largeZoom; portal pin click scaling is disabled: {ex.Message}");
            }
        }

        float radius = PortalClickRadius * zoomScale;

        foreach (PublicPortalCatalogEntry portal in _activePins.Values)
        {
            float distance = Utils.DistanceXZ(worldPoint, portal.Position);
            if (distance < radius && distance < closestDistance)
            {
                closest = portal;
                closestDistance = distance;
            }
        }

        return closest;
    }

    private static AccessTools.FieldRef<Minimap, float>? CreateLargeZoomAccessor()
    {
        try
        {
            return AccessTools.FieldRefAccess<Minimap, float>("m_largeZoom");
        }
        catch (Exception ex)
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                $"Could not bind Minimap.m_largeZoom; portal pin click scaling is disabled: {ex.Message}");
            return null;
        }
    }
}
