using System;
using System.Globalization;
using System.Reflection;
using BepInEx.Bootstrap;

namespace PortalRules;

internal enum RequiredGlobalKeyQueryResult : byte
{
    Invalid = 0,
    Unavailable = 1,
    PersonalMissing = 2,
    PersonalPresent = 3,
    SharedMissing = 4,
    SharedPresent = 5
}

/// <summary>
/// Keeps PortalRules' optional YouAreNotWorthy dependency behind one small
/// reflection boundary. If YNW is installed, an old or unavailable API never
/// falls back to a shared world-key check because that would broaden access.
/// </summary>
internal static class RequiredGlobalKeyAccess
{
    private const int SupportedApiVersion = 1;
    private const string ApiTypeName =
        "YouAreNotWorthy.YouAreNotWorthyApi";

    private static bool _initialized;
    private static bool _youAreNotWorthyInstalled;
    private static bool _apiAvailable;
    private static bool _runtimeFailureLogged;
    private static MethodInfo? _queryLocal;
    private static MethodInfo? _queryPeer;
    private static MethodInfo? _showLocalMissingRequirement;

    internal static bool YouAreNotWorthyInstalled
    {
        get
        {
            EnsureInitialized();
            return _youAreNotWorthyInstalled;
        }
    }

    internal static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _youAreNotWorthyInstalled = Chainloader.PluginInfos.TryGetValue(
            PortalRulesPlugin.YouAreNotWorthySoftDependencyGuid,
            out var pluginInfo);
        if (!_youAreNotWorthyInstalled)
        {
            PortalRulesPlugin.PortalRulesLogger.LogInfo(
                "YouAreNotWorthy is not installed; Required GlobalKeys use shared world progression.");
            return;
        }

