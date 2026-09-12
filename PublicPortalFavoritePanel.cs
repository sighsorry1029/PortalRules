using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace PortalRules;

internal sealed class PublicPortalFavoritePanel
{
    private const float PanelWidth = 292f;
    private const float ExpandedPanelHeight = 420f;
    private const float CollapsedPanelHeight = 46f;
    private const float HeaderHeight = 30f;
    private const float HeaderToggleSize = 26f;
    private const float HeaderToggleGap = 7f;
    private const int ToggleIconPixels = 32;
    private const float FareStateCheckIntervalSeconds = 0.2f;

    private static readonly Color UnaffordableColor =
        new(1f, 0.42f, 0.32f);
    private static readonly Color CooldownRowColor =
        new(0.28f, 0.28f, 0.28f, 0.65f);
    private static readonly Color CooldownLabelColor =
        new(0.68f, 0.68f, 0.68f);
    private static readonly Color CooldownTimeColor =
        new(1f, 0.68f, 0.35f);
    private static readonly Color HeaderToggleColor =
        new(1f, 0.52f, 0.16f, 0.72f);
    private static readonly Color HeaderToggleHoverColor =
        new(1f, 0.68f, 0.28f, 0.96f);
    private static readonly Color HeaderTogglePressedColor =
        new(0.78f, 0.32f, 0.08f, 0.92f);

    private GameObject? _root;
    private Sprite? _collapseIcon;
    private Sprite? _expandIcon;
    private float _nextFareStateCheckAt = -1f;
    private FavoriteFareState? _fareState;
    private Action<PublicPortalCatalogEntry>? _onTeleport;
    private Action<PublicPortalCatalogEntry>? _onRemoveFavorite;
    private Action<PublicPortalCatalogEntry>? _onHoverPortal;

    private readonly struct FavoriteFareState : IEquatable<FavoriteFareState>
    {
        private readonly PublicPortalTravelCostScope _scope;
        private readonly int _baseCoinCost;
        private readonly float _includedDistanceMeters;
        private readonly float _coinsPerKilometer;
        private readonly string _inviteCooldownState;

        internal readonly int LocalCoinCount;

        internal FavoriteFareState(string inviteCooldownState)
        {
            _scope = PublicPortalConfig.TravelCostScope.Value;
            _baseCoinCost = PublicPortalConfig.BaseCoinCost.Value;
            _includedDistanceMeters =
                PublicPortalConfig.BaseFareIncludedDistanceMeters.Value;
            _coinsPerKilometer = PublicPortalConfig.CoinsPerKilometer.Value;
            LocalCoinCount = PublicPortalTravelCost.IsEnabled
                ? PortalCoinWallet.GetLocalCoinCount()
                : 0;
            _inviteCooldownState = inviteCooldownState;
        }

        public bool Equals(FavoriteFareState other)
        {
            return _scope == other._scope &&
                   _baseCoinCost == other._baseCoinCost &&
                   _includedDistanceMeters.Equals(other._includedDistanceMeters) &&
                   _coinsPerKilometer.Equals(other._coinsPerKilometer) &&
                   LocalCoinCount == other.LocalCoinCount &&
                   string.Equals(
                       _inviteCooldownState,
                       other._inviteCooldownState,
                       StringComparison.Ordinal);
        }
    }

    public bool IsVisible => _root != null;

    public bool IsCollapsed =>
        PublicPortalConfig.FavoritePortalListCollapsed.Value.IsOn();

    public void Refresh(
        PublicPortalCatalogEntry? sourcePortal,
        IEnumerable<PublicPortalCatalogEntry> activePortals,
        Action<PublicPortalCatalogEntry> onTeleport,
        Action<PublicPortalCatalogEntry> onRemoveFavorite,
        Action<PublicPortalCatalogEntry> onHoverPortal)
    {
        Refresh(
            sourcePortal,
            activePortals,
            onTeleport,
            onRemoveFavorite,
            onHoverPortal,
            capturedFareState: null);
    }

