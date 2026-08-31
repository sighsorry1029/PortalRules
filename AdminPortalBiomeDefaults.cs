using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using YamlDotNet.Serialization;

namespace PortalRules;

internal static class AdminPortalBiomeDefaults
{
    private sealed class BiomeDefaultsYaml
    {
        [YamlMember(Alias = "format_version")]
        public int FormatVersion { get; set; }

        [YamlMember(Alias = "biomes")]
        public Dictionary<string, string?>? Biomes { get; set; }
    }

    private const int FormatVersion = 1;
    private const int MaximumBiomeCount = 4096;
    private const int MaximumBiomeNameLength = 128;
    private const int MaximumReloadAttempts = 5;
    private const long MaximumFileBytes = 512L * 1024L;
    private const string DirectoryName = "PortalRules";
    private const string FileName = "admin-portal-biome-global-keys.yml";
    private const float RegistryReadyFallbackSeconds = 5f;

    private static readonly KeyValuePair<string, string>[] VanillaTemplate =
    {
        new("Meadows", ""),
        new("BlackForest", "defeated_eikthyr"),
        new("Swamp", "defeated_gdking"),
        new("Ocean", "defeated_bonemass"),
        new("Mountain", "defeated_bonemass"),
        new("Plains", "defeated_dragon"),
        new("Mistlands", "defeated_goblinking"),
        new("AshLands", "defeated_queen"),
        new("DeepNorth", "defeated_fader")
    };

