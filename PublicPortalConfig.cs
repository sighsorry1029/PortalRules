using BepInEx;
using BepInEx.Configuration;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using ServerSync;
using UnityEngine;

namespace PortalRules;

internal static class PublicPortalConfig
{
    private static readonly ConfigSync ConfigSync =
        new(PortalRulesPlugin.ModGUID)
        {
            DisplayName = PortalRulesPlugin.ModName,
            CurrentVersion = PortalRulesPlugin.ModVersion,
            MinimumRequiredVersion = PortalRulesPlugin.ModVersion
        };
    private static readonly string ConfigFileName =
        $"{PortalRulesPlugin.ModGUID}.cfg";
    private static readonly string ConfigFileFullPath =
        Paths.ConfigPath + Path.DirectorySeparatorChar + ConfigFileName;
    private const float ConfigSaveDebounceSeconds = 0.5f;
    private const float ConfigReloadDebounceSeconds = 0.25f;
    private const float ConfigIoRetrySeconds = 1f;

    private static ConfigFile? _config;
    private static FileSystemWatcher? _watcher;
    private static bool _configurationInitialized;
    private static bool _configPersistenceActive;
    private static bool _originalSaveOnConfigSet;
    private static bool _configSavePending;
    private static bool _configReloadPending;
    private static bool _processingConfigReload;
    private static float _configSaveAt;
    private static float _configReloadAt;
    private static string _knownConfigFingerprint = "";
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

    internal static void Initialize(ConfigFile config)
    {
        Shutdown();
        _config = config;
        _originalSaveOnConfigSet = config.SaveOnConfigSet;
        config.SaveOnConfigSet = false;
        _configurationInitialized = true;
        try
        {
            BindEntries(config);
        }
        catch
        {
            Shutdown();
            throw;
        }
    }

