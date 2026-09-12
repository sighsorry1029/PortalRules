using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Configuration;
using HarmonyLib;
using Splatform;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace PortalRules;

internal sealed class PublicPortalMapController
{
    private const string AvailablePortalsHintName = "PortalRulesAvailablePortals";
    private const string AddPinHintName = "AddPin";
    private const string HintMainKeyColor = "#FFA500";

    private delegate Vector3 ScreenToWorldPointDelegate(Minimap minimap, Vector3 screenPoint);
    private delegate bool TakeInputDelegate(PlayerController controller, bool look);

    private static ScreenToWorldPointDelegate? ScreenToWorldPoint =
        CreateScreenToWorldPointDelegate();

    private static readonly TakeInputDelegate? TakeInput = CreateTakeInputDelegate();

    public static readonly PublicPortalMapController Instance = new();

    private static Font? _portalFont;

    // Shared by the legacy Text labels in pins and favorites. Borrow the game's
    // font once when creating UI; never search assets from a per-frame path.
    internal static Font PortalFont
    {
        get
        {
            if (_portalFont != null)
            {
                return _portalFont;
            }
            foreach (Font font in Resources.FindObjectsOfTypeAll<Font>())
            {
                if (font.name == "AveriaSerifLibre-Regular")
                {
                    return _portalFont = font;
                }
            }
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                "AveriaSerifLibre-Regular is unavailable; using the built-in UI font.");
            return _portalFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        }
    }

    private readonly PublicPortalPinController _pins = new();
    private readonly PublicPortalFavoritePanel _favorites = new();

    private ZDOID _sourcePortalId = ZDOID.None;
    private PublicPortalCatalogEntry? _sourcePortal;
    private Collider? _sourceTriggerCollider;
    private Collider? _playerCollider;
    private float _lostSourceAreaAt = -1f;
    private bool _showingAccessiblePins;
    private bool _showEmptyMessageWhenCatalogArrives;
    private bool _lastAdminDebugMapAccess;
    private Minimap? _availablePortalsHintOwner;
    private GameObject? _availablePortalsHint;
    private TMP_Text? _availablePortalsHintLabel;

    private PublicPortalMapController()
    {
        PublicPortalCatalog.Updated += OnCatalogUpdated;
    }

    public bool IsSelecting { get; private set; }

    public void Begin(
        TeleportWorld sourcePortal,
        Collider? sourceTriggerCollider,
        Collider playerCollider)
    {
        if (Minimap.instance == null || ZDOMan.instance == null || Player.m_localPlayer == null)
        {
            return;
        }

        if (sourcePortal == null)
        {
            return;
        }

        ZDO? sourceZdo = PublicPortalKinds.GetPortalZdo(sourcePortal);
        if (sourceZdo == null)
        {
            return;
        }

        PublicPortalTeleportService.CancelPending();
        ZDOID sourcePortalId = sourceZdo.m_uid;
        PublicPortalTeleportService.AuthorizeMapOpen(
            sourcePortalId,
            sourcePortal.m_allowAllItems,
            () => BeginAuthorized(
                sourcePortalId,
                sourcePortal,
                sourceTriggerCollider,
                playerCollider));
    }

    private void BeginAuthorized(
        ZDOID authorizedSourcePortalId,
        TeleportWorld sourcePortal,
        Collider? sourceTriggerCollider,
        Collider playerCollider)
    {
        if (Minimap.instance == null ||
            ZDOMan.instance == null ||
            Player.m_localPlayer == null ||
            sourcePortal == null ||
            playerCollider == null ||
            playerCollider.GetComponent<Player>() != Player.m_localPlayer)
        {
            return;
        }

        ZDO? sourceZdo = PublicPortalKinds.GetPortalZdo(sourcePortal);
        if (sourceZdo == null || sourceZdo.m_uid != authorizedSourcePortalId)
        {
            return;
        }

        // Inventory or the source's item rule can change while approval is in flight.
        if (!PublicPortalTeleportService.CanTeleportWithItems(sourcePortal.m_allowAllItems))
        {
            Player.m_localPlayer.Message(MessageHud.MessageType.Center, "$msg_noteleport");
            return;
        }

        IsSelecting = true;
        _sourcePortalId = authorizedSourcePortalId;
        _sourcePortal = ResolveSourcePortalEntry(sourcePortal, sourceZdo);
        _sourceTriggerCollider = sourceTriggerCollider;
        _playerCollider = playerCollider;
        _lostSourceAreaAt = -1f;

        OpenMapAt(sourcePortal.transform.position);
        InventoryGui.instance?.Hide();

        SetAccessiblePortalPinsVisible(visible: true, showMessage: false);
    }

    public void Tick()
    {
        Minimap? minimap = Minimap.instance;
        if (minimap == null)
        {
            DestroyAvailablePortalsHint();
            if (IsSelecting ||
                _showingAccessiblePins ||
                _pins.Count > 0 ||
                _favorites.IsVisible)
            {
                End();
            }

            return;
        }

        if (minimap.m_mode != Minimap.MapMode.Large)
        {
            DestroyAvailablePortalsHint();
            if (IsSelecting || _showingAccessiblePins)
            {
                End();
            }
            return;
        }

        bool hasAdminDebugMapAccess = PortalRulesPlugin.HasAdminDebugAccess;
        if (_showingAccessiblePins &&
            _lastAdminDebugMapAccess != hasAdminDebugMapAccess)
        {
            _lastAdminDebugMapAccess = hasAdminDebugMapAccess;
            RefreshVisiblePins(requestServerRefresh: false);
        }

        if (PublicPortalConfig.EnablePortalMap.Value.IsOn() &&
            PublicPortalConfig.ToggleAccessiblePortalsKey.Value.IsKeyDown() &&
            CanProcessMapShortcutInput())
        {
            ToggleAccessiblePortalPins();
        }

        UpdateAvailablePortalsHint(minimap);
        _pins.UpdateTravelBadges(
            IsSelecting,
            _sourcePortal);
        _favorites.Tick(
            IsSelecting,
            _sourcePortal,
            _pins.Portals);

        if (!IsSelecting ||
            PublicPortalConfig.AutoCloseGraceSeconds.Value <= 0f ||
            Player.m_localPlayer == null)
        {
            return;
        }

        if (IsInsideSourceTrigger())
        {
            _lostSourceAreaAt = -1f;
            return;
        }

        if (_lostSourceAreaAt < 0f)
        {
            _lostSourceAreaAt = Time.time;
            return;
        }

        if (Time.time - _lostSourceAreaAt >= PublicPortalConfig.AutoCloseGraceSeconds.Value)
        {
            Minimap.instance.SetMapMode(Minimap.MapMode.Small);
            End();
        }
    }

    private static TakeInputDelegate? CreateTakeInputDelegate()
    {
        try
        {
            var method = AccessTools.DeclaredMethod(
                typeof(PlayerController),
                "TakeInput",
                new[] { typeof(bool) });
            return method == null
                ? null
                : AccessTools.MethodDelegate<TakeInputDelegate>(
                    method,
                    instance: null,
                    virtualCall: false);
        }
        catch
        {
            return null;
        }
    }

    private static ScreenToWorldPointDelegate? CreateScreenToWorldPointDelegate()
    {
        try
        {
            var method = AccessTools.DeclaredMethod(
                typeof(Minimap),
                "ScreenToWorldPoint",
                new[] { typeof(Vector3) });
            if (method == null)
            {
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    "Minimap.ScreenToWorldPoint was not found; PortalRules map pin clicking is disabled.");
                return null;
            }

            return AccessTools.MethodDelegate<ScreenToWorldPointDelegate>(
                method,
                instance: null,
                virtualCall: false);
        }
        catch (Exception ex)
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                $"Could not bind Minimap.ScreenToWorldPoint; PortalRules map pin clicking is disabled: {ex.Message}");
            return null;
        }
    }

    private static bool CanProcessMapShortcutInput()
    {
        if (TakeInput == null || Player.m_localPlayer == null)
        {
            return false;
        }

        PlayerController controller = Player.m_localPlayer.GetComponent<PlayerController>();
        return controller != null && TakeInput(controller, false);
    }

    public void OnMapModeChanged(Minimap.MapMode mode)
    {
        if (mode == Minimap.MapMode.Large)
        {
            if (!IsSelecting && PublicPortalConfig.EnablePortalMap.Value.IsOn())
            {
                SetAccessiblePortalPinsVisible(visible: true, showMessage: false);
            }

            return;
        }

        End();
    }

    public void End()
    {
        DestroyAvailablePortalsHint();
        if (!IsSelecting &&
            !_showingAccessiblePins &&
            _pins.Count == 0 &&
            !_favorites.IsVisible)
        {
            return;
        }

        IsSelecting = false;
        _sourcePortalId = ZDOID.None;
        _sourcePortal = null;
        PublicPortalTeleportService.CancelPending();
        _sourceTriggerCollider = null;
        _playerCollider = null;
        _lostSourceAreaAt = -1f;
        SetAccessiblePortalPinsVisible(visible: false, showMessage: false);
    }

    public void HandleLeftClick(Minimap minimap)
    {
        PublicPortalCatalogEntry? target = FindClickedPortal(minimap);
        if (target.HasValue)
        {
            TryTeleportTo(target.Value);
        }
    }

    public void HandleRightClick(Minimap minimap)
    {
        PublicPortalCatalogEntry? target = FindClickedPortal(minimap);
        if (!target.HasValue)
        {
            return;
        }

        ToggleFavorite(target.Value);
        RefreshFavorites();
    }

    private void RefreshFavorites()
    {
        _favorites.Refresh(
            _sourcePortal,
            _pins.Portals,
            TryTeleportTo,
            RemoveFavorite,
            FocusFavorite);
    }

    internal void RefreshFavoritePanelFromPreference()
    {
        if (IsSelecting && _favorites.IsVisible)
        {
            RefreshFavorites();
        }
    }

    private void TryTeleportTo(PublicPortalCatalogEntry portal)
    {
        if (InviteTravelCooldownStore
            .TryGetInviteArrivalCooldownRemaining(
                portal,
                out long remainingSeconds))
        {
            Player.m_localPlayer?.Message(
                MessageHud.MessageType.Center,
                PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_invite_arrival_cooldown_remaining",
                    InviteTravelCooldownStore.FormatRemaining(
                        remainingSeconds)));
            return;
        }

        PublicPortalTeleportService.TeleportTo(
            portal,
            _sourcePortalId,
            AllowsAllItemsForCurrentMapTrip(),
            End);
    }

    private bool AllowsAllItemsForCurrentMapTrip()
    {
        return IsSelecting && _sourcePortal?.AllowsAllItems == true;
    }

    private void ToggleAccessiblePortalPins()
    {
        SetAccessiblePortalPinsVisible(!_showingAccessiblePins, showMessage: true);
    }

    private void SetAccessiblePortalPinsVisible(bool visible, bool showMessage)
    {
        if (!visible)
        {
            _showingAccessiblePins = false;
            _showEmptyMessageWhenCatalogArrives = false;
            _pins.Clear();
            _favorites.Destroy();
            return;
        }

        _showingAccessiblePins = true;
        _lastAdminDebugMapAccess = PortalRulesPlugin.HasAdminDebugAccess;
        _showEmptyMessageWhenCatalogArrives =
            showMessage && !PublicPortalCatalog.HasSnapshot;
        RefreshVisiblePins(requestServerRefresh: true);

        if (_pins.Count == 0 && PublicPortalCatalog.HasSnapshot)
        {
            if (showMessage)
            {
                Player.m_localPlayer?.Message(
                    MessageHud.MessageType.Center,
                    PortalRulesLocalization.Translate(
                        "$sighsorry_portalrules_no_accessible_portals"));
            }
        }
    }

    private PublicPortalCatalogEntry? FindClickedPortal(Minimap minimap)
    {
        if (ScreenToWorldPoint == null)
        {
            return null;
        }

        try
        {
            Vector3 worldPoint = ScreenToWorldPoint(minimap, ZInput.pointerPosition);
            return _pins.FindClosest(worldPoint);
        }
        catch (Exception ex)
        {
            ScreenToWorldPoint = null;
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                $"Could not convert the map cursor to world coordinates; PortalRules map pin clicking is disabled: {ex.Message}");
            return null;
        }
    }

    private static void OpenMapAt(Vector3 position)
    {
        if (Minimap.instance == null)
        {
            return;
        }

        bool previousNoMap = Game.m_noMap;
        Game.m_noMap = false;
        Minimap.instance.ShowPointOnMap(position);
        Game.m_noMap = previousNoMap;
    }

    private static PublicPortalCatalogEntry ResolveSourcePortalEntry(
        TeleportWorld sourcePortal,
        ZDO sourceZdo)
    {
        if (PublicPortalCatalog.TryGetClientEntry(
                sourceZdo.m_uid,
                out PublicPortalCatalogEntry entry))
        {
            return entry;
        }

        return new PublicPortalCatalogEntry(
            sourceZdo.m_uid,
            "",
            sourceZdo.GetPrefab(),
            sourcePortal.m_allowAllItems,
            sourcePortal.transform.position,
            sourcePortal.transform.rotation,
            sourceZdo.GetString(ZDOVars.s_tag, ""),
            PublicPortalCatalog.GetEffectiveAccessMode(sourceZdo),
            PublicPortalCatalog.GetEffectiveOwner(sourceZdo));
    }

    private void FocusFavorite(PublicPortalCatalogEntry portal)
    {
        if (!IsSelecting ||
            Minimap.instance == null ||
            Minimap.instance.m_mode != Minimap.MapMode.Large)
        {
            return;
        }

        OpenMapAt(portal.Position);
    }

    private bool IsInsideSourceTrigger()
    {
        if (_sourceTriggerCollider == null || _playerCollider == null)
        {
            return false;
        }

        if (!_sourceTriggerCollider.enabled || !_playerCollider.enabled)
        {
            return false;
        }

        if (_sourceTriggerCollider.bounds.Intersects(_playerCollider.bounds))
        {
            return true;
        }

        if (Player.m_localPlayer == null)
        {
            return false;
        }

        Vector3 playerPosition = Player.m_localPlayer.transform.position;
        return Vector3.Distance(_sourceTriggerCollider.ClosestPoint(playerPosition), playerPosition) <= 0.05f;
    }

    private void ToggleFavorite(PublicPortalCatalogEntry target)
    {
        if (!PublicPortalData.TryNormalizeFavoriteId(
                target.FavoriteId,
                out string id))
        {
            return;
        }

        List<string> favorites = PublicPortalData.ReadFavorites();
        string message;
        if (favorites.Remove(id))
        {
            message = PortalRulesLocalization.Translate(
                "$sighsorry_portalrules_favorite_removed");
        }
        else
        {
            bool replacedUnavailableFavorite = false;
            if (favorites.Count >= PublicPortalData.MaximumFavoriteCount)
            {
                HashSet<string> availableFavoriteIds = new(StringComparer.Ordinal);
                foreach (PublicPortalCatalogEntry portal in _pins.Portals)
                {
                    if (!string.IsNullOrWhiteSpace(portal.FavoriteId))
                    {
                        availableFavoriteIds.Add(portal.FavoriteId);
                    }
                }

                int unavailableFavoriteIndex = favorites.FindIndex(
                    candidate => !availableFavoriteIds.Contains(candidate));
                if (unavailableFavoriteIndex >= 0)
                {
                    favorites.RemoveAt(unavailableFavoriteIndex);
                    replacedUnavailableFavorite = true;
                }
            }

            if (favorites.Count >= PublicPortalData.MaximumFavoriteCount)
            {
                Player.m_localPlayer?.Message(
                    MessageHud.MessageType.TopLeft,
                    PortalRulesLocalization.Translate(
                        "$sighsorry_portalrules_favorite_limit",
                        PublicPortalData.MaximumFavoriteCount.ToString()));
                return;
            }

            favorites.Add(id);
            message = replacedUnavailableFavorite
                ? PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_favorite_added_replaced_unavailable")
                : PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_favorite_added");
        }

        PublicPortalData.WriteFavorites(favorites);
        Player.m_localPlayer?.Message(MessageHud.MessageType.TopLeft, message);
    }

    private void RemoveFavorite(PublicPortalCatalogEntry target)
    {
        if (!PublicPortalData.TryNormalizeFavoriteId(
                target.FavoriteId,
                out string id))
        {
            return;
        }

        List<string> favorites = PublicPortalData.ReadFavorites();
        if (!favorites.Remove(id))
        {
            return;
        }

        PublicPortalData.WriteFavorites(favorites);
        Player.m_localPlayer?.Message(
            MessageHud.MessageType.TopLeft,
            PortalRulesLocalization.Translate(
                "$sighsorry_portalrules_favorite_removed"));
        RefreshFavorites();
    }

    private void UpdateAvailablePortalsHint(Minimap minimap)
    {
        KeyboardShortcut shortcut = PublicPortalConfig.ToggleAccessiblePortalsKey.Value;
        bool visible = PublicPortalConfig.EnablePortalMap.Value.IsOn() &&
                       shortcut.MainKey != KeyCode.None &&
                       PlatformPrefs.GetInt("KeyHints", 1) == 1 &&
                       minimap.m_largeRoot != null &&
                       minimap.m_largeRoot.activeInHierarchy;
        if (!visible)
        {
            HideAvailablePortalsHint();
            return;
        }

        if (_availablePortalsHintOwner != minimap || _availablePortalsHint == null)
        {
            BuildAvailablePortalsHint(minimap);
        }

        if (_availablePortalsHint == null || _availablePortalsHintLabel == null)
        {
            return;
        }

        if (!_availablePortalsHint.activeSelf)
        {
            _availablePortalsHint.SetActive(true);
            MarkAvailablePortalsHintLayoutForRebuild();
        }

        string text = PortalRulesLocalization.Translate(
            _showingAccessiblePins
                ? "$sighsorry_portalrules_hint_hide_available_portals"
                : "$sighsorry_portalrules_hint_show_available_portals",
            FormatHintShortcut(shortcut));
        if (!string.Equals(_availablePortalsHintLabel.text, text, StringComparison.Ordinal))
        {
            _availablePortalsHintLabel.text = text;
            MarkAvailablePortalsHintLayoutForRebuild();
        }
    }

    private void BuildAvailablePortalsHint(Minimap minimap)
    {
        DestroyAvailablePortalsHint();

        Transform? keyboardHints =
            minimap.m_largeRoot?.transform.Find("KeyHints/keyboard_hints");
        Transform? addPinHint = keyboardHints?.Find(AddPinHintName);
        if (keyboardHints == null || addPinHint == null)
        {
            return;
        }

        Transform? existing = keyboardHints.Find(AvailablePortalsHintName);
        GameObject hint = existing != null
            ? existing.gameObject
            : Object.Instantiate(addPinHint.gameObject, keyboardHints, false);
        hint.name = AvailablePortalsHintName;

        int targetIndex = addPinHint.GetSiblingIndex();
        if (hint.transform.GetSiblingIndex() < targetIndex)
        {
            targetIndex--;
        }
        hint.transform.SetSiblingIndex(Mathf.Max(0, targetIndex));

        Transform? inputIcon = hint.transform.Find("keyboard_hint");
        if (inputIcon != null)
        {
            inputIcon.gameObject.SetActive(false);
        }

        TMP_Text? label = hint.transform.Find("Label")?.GetComponent<TMP_Text>() ??
                          hint.GetComponentInChildren<TMP_Text>(includeInactive: true);
        if (label == null)
        {
            if (existing == null)
            {
                Object.Destroy(hint);
            }
            return;
        }

        _availablePortalsHintOwner = minimap;
        _availablePortalsHint = hint;
        _availablePortalsHintLabel = label;
        label.richText = true;
        hint.SetActive(true);
        MarkAvailablePortalsHintLayoutForRebuild();
    }

    private void HideAvailablePortalsHint()
    {
        if (_availablePortalsHint != null && _availablePortalsHint.activeSelf)
        {
            _availablePortalsHint.SetActive(false);
            MarkAvailablePortalsHintLayoutForRebuild();
        }
    }

    private void DestroyAvailablePortalsHint()
    {
        if (_availablePortalsHint != null)
        {
            Object.Destroy(_availablePortalsHint);
        }

        _availablePortalsHintOwner = null;
        _availablePortalsHint = null;
        _availablePortalsHintLabel = null;
    }

    private void MarkAvailablePortalsHintLayoutForRebuild()
    {
        if (_availablePortalsHint?.transform.parent is RectTransform parent)
        {
            LayoutRebuilder.MarkLayoutForRebuild(parent);
        }
    }

    private static string FormatHintShortcut(KeyboardShortcut shortcut)
    {
        StringBuilder text = new();
        foreach (KeyCode modifier in shortcut.Modifiers)
        {
            text.Append(FormatHintKey(modifier));
            text.Append(" + ");
        }

        text.Append("<color=");
        text.Append(HintMainKeyColor);
        text.Append('>');
        text.Append(FormatHintKey(shortcut.MainKey));
        text.Append("</color>");
        return text.ToString();
    }

    private static string FormatHintKey(KeyCode key)
    {
        string value = key switch
        {
            KeyCode.LeftShift or KeyCode.RightShift =>
                PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_key_shift"),
            KeyCode.LeftControl or KeyCode.RightControl =>
                PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_key_control"),
            KeyCode.LeftAlt or KeyCode.RightAlt =>
                PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_key_alt"),
            _ => key.ToString()
        };
        return value.RemoveRichTextTags();
    }

    private void OnCatalogUpdated()
    {
        if (Minimap.instance == null ||
            Minimap.instance.m_mode != Minimap.MapMode.Large)
        {
            return;
        }

        if (IsSelecting)
        {
            if (PublicPortalCatalog.TryGetClientEntry(
                    _sourcePortalId,
                    out PublicPortalCatalogEntry sourcePortal) &&
                PublicPortalAccess.CanUsePortal(sourcePortal))
            {
                _sourcePortal = sourcePortal;
            }
            else
            {
                Player.m_localPlayer?.Message(
                    MessageHud.MessageType.Center,
                    PortalRulesLocalization.Translate(
                        "$sighsorry_portalrules_source_portal_no_longer_accessible"));
                Minimap.instance.SetMapMode(Minimap.MapMode.Small);
                End();
                return;
            }
        }

        if (!_showingAccessiblePins)
        {
            return;
        }

        RefreshVisiblePins(requestServerRefresh: false);
        if (_showEmptyMessageWhenCatalogArrives && _pins.Count == 0)
        {
            Player.m_localPlayer?.Message(
                MessageHud.MessageType.Center,
                PortalRulesLocalization.Translate(
                    "$sighsorry_portalrules_no_accessible_portals"));
        }

        _showEmptyMessageWhenCatalogArrives = false;
    }

    private void RefreshVisiblePins(bool requestServerRefresh)
    {
        _pins.Refresh(requestServerRefresh);
        if (IsSelecting)
        {
            RefreshFavorites();
        }
    }
}
