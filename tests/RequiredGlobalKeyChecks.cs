namespace BepInEx.Bootstrap
{
    public sealed class PluginInfo
    {
        public object? Instance;
    }

    public static class Chainloader
    {
        public static readonly System.Collections.Generic.Dictionary<string, PluginInfo> PluginInfos = new();
    }
}

namespace PortalRulesRequiredGlobalKeyChecks
{
    using System;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Reflection.Emit;
    using BepInEx.Bootstrap;

    public static class Checks
    {
        private const string Key = "defeated_queen";
        private static int _checks;

        private static void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            _checks++;
        }

        public static string Run()
        {
            foreach (int version in new[] { 1, 2 })
            {
                Reset();
                // Keys predate API binding: no grant or boss-death event is simulated.
                ApiBoundary.LocalKeys.Add(Key);
                var peer = new ZNetPeer();
                peer.Keys.Add(Key);
                Install(version);
                Check(RequiredGlobalKeyAccess.QueryLocal(Key) == RequiredGlobalKeyQueryResult.PersonalPresent,
                    $"v{version}: existing local key survives binding");
                Check(RequiredGlobalKeyAccess.QueryPeer(peer, Key) == RequiredGlobalKeyQueryResult.PersonalPresent,
                    $"v{version}: existing remote key is accepted");
                Check(ReferenceEquals(ApiBoundary.LastPeer, peer), "Query uses the requested peer");
                Check(RequiredGlobalKeyAccess.IsPresent(RequiredGlobalKeyAccess.QueryLocal(Key)),
                    "Existing personal key satisfies the portal gate");
                Check(RequiredGlobalKeyAccess.QueryPeer(new ZNetPeer(), Key) == RequiredGlobalKeyQueryResult.PersonalMissing,
                    "A different character does not inherit the host or another peer's keys");
                ApiBoundary.LocalKeys.Clear();
                Check(RequiredGlobalKeyAccess.QueryLocal(Key) == RequiredGlobalKeyQueryResult.PersonalMissing,
                    "Shared world completion does not replace a missing personal key");
                Check(!RequiredGlobalKeyAccess.IsPresent(RequiredGlobalKeyAccess.QueryLocal(Key)),
                    "Missing personal key blocks the portal gate");
                ApiBoundary.LocalKeys.Add(Key);
                ApiBoundary.Ready = false;
                Check(RequiredGlobalKeyAccess.QueryLocal(Key) == RequiredGlobalKeyQueryResult.Unavailable,
                    "Unavailable local data blocks access");
                Check(RequiredGlobalKeyAccess.QueryPeer(peer, Key) == RequiredGlobalKeyQueryResult.Unavailable,
                    "Unavailable peer snapshot blocks access");
                ApiBoundary.Ready = true;
                Check(RequiredGlobalKeyAccess.QueryPeer(peer, Key) == RequiredGlobalKeyQueryResult.PersonalPresent,
                    "Snapshot recovery is visible without another boss kill or API reinitialization");
                Check(RequiredGlobalKeyAccess.QueryPeer(null, Key) == RequiredGlobalKeyQueryResult.Unavailable,
                    "An unresolved peer never falls back to the local character");
                Check(RequiredGlobalKeyAccess.TryShowLocalMissingRequirement(Key) && ApiBoundary.MessageCalls == 1,
                    "The localized missing-key message remains available");
                ApiBoundary.Override = KeyResult.SharedPresent;
                Check(RequiredGlobalKeyAccess.QueryLocal(Key) == RequiredGlobalKeyQueryResult.SharedPresent,
                    "YNW's shared-key result retains its meaning");
                ApiBoundary.Override = (KeyResult)255;
                Check(RequiredGlobalKeyAccess.QueryLocal(Key) == RequiredGlobalKeyQueryResult.Unavailable,
                    "Unknown result values never authorize travel");
                ApiBoundary.Override = null;
                ApiBoundary.Throw = true;
                Check(RequiredGlobalKeyAccess.QueryLocal(Key) == RequiredGlobalKeyQueryResult.Unavailable,
                    "A query exception never authorizes travel");
                Check(ZoneSystem.instance!.Queries == 0, "Installed YNW never falls back to world keys");
            }

            foreach (int version in new[] { 0, 3 })
            {
                Reset();
                ApiBoundary.LocalKeys.Add(Key);
                Install(version);
                Check(RequiredGlobalKeyAccess.QueryLocal(Key) == RequiredGlobalKeyQueryResult.Unavailable,
                    $"Unsupported API v{version} blocks existing keys");
                Check(RequiredGlobalKeyAccess.QueryPeer(new ZNetPeer(), Key) == RequiredGlobalKeyQueryResult.Unavailable,
                    "Unsupported API blocks remote queries");
                Check(ApiBoundary.QueryCalls == 0 && ZoneSystem.instance!.Queries == 0,
                    "Unsupported API is neither invoked nor replaced by world progression");
                Check(PortalRulesPlugin.PortalRulesLogger.Messages.Exists(message => message.Contains("supported v1-v2")),
                    "Compatibility diagnostic identifies supported versions");
                Check(RequiredGlobalKeyAccess.IsPresent(RequiredGlobalKeyAccess.QueryLocal("")),
                    "A portal without a requirement remains unrestricted by YNW");
            }

            foreach (string missing in new[] { "QueryLocal", "QueryPeer" })
            {
                Reset();
                Install(2, missing);
                Check(RequiredGlobalKeyAccess.QueryLocal(Key) == RequiredGlobalKeyQueryResult.Unavailable &&
                    RequiredGlobalKeyAccess.QueryPeer(new ZNetPeer(), Key) == RequiredGlobalKeyQueryResult.Unavailable,
                    "An incomplete API blocks local and remote access: " + missing);
                Check(ApiBoundary.QueryCalls == 0 && ZoneSystem.instance!.Queries == 0,
                    "Missing methods do not enable a partial integration or world fallback");
            }

            Reset();
            ApiBoundary.LocalKeys.Add(Key);
            Install(2, "TryShowLocalMissingRequirement");
            Check(RequiredGlobalKeyAccess.QueryLocal(Key) == RequiredGlobalKeyQueryResult.PersonalPresent,
                "The optional message method does not gate key queries");
            Check(!RequiredGlobalKeyAccess.TryShowLocalMissingRequirement(Key),
                "An absent message method allows the caller's generic diagnostic");

            Reset();
            Check(!RequiredGlobalKeyAccess.YouAreNotWorthyInstalled, "Absent YNW is detected");
            Check(RequiredGlobalKeyAccess.QueryLocal(Key) == RequiredGlobalKeyQueryResult.SharedPresent,
                "Without YNW, existing world-key behavior is preserved");
            Check(RequiredGlobalKeyAccess.QueryLocal("missing") == RequiredGlobalKeyQueryResult.SharedMissing,
                "Without YNW, a missing world key still blocks access");
            RequiredGlobalKeyAccess.Shutdown();
            return $"{_checks} Required GlobalKey checks passed (complete production integration; external boundaries are doubles).";
        }