    private void Refresh(
        PublicPortalCatalogEntry? sourcePortal,
        IEnumerable<PublicPortalCatalogEntry> activePortals,
        Action<PublicPortalCatalogEntry> onTeleport,
        Action<PublicPortalCatalogEntry> onRemoveFavorite,
        Action<PublicPortalCatalogEntry> onHoverPortal,
        FavoriteFareState? capturedFareState)
    {
        EnsureRoot();
        if (_root == null)
        {
            return;
        }

        _onTeleport = onTeleport;
        _onRemoveFavorite = onRemoveFavorite;
        _onHoverPortal = onHoverPortal;

        foreach (Transform child in _root.transform.Cast<Transform>().ToArray())
        {
            Object.Destroy(child.gameObject);
        }

        bool collapsed = IsCollapsed;
        CreateHeader(
            _root.transform,
            collapsed,
            ToggleCollapsed);
        SetPanelHeight(collapsed);
        if (collapsed)
        {
            _fareState = null;
            return;
        }

        FavoriteFareState fareState = capturedFareState ??
            CaptureFavoriteFareState(activePortals);
        _fareState = fareState;
        List<string> favoriteIds = PublicPortalData.ReadFavorites();
        Dictionary<string, PublicPortalCatalogEntry> portalsByFavoriteId =
            new(StringComparer.Ordinal);
        foreach (PublicPortalCatalogEntry portal in activePortals)
        {
            if (!string.IsNullOrEmpty(portal.FavoriteId) &&
                !portalsByFavoriteId.ContainsKey(portal.FavoriteId))
            {
                portalsByFavoriteId.Add(portal.FavoriteId, portal);
            }
        }

        List<PublicPortalCatalogEntry> favorites = favoriteIds
            .Where(portalsByFavoriteId.ContainsKey)
            .Select(favoriteId => portalsByFavoriteId[favoriteId])
            .ToList();

        if (favorites.Count == 0)
        {
            CreateLabel(
                _root.transform,
                PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_favorite_portals_empty_hint"),
                12,
                FontStyle.Normal);
            return;
        }

        Sprite? coinIcon = null;
        bool coinIconResolved = false;
        foreach (PublicPortalCatalogEntry portal in favorites)
        {
            PublicPortalCatalogEntry captured = portal;
            int travelCost = 0;
            if (sourcePortal.HasValue)
            {
                PublicPortalCatalogEntry source = sourcePortal.Value;
                if (source.Id != portal.Id)
                {
                    travelCost = PublicPortalTravelCost.CalculateCost(
                        source,
                        portal);
                }
            }

            bool inviteArrivalBlocked = InviteTravelCooldownStore
                .TryGetInviteArrivalCooldownRemaining(
                    portal,
                    out long remainingSeconds);
            string cooldownText = inviteArrivalBlocked
                ? InviteTravelCooldownStore.FormatRemaining(
                    remainingSeconds)
                : "";
            if (!inviteArrivalBlocked && travelCost > 0 && !coinIconResolved)
            {
                coinIcon = PublicPortalTravelCost.GetCoinIcon();
                coinIconResolved = true;
            }

            CreateButton(
                _root.transform,
                GetPortalDisplayName(portal),
                travelCost,
                inviteArrivalBlocked,
                cooldownText,
                fareState,
                coinIcon,
                () => onTeleport(captured),
                () => onRemoveFavorite(captured),
                () => onHoverPortal(captured));
        }
    }

    public void Tick(
        bool isSelecting,
        PublicPortalCatalogEntry? sourcePortal,
        IEnumerable<PublicPortalCatalogEntry> activePortals)
    {
        if (!isSelecting || !IsVisible || IsCollapsed ||
            _onTeleport == null || _onRemoveFavorite == null || _onHoverPortal == null)
        {
            _fareState = null;
            _nextFareStateCheckAt = -1f;
            return;
        }

        float now = Time.unscaledTime;
        if (_nextFareStateCheckAt >= 0f && now < _nextFareStateCheckAt)
        {
            return;
        }

        _nextFareStateCheckAt = now + FareStateCheckIntervalSeconds;
        FavoriteFareState currentState =
            CaptureFavoriteFareState(activePortals);
        if (!_fareState.HasValue)
        {
            _fareState = currentState;
            return;
        }

        if (_fareState.Value.Equals(currentState))
        {
            return;
        }

        Refresh(
            sourcePortal,
            activePortals,
            _onTeleport,
            _onRemoveFavorite,
            _onHoverPortal,
            currentState);
    }

