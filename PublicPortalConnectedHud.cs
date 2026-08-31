using System;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PortalRules;

[HarmonyPatch]
internal static class PublicPortalConnectedHud
{
    private static readonly Color UnaffordableTravelColor =
        new(1f, 0.42f, 0.32f);
    private static readonly Color AllItemsTravelColor =
        new(0.72f, 0.92f, 0.72f);
    private static Hud? ConnectedTravelHud;
    private static GameObject? ConnectedTravelBadge;
    private static Image? ConnectedTravelCoinIcon;
    private static TextMeshProUGUI? ConnectedTravelCostText;
    private static TextMeshProUGUI? ConnectedTravelAllItemsText;
    private static string ConnectedTravelLayoutText = "";
    private static Vector2 ConnectedTravelLayoutSize =
        new(float.NaN, float.NaN);
    private static float ConnectedTravelLayoutFontSize = float.NaN;

    internal static void Shutdown()
    {
        if (ConnectedTravelBadge != null)
        {
            UnityEngine.Object.Destroy(ConnectedTravelBadge);
        }

        ConnectedTravelHud = null;
        ConnectedTravelBadge = null;
        ConnectedTravelCoinIcon = null;
        ConnectedTravelCostText = null;
        ConnectedTravelAllItemsText = null;
        ConnectedTravelLayoutText = "";
        ConnectedTravelLayoutSize = new Vector2(float.NaN, float.NaN);
        ConnectedTravelLayoutFontSize = float.NaN;
    }

    [HarmonyPatch(typeof(Hud), "UpdateCrosshair")]
    private static class ConnectedTravelCostHoverPatch
    {
        private static void Postfix(Hud __instance, Player player)
        {
            if (!TryGetConnectedTravelInfo(
                    player,
                    out int travelCost,
                    out bool allowsAllItems) ||
                !EnsureConnectedTravelBadge(__instance))
            {
                HideConnectedTravelBadge();
                return;
            }

            UpdateConnectedTravelBadge(travelCost, allowsAllItems);
            PositionConnectedTravelBadge(__instance);
        }
    }

    private static bool TryGetConnectedTravelInfo(
        Player player,
        out int travelCost,
        out bool allowsAllItems)
    {
        travelCost = 0;
        allowsAllItems = false;
        if (player == null)
        {
            return false;
        }

        GameObject hoverObject = player.GetHoverObject();
        TeleportWorld? sourcePortal = hoverObject != null
            ? hoverObject.GetComponentInParent<TeleportWorld>()
            : null;
        if (sourcePortal == null ||
            !PublicPortalKinds.IsHandledPortal(sourcePortal))
        {
            return false;
        }

        ZDO? sourceZdo = PublicPortalKinds.GetPortalZdo(sourcePortal);
        if (sourceZdo == null ||
            (PublicPortalConfig.EnablePortalMap.Value.IsOn() &&
             PublicPortalCatalog.GetEffectiveAccessMode(sourceZdo) !=
             PublicPortalAccessMode.Tagged) ||
            !PublicPortalCatalog.TryGetClientEntry(
                sourceZdo.m_uid,
                out PublicPortalCatalogEntry sourceEntry))
        {
            return false;
        }

        ZDOID targetId = sourceZdo.GetConnectionZDOID(
            ZDOExtraData.ConnectionType.Portal);
        if (targetId.IsNone() ||
            targetId == sourceZdo.m_uid ||
            !PublicPortalCatalog.TryGetClientEntry(
                targetId,
                out PublicPortalCatalogEntry targetEntry) ||
            (PublicPortalConfig.EnablePortalMap.Value.IsOn() &&
             targetEntry.AccessMode != PublicPortalAccessMode.Tagged) ||
            !PublicPortalAccess.CanUsePortal(sourceEntry) ||
            !PublicPortalAccess.CanUsePortal(targetEntry))
        {
            return false;
        }

        travelCost = PublicPortalTravelCost.CalculateCost(sourceEntry, targetEntry);
        allowsAllItems = sourcePortal.m_allowAllItems;
        return travelCost > 0 || allowsAllItems;
    }

