#nullable enable annotations
using System;
using System.Collections.Generic;
using YamlDotNet.Serialization;

// Production label methods and account ownership predicate, using actual translations.
// Catalog entries and the game's rich-text stripping API are controlled doubles.
// This does not create Unity map pins or exercise their rendered layout.
public static class PortalRulesPinLabelChecks
{
    /* ENUMS */
    private static int _checks;
    private static Dictionary<string, string> _translations = new();
    private readonly struct PortalOwner
    {
        internal readonly string Id;
        internal readonly string Name;
        internal bool IsValid => !string.IsNullOrWhiteSpace(Id);
        internal PortalOwner(string id, string name) { Id = id; Name = name; }
    }
    private sealed class PublicPortalCatalogEntry
    {
        internal string Tag = "";
        internal PublicPortalAccessMode AccessMode = PublicPortalAccessMode.Invite;
        internal PortalOwner Owner = new("builder-account", "A");
        internal int ModeOrdinal = 1;
        internal int ModeLimit = 1;
    }
    private static class PublicPortalData
    {
        internal static string LocalAccount = "visitor-account";
        internal static PortalOwner LocalOwner() => new(LocalAccount, "Current character");
        /* OWNER_METHOD */
    }
    private static class PublicPortalInteraction
    {
        internal static string AccessModeLabel(PublicPortalAccessMode mode) =>
            PortalRulesLocalization.Translate("$sighsorry_portalrules_access_mode_" + mode.ToString().ToLowerInvariant());
    }
    private static class PortalRulesLocalization
    {
        internal static string Translate(string token, params string[] args) =>
            string.Format(_translations[token.TrimStart('$')], args);
    }

    /* LABEL_METHODS */

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        _checks++;
    }

    public static string Run(string englishYaml, string koreanYaml)
    {
        var deserializer = new DeserializerBuilder().Build();
        foreach (bool korean in new[] { false, true })
        {
            _translations = deserializer.Deserialize<Dictionary<string, string>>(korean ? koreanYaml : englishYaml);
            string invite = korean ? "초대" : "Invite";
            string clan = korean ? "클랜" : "Clan";
            string namedInvite = korean ? "A의 초대" : "A's Invite";
            var entry = new PublicPortalCatalogEntry();
            PublicPortalData.LocalAccount = "visitor-account";
            Check(BuildPinName(entry) == namedInvite, "Visitor sees the original builder's Invite name");
            entry.Tag = "Harbor";
            Check(BuildPinName(entry) == "Harbor\n" + namedInvite, "Custom portal tag stays on the first line");
            entry.Tag = "";
            entry.ModeLimit = -1;
            entry.ModeOrdinal = 12;
            Check(BuildPinName(entry) == namedInvite, "Visitor never sees ordinal or unlimited quota");
            entry.Owner = new PortalOwner("builder-account", " \n\t ");
            Check(BuildPinName(entry) == invite, "Missing builder name falls back to plain Invite without quota");
            entry.Owner = new PortalOwner("builder-account", "<b>A</b>");
            Check(BuildPinName(entry) == namedInvite, "Rich text in builder name is sanitized");
            entry.Owner = new PortalOwner("builder-account", "A\r\nB\tC");
            Check(BuildPinName(entry) == (korean ? "A  B C의 초대" : "A  B C's Invite"),
                "Builder name cannot create extra label lines");

            entry.Owner = new PortalOwner("builder-account", "A");
            entry.ModeOrdinal = 1;
            entry.ModeLimit = 1;
            PublicPortalData.LocalAccount = "builder-account";
            Check(BuildPinName(entry) == invite + " 1/1", "Builder's account retains its ordinal and quota");
            entry.Tag = "Harbor";
            Check(BuildPinName(entry) == "Harbor\n" + invite + " 1/1", "Own custom tag and quota coexist");
            entry.Tag = "";
            entry.ModeLimit = -1;
            Check(BuildPinName(entry) == invite + " 1/∞", "Own unlimited Invite quota retains existing format");
            entry.ModeOrdinal = 0;
            Check(BuildPinName(entry) == invite, "Missing own ordinal retains plain Invite");
            entry.Owner = new PortalOwner("", "A");
            PublicPortalData.LocalAccount = "";
            Check(BuildPinName(entry) == namedInvite, "Two unresolved account IDs do not establish ownership");

            entry.AccessMode = PublicPortalAccessMode.Clan;
            entry.ModeOrdinal = 2;
            entry.ModeLimit = 5;
            Check(BuildPinName(entry) == clan + " 2/5", "Clan labels retain their existing quota policy");
            entry.AccessMode = PublicPortalAccessMode.Public;
            entry.Tag = "<b>Town</b>\nEast";
            Check(BuildPinName(entry) == "Town East", "Other portal modes retain sanitized tag-only labels");
        }
        return $"{_checks} portal pin label checks passed (production formatting and English/Korean resources; no Unity rendering).";
    }
}

internal static class PortalPinLabelStringExtensions
{
    internal static string RemoveRichTextTags(this string value) =>
        System.Text.RegularExpressions.Regex.Replace(value, "<[^>]*>", "");
}
