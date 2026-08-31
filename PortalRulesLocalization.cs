using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using BepInEx;
using HarmonyLib;
using YamlDotNet.RepresentationModel;

namespace PortalRules;

/// <summary>
/// Loads PortalRules-owned localization tokens and provides a non-throwing
/// translation boundary for UI and interaction code.
/// </summary>
internal static class PortalRulesLocalization
{
    internal const string PortalDeniedToken =
        "sighsorry_portalrules_portal_denied";
    internal const string MissingGlobalKeyToken =
        "sighsorry_portalrules_missing_global_key";
    internal const string OwnedTokenPrefix = "sighsorry_portalrules_";
    private const string AccessModeTokenPrefix =
        "$sighsorry_portalrules_access_mode_";

    private const string EnglishLanguage = "English";
    private const string KoreanLanguage = "Korean";
    private const string ExternalFilePrefix = PortalRulesPlugin.ModName + ".";
    private const string EmbeddedResourcePrefix =
        "PortalRules.translations.";
    private const long MaxExternalTranslationFileBytes = 1024L * 1024L;

    private static readonly object Sync = new();

    private static readonly StringComparer FileSystemPathComparer =
        Path.DirectorySeparatorChar == '\\'
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static readonly IComparer<string> DeterministicPathComparer =
        Comparer<string>.Create(static (left, right) =>
        {
            int comparison = FileSystemPathComparer.Compare(left, right);
            return comparison != 0
                ? comparison
                : StringComparer.Ordinal.Compare(left, right);
        });

    private static readonly Dictionary<string, string> BuiltInEnglish =
        new(StringComparer.Ordinal)
        {
            [PortalDeniedToken] = "You cannot use this portal.",
            [MissingGlobalKeyToken] = "You need \"{0}\"."
        };

    private static readonly Dictionary<string, Dictionary<string, string>>
        ExternalTranslations = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> LoggedFormatFailures =
        new(StringComparer.Ordinal);

    private static Dictionary<string, string> _english =
        new(BuiltInEnglish, StringComparer.Ordinal);

    private static Dictionary<string, string> _safeEnglish =
        new(BuiltInEnglish, StringComparer.Ordinal);

    private static Dictionary<string, string> _embeddedKorean =
        new(StringComparer.Ordinal);

    private static Dictionary<string, string> _active =
        new(BuiltInEnglish, StringComparer.Ordinal);

    private static string _activeLanguage = EnglishLanguage;
    private static bool _initialized;
    private static bool _addWordResolved;
    private static bool _addWordFailureLogged;
    private static MethodInfo? _addWordMethod;

    internal static string AccessModeToken(PublicPortalAccessMode mode)
    {
        return mode switch
        {
            PublicPortalAccessMode.Personal => AccessModeTokenPrefix + "personal",
            PublicPortalAccessMode.Admin => AccessModeTokenPrefix + "admin",
            PublicPortalAccessMode.Public => AccessModeTokenPrefix + "public",
            PublicPortalAccessMode.Invite => AccessModeTokenPrefix + "invite",
            PublicPortalAccessMode.Clan => AccessModeTokenPrefix + "clan",
            PublicPortalAccessMode.Tagged => AccessModeTokenPrefix + "tagged",
            _ => AccessModeTokenPrefix + "personal"
        };
    }