        try
        {
            Assembly? assembly = pluginInfo.Instance?.GetType().Assembly;
            Type? apiType = assembly?.GetType(
                ApiTypeName,
                throwOnError: false,
                ignoreCase: false);
            FieldInfo? apiVersionField = apiType?.GetField(
                "ApiVersion",
                BindingFlags.Public | BindingFlags.Static);
            object? apiVersionValue = apiVersionField?.IsLiteral == true
                ? apiVersionField.GetRawConstantValue()
                : apiVersionField?.GetValue(null);
            int apiVersion = apiVersionValue != null
                ? Convert.ToInt32(apiVersionValue, CultureInfo.InvariantCulture)
                : 0;
            _queryLocal = apiType?.GetMethod(
                "QueryLocal",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(string) },
                modifiers: null);
            _queryPeer = apiType?.GetMethod(
                "QueryPeer",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(ZNetPeer), typeof(string) },
                modifiers: null);
            _showLocalMissingRequirement = apiType?.GetMethod(
                "TryShowLocalMissingRequirement",
                BindingFlags.Public | BindingFlags.Static,
                binder: null,
                types: new[] { typeof(string) },
                modifiers: null);
            _apiAvailable =
                apiVersion == SupportedApiVersion &&
                _queryLocal != null &&
                _queryPeer != null;

            if (_apiAvailable)
            {
                PortalRulesPlugin.PortalRulesLogger.LogInfo(
                    $"Using YouAreNotWorthy Required GlobalKey API v{apiVersion}.");
                if (_showLocalMissingRequirement == null)
                {
                    PortalRulesPlugin.PortalRulesLogger.LogWarning(
                        "YouAreNotWorthy does not expose its localized missing-requirement " +
                        "message API. Key-gated travel remains fail-closed, but PortalRules " +
                        "will show a generic compatibility message.");
                }
            }
            else
            {
                PortalRulesPlugin.PortalRulesLogger.LogError(
                    $"YouAreNotWorthy is installed but its Required GlobalKey API is unavailable " +
                    $"or incompatible (found v{apiVersion}, expected v{SupportedApiVersion}). " +
                    "Key-gated portals will fail closed.");
            }
        }
        catch (Exception ex)
        {
            _apiAvailable = false;
            PortalRulesPlugin.PortalRulesLogger.LogError(
                $"Could not bind the YouAreNotWorthy Required GlobalKey API; " +
                $"key-gated portals will fail closed: {ex.GetBaseException().Message}");
        }
    }

    internal static void Shutdown()
    {
        _initialized = false;
        _youAreNotWorthyInstalled = false;
        _apiAvailable = false;
        _runtimeFailureLogged = false;
        _queryLocal = null;
        _queryPeer = null;
        _showLocalMissingRequirement = null;
    }

    internal static RequiredGlobalKeyQueryResult QueryLocal(string requiredGlobalKey)
    {
        if (!TryPrepare(requiredGlobalKey, out string normalized))
        {
            return RequiredGlobalKeyQueryResult.Invalid;
        }

        if (normalized.Length == 0)
        {
            return RequiredGlobalKeyQueryResult.SharedPresent;
        }

        EnsureInitialized();
        return _youAreNotWorthyInstalled
            ? Invoke(_queryLocal, new object?[] { normalized })
            : QuerySharedWorldKey(normalized);
    }

    internal static RequiredGlobalKeyQueryResult QueryPeer(
        ZNetPeer? peer,
        string requiredGlobalKey)
    {
        if (!TryPrepare(requiredGlobalKey, out string normalized))
        {
            return RequiredGlobalKeyQueryResult.Invalid;
        }

        if (normalized.Length == 0)
        {
            return RequiredGlobalKeyQueryResult.SharedPresent;
        }

        EnsureInitialized();
        return _youAreNotWorthyInstalled
            ? Invoke(_queryPeer, new object?[] { peer, normalized })
            : QuerySharedWorldKey(normalized);
    }

    internal static bool IsPresent(RequiredGlobalKeyQueryResult result)
    {
        return result is RequiredGlobalKeyQueryResult.PersonalPresent or
            RequiredGlobalKeyQueryResult.SharedPresent;
    }

    /// <summary>
    /// Hands a server-confirmed missing key to YNW so YNW owns localization,
    /// requirement-name resolution, and message rate limiting. A successful
    /// invocation counts as handled even when YNW suppresses a repeated message.
    /// </summary>
    internal static bool TryShowLocalMissingRequirement(
        string requiredGlobalKey)
    {
        if (!TryPrepare(requiredGlobalKey, out string normalized) ||
            normalized.Length == 0)
        {
            return false;
        }

        EnsureInitialized();
        if (!_youAreNotWorthyInstalled ||
            !_apiAvailable ||
            _showLocalMissingRequirement == null)
        {
            return false;
        }

        try
        {
            _ = _showLocalMissingRequirement.Invoke(
                null,
                new object?[] { normalized });
            return true;
        }
        catch (Exception ex)
        {
            LogRuntimeFailureOnce(
                "YouAreNotWorthy failed to show its missing-requirement message; " +
                $"PortalRules will show a generic compatibility message: {ex.GetBaseException().Message}");
            return false;
        }
    }

    private static bool TryPrepare(string value, out string normalized)
    {
        return PublicPortalData.TryNormalizeRequiredGlobalKey(
            value,
            out normalized,
            out _);
    }

    private static RequiredGlobalKeyQueryResult QuerySharedWorldKey(string key)
    {
        ZoneSystem? zoneSystem = ZoneSystem.instance;
        if (zoneSystem == null)
        {
            return RequiredGlobalKeyQueryResult.Unavailable;
        }

        string baseKey = ZoneSystem.GetKeyValue(
            key.ToLowerInvariant(),
            out string requiredValue,
            out _);
        if (!zoneSystem.GetGlobalKey(baseKey, out string currentValue))
        {
            return RequiredGlobalKeyQueryResult.SharedMissing;
        }

        return string.IsNullOrEmpty(requiredValue) ||
               string.Equals(currentValue, requiredValue, StringComparison.Ordinal)
            ? RequiredGlobalKeyQueryResult.SharedPresent
            : RequiredGlobalKeyQueryResult.SharedMissing;
    }

    private static RequiredGlobalKeyQueryResult Invoke(
        MethodInfo? method,
        object?[] arguments)
    {
        if (!_apiAvailable || method == null)
        {
            return RequiredGlobalKeyQueryResult.Unavailable;
        }

        try
        {
            object? value = method.Invoke(null, arguments);
            int numericValue = value != null
                ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
                : -1;
            if (numericValue is >= byte.MinValue and <= byte.MaxValue &&
                Enum.IsDefined(
                    typeof(RequiredGlobalKeyQueryResult),
                    (byte)numericValue))
            {
                RequiredGlobalKeyQueryResult result =
                    (RequiredGlobalKeyQueryResult)(byte)numericValue;
                if (result == RequiredGlobalKeyQueryResult.Unavailable)
                {
                    LogRuntimeFailureOnce(
                        "YouAreNotWorthy could not resolve a character key snapshot; " +
                        "the affected portal view remains fail-closed until it is available.");
                }

                return result;
            }

            LogRuntimeFailureOnce(
                $"YouAreNotWorthy returned unknown Required GlobalKey result {numericValue}; " +
                "the affected portal view remains fail-closed.");
        }
        catch (Exception ex)
        {
            LogRuntimeFailureOnce(
                $"YouAreNotWorthy Required GlobalKey query failed; " +
                $"the affected portal view remains fail-closed: {ex.GetBaseException().Message}");
        }

        return RequiredGlobalKeyQueryResult.Unavailable;
    }

    private static void LogRuntimeFailureOnce(string message)
    {
        if (_runtimeFailureLogged)
        {
            return;
        }

        _runtimeFailureLogged = true;
        PortalRulesPlugin.PortalRulesLogger.LogWarning(message);
    }

    private static void EnsureInitialized()
    {
        if (!_initialized)
        {
            Initialize();
        }
    }
}