    private static bool EnsureConnectedTravelBadge(Hud hud)
    {
        if (hud == null || hud.m_hoverName == null)
        {
            return false;
        }

        if (ConnectedTravelHud == hud &&
            ConnectedTravelBadge != null &&
            ConnectedTravelCoinIcon != null &&
            ConnectedTravelCostText != null &&
            ConnectedTravelAllItemsText != null)
        {
            if (ConnectedTravelCoinIcon.sprite == null)
            {
                ConnectedTravelCoinIcon.sprite =
                    PublicPortalTravelCost.GetCoinIcon();
            }

            return true;
        }

        if (ConnectedTravelBadge != null)
        {
            UnityEngine.Object.Destroy(ConnectedTravelBadge);
        }

        Sprite? coinIcon = PublicPortalTravelCost.GetCoinIcon();
        GameObject badge = new(
            "PortalRulesConnectedTravelInfo",
            typeof(RectTransform));
        badge.layer = hud.m_hoverName.gameObject.layer;
        badge.transform.SetParent(hud.m_hoverName.transform, false);
        RectTransform badgeRect = (RectTransform)badge.transform;
        badgeRect.anchorMin = hud.m_hoverName.rectTransform.pivot;
        badgeRect.anchorMax = hud.m_hoverName.rectTransform.pivot;
        badgeRect.pivot = new Vector2(0f, 0.5f);
        badgeRect.anchoredPosition = new Vector2(6f, 0f);
        badgeRect.sizeDelta = new Vector2(0f, 22f);

        GameObject iconObject = new(
            "CoinsIcon",
            typeof(RectTransform),
            typeof(Image));
        iconObject.layer = badge.layer;
        iconObject.transform.SetParent(badge.transform, false);
        RectTransform iconRect = (RectTransform)iconObject.transform;
        iconRect.anchorMin = new Vector2(0f, 0.5f);
        iconRect.anchorMax = new Vector2(0f, 0.5f);
        iconRect.pivot = new Vector2(0f, 0.5f);
        iconRect.anchoredPosition = Vector2.zero;
        iconRect.sizeDelta = new Vector2(20f, 20f);

        Image icon = iconObject.GetComponent<Image>();
        icon.sprite = coinIcon;
        icon.preserveAspect = true;
        icon.raycastTarget = false;

        GameObject countObject = new(
            "Count",
            typeof(RectTransform),
            typeof(TextMeshProUGUI));
        countObject.layer = badge.layer;
        countObject.transform.SetParent(badge.transform, false);
        RectTransform countRect = (RectTransform)countObject.transform;
        countRect.anchorMin = Vector2.zero;
        countRect.anchorMax = Vector2.one;
        countRect.offsetMin = new Vector2(24f, 0f);
        countRect.offsetMax = Vector2.zero;

        TextMeshProUGUI count = countObject.GetComponent<TextMeshProUGUI>();
        count.font = hud.m_hoverName.font;
        count.fontSharedMaterial = hud.m_hoverName.fontSharedMaterial;
        count.fontSize = Mathf.Max(14f, hud.m_hoverName.fontSize * 0.75f);
        count.fontStyle = FontStyles.Bold;
        count.alignment = TextAlignmentOptions.Left;
        count.textWrappingMode = TextWrappingModes.NoWrap;
        count.overflowMode = TextOverflowModes.Overflow;
        count.raycastTarget = false;

        GameObject allItemsObject = new(
            "AllItems",
            typeof(RectTransform),
            typeof(TextMeshProUGUI));
        allItemsObject.layer = badge.layer;
        allItemsObject.transform.SetParent(badge.transform, false);
        TextMeshProUGUI allItems =
            allItemsObject.GetComponent<TextMeshProUGUI>();
        allItems.font = hud.m_hoverName.font;
        allItems.fontSharedMaterial = hud.m_hoverName.fontSharedMaterial;
        allItems.fontSize = Mathf.Max(
            14f,
            hud.m_hoverName.fontSize * 0.75f);
        allItems.fontStyle = FontStyles.Bold;
        allItems.alignment = TextAlignmentOptions.Left;
        allItems.textWrappingMode = TextWrappingModes.NoWrap;
        allItems.overflowMode = TextOverflowModes.Overflow;
        allItems.color = AllItemsTravelColor;
        allItems.text = PortalRulesLocalization.Translate(
            "$sighsorry_portalrules_all_items");
        allItems.raycastTarget = false;

        badge.SetActive(false);
        ConnectedTravelHud = hud;
        ConnectedTravelBadge = badge;
        ConnectedTravelCoinIcon = icon;
        ConnectedTravelCostText = count;
        ConnectedTravelAllItemsText = allItems;
        ConnectedTravelLayoutText = "";
        ConnectedTravelLayoutSize = new Vector2(float.NaN, float.NaN);
        ConnectedTravelLayoutFontSize = float.NaN;
        return true;
    }

