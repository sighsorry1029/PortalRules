using BepInEx.Configuration;
using System;
using System.IO;
using UnityEngine;

namespace PortalRules;

internal static class PublicPortalConfig
{
    private static bool _settingHandlersRegistered;

    public static ConfigEntry<PortalRulesPlugin.Toggle> EnablePortalMap = null!;
    public static ConfigEntry<KeyboardShortcut> ToggleAccessiblePortalsKey = null!;
    public static ConfigEntry<float> AutoCloseGraceSeconds = null!;
    public static ConfigEntry<int> PortalMapWheelZoomMultiplier = null!;
    public static ConfigEntry<PortalRulesPlugin.Toggle> FavoritePortalListCollapsed = null!;
    public static ConfigEntry<KeyboardShortcut> ToggleAccessKey = null!;
    public static ConfigEntry<int> PublicAccessDurationSeconds = null!;
    public static ConfigEntry<int> MaxInvitePortalsPerAccount = null!;
    public static ConfigEntry<float> InviteDepartureCooldownHours = null!;
    public static ConfigEntry<float> InviteArrivalCooldownHours = null!;
    public static ConfigEntry<int> MaxClanPortalsPerClan = null!;
    public static ConfigEntry<PortalRulesPlugin.Toggle> EnableAccountPortalLimit = null!;
    public static ConfigEntry<int> MaxPortalsPerAccount = null!;
    public static ConfigEntry<string> CountedPortalPrefabs = null!;
    public static ConfigEntry<PublicPortalFareMode> PortalFareMode = null!;
    public static ConfigEntry<float> CoinsPerWeightKilometer = null!;

