using BepInEx.Configuration;
using UnityEngine;

namespace PortalRules;

internal static class PublicPortalConfig
{
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
    public static ConfigEntry<PublicPortalTravelCostScope> TravelCostScope = null!;
    public static ConfigEntry<int> BaseCoinCost = null!;
    public static ConfigEntry<float> BaseFareIncludedDistanceMeters = null!;
    public static ConfigEntry<float> CoinsPerKilometer = null!;

    public static void Init(PortalRulesPlugin plugin)
    {
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
        FavoritePortalListCollapsed.SettingChanged += (_, _) =>
            PublicPortalMapController.Instance
                .RefreshFavoritePanelFromPreference();

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
        PublicAccessDurationSeconds.SettingChanged += (_, _) =>
            PublicPortalCatalog.RefreshTemporaryPublicConfiguration();

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
        System.EventHandler inviteCooldownSettingChanged = (_, _) =>
            PublicPortalCatalog.RefreshInviteCooldownConfiguration();
        InviteDepartureCooldownHours.SettingChanged += inviteCooldownSettingChanged;
        InviteArrivalCooldownHours.SettingChanged += inviteCooldownSettingChanged;

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

        System.EventHandler quotaSettingChanged = (_, _) =>
            PublicPortalServerPolicy.NotifyQuotaConfigurationChanged();
        EnableAccountPortalLimit.SettingChanged += quotaSettingChanged;
        MaxPortalsPerAccount.SettingChanged += quotaSettingChanged;
        MaxInvitePortalsPerAccount.SettingChanged += quotaSettingChanged;
        MaxClanPortalsPerClan.SettingChanged += quotaSettingChanged;
        CountedPortalPrefabs.SettingChanged += quotaSettingChanged;

        TravelCostScope = plugin.ConfigEntry(
            "5 - Portal Travel Costs",
            "Travel Cost Scope",
            PublicPortalTravelCostScope.Off,
            "Off disables Coins costs. All charges every handled portal trip. AdminPortalTrips charges trips where either endpoint is an Admin Portal prefab. PersonalAndClanRoutesFree makes a trip free only when both endpoints are Personal or Clan. AllItemsSourceTrips charges only when the source portal allows every item.",
            order: 400,
            categoryOrder: 100);
        BaseCoinCost = plugin.ConfigEntry(
            "5 - Portal Travel Costs",
            "Base Coin Cost",
            10,
            new ConfigDescription(
                "Coins charged for a paid portal trip before any distance surcharge.",
                new AcceptableValueRange<int>(0, 1000000)),
            order: 300,
            categoryOrder: 100);
        BaseFareIncludedDistanceMeters = plugin.ConfigEntry(
            "5 - Portal Travel Costs",
            "Base Fare Included Distance Meters",
            1000f,
            new ConfigDescription(
                "XZ distance covered by the base fare before the per-kilometer surcharge begins.",
                new AcceptableValueRange<float>(0f, 1000000f)),
            order: 200,
            categoryOrder: 100);
        CoinsPerKilometer = plugin.ConfigEntry(
            "5 - Portal Travel Costs",
            "Coins Per Kilometer",
            5f,
            new ConfigDescription(
                "Additional Coins per kilometer beyond the distance included in the base fare. The surcharge is rounded up.",
                new AcceptableValueRange<float>(0f, 1000000f)),
            order: 100,
            categoryOrder: 100);
    }
}