    private static void BindEntries(ConfigFile config)
    {
        ConfigEntry<PortalRulesPlugin.Toggle> serverConfigLocked = ConfigEntry(
            "1 - General",
            "Lock Configuration",
            PortalRulesPlugin.Toggle.On,
            "If on, the configuration is locked and can be changed by server admins only.",
            order: 200,
            categoryOrder: 500);
        _ = ConfigSync.AddLockingConfigEntry(serverConfigLocked);

        EnablePortalMap = ConfigEntry(
            "2 - Portal Map",
            "Enable Portal Map",
            PortalRulesPlugin.Toggle.On,
            "If on, entering a portal opens a portal target map.",
            order: 400,
            categoryOrder: 400);
        ToggleAccessiblePortalsKey = ConfigEntry(
            "2 - Portal Map",
            "Toggle Accessible Portal Pins Key",
            new KeyboardShortcut(KeyCode.P),
            "Keyboard shortcut used while the large map is open to show or hide accessible portal pins.",
            synchronizedSetting: false,
            order: 300,
            categoryOrder: 400);
        AutoCloseGraceSeconds = ConfigEntry(
            "2 - Portal Map",
            "Auto Close Grace Seconds",
            0.5f,
            new ConfigDescription(
                "Seconds to wait after leaving the source portal area before closing the portal target map. Set to 0 to disable automatic closing.",
                new AcceptableValueRange<float>(0f, 2f)),
            synchronizedSetting: false,
            order: 200,
            categoryOrder: 400);
        PortalMapWheelZoomMultiplier = ConfigEntry(
            "2 - Portal Map",
            "Portal Map Wheel Zoom Multiplier",
            3,
            new ConfigDescription(
                "Multiplier applied to mouse-wheel zoom only while the portal destination map is active. 1 uses Valheim's normal zoom rate.",
                new AcceptableValueRange<int>(1, 10)),
            synchronizedSetting: false,
            order: 100,
            categoryOrder: 400);
        FavoritePortalListCollapsed = ConfigEntry(
            "2 - Portal Map",
            "Favorite Portal List Collapsed",
            PortalRulesPlugin.Toggle.Off,
            "Stores whether the Favorite Portals list is collapsed.",
            synchronizedSetting: false,
            order: 50,
            categoryOrder: 400,
            browsable: false);
        ToggleAccessKey = ConfigEntry(
            "3 - Access Modes",
            "Portal Access Modifier Key",
            new KeyboardShortcut(KeyCode.LeftShift),
            "Modifier key held while interacting with a portal to cycle its access mode.",
            synchronizedSetting: false,
            order: 600,
            categoryOrder: 300);

        PublicAccessDurationSeconds = ConfigEntry(
            "3 - Access Modes",
            "Public Access Duration Seconds",
            900,
            new ConfigDescription(
                "Seconds before a player-built Public portal returns to its authenticated Builder's Personal access. 0 disables automatic reversion.",
                new AcceptableValueRange<int>(0, 604800)),
            order: 500,
            categoryOrder: 300);
        MaxInvitePortalsPerAccount = ConfigEntry(
            "3 - Access Modes",
            "Max Invite Portals Per Account",
            1,
            new ConfigDescription(
                "Maximum player-built portals one authenticated Steam account may keep in Invite mode. -1 is unlimited. An effective value of 0 disables Invite mode and returns existing eligible Invite portals to their immutable Builder's Personal access.",
                new AcceptableValueRange<int>(-1, 10000)),
            order: 400,
            categoryOrder: 300);
        InviteDepartureCooldownHours = ConfigEntry(
            "3 - Access Modes",
            "Invite Departure Cooldown Hours",
            1f,
            new ConfigDescription(
                "Per-Steam-account, per-Invite-portal cooldown after using that portal as the source. 0 disables the departure cooldown.",
                new AcceptableValueRange<float>(0f, 8760f)),
            order: 300,
            categoryOrder: 300);
        InviteArrivalCooldownHours = ConfigEntry(
            "3 - Access Modes",
            "Invite Arrival Cooldown Hours",
            1f,
            new ConfigDescription(
                "Per-Steam-account, per-Invite-portal cooldown after using that portal as the destination. 0 disables the arrival cooldown.",
                new AcceptableValueRange<float>(0f, 8760f)),
            order: 200,
            categoryOrder: 300);
        MaxClanPortalsPerClan = ConfigEntry(
            "3 - Access Modes",
            "Max Clan Portals Per Clan",
            5,
            new ConfigDescription(
                "Maximum player-built portals that may be assigned to one Clan. -1 is unlimited and 0 disables entering Clan mode.",
                new AcceptableValueRange<int>(-1, 10000)),
            order: 100,
            categoryOrder: 300);

        EnableAccountPortalLimit = ConfigEntry(
            "4 - Account Portal Limit",
            "Enable Account Portal Limit",
            PortalRulesPlugin.Toggle.Off,
            "If on, the server limits player-built portals by authenticated SteamID64, across all characters on that Steam account. This requires a Steamworks connection; Crossplay uses PlayFab and cannot verify the required SteamID64. If off, the account limit and every portal_limit override are bypassed; Invite and Clan limits remain active.",
            order: 300,
            categoryOrder: 200);
        MaxPortalsPerAccount = ConfigEntry(
            "4 - Account Portal Limit",
            "Max Portals Per Account",
            10,
            new ConfigDescription(
                "Maximum counted portals an authenticated account may have in this world. -1 makes the global default unlimited while portal_limit overrides remain active; 0 blocks new counted portals.",
                new AcceptableValueRange<int>(-1, 10000)),
            order: 200,
            categoryOrder: 200);
        CountedPortalPrefabs = ConfigEntry(
            "4 - Account Portal Limit",
            "Counted Portal Prefabs",
            "portal_wood,portal,portal_stone",
            "Comma-separated prefab names counted by the account limit. Admin portal prefabs are always excluded.",
            order: 100,
            categoryOrder: 200);

        PortalFareMode = ConfigEntry(
            "5 - Portal Travel Costs",
            "Portal Fare Mode",
            ReadLegacyFareModeDefault(config.ConfigFilePath),
            "Off keeps Valheim's normal item teleport restrictions. Pay lets ordinary non-teleportable items use a portal for a Coins fare based on their total weight and travel distance. Items with an absolute teleport restriction remain blocked.",
            order: 200,
            categoryOrder: 100);
        CoinsPerWeightKilometer = ConfigEntry(
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

    internal static void StartPersistence()
    {
        ConfigFile config = _config ??
            throw new InvalidOperationException(
                "PortalRules configuration has not been initialized.");
        config.Save();
        RememberCurrentConfigFingerprint();
        config.SettingChanged += OnConfigSettingChanged;
        _configPersistenceActive = true;
        SetupWatcher();
    }

    internal static void Tick()
    {
        float now = Time.realtimeSinceStartup;
        if (_configReloadPending && now >= _configReloadAt)
        {
            ReloadConfigIfChanged(now);
        }

        if (_configSavePending && now >= _configSaveAt)
        {
            SaveConfigNow();
        }
    }

    internal static void Shutdown()
    {
        try
        {
            ShutdownConfigPersistence();
        }
        finally
        {
            try
            {
                UnsubscribeFromSettingChanges();
            }
            finally
            {
                if (_configurationInitialized && _config != null)
                {
                    _config.SaveOnConfigSet = _originalSaveOnConfigSet;
                }

                _configurationInitialized = false;
                _config = null;
                _configSavePending = false;
                _configReloadPending = false;
                _processingConfigReload = false;
                _knownConfigFingerprint = "";
            }
        }
    }

    private static void SetupWatcher()
    {
        _watcher = new FileSystemWatcher(Paths.ConfigPath, ConfigFileName);
        _watcher.Changed += ReadConfigValues;
        _watcher.Created += ReadConfigValues;
        _watcher.Renamed += ReadConfigValues;
        _watcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
        _watcher.EnableRaisingEvents = true;
    }

    private static void ReadConfigValues(
        object sender,
        FileSystemEventArgs args)
    {
        if (!_configPersistenceActive)
        {
            return;
        }

        _configReloadPending = true;
        _configReloadAt =
            Time.realtimeSinceStartup + ConfigReloadDebounceSeconds;
    }

    private static void OnConfigSettingChanged(
        object sender,
        SettingChangedEventArgs args)
    {
        if (!_configPersistenceActive ||
            _processingConfigReload ||
            ConfigSync.ProcessingServerUpdate)
        {
            return;
        }

        _configSavePending = true;
        _configSaveAt =
            Time.realtimeSinceStartup + ConfigSaveDebounceSeconds;
    }

    private static void ReloadConfigIfChanged(float now)
    {
        ConfigFile? config = _config;
        if (config == null)
        {
            _configReloadPending = false;
            return;
        }

        if (!File.Exists(ConfigFileFullPath))
        {
            _configReloadPending = false;
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                "Config file does not exist. Skipping reload.");
            return;
        }

        if (!TryGetConfigFingerprint(out string fingerprint))
        {
            _configReloadAt = now + ConfigIoRetrySeconds;
            return;
        }

        _configReloadPending = false;
        if (string.Equals(
                fingerprint,
                _knownConfigFingerprint,
                StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            PortalRulesPlugin.PortalRulesLogger.LogDebug(
                "Reloading configuration...");
            _processingConfigReload = true;
            config.Reload();
            _configSavePending = false;
            _knownConfigFingerprint =
                TryGetConfigFingerprint(out string reloadedFingerprint)
                    ? reloadedFingerprint
                    : fingerprint;
            PortalRulesPlugin.PortalRulesLogger.LogInfo(
                "Configuration reload complete.");
        }
        catch (Exception ex)
        {
            _configReloadPending = true;
            _configReloadAt = now + ConfigIoRetrySeconds;
            PortalRulesPlugin.PortalRulesLogger.LogError(
                $"Error reloading configuration: {ex.Message}");
        }
        finally
        {
            _processingConfigReload = false;
        }
    }

    private static void SaveConfigNow()
    {
        ConfigFile? config = _config;
        if (config == null)
        {
            _configSavePending = false;
            return;
        }

        try
        {
            config.Save();
            _configSavePending = false;
            RememberCurrentConfigFingerprint();
        }
        catch (Exception ex)
        {
            _configSavePending = true;
            _configSaveAt =
                Time.realtimeSinceStartup + ConfigIoRetrySeconds;
            PortalRulesPlugin.PortalRulesLogger.LogError(
                $"Error saving configuration: {ex.Message}");
        }
    }

    private static void RememberCurrentConfigFingerprint()
    {
        if (TryGetConfigFingerprint(out string fingerprint))
        {
            _knownConfigFingerprint = fingerprint;
        }
    }

    private static bool TryGetConfigFingerprint(out string fingerprint)
    {
        fingerprint = "";
        try
        {
            using FileStream stream = new(
                ConfigFileFullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using SHA256 sha256 = SHA256.Create();
            fingerprint = Convert.ToBase64String(
                sha256.ComputeHash(stream));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void ShutdownConfigPersistence()
    {
        try
        {
            if (_watcher != null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        }
        finally
        {
            if (_configPersistenceActive)
            {
                if (_config != null)
                {
                    _config.SettingChanged -= OnConfigSettingChanged;
                }

                try
                {
                    if (_configSavePending)
                    {
                        SaveConfigNow();
                    }
                }
                finally
                {
                    _configPersistenceActive = false;
                }
            }
        }
    }

    private static void UnsubscribeFromSettingChanges()
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
            UnsubscribeFromSettingChanges();
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

    private static ConfigEntry<T> ConfigEntry<T>(
        string group,
        string name,
        T value,
        ConfigDescription description,
        bool synchronizedSetting = true,
        int? order = null,
        int? categoryOrder = null,
        bool? browsable = null)
    {
        object[] tags = description.Tags ?? Array.Empty<object>();
        if (order.HasValue || categoryOrder.HasValue || browsable.HasValue)
        {
            tags = tags
                .Concat(new object[]
                {
                    new ConfigurationManagerAttributes
                    {
                        Order = order,
                        CategoryOrder = categoryOrder,
                        Browsable = browsable
                    }
                })
                .ToArray();
        }

        ConfigDescription extendedDescription = new(
            description.Description +
            (synchronizedSetting
                ? " [Synced with Server]"
                : " [Not Synced with Server]"),
            description.AcceptableValues,
            tags);
        ConfigFile config = _config ??
            throw new InvalidOperationException(
                "PortalRules configuration has not been initialized.");
        ConfigEntry<T> configEntry =
            config.Bind(group, name, value, extendedDescription);
        SyncedConfigEntry<T> syncedConfigEntry =
            ConfigSync.AddConfigEntry(configEntry);
        syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

        return configEntry;
    }

    private static ConfigEntry<T> ConfigEntry<T>(
        string group,
        string name,
        T value,
        string description,
        bool synchronizedSetting = true,
        int? order = null,
        int? categoryOrder = null,
        bool? browsable = null)
    {
        return ConfigEntry(
            group,
            name,
            value,
            new ConfigDescription(description),
            synchronizedSetting,
            order,
            categoryOrder,
            browsable);
    }

    private sealed class ConfigurationManagerAttributes
    {
        public int? Order { get; set; }

        public int? CategoryOrder { get; set; }

        public bool? Browsable { get; set; }
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
