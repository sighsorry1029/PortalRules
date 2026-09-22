#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using YamlDotNet.Serialization;

// Production identity/registry methods with controlled transport and persistence boundaries.
namespace PortalRulesCrossplayChecks
{
    public enum OnlineBackendType { Steamworks, PlayFab, Other }
    public class ZNet
    {
        public static OnlineBackendType m_onlineBackend;
        public static ZNet? instance = new ZNet();
        public bool Server = true;
        public bool IsServer() => Server;
    }
    public class ZNetPeer
    {
        public bool Ready = true;
        public ISocket? m_socket;
        public bool IsReady() => Ready;
    }
    public interface ISocket
    {
        bool IsConnected();
    }
    public class ZPlayFabSocket : ISocket
    {
        public string m_remotePlayerId = "AB1234";
        public bool Connected = true;
        public bool IsConnected() => Connected;
    }
    public sealed class BufferingPlayFabSocket : ZPlayFabSocket, ISocket
    {
        private readonly ISocket _original;
        public BufferingPlayFabSocket(ISocket original)
        {
            _original = original;
            Connected = false;
        }
        public new bool IsConnected() => _original.IsConnected();
    }
    public sealed class OtherSocket : ISocket
    {
        public bool IsConnected() => true;
    }
    public class EntityKey { public string? Id; }
    public class PlayFabManager
    {
        public static bool IsLoggedIn;
        public static PlayFabManager instance = new PlayFabManager();
        public EntityKey? Entity;
    }
    public class Setting { public Toggle Value; }
    public enum Toggle { Off, On }
    public static class ToggleExtensions { public static bool IsOn(this Toggle t) => t == Toggle.On; }
    public static class PublicPortalConfig
    {
        public static Setting EnableAccountPortalLimit = new Setting();
        /* CONFIG_PROPERTY */
    }
    public static class PublicPortalData
    {
        public static string SteamId = "76561198000000001";
        private static bool TryGetPeerSteamId64(ZNetPeer? peer, out string id)
        { id = SteamId; return ZNet.m_onlineBackend == OnlineBackendType.Steamworks && peer != null; }
        private static bool TryGetLocalSteamId64(out string id)
        { id = SteamId; return ZNet.m_onlineBackend == OnlineBackendType.Steamworks; }
        /* DATA_PROPERTY */
        /* DATA_METHODS */
    }
    public static class PortalRulesPlugin
    {
        public static Logger PortalRulesLogger = new Logger();
        public class Logger { public void LogError(string message) { } }
    }
    public static class PortalAccountStore
    {
        private sealed class PlayerIdentitiesYaml
        {
            [YamlMember(Alias = "format_version")]
            public int FormatVersion { get; set; }
            [YamlMember(Alias = "identities")]
            public Dictionary<string, string>? Identities { get; set; }
        }
        private const int IdentityFormatVersion = 1;
        private const long MaximumIdentityFileBytes = 2L * 1024L * 1024L;
        private static readonly IDeserializer Deserializer = new DeserializerBuilder().WithDuplicateKeyChecking().Build();
        private const int MaximumIdentityCount = 16384;
        private static bool _active = true;
        private static readonly Dictionary<long, string> PlayerIdentities = new Dictionary<long, string>();
        private static readonly HashSet<long> ConflictedPlayerIds = new HashSet<long>();
        public static int SavesRequested;
        private static void MarkPlayerIdentitiesDirty(DateTime now) => SavesRequested++;
        /* STORE_PROPERTY */
        public static string CurrentFile => IdentityFileName;
        public static bool SaveReload(string path)
        {
            File.WriteAllText(path, SerializePlayerIdentities());
            if (!TryReadPlayerIdentities(path, out var loaded)) return false;
            ReplacePlayerIdentities(loaded);
            ConflictedPlayerIds.Clear();
            return true;
        }
        public static bool ReadFile(string path) => TryReadPlayerIdentities(path, out _);
        /* STORE_METHODS */
    }
    public static class Checks
    {
        private static int _checks;
        private static void Check(bool value, string message)
        { if (!value) throw new Exception(message); _checks++; }
        public static string Run()
        {
            foreach (var backend in new[] { OnlineBackendType.Steamworks, OnlineBackendType.PlayFab })
            foreach (var toggle in new[] { Toggle.Off, Toggle.On })
            {
                ZNet.m_onlineBackend = backend;
                PublicPortalConfig.EnableAccountPortalLimit.Value = toggle;
                Check(PublicPortalConfig.IsAccountPortalLimitEnabled ==
                    (backend == OnlineBackendType.Steamworks && toggle == Toggle.On), "Effective limit matrix");
                Check(PublicPortalConfig.EnableAccountPortalLimit.Value == toggle, "Saved setting is untouched");
            }
            Check(PublicPortalData.TryNormalizeAccountId("Steam_76561198000000001", out var id) &&
                id == "76561198000000001", "Steam saved identity stays compatible");
            Check(PublicPortalData.TryNormalizeAccountId("PlayFab_ab1234", out id) &&
                id == "PlayFab_AB1234", "PlayFab namespace is canonical");
            foreach (var bad in new string?[] { null, "", "PlayFab_", "PlayFab_../x", "PlayFab_ AB", "PlayFab_A\n", "PlayFab_" + new string('A',65), "Xbox_123", "player:123" })
                Check(!PublicPortalData.TryNormalizeAccountId(bad, out _), "Reject malformed identity");
            Check(!PublicPortalData.TryNormalizeSteamId64("PlayFab_AB1234", out _), "No PlayFab-to-Steam alias");
            ZNet.m_onlineBackend = OnlineBackendType.PlayFab;
            var socket = new ZPlayFabSocket();
            var peer = new ZNetPeer { m_socket = socket };
            Check(PublicPortalData.TryGetPeerAccountId(peer, out id) && id == "PlayFab_AB1234", "Party entity identifies remote user");
            var bufferingSocket = new BufferingPlayFabSocket(socket);
            peer.m_socket = bufferingSocket;
            Check(!((ZPlayFabSocket)bufferingSocket).IsConnected() &&
                ((ISocket)bufferingSocket).IsConnected(), "ServerSync wrapper exposes connection through ISocket");
            Check(PublicPortalData.TryGetPeerAccountId(peer, out id) && id == "PlayFab_AB1234", "ServerSync-wrapped Party entity identifies remote user");
            peer.m_socket = socket;
            Check(!PublicPortalData.TryGetPeerAccountId(null, out _), "Missing peer denied");
            peer.Ready = false;
            Check(!PublicPortalData.TryGetPeerAccountId(peer, out _), "Unready peer denied");
            peer.Ready = true; socket.Connected = false;
            Check(!PublicPortalData.TryGetPeerAccountId(peer, out _), "Disconnected peer denied");
            socket.Connected = true; peer.m_socket = new OtherSocket();
            Check(!PublicPortalData.TryGetPeerAccountId(peer, out _), "Wrong socket transport denied");
            peer.m_socket = socket; socket.m_remotePlayerId = "";
            Check(!PublicPortalData.TryGetPeerAccountId(peer, out _), "Missing entity denied");
            socket.m_remotePlayerId = "AB1234"; ZNet.instance!.Server = false;
            Check(!PublicPortalData.TryGetPeerAccountId(peer, out _), "Client cannot authenticate remote account");
            ZNet.instance.Server = true;
            PlayFabManager.instance.Entity = new EntityKey { Id = "AB1234" };
            Check(!PublicPortalData.TryGetLocalAccountId(out _), "Local login required");
            PlayFabManager.IsLoggedIn = true;
            Check(PublicPortalData.TryGetLocalAccountId(out var local) && local == "PlayFab_AB1234", "Local and remote entity use same namespace");
            PlayFabManager.instance.Entity = null;
            Check(!PublicPortalData.TryGetLocalAccountId(out _), "Missing local entity denied");
            Check(PortalAccountStore.TryRememberIdentity(100, "PlayFab_AB1234", out var added) && added, "Record PlayFab builder");
            int saves = PortalAccountStore.SavesRequested;
            Check(PortalAccountStore.TryRememberIdentity(100, "PlayFab_AB1234", out added) && !added &&
                PortalAccountStore.SavesRequested == saves, "Repeated identity does not schedule another save");
            Check(PortalAccountStore.TryResolveAccountId(100, out id) && id == "PlayFab_AB1234", "Resolve persisted builder mapping");
            Check(!PortalAccountStore.TryRememberIdentity(100, "PlayFab_FF5678", out _), "Same character different account denied");
            Check(!PortalAccountStore.TryResolveAccountId(100, out _), "Conflicted character remains blocked");
            Check(!PortalAccountStore.TryRememberIdentity(100, "PlayFab_AB1234", out _), "Conflict cannot be cleared by another request");
            Check(!PortalAccountStore.TryRememberIdentity(0, "PlayFab_AB1234", out _), "Zero character denied");
            string scratch = Path.GetTempFileName();
            try
            {
                Check(PortalAccountStore.SaveReload(scratch) &&
                    PortalAccountStore.TryResolveAccountId(100, out id) && id == "PlayFab_AB1234", "PlayFab survives actual YAML save/reload; collision did not overwrite mapping");
                File.WriteAllText(scratch, "format_version: 1\nidentities:\n  \"200\": \"76561198000000001\"\n");
                Check(PortalAccountStore.ReadFile(scratch), "Existing Steam identity YAML still loads");
                File.WriteAllText(scratch, "format_version: 1\nidentities:\n  \"200\": \"PlayFab_../bad\"\n");
                Check(!PortalAccountStore.ReadFile(scratch), "Malformed persisted identity rejected");
            }
            finally { File.Delete(scratch); }
            Check(PortalAccountStore.CurrentFile == "player-identities-playfab.yml", "Crossplay registry is separate");
            var reconnected = new ZNetPeer { m_socket = new ZPlayFabSocket { m_remotePlayerId = "ab1234" } };
            Check(PublicPortalData.TryGetPeerAccountId(reconnected, out id) && id == "PlayFab_AB1234", "Reconnect keeps entity identity independent of peer object");
            ZNet.m_onlineBackend = OnlineBackendType.Steamworks;
            Check(PortalAccountStore.CurrentFile == "player-identities.yml", "Existing Steam registry path preserved");
            Check(PublicPortalData.TryGetLocalAccountId(out id) && id == PublicPortalData.SteamId, "Steam local path preserved");
            Check(PublicPortalData.TryGetPeerAccountId(peer, out id) && id == PublicPortalData.SteamId, "Steam peer path preserved");
            return $"{_checks} Crossplay identity/policy checks passed (production methods; transport/storage boundaries are test doubles).";
        }
    }
}
