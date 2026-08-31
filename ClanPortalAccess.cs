using System;
using System.Reflection;
using BepInEx.Bootstrap;
using Splatform;

namespace PortalRules;

internal readonly struct PortalClanMembership
{
    private readonly string? _primaryClanId;
    private readonly string? _primaryClanName;
    private readonly string? _guestClanId;

    public PortalClanMembership(
        string primaryClanId,
        string primaryClanName,
        string guestClanId)
    {
        _primaryClanId = Normalize(primaryClanId);
        _primaryClanName = _primaryClanId.Length == 0
            ? string.Empty
            : Normalize(primaryClanName);
        _guestClanId = Normalize(guestClanId);
    }

    public string PrimaryClanId => _primaryClanId ?? string.Empty;
    public string PrimaryClanName => _primaryClanName ?? string.Empty;
    public string GuestClanId => _guestClanId ?? string.Empty;
    public bool HasPrimaryClan => PrimaryClanId.Length != 0;

    public bool ContainsClan(string clanId)
    {
        string canonicalClanId = Normalize(clanId);
        return canonicalClanId.Length != 0 &&
               (string.Equals(
                    canonicalClanId,
                    PrimaryClanId,
                    StringComparison.Ordinal) ||
                string.Equals(
                    canonicalClanId,
                    GuestClanId,
                    StringComparison.Ordinal));
    }

    private static string Normalize(string? value)
    {
        return (value ?? string.Empty).Trim();
    }
}

internal static class ClanPortalAccess
{
    private const string ClanApiTypeName = "Clan.ClanApi";
    private const int MinimumApiVersion = 4;
    private const string ResolvedResultName = "Resolved";

    private static readonly object Sync = new();
    private static MemberInfo? _apiVersionMember;
    private static MethodInfo? _resolveMembershipsMethod;
    private static EventInfo? _registryChangedEventInfo;
    private static Delegate? _registryChangedHandler;
    private static bool _initialized;
    private static bool _apiAvailable;
    private static bool _registryChangedEventBound;
    private static bool _bindingFailureLogged;
    private static bool _invocationFailureLogged;

    internal static event Action? RegistryChanged;

    internal static bool IsServerRegistryAvailable
    {
        get
        {
            lock (Sync)
            {
                return IsApiAvailableLocked();
            }
        }
    }