    public static void Init(PortalRulesPlugin plugin)
    {
        Shutdown();

        EnablePortalMap = plugin.ConfigEntry(
            "2 - Portal Map",
            "Enable Portal Map",
            PortalRulesPlugin.Toggle.On,
            "If on, entering a portal opens a portal target map.",
            order: 400,
            categoryOrder: 400);
        ToggleAccessiblePortalsKey = plugin.ConfigEntry(
            "2 - Portal Map",
            "Toggle Accessible Portal Pins Key",
            new KeyboardShortcut(KeyCode.P),
            "Keyboard shortcut used while the large map is open to show or hide accessible portal pins.",
            synchronizedSetting: false,
            order: 300,
            categoryOrder: 400);
        AutoCloseGraceSeconds = plugin.ConfigEntry(
            "2 - Portal Map",
            "Auto Close Grace Seconds",
            0.5f,
            new ConfigDescription(
                "Seconds to wait after leaving the source portal area before closing the portal target map. Set to 0 to disable automatic closing.",
                new AcceptableValueRange<float>(0f, 2f)),
            synchronizedSetting: false,
            order: 200,
            categoryOrder: 400);
        PortalMapWheelZoomMultiplier = plugin.ConfigEntry(
            "2 - Portal Map",
            "Portal Map Wheel Zoom Multiplier",
            3,
            new ConfigDescription(
                "Multiplier applied to mouse-wheel zoom only while the portal destination map is active. 1 uses Valheim's normal zoom rate.",
                new AcceptableValueRange<int>(1, 10)),
            synchronizedSetting: false,
            order: 100,
            categoryOrder: 400);
        FavoritePortalListCollapsed = plugin.ConfigEntry(
            "2 - Portal Map",
            "Favorite Portal List Collapsed",
            PortalRulesPlugin.Toggle.Off,
            "Stores whether the Favorite Portals list is collapsed.",
            synchronizedSetting: false,
            order: 50,
            categoryOrder: 400,
            browsable: false);
        ToggleAccessKey = plugin.ConfigEntry(
            "3 - Access Modes",
            "Portal Access Modifier Key",
            new KeyboardShortcut(KeyCode.LeftShift),
            "Modifier key held while interacting with a portal to cycle its access mode.",
            synchronizedSetting: false,
            order: 600,
            categoryOrder: 300);

        PublicAccessDurationSeconds = plugin.ConfigEntry(
            "3 - Access Modes",
            "Public Access Duration Seconds",
            900,
            new ConfigDescription(
                "Seconds before a player-built Public portal returns to its authenticated Builder's Personal access. 0 disables automatic reversion.",
                new AcceptableValueRange<int>(0, 604800)),
            order: 500,
            categoryOrder: 300);
        MaxInvitePortalsPerAccount = plugin.ConfigEntry(
            "3 - Access Modes",
            "Max Invite Portals Per Account",
            1,
            new ConfigDescription(
                "Maximum player-built portals one authenticated Steam account may keep in Invite mode. -1 is unlimited. An effective value of 0 disables Invite mode and returns existing eligible Invite portals to their immutable Builder's Personal access.",
                new AcceptableValueRange<int>(-1, 10000)),
            order: 400,
            categoryOrder: 300);
        InviteDepartureCooldownHours = plugin.ConfigEntry(
            "3 - Access Modes",
            "Invite Departure Cooldown Hours",
            1f,
            new ConfigDescription(
                "Per-Steam-account, per-Invite-portal cooldown after using that portal as the source. 0 disables the departure cooldown.",
                new AcceptableValueRange<float>(0f, 8760f)),
            order: 300,
            categoryOrder: 300);
        InviteArrivalCooldownHours = plugin.ConfigEntry(
            "3 - Access Modes",
            "Invite Arrival Cooldown Hours",
            1f,
            new ConfigDescription(
                "Per-Steam-account, per-Invite-portal cooldown after using that portal as the destination. 0 disables the arrival cooldown.",
                new AcceptableValueRange<float>(0f, 8760f)),
            order: 200,
            categoryOrder: 300);
        MaxClanPortalsPerClan = plugin.ConfigEntry(
            "3 - Access Modes",
            "Max Clan Portals Per Clan",
            5,
            new ConfigDescription(
                "Maximum player-built portals that may be assigned to one Clan. -1 is unlimited and 0 disables entering Clan mode.",
                new AcceptableValueRange<int>(-1, 10000)),
            order: 100,
            categoryOrder: 300);

        EnableAccountPortalLimit = plugin.ConfigEntry(
            "4 - Account Portal Limit",
            "Enable Account Portal Limit",
            PortalRulesPlugin.Toggle.On,
            "If on, the server limits player-built portals by authenticated SteamID64, across all characters on that Steam account. If off, the account limit and every portal_limit override are bypassed; Invite and Clan limits remain active.",
            order: 300,
            categoryOrder: 200);
        MaxPortalsPerAccount = plugin.ConfigEntry(
            "4 - Account Portal Limit",
            "Max Portals Per Account",
            10,
            new ConfigDescription(
                "Maximum counted portals an authenticated account may have in this world. -1 makes the global default unlimited while portal_limit overrides remain active; 0 blocks new counted portals.",
                new AcceptableValueRange<int>(-1, 10000)),
            order: 200,
            categoryOrder: 200);
        CountedPortalPrefabs = plugin.ConfigEntry(
            "4 - Account Portal Limit",
            "Counted Portal Prefabs",
            "portal_wood,portal,portal_stone",
            "Comma-separated prefab names counted by the account limit. Admin portal prefabs are always excluded.",
            order: 100,
            categoryOrder: 200);

        PortalFareMode = plugin.ConfigEntry(
            "5 - Portal Travel Costs",
            "Portal Fare Mode",
            ReadLegacyFareModeDefault(plugin.Config.ConfigFilePath),
            "Off keeps Valheim's normal item teleport restrictions. Pay lets ordinary non-teleportable items use a portal for a Coins fare based on their total weight and travel distance. Items with an absolute teleport restriction remain blocked.",
            order: 200,
            categoryOrder: 100);
        CoinsPerWeightKilometer = plugin.ConfigEntry(
            "5 - Portal Travel Costs",
            "Coins Per Weight Kilometer",
            0.1f,
            new ConfigDescription(
                "Coins charged per unit of ordinary non-teleportable item weight per kilometer of XZ travel distance. The final fare is rounded up.",
                new AcceptableValueRange<float>(0f, 1000000f)),
            order: 100,
            categoryOrder: 100);

        SubscribeToSettingChanges();
    }

