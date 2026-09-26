using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace PortalRules;

[BepInPlugin(ModGUID, ModName, ModVersion)]
[BepInDependency(ClanSoftDependencyGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(CurrencyPocketSoftDependencyGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(EndosCoinPurseSoftDependencyGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInDependency(YouAreNotWorthySoftDependencyGuid, BepInDependency.DependencyFlags.SoftDependency)]
[BepInIncompatibility(TargetPortalIncompatibilityGuid)]
public class PortalRulesPlugin : BaseUnityPlugin
{
    internal const string ModName = "PortalRules";
    internal const string ModVersion = "1.1.0";
    internal const string Author = "sighsorry";
    internal const string ModGUID = $"{Author}.{ModName}";
    internal const string ClanSoftDependencyGuid = "sighsorry.Clan";
    internal const string CurrencyPocketSoftDependencyGuid = "Azumatt.CurrencyPocket";
    internal const string EndosCoinPurseSoftDependencyGuid = "EndosCoinPurse";
    internal const string YouAreNotWorthySoftDependencyGuid = "sighsorry.YouAreNotWorthy";
    internal const string TargetPortalIncompatibilityGuid = "org.bepinex.plugins.targetportal";
    private readonly Harmony _harmony = new(ModGUID);
    public static readonly ManualLogSource PortalRulesLogger = BepInEx.Logging.Logger.CreateLogSource(ModName);
    private int _clanRegistryDirty;

    public enum Toggle
    {
        On = 1,
        Off = 0
    }

    public void Awake()
    {
        Game.isModded = true;
        try
        {
            PortalRulesLocalization.Initialize();
            PublicPortalConfig.Initialize(Config);
            RequiredGlobalKeyAccess.Initialize();

            Assembly assembly = Assembly.GetExecutingAssembly();
            ClanPortalAccess.RegistryChanged += OnClanRegistryChanged;
            ClanPortalAccess.Initialize();
            _harmony.PatchAll(assembly);
            PublicPortalConfig.StartPersistence();
        }
        catch
        {
            PublicPortalConfig.Shutdown();
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
        PublicPortalConfig.Tick();
        if (Interlocked.Exchange(ref _clanRegistryDirty, 0) != 0)
        {
            PublicPortalCatalog.RefreshClanViews();
        }
    }

    private void OnDestroy()
    {
        try
        {
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

    internal static bool IsAdmin =>
        ZNet.instance == null ||
        ZNet.instance.IsServer() ||
        PublicPortalData.ServerAssignedIsAdmin;

    internal static bool HasAdminDebugAccess =>
        IsAdmin && Player.m_debugMode;
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
