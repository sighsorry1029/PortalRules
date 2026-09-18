using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using BepInEx;
using YamlDotNet.Serialization;

namespace PortalRules;

internal static class PortalAccountStore
{
    private sealed class PlayerIdentitiesYaml
    {
        [YamlMember(Alias = "format_version")]
        public int FormatVersion { get; set; }

        [YamlMember(Alias = "identities")]
        public Dictionary<string, string>? Identities { get; set; }
    }

    private sealed class PortalLimitOverridesYaml
    {
        [YamlMember(Alias = "format_version")]
        public int FormatVersion { get; set; }

        [YamlMember(Alias = "overrides")]
        public Dictionary<string, string>? Overrides { get; set; }
    }

    private readonly struct PortalLimitOverride
    {
        public readonly int PortalLimit;
        public readonly int? InviteLimit;

        public PortalLimitOverride(int portalLimit, int? inviteLimit)
        {
            PortalLimit = portalLimit;
            InviteLimit = inviteLimit;
        }

        public bool Matches(PortalLimitOverride other)
        {
            return PortalLimit == other.PortalLimit &&
                   InviteLimit == other.InviteLimit;
        }
    }

    private const int IdentityFormatVersion = 1;
    private const int OverrideFormatVersion = 2;
    private const int MaximumIdentityCount = 16384;
    private const int MaximumOverrideCount = 4096;
    private const int MaximumOverrideReloadAttempts = 5;
    private const long MaximumIdentityFileBytes = 2L * 1024L * 1024L;
    private const long MaximumOverrideFileBytes = 512L * 1024L;
    private const string DirectoryName = "PortalRules";
    private static string IdentityFileName =>
        PublicPortalData.IsCrossplay ? "player-identities-playfab.yml" : "player-identities.yml";
    private const string OverrideFileName = "portal-limit-overrides.yml";

    private static readonly TimeSpan IdentitySaveDebounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IdentityMaximumSaveDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdentitySaveRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OverrideReloadDebounce = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan OverrideReloadRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan OverridePollingInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WatcherRestartRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithDuplicateKeyChecking()
        .Build();
    private static readonly Dictionary<long, string> PlayerIdentities = new();
    private static readonly HashSet<long> ConflictedPlayerIds = new();

    private static Dictionary<string, PortalLimitOverride> _portalLimitOverrides =
        new(StringComparer.Ordinal);
    private static FileSystemWatcher? _overrideWatcher;
    private static string _configurationDirectory = "";
    private static string _identityFilePath = "";
    private static string _overrideFilePath = "";
    private static bool _active;
    private static bool _hasValidOverrideSnapshot;
    private static bool _identityDirty;
    private static DateTime _identityFirstDirtyUtc;
    private static DateTime _identitySaveNotBeforeUtc;
    private static DateTime _nextOverridePollUtc;
    private static int _overrideReloadRequested;
    private static int _overrideReloadAttempts;
    private static int _overrideWatcherRestartRequested;
    private static long _overrideReloadNotBeforeUtcTicks;
    private static long _overrideWatcherRestartNotBeforeUtcTicks;

    internal static bool IsActive => _active;
    internal static bool HasValidOverrideSnapshot =>
        _active && _hasValidOverrideSnapshot;