    internal static void Initialize()
    {
        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            Assembly? installedAssembly = GetInstalledClanAssembly();
            if (installedAssembly == null)
            {
                return;
            }

            BindAssemblyLocked(installedAssembly);
            if (_resolveMembershipsMethod == null ||
                _registryChangedEventInfo == null ||
                !TryReadApiVersion(out int apiVersion) ||
                apiVersion < MinimumApiVersion)
            {
                LogBindingFailureOnce(
                    $"Clan integration requires API version {MinimumApiVersion} " +
                    "with ResolveMemberships and RegistryChanged; integration is disabled.");
                ResetBindingLocked();
                return;
            }

            if (!TryBindRegistryChangedEventLocked())
            {
                LogBindingFailureOnce(
                    "Could not bind Clan.RegistryChanged; Clan integration is disabled.");
                ResetBindingLocked();
                return;
            }

            _apiAvailable = true;
        }
    }

    internal static void Shutdown()
    {
        lock (Sync)
        {
            _initialized = false;
            ResetBindingLocked();
        }
    }

    internal static bool TryResolvePeerMembership(
        ZNetPeer? peer,
        out PortalClanMembership membership)
    {
        membership = default;
        return TryGetPeerIdentity(peer, out string platformId, out long playerId) &&
               TryResolveMembership(platformId, playerId, out membership);
    }

    internal static bool TryResolveLocalMembership(
        out PortalClanMembership membership)
    {
        membership = default;
        if (!TryGetLocalIdentity(out string platformId, out long playerId))
        {
            return false;
        }

        return TryResolveMembership(platformId, playerId, out membership);
    }

    internal static bool TryResolveBuilderMembership(
        PortalBuilder builder,
        out PortalClanMembership membership)
    {
        membership = default;
        return builder.IsValid &&
               builder.CharacterPlayerId != 0L &&
               TryResolveMembership(
                   builder.AccountId,
                   builder.CharacterPlayerId,
                   out membership);
    }

    internal static bool IsRequesterInBuilderPrimaryClan(
        PortalBuilder builder,
        ZNetPeer? requesterPeer)
    {
        if (!TryResolveBuilderMembership(
                builder,
                out PortalClanMembership builderMembership) ||
            !builderMembership.HasPrimaryClan)
        {
            return false;
        }

        bool requesterResolved = requesterPeer != null
            ? TryResolvePeerMembership(
                requesterPeer,
                out PortalClanMembership requesterMembership)
            : TryResolveLocalMembership(out requesterMembership);
        return requesterResolved &&
               requesterMembership.ContainsClan(
                   builderMembership.PrimaryClanId);
    }

    private static bool TryResolveMembership(
        string platformId,
        long playerId,
        out PortalClanMembership membership)
    {
        membership = default;
        if (string.IsNullOrWhiteSpace(platformId) || playerId == 0L)
        {
            return false;
        }

        MethodInfo? resolveMethod;
        lock (Sync)
        {
            if (!IsApiAvailableLocked())
            {
                return false;
            }

            resolveMethod = _resolveMembershipsMethod;
        }

        if (resolveMethod == null)
        {
            return false;
        }

        object?[] arguments =
        {
            platformId.Trim(),
            playerId,
            null,
            null,
            null,
            null
        };

        try
        {
            object? resolution = resolveMethod.Invoke(null, arguments);
            if (!string.Equals(
                    resolution?.ToString(),
                    ResolvedResultName,
                    StringComparison.Ordinal))
            {
                return false;
            }

            membership = new PortalClanMembership(
                arguments[2] as string ?? string.Empty,
                arguments[3] as string ?? string.Empty,
                arguments[4] as string ?? string.Empty);
            return true;
        }
        catch (Exception ex)
        {
            LogInvocationFailureOnce(ex);
            membership = default;
            return false;
        }
    }

    private static bool TryGetPeerIdentity(
        ZNetPeer? peer,
        out string platformId,
        out long playerId)
    {
        platformId = string.Empty;
        playerId = 0L;
        return PublicPortalData.TryGetPeerSteamId64(peer, out platformId) &&
               PublicPortalData.TryGetAuthenticatedPeerPlayerId(peer, out playerId);
    }

    private static bool TryGetLocalIdentity(
        out string platformId,
        out long playerId)
    {
        platformId = string.Empty;
        playerId = 0L;
        try
        {
            IDistributionPlatform? platform = PlatformManager.DistributionPlatform;
            Player? localPlayer = Player.m_localPlayer;
            if (platform?.LocalUser == null || localPlayer == null)
            {
                return false;
            }

            platformId = platform.LocalUser.PlatformUserID.ToString().Trim();
            playerId = localPlayer.GetPlayerID();
            return platformId.Length != 0 && playerId != 0L;
        }
        catch (Exception)
        {
            platformId = string.Empty;
            playerId = 0L;
            return false;
        }
    }

    private static bool IsApiAvailableLocked()
    {
        return _initialized &&
               _apiAvailable &&
               _resolveMembershipsMethod != null &&
               _registryChangedEventBound;
    }

    private static void BindAssemblyLocked(Assembly assembly)
    {
        Type? apiType = assembly.GetType(ClanApiTypeName, throwOnError: false);
        if (apiType == null)
        {
            return;
        }

        _apiVersionMember =
            (MemberInfo?)apiType.GetProperty(
                "ApiVersion",
                BindingFlags.Public | BindingFlags.Static) ??
            apiType.GetField(
                "ApiVersion",
                BindingFlags.Public | BindingFlags.Static);
        _resolveMembershipsMethod = apiType.GetMethod(
            "ResolveMemberships",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            new[]
            {
                typeof(string),
                typeof(long),
                typeof(string).MakeByRefType(),
                typeof(string).MakeByRefType(),
                typeof(string).MakeByRefType(),
                typeof(string).MakeByRefType()
            },
            modifiers: null);
        _registryChangedEventInfo = apiType.GetEvent(
            "RegistryChanged",
            BindingFlags.Public | BindingFlags.Static);
    }

    private static Assembly? GetInstalledClanAssembly()
    {
        try
        {
            return Chainloader.PluginInfos.TryGetValue(
                    PortalRulesPlugin.ClanSoftDependencyGuid,
                    out var pluginInfo)
                ? pluginInfo.Instance?.GetType().Assembly
                : null;
        }
        catch (Exception ex)
        {
            LogBindingFailureOnce(
                $"Could not inspect the installed Clan plugin; integration is disabled: {ex.Message}");
            return null;
        }
    }

    private static bool TryReadApiVersion(out int apiVersion)
    {
        apiVersion = 0;
        try
        {
            object? value = _apiVersionMember switch
            {
                PropertyInfo property => property.GetValue(null, null),
                FieldInfo field => field.GetValue(null),
                _ => null
            };
            if (value == null)
            {
                return false;
            }

            apiVersion = Convert.ToInt32(value);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryBindRegistryChangedEventLocked()
    {
        if (_registryChangedEventInfo?.EventHandlerType != typeof(Action))
        {
            return false;
        }

        try
        {
            _registryChangedHandler = (Action)HandleRegistryChanged;
            _registryChangedEventInfo.AddEventHandler(
                null,
                _registryChangedHandler);
            _registryChangedEventBound = true;
            return true;
        }
        catch (Exception)
        {
            _registryChangedHandler = null;
            _registryChangedEventBound = false;
            return false;
        }
    }

    private static void HandleRegistryChanged()
    {
        Delegate[] subscribers;
        lock (Sync)
        {
            if (!_initialized || RegistryChanged == null)
            {
                return;
            }

            subscribers = RegistryChanged.GetInvocationList();
        }

        foreach (Delegate subscriber in subscribers)
        {
            try
            {
                ((Action)subscriber)();
            }
            catch (Exception)
            {
            }
        }
    }

    private static void ResetBindingLocked()
    {
        if (_registryChangedEventBound &&
            _registryChangedEventInfo != null &&
            _registryChangedHandler != null)
        {
            try
            {
                _registryChangedEventInfo.RemoveEventHandler(
                    null,
                    _registryChangedHandler);
            }
            catch (Exception)
            {
            }
        }

        _apiAvailable = false;
        _registryChangedEventBound = false;
        _registryChangedHandler = null;
        _registryChangedEventInfo = null;
        _resolveMembershipsMethod = null;
        _apiVersionMember = null;
    }

    private static void LogBindingFailureOnce(string message)
    {
        if (_bindingFailureLogged)
        {
            return;
        }

        _bindingFailureLogged = true;
        PortalRulesPlugin.PortalRulesLogger.LogWarning(message);
    }

    private static void LogInvocationFailureOnce(Exception exception)
    {
        lock (Sync)
        {
            if (_invocationFailureLogged)
            {
                return;
            }

            _invocationFailureLogged = true;
        }

        PortalRulesPlugin.PortalRulesLogger.LogWarning(
            $"Clan.ResolveMemberships failed; the request was denied: {exception.Message}");
    }
}