    internal static bool IsAccessModeToken(string token)
    {
        if (string.IsNullOrEmpty(token) ||
            !token.StartsWith(AccessModeTokenPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string suffix = token.Substring(AccessModeTokenPrefix.Length);
        return Enum.TryParse(
                   suffix,
                   ignoreCase: true,
                   out PublicPortalAccessMode mode) &&
               Enum.IsDefined(typeof(PublicPortalAccessMode), mode) &&
               string.Equals(
                   token,
                   AccessModeToken(mode),
                   StringComparison.Ordinal);
    }

    /// <summary>
    /// Loads embedded resources and external overrides. Every failure is
    /// contained so localization can never prevent plugin or world startup.
    /// </summary>
    internal static void Initialize()
    {
        lock (Sync)
        {
            if (_initialized)
            {
                return;
            }

            try
            {
                Dictionary<string, string> safeEnglish =
                    new(BuiltInEnglish, StringComparer.Ordinal);
                if (TryReadEmbedded(
                        EnglishLanguage,
                        out Dictionary<string, string> embeddedEnglish))
                {
                    Merge(safeEnglish, embeddedEnglish);
                }

                Dictionary<string, string> english =
                    new(safeEnglish, StringComparer.Ordinal);

                Dictionary<string, string> korean =
                    new(StringComparer.Ordinal);
                if (TryReadEmbedded(
                        KoreanLanguage,
                        out Dictionary<string, string> embeddedKorean))
                {
                    Merge(korean, embeddedKorean);
                }

                Dictionary<string, Dictionary<string, string>> external =
                    LoadExternalTranslations();
                if (external.TryGetValue(
                        EnglishLanguage,
                        out Dictionary<string, string> externalEnglish))
                {
                    Merge(english, externalEnglish);
                }

                _english = english;
                _safeEnglish = safeEnglish;
                _embeddedKorean = korean;
                ExternalTranslations.Clear();
                foreach (KeyValuePair<string, Dictionary<string, string>> entry in external)
                {
                    ExternalTranslations[entry.Key] = entry.Value;
                }
            }
            catch (Exception ex)
            {
                PortalRulesPlugin.PortalRulesLogger.LogError(
                    $"PortalRules localization initialization failed; using built-in English: {ex}");
                ResetToBuiltInEnglish();
            }
            finally
            {
                _initialized = true;
            }
        }

        ApplyCurrentLanguage();
    }

    /// <summary>
    /// Resolves a PortalRules token and formats its zero-based placeholders.
    /// Tokens may be supplied with or without Valheim's leading '$'.
    /// </summary>
    internal static string Translate(string token, params string[] args)
    {
        EnsureInitialized();

        string originalToken = (token ?? string.Empty).Trim();
        string normalized = NormalizeToken(token);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        string activeTemplate;
        string englishTemplate;
        string language;
        bool knownToken;
        lock (Sync)
        {
            language = _activeLanguage;
            bool hasEnglish = _safeEnglish.TryGetValue(
                normalized,
                out englishTemplate!);
            if (!hasEnglish)
            {
                englishTemplate = BuiltInEnglish.TryGetValue(
                    normalized,
                    out string? builtIn)
                    ? builtIn
                    : normalized;
            }

            bool hasActive = _active.TryGetValue(
                normalized,
                out string? localized);
            activeTemplate = hasActive ? localized! : englishTemplate;
            knownToken = hasActive || hasEnglish;
        }

        string[] formatArguments = args ?? Array.Empty<string>();
        if (!normalized.StartsWith(
                OwnedTokenPrefix,
                StringComparison.Ordinal)
            || !knownToken)
        {
            return TranslateVanilla(
                originalToken,
                normalized,
                formatArguments);
        }

        try
        {
            return string.Format(
                CultureInfo.CurrentCulture,
                activeTemplate,
                formatArguments);
        }
        catch (FormatException ex)
        {
            LogFormatFailureOnce(language, normalized, ex);
            if (!string.Equals(
                    activeTemplate,
                    englishTemplate,
                    StringComparison.Ordinal))
            {
                try
                {
                    return string.Format(
                        CultureInfo.CurrentCulture,
                        englishTemplate,
                        formatArguments);
                }
                catch (FormatException fallbackException)
                {
                    LogFormatFailureOnce(
                        EnglishLanguage,
                        normalized,
                        fallbackException);
                }
            }

            return englishTemplate;
        }
        catch (Exception ex)
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                $"Could not translate PortalRules token '{normalized}'; using English text: {ex.Message}");
            return englishTemplate;
        }
    }