    private static FavoriteFareState CaptureFavoriteFareState(
        IEnumerable<PublicPortalCatalogEntry> activePortals)
    {
        List<string> favoriteIds = PublicPortalData.ReadFavorites();
        string inviteCooldownState = string.Join(
            "|",
            activePortals
                .Where(portal =>
                    favoriteIds.Contains(portal.FavoriteId) &&
                    portal.AccessMode == PublicPortalAccessMode.Invite)
                .OrderBy(portal => portal.FavoriteId, StringComparer.Ordinal)
                .Select(portal =>
                    InviteTravelCooldownStore.TryGetInviteArrivalCooldownRemaining(
                        portal,
                        out long remainingSeconds)
                        ? $"{portal.FavoriteId}:" +
                          InviteTravelCooldownStore.FormatRemaining(remainingSeconds)
                        : $"{portal.FavoriteId}:-"));
        return new FavoriteFareState(inviteCooldownState);
    }

    public void Destroy()
    {
        if (_root != null)
        {
            Object.Destroy(_root);
            _root = null;
        }

        DestroyIcon(ref _collapseIcon);
        DestroyIcon(ref _expandIcon);
        _fareState = null;
        _nextFareStateCheckAt = -1f;
        _onTeleport = null;
        _onRemoveFavorite = null;
        _onHoverPortal = null;
    }

    private void EnsureRoot()
    {
        if (_root != null || Minimap.instance == null)
        {
            return;
        }

        _root = new GameObject("PublicPortalFavorites", typeof(RectTransform), typeof(Image), typeof(VerticalLayoutGroup));
        _root.transform.SetParent(Minimap.instance.m_largeRoot.transform, false);

        RectTransform rect = (RectTransform)_root.transform;
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(0f, 1f);
        rect.pivot = new Vector2(0f, 1f);
        rect.anchoredPosition = new Vector2(24f, -52f);
        rect.sizeDelta = new Vector2(PanelWidth, ExpandedPanelHeight);

        Image background = _root.GetComponent<Image>();
        background.color = new Color(0f, 0f, 0f, 0.45f);

        VerticalLayoutGroup layout = _root.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(8, 8, 8, 8);
        layout.spacing = 6f;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
    }

    private void CreateHeader(
        Transform parent,
        bool collapsed,
        Action onToggle)
    {
        GameObject headerObject = new(
            "FavoriteHeader",
            typeof(RectTransform),
            typeof(LayoutElement));
        headerObject.transform.SetParent(parent, false);
        LayoutElement headerLayout = headerObject.GetComponent<LayoutElement>();
        headerLayout.preferredHeight = HeaderHeight;

        Text title = CreateLabel(
            headerObject.transform,
            PortalRulesLocalization.Translate(
                "$sighsorry_portalrules_favorite_portals_title"),
            14,
            FontStyle.Bold);
        title.raycastTarget = false;
        RectTransform titleRect = title.rectTransform;
        titleRect.anchorMin = Vector2.zero;
        titleRect.anchorMax = Vector2.one;
        titleRect.offsetMin = Vector2.zero;
        titleRect.offsetMax = new Vector2(
            -(HeaderToggleSize + HeaderToggleGap),
            0f);

        GameObject toggleObject = new(
            "FavoriteListToggle",
            typeof(RectTransform),
            typeof(Image),
            typeof(Button));
        toggleObject.transform.SetParent(headerObject.transform, false);
        RectTransform toggleRect = (RectTransform)toggleObject.transform;
        toggleRect.anchorMin = new Vector2(1f, 0.5f);
        toggleRect.anchorMax = new Vector2(1f, 0.5f);
        toggleRect.pivot = new Vector2(1f, 0.5f);
        toggleRect.anchoredPosition = Vector2.zero;
        toggleRect.sizeDelta = new Vector2(
            HeaderToggleSize,
            HeaderToggleSize);

        Image toggleBackground = toggleObject.GetComponent<Image>();
        toggleBackground.color = Color.clear;
        Button toggleButton = toggleObject.GetComponent<Button>();
        toggleButton.colors = new ColorBlock
        {
            normalColor = HeaderToggleColor,
            highlightedColor = HeaderToggleHoverColor,
            pressedColor = HeaderTogglePressedColor,
            selectedColor = HeaderToggleColor,
            disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.35f),
            colorMultiplier = 1f,
            fadeDuration = 0.08f
        };
        toggleButton.onClick.AddListener(() => onToggle());