    internal static void BeginServerSession()
    {
        if (_active || ZNet.instance == null || !ZNet.instance.IsServer())
        {
            return;
        }

        if (!EndServerSession())
        {
            PortalRulesPlugin.PortalRulesLogger.LogError(
                "PortalRules identity storage retained unsaved data from the previous session; " +
                "the new session will remain unavailable until that data can be saved.");
            return;
        }

        _active = true;
        PlayerIdentities.Clear();
        ConflictedPlayerIds.Clear();
        _portalLimitOverrides =
            new Dictionary<string, PortalLimitOverride>(StringComparer.Ordinal);
        _hasValidOverrideSnapshot = false;
        _identityDirty = false;
        _identityFirstDirtyUtc = DateTime.MinValue;
        _identitySaveNotBeforeUtc = DateTime.MinValue;
        _nextOverridePollUtc = DateTime.UtcNow + OverridePollingInterval;
        Interlocked.Exchange(ref _overrideReloadRequested, 0);
        Interlocked.Exchange(ref _overrideReloadAttempts, 0);
        Interlocked.Exchange(ref _overrideWatcherRestartRequested, 0);
        Interlocked.Exchange(ref _overrideReloadNotBeforeUtcTicks, 0L);
        Interlocked.Exchange(ref _overrideWatcherRestartNotBeforeUtcTicks, 0L);

        _configurationDirectory = Path.Combine(Paths.ConfigPath, DirectoryName);
        _identityFilePath = Path.Combine(_configurationDirectory, IdentityFileName);
        _overrideFilePath = Path.Combine(_configurationDirectory, OverrideFileName);

        try
        {
            Directory.CreateDirectory(_configurationDirectory);
            LoadPlayerIdentities();
            EnsureOverrideTemplate();
            try
            {
                StartOverrideWatcher();
            }
            catch (Exception ex)
            {
                StopOverrideWatcher();
                Interlocked.Exchange(ref _overrideWatcherRestartRequested, 1);
                ScheduleOverrideWatcherRestart(WatcherRestartRetryDelay);
                PortalRulesPlugin.PortalRulesLogger.LogError(
                    $"Failed to watch {OverrideFileName}; periodic reload remains active: " +
                    ex.Message);
            }

            if (!TryReloadOverrides(logOnlyWhenChanged: false, out _))
            {
                ScheduleOverrideReload(OverrideReloadRetryDelay);
            }
        }
        catch (Exception ex)
        {
            StopOverrideWatcher();
            _active = false;
            PortalRulesPlugin.PortalRulesLogger.LogError(
                $"Failed to initialize PortalRules account files: {ex.Message}");
        }

        if (_active && PublicPortalData.IsCrossplay)
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                "PortalRules uses PlayFab entity ownership in Crossplay. " +
                "The overall account portal limit is automatically disabled; the saved setting is unchanged.");
        }
    }

    internal static bool EndServerSession()
    {
        bool flushed = FlushPlayerIdentities();
        StopOverrideWatcher();
        _active = false;
        if (!flushed && _identityDirty)
        {
            PortalRulesPlugin.PortalRulesLogger.LogError(
                "PortalRules identity storage could not complete its final save; " +
                "unsaved identities are being retained in memory for retry.");
            return false;
        }

        PlayerIdentities.Clear();
        ConflictedPlayerIds.Clear();
        _portalLimitOverrides =
            new Dictionary<string, PortalLimitOverride>(StringComparer.Ordinal);
        _hasValidOverrideSnapshot = false;
        _identityDirty = false;
        _identityFirstDirtyUtc = DateTime.MinValue;
        _identitySaveNotBeforeUtc = DateTime.MinValue;
        _nextOverridePollUtc = DateTime.MinValue;
        _configurationDirectory = "";
        _identityFilePath = "";
        _overrideFilePath = "";
        Interlocked.Exchange(ref _overrideReloadRequested, 0);
        Interlocked.Exchange(ref _overrideReloadAttempts, 0);
        Interlocked.Exchange(ref _overrideWatcherRestartRequested, 0);
        Interlocked.Exchange(ref _overrideReloadNotBeforeUtcTicks, 0L);
        Interlocked.Exchange(ref _overrideWatcherRestartNotBeforeUtcTicks, 0L);
        return true;
    }

    internal static bool Tick()
    {
        DateTime nowUtc = DateTime.UtcNow;
        bool restartCurrentServerSession =
            !_active && _identityDirty && !string.IsNullOrEmpty(_identityFilePath);
        if (_identityDirty && nowUtc >= _identitySaveNotBeforeUtc &&
            SavePlayerIdentities(nowUtc) && restartCurrentServerSession)
        {
            EndServerSession();
            if (ZNet.instance != null && ZNet.instance.IsServer())
            {
                BeginServerSession();
            }
        }

        if (!_active)
        {
            return false;
        }

        if (Volatile.Read(ref _overrideWatcherRestartRequested) != 0 &&
            nowUtc.Ticks >=
            Interlocked.Read(ref _overrideWatcherRestartNotBeforeUtcTicks))
        {
            Interlocked.Exchange(ref _overrideWatcherRestartRequested, 0);
            try
            {
                StartOverrideWatcher();
                RequestOverrideReload();
            }
            catch (Exception ex)
            {
                PortalRulesPlugin.PortalRulesLogger.LogError(
                    $"Failed to restart the {OverrideFileName} watcher: {ex.Message}");
                Interlocked.Exchange(ref _overrideWatcherRestartRequested, 1);
                ScheduleOverrideWatcherRestart(WatcherRestartRetryDelay);
            }
        }

        if (nowUtc >= _nextOverridePollUtc)
        {
            _nextOverridePollUtc = nowUtc + OverridePollingInterval;
            if (Volatile.Read(ref _overrideReloadRequested) == 0)
            {
                ScheduleOverrideReload(TimeSpan.Zero);
            }
        }

        if (Volatile.Read(ref _overrideReloadRequested) != 0 &&
            nowUtc.Ticks >= Interlocked.Read(ref _overrideReloadNotBeforeUtcTicks))
        {
            Interlocked.Exchange(ref _overrideReloadRequested, 0);
            if (TryReloadOverrides(logOnlyWhenChanged: true, out bool changed))
            {
                Interlocked.Exchange(ref _overrideReloadAttempts, 0);
                return changed;
            }

            if (Interlocked.Increment(ref _overrideReloadAttempts) <=
                MaximumOverrideReloadAttempts)
            {
                ScheduleOverrideReload(OverrideReloadRetryDelay);
            }
        }

        return false;
    }

    internal static bool FlushPlayerIdentities()
    {
        if (!_identityDirty)
        {
            return true;
        }

        if (string.IsNullOrEmpty(_identityFilePath))
        {
            return false;
        }

        return SavePlayerIdentities(DateTime.UtcNow);
    }

    internal static bool TryRememberIdentity(
        long playerId,
        string steamId,
        out bool added)
    {
        added = false;
        if (!_active ||
            playerId == 0L ||
            !PublicPortalData.TryNormalizeAccountId(steamId, out string canonicalSteamId))
        {
            return false;
        }

        if (ConflictedPlayerIds.Contains(playerId))
        {
            return false;
        }

        if (PlayerIdentities.TryGetValue(playerId, out string existingSteamId))
        {
            if (string.Equals(existingSteamId, canonicalSteamId, StringComparison.Ordinal))
            {
                return true;
            }

            ConflictedPlayerIds.Add(playerId);
            PortalRulesPlugin.PortalRulesLogger.LogError(
                $"Refused identity collision for playerID {playerId}: " +
                $"stored account {existingSteamId}, authenticated account {canonicalSteamId}. " +
                "The stored mapping was not overwritten and this playerID is blocked for this session.");
            return false;
        }

        if (PlayerIdentities.Count >= MaximumIdentityCount)
        {
            PortalRulesPlugin.PortalRulesLogger.LogError(
                $"Refused playerID {playerId}: {IdentityFileName} reached " +
                $"the {MaximumIdentityCount} identity limit.");
            return false;
        }

        PlayerIdentities.Add(playerId, canonicalSteamId);
        added = true;
        MarkPlayerIdentitiesDirty(DateTime.UtcNow);
        return true;
    }

    internal static bool TryResolveAccountId(long playerId, out string steamId)
    {
        steamId = "";
        return _active &&
               playerId != 0L &&
               !ConflictedPlayerIds.Contains(playerId) &&
               PlayerIdentities.TryGetValue(playerId, out steamId);
    }

    internal static int GetEffectivePortalLimit(string steamId, int defaultLimit)
    {
        if (!PublicPortalData.TryNormalizeSteamId64(steamId, out string canonicalSteamId))
        {
            return 0;
        }

        return _active &&
               _portalLimitOverrides.TryGetValue(
                   canonicalSteamId,
                   out PortalLimitOverride accountOverride)
            ? accountOverride.PortalLimit
            : Math.Max(
                -1,
                Math.Min(PublicPortalData.MaximumPortalLimit, defaultLimit));
    }

    internal static int GetEffectiveInvitePortalLimit(string steamId, int defaultLimit)
    {
        if (!PublicPortalData.TryNormalizeAccountId(steamId, out string canonicalSteamId))
        {
            return 0;
        }

        int normalizedDefault =
            Math.Max(
                -1,
                Math.Min(PublicPortalData.MaximumPortalLimit, defaultLimit));
        return _active &&
               _portalLimitOverrides.TryGetValue(
                   canonicalSteamId,
                   out PortalLimitOverride accountOverride) &&
               accountOverride.InviteLimit.HasValue
            ? accountOverride.InviteLimit.Value
            : normalizedDefault;
    }

    private static void LoadPlayerIdentities()
    {
        if (!File.Exists(_identityFilePath))
        {
            string missingPrimaryBackupPath = _identityFilePath + ".bak";
            if (File.Exists(missingPrimaryBackupPath) &&
                TryReadPlayerIdentities(
                    missingPrimaryBackupPath,
                    out Dictionary<long, string> missingPrimaryBackup))
            {
                ReplacePlayerIdentities(missingPrimaryBackup);
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    $"Recovered missing {IdentityFileName} from its last valid backup.");
            }

            _identityDirty = true;
            SavePlayerIdentities(DateTime.UtcNow);
            return;
        }

        if (TryReadPlayerIdentities(_identityFilePath, out Dictionary<long, string> loaded))
        {
            ReplacePlayerIdentities(loaded);
            return;
        }

        string backupPath = _identityFilePath + ".bak";
        QuarantineInvalidIdentityFile();
        if (File.Exists(backupPath) &&
            TryReadPlayerIdentities(backupPath, out Dictionary<long, string> backup))
        {
            ReplacePlayerIdentities(backup);
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                $"Recovered {IdentityFileName} from its last valid backup.");
        }
        else
        {
            PlayerIdentities.Clear();
            PortalRulesPlugin.PortalRulesLogger.LogError(
                $"Could not load {IdentityFileName} or its backup; " +
                "existing unstamped portals will remain unattributed until identities are learned again.");
        }

        _identityDirty = true;
        SavePlayerIdentities(DateTime.UtcNow);
    }

    private static bool TryReadPlayerIdentities(
        string path,
        out Dictionary<long, string> identities)
    {
        identities = new Dictionary<long, string>();
        try
        {
            FileInfo file = new(path);
            if (file.Length > MaximumIdentityFileBytes)
            {
                throw new InvalidDataException(
                    $"file exceeds {MaximumIdentityFileBytes} bytes");
            }

            PlayerIdentitiesYaml? data =
                Deserializer.Deserialize<PlayerIdentitiesYaml>(File.ReadAllText(path));
            if (data == null ||
                data.FormatVersion != IdentityFormatVersion ||
                data.Identities == null)
            {
                throw new InvalidDataException(
                    $"format_version must be {IdentityFormatVersion} and identities must be present");
            }

            if (data.Identities.Count > MaximumIdentityCount)
            {
                throw new InvalidDataException(
                    $"identities exceeds {MaximumIdentityCount} entries");
            }

            foreach (KeyValuePair<string, string> pair in data.Identities)
            {
                if (!long.TryParse(
                        pair.Key,
                        NumberStyles.AllowLeadingSign,
                        CultureInfo.InvariantCulture,
                        out long playerId) ||
                    playerId == 0L ||
                    !string.Equals(
                        pair.Key,
                        playerId.ToString(CultureInfo.InvariantCulture),
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"identity key '{pair.Key}' is not a canonical nonzero playerID");
                }

                if (!PublicPortalData.TryNormalizeAccountId(
                        pair.Value,
                        out string canonicalSteamId) ||
                    !string.Equals(pair.Value, canonicalSteamId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"identity value for playerID {pair.Key} is not a canonical account ID");
                }

                identities.Add(playerId, canonicalSteamId);
            }

            return true;
        }
        catch (Exception ex)
        {
            PortalRulesPlugin.PortalRulesLogger.LogError(
                $"Failed to read {Path.GetFileName(path)}: {ex.Message}");
            identities.Clear();
            return false;
        }
    }

    private static void ReplacePlayerIdentities(Dictionary<long, string> identities)
    {
        PlayerIdentities.Clear();
        foreach (KeyValuePair<long, string> pair in identities)
        {
            PlayerIdentities.Add(pair.Key, pair.Value);
        }
    }

    private static bool SavePlayerIdentities(DateTime nowUtc)
    {
        try
        {
            PortalRulesFileIO.WriteAtomicFile(
                _identityFilePath,
                SerializePlayerIdentities(),
                _identityFilePath + ".bak");
            _identityDirty = false;
            _identityFirstDirtyUtc = DateTime.MinValue;
            _identitySaveNotBeforeUtc = DateTime.MinValue;
            return true;
        }
        catch (Exception ex)
        {
            _identityDirty = true;
            _identityFirstDirtyUtc = nowUtc;
            _identitySaveNotBeforeUtc = nowUtc + IdentitySaveRetryDelay;
            PortalRulesPlugin.PortalRulesLogger.LogError(
                $"Failed to save {IdentityFileName}; retrying later: {ex.Message}");
            return false;
        }
    }

    private static string SerializePlayerIdentities()
    {
        StringBuilder yaml = new();
        yaml.AppendLine("# AUTO-GENERATED by PortalRules. Do not edit while the server is running.");
        yaml.Append("format_version: ")
            .AppendLine(IdentityFormatVersion.ToString(CultureInfo.InvariantCulture));
        if (PlayerIdentities.Count == 0)
        {
            yaml.AppendLine("identities: {}");
            return yaml.ToString();
        }

        yaml.AppendLine("identities:");
        foreach (KeyValuePair<long, string> pair in PlayerIdentities.OrderBy(pair => pair.Key))
        {
            yaml.Append("  \"")
                .Append(pair.Key.ToString(CultureInfo.InvariantCulture))
                .Append("\": \"")
                .Append(pair.Value)
                .AppendLine("\"");
        }

        return yaml.ToString();
    }

    private static void MarkPlayerIdentitiesDirty(DateTime nowUtc)
    {
        if (!_identityDirty)
        {
            _identityFirstDirtyUtc = nowUtc;
        }

        _identityDirty = true;
        DateTime debounceDeadline = nowUtc + IdentitySaveDebounce;
        DateTime maximumDeadline = _identityFirstDirtyUtc + IdentityMaximumSaveDelay;
        _identitySaveNotBeforeUtc = debounceDeadline < maximumDeadline
            ? debounceDeadline
            : maximumDeadline;
    }

    private static void QuarantineInvalidIdentityFile()
    {
        if (!File.Exists(_identityFilePath))
        {
            return;
        }

        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string quarantinePath = _identityFilePath + ".invalid-" + timestamp;
        if (File.Exists(quarantinePath))
        {
            quarantinePath += "-" + Guid.NewGuid().ToString("N");
        }

        File.Move(_identityFilePath, quarantinePath);
        PortalRulesPlugin.PortalRulesLogger.LogWarning(
            $"Moved invalid {IdentityFileName} to {Path.GetFileName(quarantinePath)}.");
    }

    private static void EnsureOverrideTemplate()
    {
        if (File.Exists(_overrideFilePath))
        {
            return;
        }

        const string template =
            "# PortalRules per-Steam-account portal limit overrides.\n" +
            "# Keys must be bare 17-digit SteamID64 values.\n" +
            "# Each value is: portal_limit OR portal_limit, invite_limit\n" +
            "# portal_limit: -1 = unlimited, 0 = block new counted portals, 1..10000 = custom limit.\n" +
            "# invite_limit: -1 = unlimited, 0 = disable Invite and return existing eligible Invite portals to the Builder's Personal access, 1..10000 = custom limit.\n" +
            "# If invite_limit is omitted, the current server config value is used dynamically.\n" +
            "# A trailing comma is invalid; omit the comma and second value together.\n" +
            "# Schema examples:\n" +
            "# format_version: 2\n" +
            "# overrides:\n" +
            "#   \"76561198000000000\": 10    ## portal_limit; invite_limit uses server config\n" +
            "#   \"76561198000000001\": 10, 1 ## portal_limit, invite_limit\n" +
            "# format_version 1 remains unsupported; no legacy migration is performed.\n" +
            "format_version: 2\n" +
            "overrides: {}\n";
        PortalRulesFileIO.WriteAtomicFile(
            _overrideFilePath,
            template,
            backupPath: null);
    }

    private static bool TryReloadOverrides(
        bool logOnlyWhenChanged,
        out bool changed)
    {
        changed = false;
        if (!File.Exists(_overrideFilePath))
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                $"{OverrideFileName} is missing; retaining the last valid overrides. " +
                "Use 'overrides: {}' to clear them.");
            return false;
        }

        try
        {
            FileInfo file = new(_overrideFilePath);
            if (file.Length > MaximumOverrideFileBytes)
            {
                throw new InvalidDataException(
                    $"file exceeds {MaximumOverrideFileBytes} bytes");
            }

            PortalLimitOverridesYaml? data =
                Deserializer.Deserialize<PortalLimitOverridesYaml>(
                    File.ReadAllText(_overrideFilePath));
            if (data == null ||
                data.FormatVersion != OverrideFormatVersion ||
                data.Overrides == null)
            {
                throw new InvalidDataException(
                    $"format_version must be {OverrideFormatVersion} and overrides must be present");
            }

            if (data.Overrides.Count > MaximumOverrideCount)
            {
                throw new InvalidDataException(
                    $"overrides exceeds {MaximumOverrideCount} entries");
            }

            Dictionary<string, PortalLimitOverride> loaded = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> pair in data.Overrides)
            {
                if (!PublicPortalData.TryNormalizeSteamId64(
                        pair.Key,
                        out string canonicalSteamId) ||
                    !string.Equals(pair.Key, canonicalSteamId, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"override key '{pair.Key}' is not a bare SteamID64");
                }

                string[] values = (pair.Value ?? "").Split(',');
                if (values.Length is < 1 or > 2 ||
                    !int.TryParse(
                        values[0].Trim(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int portalLimit))
                {
                    throw new InvalidDataException(
                        $"override for {pair.Key} must be 'portal_limit' or " +
                        "'portal_limit, invite_limit'");
                }

                ValidateOverrideLimit(
                    pair.Key,
                    "portal_limit",
                    portalLimit);
                int? inviteLimit = null;
                if (values.Length == 2)
                {
                    if (!int.TryParse(
                            values[1].Trim(),
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out int parsedInviteLimit))
                    {
                        throw new InvalidDataException(
                            $"override for {pair.Key} must be 'portal_limit' or " +
                            "'portal_limit, invite_limit'");
                    }

                    ValidateOverrideLimit(
                        pair.Key,
                        "invite_limit",
                        parsedInviteLimit);
                    inviteLimit = parsedInviteLimit;
                }

                loaded.Add(
                    canonicalSteamId,
                    new PortalLimitOverride(portalLimit, inviteLimit));
            }

            changed = !_hasValidOverrideSnapshot ||
                      !DictionariesEqual(_portalLimitOverrides, loaded);
            _portalLimitOverrides = loaded;
            _hasValidOverrideSnapshot = true;
            if (!logOnlyWhenChanged || changed)
            {
                PortalRulesPlugin.PortalRulesLogger.LogInfo(
                    $"Loaded {_portalLimitOverrides.Count} account limit override(s).");
            }

            return true;
        }
        catch (Exception ex)
        {
            PortalRulesPlugin.PortalRulesLogger.LogError(
                $"Failed to reload {OverrideFileName}; retaining the last valid overrides: " +
                ex.Message);
            return false;
        }
    }

    private static void ValidateOverrideLimit(
        string steamId,
        string fieldName,
        int value)
    {
        if (value < -1 || value > PublicPortalData.MaximumPortalLimit)
        {
            throw new InvalidDataException(
                $"{fieldName} override for {steamId} must be between -1 and " +
                PublicPortalData.MaximumPortalLimit);
        }
    }

    private static bool DictionariesEqual(
        Dictionary<string, PortalLimitOverride> left,
        Dictionary<string, PortalLimitOverride> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, PortalLimitOverride> pair in left)
        {
            if (!right.TryGetValue(pair.Key, out PortalLimitOverride value) ||
                !value.Matches(pair.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static void StartOverrideWatcher()
    {
        StopOverrideWatcher();
        _overrideWatcher = new FileSystemWatcher(_configurationDirectory, OverrideFileName)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName |
                           NotifyFilters.LastWrite |
                           NotifyFilters.Size
        };
        _overrideWatcher.Changed += OnOverrideFileChanged;
        _overrideWatcher.Created += OnOverrideFileChanged;
        _overrideWatcher.Deleted += OnOverrideFileChanged;
        _overrideWatcher.Renamed += OnOverrideFileRenamed;
        _overrideWatcher.Error += OnOverrideWatcherError;
        _overrideWatcher.EnableRaisingEvents = true;
    }

    private static void StopOverrideWatcher()
    {
        if (_overrideWatcher == null)
        {
            return;
        }

        _overrideWatcher.EnableRaisingEvents = false;
        _overrideWatcher.Changed -= OnOverrideFileChanged;
        _overrideWatcher.Created -= OnOverrideFileChanged;
        _overrideWatcher.Deleted -= OnOverrideFileChanged;
        _overrideWatcher.Renamed -= OnOverrideFileRenamed;
        _overrideWatcher.Error -= OnOverrideWatcherError;
        _overrideWatcher.Dispose();
        _overrideWatcher = null;
    }

    private static void OnOverrideFileChanged(object sender, FileSystemEventArgs args)
    {
        RequestOverrideReload();
    }

    private static void OnOverrideFileRenamed(object sender, RenamedEventArgs args)
    {
        RequestOverrideReload();
    }

    private static void OnOverrideWatcherError(object sender, ErrorEventArgs args)
    {
        PortalRulesPlugin.PortalRulesLogger.LogError(
            $"The {OverrideFileName} watcher failed and will be restarted: " +
            args.GetException().Message);
        Interlocked.Exchange(ref _overrideWatcherRestartRequested, 1);
        ScheduleOverrideWatcherRestart(WatcherRestartRetryDelay);
        RequestOverrideReload();
    }

    private static void RequestOverrideReload()
    {
        Interlocked.Exchange(ref _overrideReloadAttempts, 0);
        ScheduleOverrideReload(OverrideReloadDebounce);
    }

    private static void ScheduleOverrideReload(TimeSpan delay)
    {
        Interlocked.Exchange(
            ref _overrideReloadNotBeforeUtcTicks,
            (DateTime.UtcNow + delay).Ticks);
        Interlocked.Exchange(ref _overrideReloadRequested, 1);
    }

    private static void ScheduleOverrideWatcherRestart(TimeSpan delay)
    {
        Interlocked.Exchange(
            ref _overrideWatcherRestartNotBeforeUtcTicks,
            (DateTime.UtcNow + delay).Ticks);
    }

}