    internal static void ApplyCurrentLanguage()
    {
        try
        {
            Localization? localization = Localization.instance;
            string language = GetSelectedLanguage(localization);
            ApplyLanguage(localization, language);
        }
        catch (Exception ex)
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                $"Could not apply the current PortalRules language; keeping English: {ex.Message}");
            SetActiveLanguage(EnglishLanguage);
        }
    }

    internal static void ApplyLanguage(
        Localization? localization,
        string? language)
    {
        EnsureInitialized();

        string normalizedLanguage = language?.Trim() ?? string.Empty;
        string selectedLanguage = normalizedLanguage.Length == 0
            ? EnglishLanguage
            : normalizedLanguage;
        Dictionary<string, string> active =
            BuildActiveLanguage(selectedLanguage);

        lock (Sync)
        {
            _activeLanguage = selectedLanguage;
            _active = active;
        }

        RegisterActiveTranslations(localization, active);
    }

    private static void EnsureInitialized()
    {
        bool initialized;
        lock (Sync)
        {
            initialized = _initialized;
        }

        if (!initialized)
        {
            Initialize();
        }
    }

    private static Dictionary<string, string> BuildActiveLanguage(
        string language)
    {
        lock (Sync)
        {
            Dictionary<string, string> active =
                new(_english, StringComparer.Ordinal);
            if (string.Equals(
                    language,
                    KoreanLanguage,
                    StringComparison.OrdinalIgnoreCase))
            {
                Merge(active, _embeddedKorean);
            }

            if (!string.Equals(
                    language,
                    EnglishLanguage,
                    StringComparison.OrdinalIgnoreCase)
                && ExternalTranslations.TryGetValue(
                    language,
                    out Dictionary<string, string> external))
            {
                Merge(active, external);
            }

            return active;
        }
    }

    private static void SetActiveLanguage(string language)
    {
        Dictionary<string, string> active = BuildActiveLanguage(language);
        lock (Sync)
        {
            _activeLanguage = language;
            _active = active;
        }
    }

    private static string GetSelectedLanguage(Localization? localization)
    {
        if (localization == null)
        {
            return EnglishLanguage;
        }

        try
        {
            string language = localization.GetSelectedLanguage();
            return string.IsNullOrWhiteSpace(language)
                ? EnglishLanguage
                : language.Trim();
        }
        catch
        {
            return EnglishLanguage;
        }
    }

    private static Dictionary<string, Dictionary<string, string>>
        LoadExternalTranslations()
    {
        Dictionary<string, List<string>> filesByLanguage =
            DiscoverExternalFiles();
        Dictionary<string, Dictionary<string, string>> result =
            new(StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, List<string>> languageFiles in
                 filesByLanguage.OrderBy(
                     static pair => pair.Key,
                     StringComparer.OrdinalIgnoreCase)
                 .ThenBy(
                     static pair => pair.Key,
                     StringComparer.Ordinal))
        {
            Dictionary<string, string> merged =
                new(StringComparer.Ordinal);
            Dictionary<string, string> sourceByToken =
                new(StringComparer.Ordinal);

            foreach (string path in languageFiles.Value
                         .OrderBy(
                             static value => value,
                             StringComparer.OrdinalIgnoreCase)
                         .ThenBy(
                             static value => value,
                             StringComparer.Ordinal))
            {
                if (!TryReadFile(
                        path,
                        out Dictionary<string, string> translations))
                {
                    continue;
                }

                foreach (KeyValuePair<string, string> translation in translations)
                {
                    if (sourceByToken.TryGetValue(
                            translation.Key,
                            out string? previousSource))
                    {
                        PortalRulesPlugin.PortalRulesLogger.LogWarning(
                            $"Duplicate PortalRules translation token '{translation.Key}' for language " +
                            $"'{languageFiles.Key}' in '{previousSource}' and '{path}'; " +
                            "the later ordinal path wins.");
                    }

                    merged[translation.Key] = translation.Value;
                    sourceByToken[translation.Key] = path;
                }
            }

            if (merged.Count > 0)
            {
                result[languageFiles.Key] = merged;
            }
        }

        return result;
    }

    private static Dictionary<string, List<string>> DiscoverExternalFiles()
    {
        Dictionary<string, List<string>> filesByLanguage =
            new(StringComparer.OrdinalIgnoreCase);
        string root = Paths.BepInExRootPath;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return filesByLanguage;
        }

        Stack<string> pending = new();
        HashSet<string> visited = new(FileSystemPathComparer);
        pending.Push(Path.GetFullPath(root));

        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            if (!visited.Add(directory))
            {
                continue;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch (Exception ex)
            {
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    $"Could not inspect PortalRules translation directory '{directory}': {ex.Message}");
                files = Array.Empty<string>();
            }

            foreach (string file in files)
            {
                if (!TryGetExternalLanguage(file, out string language))
                {
                    continue;
                }

                if (!filesByLanguage.TryGetValue(
                        language,
                        out List<string>? languageFiles))
                {
                    languageFiles = new List<string>();
                    filesByLanguage[language] = languageFiles;
                }

                languageFiles.Add(Path.GetFullPath(file));
            }

            string[] childDirectories;
            try
            {
                childDirectories = Directory.GetDirectories(directory);
            }
            catch (Exception ex)
            {
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    $"Could not inspect child directories below '{directory}': {ex.Message}");
                continue;
            }

            Array.Sort(childDirectories, DeterministicPathComparer);
            for (int index = childDirectories.Length - 1; index >= 0; index--)
            {
                string child = childDirectories[index];
                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    pending.Push(Path.GetFullPath(child));
                }
                catch (Exception ex)
                {
                    PortalRulesPlugin.PortalRulesLogger.LogWarning(
                        $"Could not inspect PortalRules translation path '{child}': {ex.Message}");
                }
            }
        }

        return filesByLanguage;
    }

    private static bool TryGetExternalLanguage(
        string path,
        out string language)
    {
        language = string.Empty;
        string extension = Path.GetExtension(path);
        if (!string.Equals(extension, ".yml", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".yaml", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string stem = Path.GetFileNameWithoutExtension(path);
        if (!stem.StartsWith(
                ExternalFilePrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        language = stem.Substring(ExternalFilePrefix.Length);
        if (string.IsNullOrWhiteSpace(language)
            || language.Length != language.Trim().Length
            || language.IndexOf('.') >= 0)
        {
            language = string.Empty;
            return false;
        }

        return true;
    }

    private static bool TryReadEmbedded(
        string language,
        out Dictionary<string, string> translations)
    {
        translations = new Dictionary<string, string>(StringComparer.Ordinal);
        string resourceName = EmbeddedResourcePrefix + language + ".yml";
        try
        {
            Assembly assembly = typeof(PortalRulesLocalization).Assembly;
            using Stream? stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    $"Embedded PortalRules translation '{resourceName}' was not found.");
                return false;
            }

            using StreamReader reader = new(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true);
            return TryParseTranslations(
                reader.ReadToEnd(),
                resourceName,
                out translations);
        }
        catch (Exception ex)
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                $"Could not read embedded PortalRules translation '{resourceName}': {ex.Message}");
            return false;
        }
    }

    private static bool TryReadFile(
        string path,
        out Dictionary<string, string> translations)
    {
        translations = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            long fileLength = new FileInfo(path).Length;
            if (fileLength > MaxExternalTranslationFileBytes)
            {
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    $"PortalRules translation '{path}' is {fileLength} bytes and exceeds the " +
                    $"{MaxExternalTranslationFileBytes}-byte limit; the file was ignored.");
                return false;
            }

            return TryParseTranslations(
                File.ReadAllText(path),
                path,
                out translations);
        }
        catch (Exception ex)
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                $"Could not read PortalRules translation '{path}': {ex.Message}");
            return false;
        }
    }

    private static bool TryParseTranslations(
        string content,
        string source,
        out Dictionary<string, string> translations)
    {
        translations = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(content))
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                $"PortalRules translation '{source}' is empty and was ignored.");
            return false;
        }

        try
        {
            YamlStream yaml = new();
            using StringReader reader = new(content);
            yaml.Load(reader);
            if (yaml.Documents.Count != 1
                || yaml.Documents[0].RootNode is not YamlMappingNode root)
            {
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    $"PortalRules translation '{source}' must contain exactly one mapping document.");
                return false;
            }

            foreach (KeyValuePair<YamlNode, YamlNode> entry in root.Children)
            {
                if (entry.Key is not YamlScalarNode keyNode
                    || string.IsNullOrWhiteSpace(keyNode.Value))
                {
                    PortalRulesPlugin.PortalRulesLogger.LogWarning(
                        $"PortalRules translation '{source}' contains an empty or non-scalar token and was ignored.");
                    continue;
                }

                string token = NormalizeToken(keyNode.Value!);
                if (!token.StartsWith(
                        OwnedTokenPrefix,
                        StringComparison.Ordinal))
                {
                    PortalRulesPlugin.PortalRulesLogger.LogWarning(
                        $"Translation token '{keyNode.Value}' in '{source}' is not owned by PortalRules and was ignored.");
                    continue;
                }

                if (entry.Value is not YamlScalarNode valueNode
                    || string.IsNullOrWhiteSpace(valueNode.Value))
                {
                    PortalRulesPlugin.PortalRulesLogger.LogWarning(
                        $"PortalRules translation token '{token}' in '{source}' has no scalar text and was ignored.");
                    continue;
                }

                if (translations.ContainsKey(token))
                {
                    PortalRulesPlugin.PortalRulesLogger.LogWarning(
                        $"Duplicate PortalRules translation token '{token}' inside '{source}'; the later value wins.");
                }

                translations[token] = valueNode.Value!;
            }

            return translations.Count > 0;
        }
        catch (Exception ex)
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                $"Could not parse PortalRules translation '{source}': {ex.Message}");
            translations.Clear();
            return false;
        }
    }

    private static string NormalizeToken(string? token)
    {
        string normalized = (token ?? string.Empty).Trim();
        return normalized.StartsWith("$", StringComparison.Ordinal)
            ? normalized.Substring(1)
            : normalized;
    }

    private static string TranslateVanilla(
        string originalToken,
        string normalizedToken,
        string[] args)
    {
        Localization? localization = Localization.instance;
        if (localization == null)
        {
            return originalToken;
        }

        string lookupToken = originalToken.StartsWith(
            "$",
            StringComparison.Ordinal)
            ? originalToken
            : "$" + normalizedToken;
        try
        {
            return localization.Localize(lookupToken, args);
        }
        catch (Exception ex)
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                $"Could not delegate localization token '{lookupToken}' to Valheim: {ex.Message}");
            return originalToken;
        }
    }

    private static void RegisterActiveTranslations(
        Localization? localization,
        IReadOnlyDictionary<string, string> translations)
    {
        if (localization == null)
        {
            return;
        }

        MethodInfo? addWord = ResolveAddWord();
        if (addWord == null)
        {
            LogAddWordFailureOnce(
                "Localization.AddWord(string, string) could not be resolved; " +
                "PortalRules.Translate remains available.");
            return;
        }

        foreach (KeyValuePair<string, string> translation in translations)
        {
            try
            {
                addWord.Invoke(
                    localization,
                    new object[] { translation.Key, translation.Value });
            }
            catch (Exception ex)
            {
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    $"Could not register PortalRules localization token '{translation.Key}' " +
                    $"through Localization.AddWord: {ex.GetBaseException().Message}");
            }
        }
    }

    private static MethodInfo? ResolveAddWord()
    {
        lock (Sync)
        {
            if (_addWordResolved)
            {
                return _addWordMethod;
            }

            _addWordResolved = true;
            try
            {
                _addWordMethod = AccessTools.DeclaredMethod(
                    typeof(Localization),
                    "AddWord",
                    new[] { typeof(string), typeof(string) });
            }
            catch (Exception ex)
            {
                LogAddWordFailureOnce(
                    "Localization.AddWord binding failed: " + ex.Message);
            }

            return _addWordMethod;
        }
    }

    private static void LogAddWordFailureOnce(string message)
    {
        lock (Sync)
        {
            if (_addWordFailureLogged)
            {
                return;
            }

            _addWordFailureLogged = true;
        }

        PortalRulesPlugin.PortalRulesLogger.LogWarning(message);
    }

    private static void Merge(
        IDictionary<string, string> destination,
        IReadOnlyDictionary<string, string> source)
    {
        foreach (KeyValuePair<string, string> translation in source)
        {
            destination[translation.Key] = translation.Value;
        }
    }

    private static void ResetToBuiltInEnglish()
    {
        lock (Sync)
        {
            _english = new Dictionary<string, string>(
                BuiltInEnglish,
                StringComparer.Ordinal);
            _safeEnglish = new Dictionary<string, string>(
                BuiltInEnglish,
                StringComparer.Ordinal);
            _embeddedKorean = new Dictionary<string, string>(
                StringComparer.Ordinal);
            _active = new Dictionary<string, string>(
                BuiltInEnglish,
                StringComparer.Ordinal);
            _activeLanguage = EnglishLanguage;
            ExternalTranslations.Clear();
        }
    }

    private static void LogFormatFailureOnce(
        string language,
        string token,
        FormatException exception)
    {
        string key = language + "\n" + token;
        lock (Sync)
        {
            if (!LoggedFormatFailures.Add(key))
            {
                return;
            }
        }

        PortalRulesPlugin.PortalRulesLogger.LogWarning(
            $"Invalid format string for PortalRules token '{token}' in language " +
            $"'{language}'; using the English fallback: {exception.Message}");
    }
}

[HarmonyPatch(typeof(Localization), nameof(Localization.SetupLanguage))]
internal static class PortalRulesLocalizationSetupLanguagePatch
{
    private static void Postfix(Localization __instance, string language)
    {
        try
        {
            PortalRulesLocalization.ApplyLanguage(__instance, language);
        }
        catch (Exception ex)
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                $"Could not apply PortalRules translations after a language change: {ex.Message}");
        }
    }
}

[HarmonyPatch(typeof(FejdStartup), "SetupGui")]
internal static class PortalRulesLocalizationSetupGuiPatch
{
    private static void Postfix()
    {
        try
        {
            PortalRulesLocalization.ApplyCurrentLanguage();
        }
        catch (Exception ex)
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                $"Could not apply PortalRules translations while setting up the GUI: {ex.Message}");
        }
    }
}
