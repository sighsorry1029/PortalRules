using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ServerSync;
using UnityEngine;

namespace PortalRules;

[BepInPlugin(ModGUID, ModName, ModVersion)]
[BepInDependency(ClanSoftDependencyGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(CurrencyPocketSoftDependencyGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(YouAreNotWorthySoftDependencyGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInIncompatibility(TargetPortalIncompatibilityGuid)]
public class PortalRulesPlugin : BaseUnityPlugin
{
    internal const string ModName = "PortalRules";
    internal const string ModVersion = "1.0.4";
    internal const string Author = "sighsorry";
    internal const string ModGUID = $"{Author}.{ModName}";
    internal const string ClanSoftDependencyGuid = "sighsorry.Clan";
    internal const string CurrencyPocketSoftDependencyGuid = "Azumatt.CurrencyPocket";
    internal const string YouAreNotWorthySoftDependencyGuid = "sighsorry.YouAreNotWorthy";
    internal const string TargetPortalIncompatibilityGuid = "org.bepinex.plugins.targetportal";
    private static readonly string ConfigFileName = $"{ModGUID}.cfg";
    private static readonly string ConfigFileFullPath = Paths.ConfigPath + Path.DirectorySeparatorChar + ConfigFileName;
    private readonly Harmony _harmony = new(ModGUID);
    public static readonly ManualLogSource PortalRulesLogger = BepInEx.Logging.Logger.CreateLogSource(ModName);
    private static readonly ConfigSync ConfigSync = new(ModGUID) { DisplayName = ModName, CurrentVersion = ModVersion, MinimumRequiredVersion = ModVersion };
    private FileSystemWatcher? _watcher;
    private readonly object _reloadLock = new();
    private DateTime _lastConfigReloadTime;
    private int _clanRegistryDirty;
    private const long RELOAD_DELAY = 10000000; // One second

    public enum Toggle
    {
        On = 1,
        Off = 0
    }

    public void Awake()
    {
        Game.isModded = true;
        bool saveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;
        try
        {
            PortalRulesLocalization.Initialize();
            _serverConfigLocked = ConfigEntry(
                "1 - General",
                "Lock Configuration",
                Toggle.On,
                "If on, the configuration is locked and can be changed by server admins only.",
                order: 200,
                categoryOrder: 500);
            _ = ConfigSync.AddLockingConfigEntry(_serverConfigLocked);
            PublicPortalConfig.Init(this);
            RequiredGlobalKeyAccess.Initialize();

            Assembly assembly = Assembly.GetExecutingAssembly();
            ClanPortalAccess.RegistryChanged += OnClanRegistryChanged;
            ClanPortalAccess.Initialize();
            _harmony.PatchAll(assembly);
            Config.Save();
        }
        finally
        {
            Config.SaveOnConfigSet = saveOnSet;
        }

        SetupWatcher();
    }

    private void Update()
    {
        if (AdminPortalBiomeDefaults.Tick())
        {
            PublicPortalCatalog.RefreshAndBroadcast();
        }
        if (PortalAccountStore.Tick())
        {
            PublicPortalServerPolicy.NotifyQuotaConfigurationChanged();
        }
        InviteTravelCooldownStore.Tick();
        PublicPortalTeleportService.Tick();
        PublicPortalCatalog.TickIdentityRegistry();
        AdminPortalPrefabManager.Tick();
        PublicPortalMapController.Instance.Tick();
        if (Interlocked.Exchange(ref _clanRegistryDirty, 0) != 0)
        {
            PublicPortalCatalog.RefreshClanViews();
        }
    }

    private void OnDestroy()
    {
        try
        {
            TryCleanup("configuration watcher", () =>
            {
                if (_watcher == null)
                {
                    return;
                }

                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            });
            TryCleanup("portal map", PublicPortalMapController.Instance.End);
            TryCleanup("connected portal HUD", PublicPortalConnectedHud.Shutdown);
            TryCleanup("portal catalog", PublicPortalCatalog.Shutdown);
            TryCleanup("server policy", PublicPortalServerPolicy.Shutdown);
            TryCleanup("teleport service", PublicPortalTeleportService.Shutdown);
            TryCleanup("portal interaction", PublicPortalInteraction.Shutdown);
            TryCleanup("admin portal operations", AdminPortalOperations.Shutdown);
            TryCleanup("admin portal prefabs", AdminPortalPrefabManager.Shutdown);
            TryCleanup("GlobalKey integration", RequiredGlobalKeyAccess.Shutdown);
            TryCleanup("Clan event subscription", () =>
                ClanPortalAccess.RegistryChanged -= OnClanRegistryChanged);
            TryCleanup("Clan integration", ClanPortalAccess.Shutdown);
            TryCleanup("configuration save", () => SaveWithRespectToConfigSet());
        }
        finally
        {
            TryCleanup("Harmony patches", _harmony.UnpatchSelf);
        }
    }

    private static void TryCleanup(string component, Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception ex)
        {
            PortalRulesLogger.LogError(
                $"Failed to clean up {component}: {ex.Message}");
        }
    }

    private void OnClanRegistryChanged()
    {
        Interlocked.Exchange(ref _clanRegistryDirty, 1);
    }

    private void SetupWatcher()
    {
        _watcher = new FileSystemWatcher(Paths.ConfigPath, ConfigFileName);
        _watcher.Changed += ReadConfigValues;
        _watcher.Created += ReadConfigValues;
        _watcher.Renamed += ReadConfigValues;
        _watcher.SynchronizingObject = ThreadingHelper.SynchronizingObject;
        _watcher.EnableRaisingEvents = true;
    }

    private void ReadConfigValues(object sender, FileSystemEventArgs e)
    {
        DateTime now = DateTime.Now;
        long time = now.Ticks - _lastConfigReloadTime.Ticks;
        if (time < RELOAD_DELAY)
        {
            return;
        }

        lock (_reloadLock)
        {
            if (!File.Exists(ConfigFileFullPath))
            {
                PortalRulesLogger.LogWarning("Config file does not exist. Skipping reload.");
                return;
            }

            try
            {
                PortalRulesLogger.LogDebug("Reloading configuration...");
                SaveWithRespectToConfigSet(true);
                PortalRulesLogger.LogInfo("Configuration reload complete.");
            }
            catch (Exception ex)
            {
                PortalRulesLogger.LogError($"Error reloading configuration: {ex.Message}");
            }
        }

        _lastConfigReloadTime = now;
    }

    private void SaveWithRespectToConfigSet(bool reload = false)
    {
        bool originalSaveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;
        try
        {
            if (reload)
            {
                Config.Reload();
            }

            Config.Save();
        }
        finally
        {
            Config.SaveOnConfigSet = originalSaveOnSet;
        }
    }


    #region ConfigOptions

    private static ConfigEntry<Toggle> _serverConfigLocked = null!;

    internal static bool IsAdmin =>
        ZNet.instance == null ||
        ZNet.instance.IsServer() ||
        PublicPortalData.ServerAssignedIsAdmin;

    internal static bool HasAdminDebugAccess =>
        IsAdmin && Player.m_debugMode;

    internal ConfigEntry<T> ConfigEntry<T>(
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
        ConfigEntry<T> configEntry = Config.Bind(group, name, value, extendedDescription);
        SyncedConfigEntry<T> syncedConfigEntry = ConfigSync.AddConfigEntry(configEntry);
        syncedConfigEntry.SynchronizedConfig = synchronizedSetting;

        return configEntry;
    }

    internal ConfigEntry<T> ConfigEntry<T>(
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

    #endregion
}

public static class KeyboardExtensions
{
    public static bool IsKeyDown(this KeyboardShortcut shortcut)
    {
        return shortcut.MainKey != KeyCode.None && Input.GetKeyDown(shortcut.MainKey) && shortcut.Modifiers.All(Input.GetKey);
    }

    public static bool IsKeyHeld(this KeyboardShortcut shortcut)
    {
        return shortcut.MainKey != KeyCode.None && Input.GetKey(shortcut.MainKey) && shortcut.Modifiers.All(Input.GetKey);
    }
}

public static class ToggleExtentions
{
    public static bool IsOn(this PortalRulesPlugin.Toggle value)
    {
        return value == PortalRulesPlugin.Toggle.On;
    }

    public static bool IsOff(this PortalRulesPlugin.Toggle value)
    {
        return value == PortalRulesPlugin.Toggle.Off;
    }
}