    private static void UpdateConnectedTravelBadge(
        int travelCost,
        bool allowsAllItems)
    {
        if (ConnectedTravelBadge == null ||
            ConnectedTravelCoinIcon == null ||
            ConnectedTravelCostText == null ||
            ConnectedTravelAllItemsText == null)
        {
            return;
        }

        float cursor = 0f;
        bool hasCost = travelCost > 0;
        Sprite? coinIcon = hasCost
            ? ConnectedTravelCoinIcon.sprite ??
              PublicPortalTravelCost.GetCoinIcon()
            : null;
        bool showCoinIcon = coinIcon != null;
        ConnectedTravelCoinIcon.gameObject.SetActive(showCoinIcon);
        if (showCoinIcon)
        {
            ConnectedTravelCoinIcon.sprite = coinIcon;
            SetConnectedTravelChildRect(
                ConnectedTravelCoinIcon.rectTransform,
                cursor,
                20f);
            cursor += 24f;
        }

        ConnectedTravelCostText.gameObject.SetActive(hasCost);
        if (hasCost)
        {
            ConnectedTravelCostText.text = showCoinIcon
                ? PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_quantity",
                    travelCost.ToString())
                : PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_coins_quantity",
                    travelCost.ToString());
            ConnectedTravelCostText.color =
                PortalCoinWallet.GetLocalCoinCount() >= travelCost
                    ? Color.white
                    : UnaffordableTravelColor;
            float costWidth =
                Mathf.Ceil(ConnectedTravelCostText.preferredWidth) + 2f;
            SetConnectedTravelChildRect(
                ConnectedTravelCostText.rectTransform,
                cursor,
                costWidth);
            cursor += costWidth;
        }

        ConnectedTravelAllItemsText.gameObject.SetActive(allowsAllItems);
        if (allowsAllItems)
        {
            if (cursor > 0f)
            {
                cursor += 8f;
            }

            ConnectedTravelAllItemsText.text = hasCost
                ? PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_all_items_bulleted")
                : PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_all_items");
            float allItemsWidth =
                Mathf.Ceil(ConnectedTravelAllItemsText.preferredWidth) + 2f;
            SetConnectedTravelChildRect(
                ConnectedTravelAllItemsText.rectTransform,
                cursor,
                allItemsWidth);
            cursor += allItemsWidth;
        }

        ((RectTransform)ConnectedTravelBadge.transform).sizeDelta =
            new Vector2(cursor, 22f);
        if (!ConnectedTravelBadge.activeSelf)
        {
            ConnectedTravelBadge.SetActive(true);
        }
    }

    private static void SetConnectedTravelChildRect(
        RectTransform rect,
        float x,
        float width)
    {
        rect.anchorMin = new Vector2(0f, 0.5f);
        rect.anchorMax = new Vector2(0f, 0.5f);
        rect.pivot = new Vector2(0f, 0.5f);
        rect.anchoredPosition = new Vector2(x, 0f);
        rect.sizeDelta = new Vector2(width, 20f);
    }

    private static void PositionConnectedTravelBadge(Hud hud)
    {
        if (ConnectedTravelBadge == null || hud.m_hoverName == null)
        {
            return;
        }

        RectTransform hoverRect = hud.m_hoverName.rectTransform;
        Vector2 rectSize = hoverRect.rect.size;
        string hoverText = hud.m_hoverName.text ?? "";
        float fontSize = hud.m_hoverName.fontSize;
        if (string.Equals(
                ConnectedTravelLayoutText,
                hoverText,
                StringComparison.Ordinal) &&
            ConnectedTravelLayoutSize == rectSize &&
            ConnectedTravelLayoutFontSize.Equals(fontSize))
        {
            return;
        }

        ConnectedTravelLayoutText = hoverText;
        ConnectedTravelLayoutSize = rectSize;
        ConnectedTravelLayoutFontSize = fontSize;

        RectTransform badgeRect =
            (RectTransform)ConnectedTravelBadge.transform;
        try
        {
            hud.m_hoverName.ForceMeshUpdate();
            TMP_TextInfo textInfo = hud.m_hoverName.textInfo;
            if (textInfo == null ||
                textInfo.lineCount <= 0 ||
                textInfo.lineInfo[0].characterCount <= 0)
            {
                SetConnectedTravelFallbackPosition(badgeRect);
                return;
            }

            TMP_LineInfo firstLine = textInfo.lineInfo[0];
            float x = firstLine.lineExtents.max.x + 6f;
            float y = (firstLine.ascender + firstLine.descender) * 0.5f;
            if (float.IsNaN(x) ||
                float.IsInfinity(x) ||
                float.IsNaN(y) ||
                float.IsInfinity(y))
            {
                SetConnectedTravelFallbackPosition(badgeRect);
                return;
            }

            badgeRect.anchorMin = hoverRect.pivot;
            badgeRect.anchorMax = hoverRect.pivot;
            badgeRect.pivot = new Vector2(0f, 0.5f);
            badgeRect.anchoredPosition = new Vector2(x, y);
        }
        catch (Exception ex)
        {
            SetConnectedTravelFallbackPosition(badgeRect);
            PortalRulesPlugin.PortalRulesLogger.LogDebug(
                $"Could not align the connected portal fare with the first hover line: {ex.Message}");
        }
    }

    private static void SetConnectedTravelFallbackPosition(
        RectTransform badgeRect)
    {
        badgeRect.anchorMin = new Vector2(0.5f, 0f);
        badgeRect.anchorMax = new Vector2(0.5f, 0f);
        badgeRect.pivot = new Vector2(0.5f, 1f);
        badgeRect.anchoredPosition = new Vector2(0f, -4f);
    }

    private static void HideConnectedTravelBadge()
    {
        if (ConnectedTravelBadge != null && ConnectedTravelBadge.activeSelf)
        {
            ConnectedTravelBadge.SetActive(false);
        }
    }
}