    internal static void Shutdown()
    {
        if (!_settingHandlersRegistered)
        {
            return;
        }

        FavoritePortalListCollapsed.SettingChanged -=
            OnFavoritePortalListCollapsedChanged;
        PublicAccessDurationSeconds.SettingChanged -=
            OnPublicAccessDurationChanged;
        InviteDepartureCooldownHours.SettingChanged -=
            OnInviteCooldownChanged;
        InviteArrivalCooldownHours.SettingChanged -=
            OnInviteCooldownChanged;
        EnableAccountPortalLimit.SettingChanged -=
            OnQuotaSettingChanged;
        MaxPortalsPerAccount.SettingChanged -=
            OnQuotaSettingChanged;
        MaxInvitePortalsPerAccount.SettingChanged -=
            OnQuotaSettingChanged;
        MaxClanPortalsPerClan.SettingChanged -=
            OnQuotaSettingChanged;
        CountedPortalPrefabs.SettingChanged -=
            OnQuotaSettingChanged;
        _settingHandlersRegistered = false;
    }

    private static void SubscribeToSettingChanges()
    {
        _settingHandlersRegistered = true;
        try
        {
            FavoritePortalListCollapsed.SettingChanged +=
                OnFavoritePortalListCollapsedChanged;
            PublicAccessDurationSeconds.SettingChanged +=
                OnPublicAccessDurationChanged;
            InviteDepartureCooldownHours.SettingChanged +=
                OnInviteCooldownChanged;
            InviteArrivalCooldownHours.SettingChanged +=
                OnInviteCooldownChanged;
            EnableAccountPortalLimit.SettingChanged +=
                OnQuotaSettingChanged;
            MaxPortalsPerAccount.SettingChanged +=
                OnQuotaSettingChanged;
            MaxInvitePortalsPerAccount.SettingChanged +=
                OnQuotaSettingChanged;
            MaxClanPortalsPerClan.SettingChanged +=
                OnQuotaSettingChanged;
            CountedPortalPrefabs.SettingChanged +=
                OnQuotaSettingChanged;
        }
        catch
        {
            Shutdown();
            throw;
        }
    }

    private static void OnFavoritePortalListCollapsedChanged(
        object sender,
        EventArgs args)
    {
        PublicPortalMapController.Instance.RefreshFavoritePanelFromPreference();
    }

    private static void OnPublicAccessDurationChanged(
        object sender,
        EventArgs args)
    {
        PublicPortalCatalog.RefreshTemporaryPublicConfiguration();
    }

    private static void OnInviteCooldownChanged(
        object sender,
        EventArgs args)
    {
        PublicPortalCatalog.RefreshInviteCooldownConfiguration();
    }

    private static void OnQuotaSettingChanged(
        object sender,
        EventArgs args)
    {
        PublicPortalServerPolicy.NotifyQuotaConfigurationChanged();
    }

    private static PublicPortalFareMode ReadLegacyFareModeDefault(
        string configFilePath)
    {
        try
        {
            if (!File.Exists(configFilePath))
            {
                return PublicPortalFareMode.Off;
            }

            bool inFareSection = false;
            foreach (string rawLine in File.ReadLines(configFilePath))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal) &&
                    line.EndsWith("]", StringComparison.Ordinal))
                {
                    inFareSection = string.Equals(
                        line,
                        "[5 - Portal Travel Costs]",
                        StringComparison.Ordinal);
                    continue;
                }

                if (!inFareSection ||
                    !line.StartsWith(
                        "Travel Cost Scope =",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string value = line.Substring(line.IndexOf('=') + 1).Trim();
                return string.Equals(
                    value,
                    nameof(PublicPortalFareMode.Off),
                    StringComparison.OrdinalIgnoreCase)
                    ? PublicPortalFareMode.Off
                    : PublicPortalFareMode.Pay;
            }
        }
        catch (Exception ex)
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                $"Could not read the legacy portal fare setting; defaulting to Off: {ex.Message}");
        }

        return PublicPortalFareMode.Off;
    }
}