        GameObject iconObject = new(
            "Icon",
            typeof(RectTransform),
            typeof(Image));
        iconObject.transform.SetParent(toggleObject.transform, false);
        RectTransform iconRect = (RectTransform)iconObject.transform;
        iconRect.anchorMin = new Vector2(0.5f, 0.5f);
        iconRect.anchorMax = new Vector2(0.5f, 0.5f);
        iconRect.pivot = new Vector2(0.5f, 0.5f);
        iconRect.anchoredPosition = Vector2.zero;
        iconRect.sizeDelta = new Vector2(14f, 14f);

        Image icon = iconObject.GetComponent<Image>();
        icon.sprite = GetToggleIcon(pointsUp: !collapsed);
        icon.preserveAspect = true;
        icon.raycastTarget = false;
        icon.color = Color.white;
        toggleButton.targetGraphic = icon;
        icon.CrossFadeColor(
            HeaderToggleColor,
            0f,
            ignoreTimeScale: true,
            useAlpha: true);
    }

    private void SetPanelHeight(bool collapsed)
    {
        if (_root == null)
        {
            return;
        }

        RectTransform rect = (RectTransform)_root.transform;
        rect.sizeDelta = new Vector2(
            PanelWidth,
            collapsed ? CollapsedPanelHeight : ExpandedPanelHeight);
        LayoutRebuilder.MarkLayoutForRebuild(rect);
    }

    private void ToggleCollapsed()
    {
        PublicPortalConfig.FavoritePortalListCollapsed.Value =
            IsCollapsed
                ? PortalRulesPlugin.Toggle.Off
                : PortalRulesPlugin.Toggle.On;
    }

    private Sprite GetToggleIcon(bool pointsUp)
    {
        if (pointsUp)
        {
            _collapseIcon ??= CreateToggleIcon(
                pointsUp: true,
                "FavoritePortals.Collapse");
            return _collapseIcon;
        }

        _expandIcon ??= CreateToggleIcon(
            pointsUp: false,
            "FavoritePortals.Expand");
        return _expandIcon;
    }

    private static Sprite CreateToggleIcon(bool pointsUp, string name)
    {
        Color32[] pixels = new Color32[ToggleIconPixels * ToggleIconPixels];
        const int minimumY = 9;
        const int maximumY = 23;
        const int centerX = 16;
        const int maximumHalfWidth = 9;
        int height = maximumY - minimumY;
        for (int y = minimumY; y <= maximumY; y++)
        {
            float progress = (y - minimumY) / (float)height;
            float widthProgress = pointsUp ? 1f - progress : progress;
            int halfWidth = Mathf.Max(
                1,
                Mathf.RoundToInt(maximumHalfWidth * widthProgress));
            for (int x = centerX - halfWidth;
                 x <= centerX + halfWidth;
                 x++)
            {
                pixels[y * ToggleIconPixels + x] = Color.white;
            }
        }

        Texture2D texture = new(
            ToggleIconPixels,
            ToggleIconPixels,
            TextureFormat.RGBA32,
            mipChain: false)
        {
            name = name + ".Texture",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave
        };
        texture.SetPixels32(pixels);
        texture.Apply(updateMipmaps: false, makeNoLongerReadable: true);

        Sprite sprite = Sprite.Create(
            texture,
            new Rect(0f, 0f, ToggleIconPixels, ToggleIconPixels),
            new Vector2(0.5f, 0.5f),
            ToggleIconPixels,
            0,
            SpriteMeshType.FullRect);
        sprite.name = name;
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }

    private static void DestroyIcon(ref Sprite? sprite)
    {
        if (sprite == null)
        {
            return;
        }

        Texture2D texture = sprite.texture;
        Object.Destroy(sprite);
        Object.Destroy(texture);
        sprite = null;
    }

    private static string GetPortalDisplayName(PublicPortalCatalogEntry portal)
    {
        string tag = SanitizeDisplayLine(portal.Tag);
        if (!string.IsNullOrWhiteSpace(tag))
        {
            return tag;
        }

        PortalOwner owner = portal.Owner;
        string sanitizedOwnerName = SanitizeDisplayLine(owner.Name);
        string accessModeLabel =
            PublicPortalInteraction.AccessModeLabel(portal.AccessMode);
        return owner.IsValid &&
               !string.IsNullOrWhiteSpace(sanitizedOwnerName)
            ? PortalRulesLocalization.Translate(
                "$sighsorry_portalrules_portal_name_with_owner",
                accessModeLabel,
                sanitizedOwnerName)
            : accessModeLabel;
    }

    private static string SanitizeDisplayLine(string? value)
    {
        return (value ?? "")
            .RemoveRichTextTags()
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('\t', ' ')
            .Trim();
    }

    private static Text CreateLabel(Transform parent, string text, int fontSize, FontStyle style)
    {
        GameObject labelObject = new("Label", typeof(RectTransform), typeof(Text), typeof(LayoutElement));
        labelObject.transform.SetParent(parent, false);
        LayoutElement layout = labelObject.GetComponent<LayoutElement>();
        layout.preferredHeight = fontSize + 10f;

        Text label = labelObject.GetComponent<Text>();
        label.font = PublicPortalMapController.PortalFont;
        label.fontSize = fontSize;
        label.fontStyle = style;
        label.alignment = TextAnchor.MiddleLeft;
        label.horizontalOverflow = HorizontalWrapMode.Wrap;
        label.verticalOverflow = VerticalWrapMode.Truncate;
        label.color = Color.white;
        label.text = text;
        return label;
    }

    private static void CreateButton(
        Transform parent,
        string text,
        int travelCost,
        bool cooldownBlocked,
        string cooldownText,
        FavoriteFareState fareState,
        Sprite? coinIcon,
        Action onClick,
        Action onRightClick,
        Action onPointerEnter)
    {
        GameObject buttonObject = new("PortalButton", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        buttonObject.transform.SetParent(parent, false);
        Image background = buttonObject.GetComponent<Image>();
        background.color = cooldownBlocked
            ? CooldownRowColor
            : new Color(1f, 1f, 1f, 0.14f);

        LayoutElement layout = buttonObject.GetComponent<LayoutElement>();
        layout.preferredHeight = 30f;

        Button button = buttonObject.GetComponent<Button>();
        button.onClick.AddListener(() => onClick());
        button.interactable = !cooldownBlocked;
        if (cooldownBlocked)
        {
            button.transition = Selectable.Transition.None;
        }

        FavoriteRowPointerHandler pointerHandler = buttonObject.AddComponent<FavoriteRowPointerHandler>();
        pointerHandler.Initialize(onRightClick, onPointerEnter);

        float travelInfoWidth = cooldownBlocked
            ? CreateCooldownDisplay(
                buttonObject.transform,
                cooldownText)
            : travelCost > 0
                ? CreateTravelInfoDisplay(
                    buttonObject.transform,
                    travelCost,
                    fareState,
                    coinIcon)
                : 0f;
        Text label = CreateLabel(buttonObject.transform, text, 12, FontStyle.Normal);
        if (cooldownBlocked)
        {
            label.color = CooldownLabelColor;
        }
        RectTransform rect = (RectTransform)label.transform;
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.offsetMin = new Vector2(8f, 0f);
        rect.offsetMax = new Vector2(
            travelInfoWidth > 0f ? -(travelInfoWidth + 16f) : -8f,
            0f);
    }

    private static float CreateCooldownDisplay(
        Transform parent,
        string cooldownText)
    {
        Text remaining = CreateLabel(
            parent,
            cooldownText,
            12,
            FontStyle.Bold);
        remaining.name = "InviteCooldown";
        remaining.alignment = TextAnchor.MiddleRight;
        remaining.horizontalOverflow = HorizontalWrapMode.Overflow;
        remaining.color = CooldownTimeColor;
        remaining.raycastTarget = false;

        float width = Mathf.Ceil(remaining.preferredWidth) + 2f;
        RectTransform rect = (RectTransform)remaining.transform;
        rect.anchorMin = new Vector2(1f, 0.5f);
        rect.anchorMax = new Vector2(1f, 0.5f);
        rect.pivot = new Vector2(1f, 0.5f);
        rect.anchoredPosition = new Vector2(-8f, 0f);
        rect.sizeDelta = new Vector2(width, 20f);
        return width;
    }

    private static float CreateTravelInfoDisplay(
        Transform parent,
        int travelCost,
        FavoriteFareState fareState,
        Sprite? coinIcon)
    {
        GameObject infoRoot = new("TravelInfo", typeof(RectTransform));
        infoRoot.transform.SetParent(parent, false);
        RectTransform infoRect = (RectTransform)infoRoot.transform;
        infoRect.anchorMin = new Vector2(1f, 0.5f);
        infoRect.anchorMax = new Vector2(1f, 0.5f);
        infoRect.pivot = new Vector2(1f, 0.5f);
        infoRect.anchoredPosition = new Vector2(-8f, 0f);

        float cursor = 0f;
        if (travelCost > 0)
        {
            if (coinIcon != null)
            {
                GameObject iconObject = new(
                    "CoinsIcon",
                    typeof(RectTransform),
                    typeof(Image));
                iconObject.transform.SetParent(infoRoot.transform, false);
                RectTransform iconRect = (RectTransform)iconObject.transform;
                SetLeftAlignedRect(iconRect, cursor, 20f);

                Image icon = iconObject.GetComponent<Image>();
                icon.sprite = coinIcon;
                icon.preserveAspect = true;
                icon.raycastTarget = false;
                cursor += 24f;
            }

            string costText = coinIcon != null
                ? PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_quantity",
                    travelCost.ToString())
                : PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_coins_quantity",
                    travelCost.ToString());
            Text count = CreateLabel(
                infoRoot.transform,
                costText,
                12,
                FontStyle.Bold);
            count.alignment = TextAnchor.MiddleLeft;
            count.horizontalOverflow = HorizontalWrapMode.Overflow;
            count.color = fareState.LocalCoinCount >= travelCost
                ? Color.white
                : UnaffordableColor;
            count.raycastTarget = false;
            float countWidth = Mathf.Ceil(count.preferredWidth) + 2f;
            SetLeftAlignedRect((RectTransform)count.transform, cursor, countWidth);
            cursor += countWidth;
        }

        infoRect.sizeDelta = new Vector2(cursor, 22f);
        return cursor;
    }

    private static void SetLeftAlignedRect(RectTransform rect, float x, float width)
    {
        rect.anchorMin = new Vector2(0f, 0.5f);
        rect.anchorMax = new Vector2(0f, 0.5f);
        rect.pivot = new Vector2(0f, 0.5f);
        rect.anchoredPosition = new Vector2(x, 0f);
        rect.sizeDelta = new Vector2(width, 20f);
    }

    private sealed class FavoriteRowPointerHandler : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler
    {
        private Action? _onRightClick;
        private Action? _onPointerEnter;

        internal void Initialize(Action onRightClick, Action onPointerEnter)
        {
            _onRightClick = onRightClick;
            _onPointerEnter = onPointerEnter;
        }

        public void OnPointerClick(PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Right)
            {
                return;
            }

            eventData.Use();
            _onRightClick?.Invoke();
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            _onPointerEnter?.Invoke();
        }
    }
}