        private static void Reset()
        {
            RequiredGlobalKeyAccess.Shutdown();
            Chainloader.PluginInfos.Clear();
            PortalRulesPlugin.PortalRulesLogger.Messages.Clear();
            ApiBoundary.LocalKeys.Clear();
            ApiBoundary.Ready = true;
            ApiBoundary.Throw = false;
            ApiBoundary.Override = null;
            ApiBoundary.LastPeer = null;
            ApiBoundary.QueryCalls = ApiBoundary.MessageCalls = 0;
            ZoneSystem.instance = new ZoneSystem();
            ZoneSystem.instance.Keys.Add(Key);
        }

        private static void Install(int version, string? missing = null)
        {
            var assembly = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName("YNWFixture" + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
            TypeBuilder api = assembly.DefineDynamicModule("api").DefineType(
                "YouAreNotWorthy.YouAreNotWorthyApi", TypeAttributes.Public);
            api.DefineDefaultConstructor(MethodAttributes.Public);
            api.DefineField("ApiVersion", typeof(int),
                FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault)
                .SetConstant(version);
            foreach (string name in new[] { "QueryLocal", "QueryPeer", "TryShowLocalMissingRequirement" })
            {
                if (name == missing) continue;
                MethodInfo target = typeof(ApiBoundary).GetMethod(name)!;
                Type[] parameters = Array.ConvertAll(target.GetParameters(), parameter => parameter.ParameterType);
                ILGenerator body = api.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static,
                    target.ReturnType, parameters).GetILGenerator();
                for (int i = 0; i < parameters.Length; i++) body.Emit(OpCodes.Ldarg, (short)i);
                body.Emit(OpCodes.Call, target);
                body.Emit(OpCodes.Ret);
            }
            Chainloader.PluginInfos[PortalRulesPlugin.YouAreNotWorthySoftDependencyGuid] =
                new PluginInfo { Instance = Activator.CreateInstance(api.CreateType()!) };
        }
    }

    // The public key result contract shared by YNW API v1 and v2.
    public enum KeyResult : byte
    {
        Invalid = 0, Unavailable = 1, PersonalMissing = 2,
        PersonalPresent = 3, SharedMissing = 4, SharedPresent = 5
    }

    public static class ApiBoundary
    {
        public static readonly HashSet<string> LocalKeys = new();
        public static bool Ready, Throw;
        public static KeyResult? Override;
        public static ZNetPeer? LastPeer;
        public static int QueryCalls, MessageCalls;

        public static KeyResult QueryLocal(string key) => Query(LocalKeys, key);
        public static KeyResult QueryPeer(ZNetPeer? peer, string key)
        {
            LastPeer = peer;
            return peer == null ? KeyResult.Unavailable : Query(peer.Keys, key);
        }
        private static KeyResult Query(HashSet<string> keys, string key)
        {
            QueryCalls++;
            if (Throw) throw new InvalidOperationException("Controlled API failure");
            if (!Ready) return KeyResult.Unavailable;
            return Override ?? (keys.Contains(key) ? KeyResult.PersonalPresent : KeyResult.PersonalMissing);
        }
        public static bool TryShowLocalMissingRequirement(string key) { MessageCalls++; return true; }
    }

    public sealed class ZNetPeer { public readonly HashSet<string> Keys = new(); }
    public sealed class ZoneSystem
    {
        public static ZoneSystem? instance;
        public readonly HashSet<string> Keys = new();
        public int Queries;
        public bool GetGlobalKey(string key, out string value) { Queries++; value = ""; return Keys.Contains(key); }
        public static string GetKeyValue(string key, out string value, out string full)
        { value = ""; full = key; return key; }
    }
    internal static class PublicPortalData
    {
        internal static bool TryNormalizeRequiredGlobalKey(string value, out string normalized, out string error)
        { normalized = value.Trim(); error = ""; return true; }
    }
    internal static class PortalRulesPlugin
    {
        internal const string YouAreNotWorthySoftDependencyGuid = "sighsorry.YouAreNotWorthy";
        internal static readonly Logger PortalRulesLogger = new();
    }
    internal sealed class Logger
    {
        internal readonly List<string> Messages = new();
        internal void LogInfo(string message) => Messages.Add(message);
        internal void LogWarning(string message) => Messages.Add(message);
        internal void LogError(string message) => Messages.Add(message);
    }
}