    private static readonly HashSet<string> AmbiguousPlainYamlScalars =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "null", "true", "false", "y", "n", "yes", "no", "on", "off"
        };

    private static readonly TimeSpan ReloadDebounce = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReloadRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WatcherRestartRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithDuplicateKeyChecking()
        .Build();

    private static Dictionary<string, string> _defaults =
        new(StringComparer.OrdinalIgnoreCase);
    private static FileSystemWatcher? _watcher;
    private static string _configurationDirectory = "";
    private static string _filePath = "";
    private static bool _active;
    private static bool _hasValidSnapshot;
    private static bool _templatePending;
    private static bool _biomeRegistryReady;
    private static bool _registryFallbackLogged;
    private static float _registryFallbackNotBefore;
    private static DateTime _templateRetryNotBeforeUtc;
    private static DateTime _nextPollUtc;
    private static int _reloadRequested;
    private static int _reloadAttempts;
    private static int _watcherRestartRequested;
    private static long _reloadNotBeforeUtcTicks;
    private static long _watcherRestartNotBeforeUtcTicks;

    internal static bool IsBiomeRegistryReady => _biomeRegistryReady;
    internal static bool HasValidSnapshot => _active && _hasValidSnapshot;

    internal static void BeginServerSession()
    {
        if (_active || ZNet.instance == null || !ZNet.instance.IsServer())
        {
            return;
        }

        // A failed file-store initialization may be retried after ZoneSystem has
        // already completed. Preserve that lifecycle fact within the same network
        // session so a config I/O failure cannot block the portal catalog.
        bool registryAlreadyReady = _biomeRegistryReady;
        EndServerSession();
        _active = true;
        _defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _hasValidSnapshot = false;
        _templatePending = false;
        _biomeRegistryReady = registryAlreadyReady;
        _registryFallbackLogged = false;
        _registryFallbackNotBefore =
            Time.realtimeSinceStartup + RegistryReadyFallbackSeconds;
        _templateRetryNotBeforeUtc = DateTime.MinValue;
        _nextPollUtc = DateTime.UtcNow + PollingInterval;
        Interlocked.Exchange(ref _reloadRequested, 0);
        Interlocked.Exchange(ref _reloadAttempts, 0);
        Interlocked.Exchange(ref _watcherRestartRequested, 0);
        Interlocked.Exchange(ref _reloadNotBeforeUtcTicks, 0L);
        Interlocked.Exchange(ref _watcherRestartNotBeforeUtcTicks, 0L);

        _configurationDirectory = Path.Combine(Paths.ConfigPath, DirectoryName);
        _filePath = Path.Combine(_configurationDirectory, FileName);

        try
        {
            Directory.CreateDirectory(_configurationDirectory);
            // Expand World Data registers its custom biome names after the network
            // session can begin. Defer first-file generation until the catalog's
            // registry-ready point so Enum.GetNames observes that final registry.
            _templatePending = !File.Exists(_filePath);
            try
            {
                StartWatcher();
            }
            catch (Exception ex)
            {
                StopWatcher();
                Interlocked.Exchange(ref _watcherRestartRequested, 1);
                ScheduleWatcherRestart(WatcherRestartRetryDelay);
                PortalRulesPlugin.PortalRulesLogger.LogError(
                    $"Failed to watch {FileName}; periodic reload remains active: " +
                    ex.Message);
            }

            if (_templatePending)
            {
                PortalRulesPlugin.PortalRulesLogger.LogInfo(
                    $"{FileName} will be generated after the biome registry is ready.");
            }
            else if (!TryReload(logOnlyWhenChanged: false, out _))
            {
                ScheduleReload(ReloadRetryDelay);
            }
        }
        catch (Exception ex)
        {
            StopWatcher();
            _active = false;
            _templatePending = false;
            PortalRulesPlugin.PortalRulesLogger.LogError(
                $"Failed to initialize {FileName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates the first configuration snapshot once the biome registry is ready.
    /// This method is intentionally idempotent and must be called only from a
    /// server lifecycle point at which Expand World Data has registered its biomes.
    /// </summary>
    internal static bool EnsureTemplateReady()
    {
        if (ZNet.instance == null || !ZNet.instance.IsServer())
        {
            return false;
        }

        BeginServerSession();
        if (!_active)
        {
            return false;
        }

        if (!_templatePending)
        {
            if (_hasValidSnapshot || !File.Exists(_filePath))
            {
                return _hasValidSnapshot;
            }

            return TryReloadImmediately();
        }

        if (!File.Exists(_filePath) &&
            DateTime.UtcNow < _templateRetryNotBeforeUtc)
        {
            return false;
        }

        string sessionFilePath = _filePath;
        try
        {
            bool created = false;
            if (!File.Exists(sessionFilePath))
            {
                string template = BuildRegistryTemplate();
                created = TryCreateFileWithoutOverwrite(sessionFilePath, template);
            }

            // A server session can be torn down between lifecycle callbacks. Do
            // not publish a snapshot into a different or ended session.
            if (!_active ||
                !string.Equals(_filePath, sessionFilePath, StringComparison.Ordinal) ||
                !File.Exists(sessionFilePath))
            {
                return false;
            }

            _templatePending = false;
            _templateRetryNotBeforeUtc = DateTime.MinValue;
            if (created)
            {
                PortalRulesPlugin.PortalRulesLogger.LogInfo(
                    $"Generated {FileName} from the registered biome names.");
            }

            return TryReloadImmediately();
        }
        catch (Exception ex)
        {
            _templateRetryNotBeforeUtc = DateTime.UtcNow + ReloadRetryDelay;
            PortalRulesPlugin.PortalRulesLogger.LogError(
                $"Failed to generate {FileName}; it will remain pending until " +
                $"the next retry: {ex.Message}");
            return false;
        }
    }

    internal static void EndServerSession()
    {
        StopWatcher();
        _active = false;
        _defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _hasValidSnapshot = false;
        _templatePending = false;
        _biomeRegistryReady = false;
        _registryFallbackLogged = false;
        _registryFallbackNotBefore = 0f;
        _templateRetryNotBeforeUtc = DateTime.MinValue;
        _nextPollUtc = DateTime.MinValue;
        _configurationDirectory = "";
        _filePath = "";
        Interlocked.Exchange(ref _reloadRequested, 0);
        Interlocked.Exchange(ref _reloadAttempts, 0);
        Interlocked.Exchange(ref _watcherRestartRequested, 0);
        Interlocked.Exchange(ref _reloadNotBeforeUtcTicks, 0L);
        Interlocked.Exchange(ref _watcherRestartNotBeforeUtcTicks, 0L);
    }

    internal static bool Tick()
    {
        TryCompleteBiomeRegistryFallback();
        if (!_active)
        {
            return false;
        }

        DateTime nowUtc = DateTime.UtcNow;
        if (Volatile.Read(ref _watcherRestartRequested) != 0 &&
            nowUtc.Ticks >=
            Interlocked.Read(ref _watcherRestartNotBeforeUtcTicks))
        {
            Interlocked.Exchange(ref _watcherRestartRequested, 0);
            try
            {
                StartWatcher();
                RequestReload();
            }
            catch (Exception ex)
            {
                PortalRulesPlugin.PortalRulesLogger.LogError(
                    $"Failed to restart the {FileName} watcher: {ex.Message}");
                Interlocked.Exchange(ref _watcherRestartRequested, 1);
                ScheduleWatcherRestart(WatcherRestartRetryDelay);
            }
        }

        if (_templatePending)
        {
            if (!File.Exists(_filePath))
            {
                // Before the registry-ready point, generation must remain deferred
                // so Expand World Data can finish registering custom biome names.
                // Afterwards, retry transient create failures at a bounded rate.
                if (_biomeRegistryReady && nowUtc >= _templateRetryNotBeforeUtc)
                {
                    bool hadValidSnapshot = _hasValidSnapshot;
                    bool loaded = EnsureTemplateReady();
                    return loaded && !hadValidSnapshot;
                }

                return false;
            }

            // An operator created the file while registry generation was pending.
            // Existing files always win and are never replaced by the template.
            _templatePending = false;
            ScheduleReload(TimeSpan.Zero);
        }

        if (nowUtc >= _nextPollUtc)
        {
            _nextPollUtc = nowUtc + PollingInterval;
            if (Volatile.Read(ref _reloadRequested) == 0)
            {
                ScheduleReload(TimeSpan.Zero);
            }
        }

        if (Volatile.Read(ref _reloadRequested) == 0 ||
            nowUtc.Ticks < Interlocked.Read(ref _reloadNotBeforeUtcTicks))
        {
            return false;
        }

        Interlocked.Exchange(ref _reloadRequested, 0);
        if (TryReload(logOnlyWhenChanged: true, out bool changed))
        {
            Interlocked.Exchange(ref _reloadAttempts, 0);
            return changed;
        }

        if (Interlocked.Increment(ref _reloadAttempts) <= MaximumReloadAttempts)
        {
            ScheduleReload(ReloadRetryDelay);
        }

        return false;
    }

    private static void TryCompleteBiomeRegistryFallback()
    {
        if (_biomeRegistryReady ||
            ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            ZoneSystem.instance == null ||
            Time.realtimeSinceStartup < _registryFallbackNotBefore)
        {
            return;
        }

        if (!_registryFallbackLogged)
        {
            _registryFallbackLogged = true;
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                "The ZoneSystem biome-registry completion hook was not observed; " +
                "using the currently registered biome names as a fallback.");
        }

        CompleteBiomeRegistryInitialization();
    }

    private static void CompleteBiomeRegistryInitialization()
    {
        BeginServerSession();
        if (_biomeRegistryReady)
        {
            return;
        }

        _biomeRegistryReady = true;
        if (_active)
        {
            EnsureTemplateReady();
        }

        PublicPortalCatalog.RefreshAndBroadcast();
    }

    internal static bool TryResolve(Vector3 position, out string requiredGlobalKey)
    {
        requiredGlobalKey = "";
        if (!_active || !_hasValidSnapshot || WorldGenerator.instance == null)
        {
            return false;
        }

        Heightmap.Biome biome = WorldGenerator.instance.GetBiome(position);
        string? biomeName = Enum.GetName(typeof(Heightmap.Biome), biome);
        // Expand World Data patches Enum.GetName for its runtime biome registry.
        // Do not fall back to the dynamic numeric value, which is not stable.
        return !string.IsNullOrWhiteSpace(biomeName) &&
               _defaults.TryGetValue(biomeName.Trim(), out requiredGlobalKey);
    }

    private static string BuildRegistryTemplate()
    {
        return BuildRegistryTemplate(
            Enum.GetNames(typeof(Heightmap.Biome)));
    }

    private static string BuildRegistryTemplate(string[] registeredNames)
    {
        if (registeredNames.Length > MaximumBiomeCount)
        {
            throw new InvalidDataException(
                $"biome registry exceeds {MaximumBiomeCount} entries");
        }

        Dictionary<string, string> discovered =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (string rawName in registeredNames)
        {
            string biomeName = (rawName ?? "").Trim();
            if (string.Equals(biomeName, "None", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(biomeName, "All", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                ValidateBiomeName(biomeName);
            }
            catch (InvalidDataException)
            {
                string displayedName = biomeName.Length <= 64
                    ? biomeName
                    : biomeName.Substring(0, 64) + "...";
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    $"Skipped invalid registered biome name " +
                    $"{FormatYamlKey(displayedName)} while generating {FileName}.");
                continue;
            }

            if (!discovered.TryGetValue(biomeName, out string existingName) ||
                string.CompareOrdinal(biomeName, existingName) < 0)
            {
                // Enum aliases and third-party registries may differ only by case.
                // Retain a deterministic spelling for the single YAML entry.
                discovered[biomeName] = biomeName;
            }
        }

        StringBuilder yaml = new();
        yaml.AppendLine("# PortalRules default GlobalKey requirements for newly initialized Admin portals.");
        yaml.AppendLine("# This file is generated once from the server's registered biome names and is");
        yaml.AppendLine("# never automatically edited. Delete it before the next server session to rebuild it.");
        yaml.AppendLine("# Biome names are case-insensitive. Expand World Data custom biomes use the stable");
        yaml.AppendLine("# `biome:` name from its biome YAML, not a translated name or numeric biome value.");
        yaml.AppendLine("# The biome is resolved from each portal's actual world position.");
        yaml.AppendLine("# A RequiredGlobalKey already stored in the portal ZDO wins over this default.");
        yaml.AppendLine("# This includes InfinityHammer blueprint data and EWD objectData/locationObjectData.");
        yaml.AppendLine("# An explicitly stored empty RequiredGlobalKey suppresses the biome default.");
        yaml.AppendLine("# With YouAreNotWorthy, the selected key is checked per character; otherwise it");
        yaml.AppendLine("# is checked against the world's shared GlobalKeys.");
        yaml.AppendLine("# An empty value means that biome has no default required GlobalKey.");
        yaml.AppendLine("# Reloads affect only Admin portals whose initial authority is resolved afterwards.");
        yaml.Append("format_version: ")
            .AppendLine(FormatVersion.ToString(CultureInfo.InvariantCulture));
        yaml.AppendLine("biomes:");

        foreach (KeyValuePair<string, string> vanilla in VanillaTemplate)
        {
            discovered.Remove(vanilla.Key);
            AppendBiome(yaml, vanilla.Key, vanilla.Value);
        }

        List<string> additionalNames = new(discovered.Values);
        additionalNames.Sort(CompareBiomeNames);
        if (VanillaTemplate.Length + additionalNames.Count > MaximumBiomeCount)
        {
            throw new InvalidDataException(
                $"generated biome list exceeds {MaximumBiomeCount} entries");
        }

        foreach (string biomeName in additionalNames)
        {
            AppendBiome(yaml, biomeName, "");
        }

        string template = yaml.ToString();
        if (Encoding.UTF8.GetByteCount(template) > MaximumFileBytes)
        {
            throw new InvalidDataException(
                $"generated template exceeds {MaximumFileBytes} bytes");
        }

        return template;
    }

    private static int CompareBiomeNames(string left, string right)
    {
        int caseInsensitive = StringComparer.OrdinalIgnoreCase.Compare(left, right);
        return caseInsensitive != 0
            ? caseInsensitive
            : StringComparer.Ordinal.Compare(left, right);
    }

    private static void AppendBiome(
        StringBuilder yaml,
        string biomeName,
        string requiredGlobalKey)
    {
        yaml.Append("  ")
            .Append(FormatYamlKey(biomeName))
            .Append(':');
        if (requiredGlobalKey.Length != 0)
        {
            yaml.Append(' ')
                .Append(requiredGlobalKey);
        }

        yaml.AppendLine();
    }

    private static string FormatYamlKey(string value)
    {
        if (IsSafePlainYamlKey(value))
        {
            return value;
        }

        StringBuilder escaped = new(value.Length + 2);
        escaped.Append('"');
        foreach (char character in value)
        {
            switch (character)
            {
                case '\\':
                    escaped.Append("\\\\");
                    break;
                case '"':
                    escaped.Append("\\\"");
                    break;
                case '\u2028':
                case '\u2029':
                    escaped.Append("\\u")
                        .Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
                    break;
                default:
                    if (char.IsControl(character))
                    {
                        escaped.Append("\\u")
                            .Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        escaped.Append(character);
                    }

                    break;
            }
        }

        return escaped.Append('"').ToString();
    }

    private static bool IsSafePlainYamlKey(string value)
    {
        if (value.Length == 0 ||
            AmbiguousPlainYamlScalars.Contains(value) ||
            !(char.IsLetter(value[0]) || value[0] == '_'))
        {
            return false;
        }

        foreach (char character in value)
        {
            if (!char.IsLetterOrDigit(character) &&
                character != '_' &&
                character != '-')
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryCreateFileWithoutOverwrite(string path, string content)
    {
        string directory = Path.GetDirectoryName(path) ??
                           throw new InvalidOperationException(
                               "Configuration directory is unavailable.");
        string stagingPath = Path.Combine(
            directory,
            "." + Path.GetFileName(path) + "." +
            Guid.NewGuid().ToString("N") + ".pending");
        try
        {
            PortalRulesFileIO.WriteAtomicFile(
                stagingPath,
                content,
                backupPath: null);
            try
            {
                // Same-directory move publishes the fully written file atomically,
                // and the no-overwrite overload protects a concurrently created file.
                File.Move(stagingPath, path);
                return true;
            }
            catch (IOException) when (File.Exists(path))
            {
                return false;
            }
        }
        finally
        {
            if (File.Exists(stagingPath))
            {
                File.Delete(stagingPath);
            }
        }
    }

    private static bool TryReloadImmediately()
    {
        Interlocked.Exchange(ref _reloadRequested, 0);
        Interlocked.Exchange(ref _reloadAttempts, 0);
        if (TryReload(logOnlyWhenChanged: false, out _))
        {
            _nextPollUtc = DateTime.UtcNow + PollingInterval;
            return true;
        }

        ScheduleReload(ReloadRetryDelay);
        return false;
    }

    private static bool TryReload(bool logOnlyWhenChanged, out bool changed)
    {
        changed = false;
        if (!File.Exists(_filePath))
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                $"{FileName} is missing; retaining the last valid biome defaults. " +
                "Use 'biomes: {}' to clear them.");
            return false;
        }

        try
        {
            FileInfo file = new(_filePath);
            if (file.Length > MaximumFileBytes)
            {
                throw new InvalidDataException(
                    $"file exceeds {MaximumFileBytes} bytes");
            }

            BiomeDefaultsYaml? data =
                Deserializer.Deserialize<BiomeDefaultsYaml>(File.ReadAllText(_filePath));
            if (data == null ||
                data.FormatVersion != FormatVersion ||
                data.Biomes == null)
            {
                throw new InvalidDataException(
                    $"format_version must be {FormatVersion} and biomes must be present");
            }

            if (data.Biomes.Count > MaximumBiomeCount)
            {
                throw new InvalidDataException(
                    $"biomes exceeds {MaximumBiomeCount} entries");
            }

            Dictionary<string, string> loaded =
                new(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string?> pair in data.Biomes)
            {
                string biomeName = (pair.Key ?? "").Trim();
                ValidateBiomeName(biomeName);
                if (!PublicPortalData.TryNormalizeRequiredGlobalKey(
                        pair.Value,
                        out string requiredGlobalKey,
                        out RequiredGlobalKeyValidationFailure failure))
                {
                    throw new InvalidDataException(
                        $"GlobalKey for biome '{biomeName}' is invalid ({failure})");
                }

                if (loaded.ContainsKey(biomeName))
                {
                    throw new InvalidDataException(
                        $"biome '{biomeName}' is duplicated after case and whitespace normalization");
                }

                loaded.Add(biomeName, requiredGlobalKey);
            }

            changed = !_hasValidSnapshot || !DictionariesEqual(_defaults, loaded);
            _defaults = loaded;
            _hasValidSnapshot = true;
            if (!logOnlyWhenChanged || changed)
            {
                PortalRulesPlugin.PortalRulesLogger.LogInfo(
                    $"Loaded {_defaults.Count} Admin portal biome GlobalKey default(s).");
            }

            return true;
        }
        catch (Exception ex)
        {
            PortalRulesPlugin.PortalRulesLogger.LogError(
                $"Failed to reload {FileName}; retaining the last valid biome defaults: " +
                ex.Message);
            return false;
        }
    }

    private static void ValidateBiomeName(string biomeName)
    {
        if (biomeName.Length == 0)
        {
            throw new InvalidDataException("biome names must not be empty");
        }

        if (biomeName.Length > MaximumBiomeNameLength)
        {
            throw new InvalidDataException(
                $"biome name '{biomeName}' exceeds {MaximumBiomeNameLength} characters");
        }

        foreach (char character in biomeName)
        {
            if (char.IsControl(character))
            {
                throw new InvalidDataException(
                    $"biome name '{biomeName}' contains control characters");
            }
        }

        if (long.TryParse(
                biomeName,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out _))
        {
            throw new InvalidDataException(
                $"biome '{biomeName}' is numeric; use its stable biome name instead");
        }
    }

    private static bool DictionariesEqual(
        Dictionary<string, string> left,
        Dictionary<string, string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, string> pair in left)
        {
            if (!right.TryGetValue(pair.Key, out string value) ||
                !string.Equals(value, pair.Value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void StartWatcher()
    {
        StopWatcher();
        _watcher = new FileSystemWatcher(_configurationDirectory, FileName)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName |
                           NotifyFilters.LastWrite |
                           NotifyFilters.Size
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;
        _watcher.Deleted += OnFileChanged;
        _watcher.Renamed += OnFileRenamed;
        _watcher.Error += OnWatcherError;
        _watcher.EnableRaisingEvents = true;
    }

    private static void StopWatcher()
    {
        if (_watcher == null)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnFileChanged;
        _watcher.Created -= OnFileChanged;
        _watcher.Deleted -= OnFileChanged;
        _watcher.Renamed -= OnFileRenamed;
        _watcher.Error -= OnWatcherError;
        _watcher.Dispose();
        _watcher = null;
    }

    private static void OnFileChanged(object sender, FileSystemEventArgs args)
    {
        RequestReload();
    }

    private static void OnFileRenamed(object sender, RenamedEventArgs args)
    {
        RequestReload();
    }

    private static void OnWatcherError(object sender, ErrorEventArgs args)
    {
        PortalRulesPlugin.PortalRulesLogger.LogError(
            $"The {FileName} watcher failed and will be restarted: " +
            args.GetException().Message);
        Interlocked.Exchange(ref _watcherRestartRequested, 1);
        ScheduleWatcherRestart(WatcherRestartRetryDelay);
        RequestReload();
    }

    private static void RequestReload()
    {
        Interlocked.Exchange(ref _reloadAttempts, 0);
        ScheduleReload(ReloadDebounce);
    }

    private static void ScheduleReload(TimeSpan delay)
    {
        Interlocked.Exchange(
            ref _reloadNotBeforeUtcTicks,
            (DateTime.UtcNow + delay).Ticks);
        Interlocked.Exchange(ref _reloadRequested, 1);
    }

    private static void ScheduleWatcherRestart(TimeSpan delay)
    {
        Interlocked.Exchange(
            ref _watcherRestartNotBeforeUtcTicks,
            (DateTime.UtcNow + delay).Ticks);
    }

    [HarmonyPatch(typeof(ZoneSystem), "Start")]
    [HarmonyAfter("expand_world_data")]
    [HarmonyPriority(Priority.Last)]
    private static class BiomeRegistryReadyPatch
    {
        private static void Postfix()
        {
            if (ZNet.instance != null && ZNet.instance.IsServer())
            {
                CompleteBiomeRegistryInitialization();
            }
        }
    }
}
