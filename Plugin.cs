using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
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
    internal const string ModVersion = "1.0.5";
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
    private bool _configPersistenceActive;
    private bool _originalSaveOnConfigSet;
    private bool _configSavePending;
    private bool _configReloadPending;
    private bool _processingConfigReload;
    private float _configSaveAt;
    private float _configReloadAt;
    private string _knownConfigFingerprint = "";
    private int _clanRegistryDirty;
    private const float ConfigSaveDebounceSeconds = 0.5f;
    private const float ConfigReloadDebounceSeconds = 0.25f;
    private const float ConfigIoRetrySeconds = 1f;

    public enum Toggle
    {
        On = 1,
        Off = 0
    }

    public void Awake()
    {
        Game.isModded = true;
        _originalSaveOnConfigSet = Config.SaveOnConfigSet;
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
            RememberCurrentConfigFingerprint();
            Config.SettingChanged += OnConfigSettingChanged;
            _configPersistenceActive = true;
            SetupWatcher();
        }
        catch
        {
            Config.SettingChanged -= OnConfigSettingChanged;
            PublicPortalConfig.Shutdown();
            Config.SaveOnConfigSet = _originalSaveOnConfigSet;
            throw;
        }
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
        TickConfigPersistence();
        if (Interlocked.Exchange(ref _clanRegistryDirty, 0) != 0)
        {
            PublicPortalCatalog.RefreshClanViews();
        }
    }

    private void OnDestroy()
    {
        try
        {
            TryCleanup("configuration persistence", ShutdownConfigPersistence);
            TryCleanup("portal configuration", PublicPortalConfig.Shutdown);
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
        if (!_configPersistenceActive)
        {
            return;
        }

        _configReloadPending = true;
        _configReloadAt =
            Time.realtimeSinceStartup + ConfigReloadDebounceSeconds;
    }

    private void OnConfigSettingChanged(
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

    private void TickConfigPersistence()
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

    private void ReloadConfigIfChanged(float now)
    {
        if (!File.Exists(ConfigFileFullPath))
        {
            _configReloadPending = false;
            PortalRulesLogger.LogWarning(
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
            PortalRulesLogger.LogDebug("Reloading configuration...");
            _processingConfigReload = true;
            Config.Reload();
            _configSavePending = false;
            _knownConfigFingerprint =
                TryGetConfigFingerprint(out string reloadedFingerprint)
                    ? reloadedFingerprint
                    : fingerprint;
            PortalRulesLogger.LogInfo("Configuration reload complete.");
        }
        catch (Exception ex)
        {
            _configReloadPending = true;
            _configReloadAt = now + ConfigIoRetrySeconds;
            PortalRulesLogger.LogError(
                $"Error reloading configuration: {ex.Message}");
        }
        finally
        {
            _processingConfigReload = false;
        }
    }

    private void SaveConfigNow()
    {
        try
        {
            Config.Save();
            _configSavePending = false;
            RememberCurrentConfigFingerprint();
        }
        catch (Exception ex)
        {
            _configSavePending = true;
            _configSaveAt =
                Time.realtimeSinceStartup + ConfigIoRetrySeconds;
            PortalRulesLogger.LogError(
                $"Error saving configuration: {ex.Message}");
        }
    }

    private void RememberCurrentConfigFingerprint()
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

    private void ShutdownConfigPersistence()
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
                Config.SettingChanged -= OnConfigSettingChanged;
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
                    Config.SaveOnConfigSet = _originalSaveOnConfigSet;
                }
            }
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
