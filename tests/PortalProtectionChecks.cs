#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;

namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class)]
    internal sealed class HarmonyPatch : Attribute
    {
        public HarmonyPatch(Type type, string name, params Type[] args) { }
    }
    internal static class AccessTools
    {
        public static MethodInfo Method(Type type, string name, Type[] args) =>
            type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static, null, args, null)!;
    }
    internal sealed class CodeInstruction
    {
        internal OpCode opcode;
        internal object? operand;
        internal readonly List<Label> labels = new();
        internal CodeInstruction(OpCode op, object? value = null) { opcode = op; operand = value; }
        internal bool Calls(MethodInfo method) => (opcode == OpCodes.Call || opcode == OpCodes.Callvirt) && Equals(operand, method);
    }
}
namespace UnityEngine
{
    internal static class Time { internal static float realtimeSinceStartup = 100; }
    public struct Vector3
    {
        internal float x;
        internal Vector3(float value) { x = value; }
        internal float sqrMagnitude => x * x;
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x - b.x);
    }
    public sealed class Transform { internal Vector3 position; }
    internal class GameObject
    {
        internal readonly Dictionary<Type, object> Components = new();
        internal T? GetComponent<T>() where T : class => Components.TryGetValue(typeof(T), out var o) ? (T)o : null;
        internal void Add<T>(T obj) where T : class { Components[typeof(T)] = obj; }
    }
}
namespace BepInEx.Bootstrap
{
    internal static class Chainloader
    {
        internal static readonly Dictionary<string, TestPluginInfo> PluginInfos = new();
    }
    internal sealed class TestPluginInfo { internal object? Instance; }
}
#if STUWARD_API
public sealed class PrivateArea
{
    internal bool Enabled = true, Managed = true, Valid = true, Ghost;
    internal float Center, Radius = 10;
    public bool IsInside(UnityEngine.Vector3 point, float radius) => Math.Abs(point.x - Center) <= Radius + radius;
}
public sealed class Container { public UnityEngine.Transform transform = new(); }
namespace STUWard
{
    internal static class Plugin { internal static bool IsReady = true; }
    internal static class ZNet { internal static object? instance = new(); }
    internal enum WardRestrictionOptions { Containers }
    internal static class WardAccess
    {
        internal static readonly List<PrivateArea> Candidates = new();
        internal static bool ThrowOnQuery;
        internal static int Queries;
        internal static bool HasEnabledManagedWards() => Candidates.Count != 0;
        internal static bool IsManagedWard(PrivateArea area, bool requireEnabled) =>
            area.Managed && area.Valid && !area.Ghost && (!requireEnabled || area.Enabled);
        internal static IReadOnlyList<PrivateArea> GetCandidateManagedWards(UnityEngine.Vector3 point, float radius, bool requireEnabled)
        {
            Queries++;
            if (ThrowOnQuery) throw new InvalidOperationException("simulated ward query failure");
            // Deliberately include stale/disabled and coarse spatial candidates.
            return Candidates;
        }
        internal enum AccessDecision { NoWard, Allowed }
        internal readonly struct AccessResult { internal AccessDecision Decision => AccessDecision.NoWard; internal bool IsDenied => false; }
        internal static AccessResult EvaluateRestrictionAccessAgainstCandidates(WardRestrictionOptions restriction,
            UnityEngine.Vector3 point, float radius, long playerId, IReadOnlyList<PrivateArea> candidates, bool flash) => default;
    }
}
#endif
namespace PortalRules
{
    internal readonly struct ZDOID
    {
        internal readonly long Id;
        internal ZDOID(long id) { Id = id; }
        internal bool IsNone() => Id == 0;
    }
    internal static class ZDOVars
    {
        internal const int s_creator = 1, s_dead = 2, s_tamed = 3, s_health = 4;
    }
    internal sealed class ZDO
    {
        internal ZDOID m_uid;
        internal int Prefab = 1;
        internal long Owner = 100, Creator = 1;
        internal bool Valid = true, Dead, Tamed;
        internal float Health = 100;
        internal Vector3 Position;
        internal PortalBuilder Builder = new("builder");
        internal bool IsValid() => Valid;
        internal int GetPrefab() => Prefab;
        internal long GetOwner() => Owner;
        internal Vector3 GetPosition() => Position;
        internal long GetLong(int key) => Creator;
        internal bool GetBool(int key) => key == ZDOVars.s_dead ? Dead : Tamed;
        internal float GetFloat(int key, float fallback) => Health;
    }
    internal sealed class ZNetView
    {
        internal ZDO Zdo = null!;
        internal bool IsValid() => Zdo.Valid;
        internal bool IsOwner() => Zdo.Owner == ZDOMan.GetSessionID();
        internal ZDO GetZDO() => Zdo;
    }
    internal sealed class ZDOMan
    {
        internal static ZDOMan instance = new();
        internal static long GetSessionID() => 100;
        internal readonly Dictionary<ZDOID, ZDO> Objects = new();
        internal ZDO? GetZDO(ZDOID id) => Objects.TryGetValue(id, out var z) ? z : null;
    }
    internal sealed class ZNetScene
    {
        internal static ZNetScene instance = new();
        internal readonly Dictionary<ZDOID, GameObject> Objects = new();
        internal readonly Dictionary<int, GameObject> Prefabs = new();
        internal GameObject? FindInstance(ZDOID id) => Objects.TryGetValue(id, out var obj) ? obj : null;
        internal GameObject? GetPrefab(int id) => Prefabs.TryGetValue(id, out var obj) ? obj : null;
    }
    internal sealed class ZRpc
    {
        internal readonly List<(string Name, object[] Args)> Sent = new();
        internal void Invoke(string name, params object[] args) => Sent.Add((name, args));
        internal void Register(string name, Action<ZRpc> action) { }
        internal void Register<T1,T2>(string name, Action<ZRpc,T1,T2> action) { }
        internal void Register<T1,T2,T3>(string name, Action<ZRpc,T1,T2,T3> action) { }
    }
    internal sealed class ZNetPeer
    {
        internal long m_uid = 200;
        internal ZDOID m_characterID = new(2000);
        internal ZRpc m_rpc = new();
        internal bool Ready = true, Authenticated = true, Clan, Admin;
        internal string Account = "stranger";
        internal Vector3 Position;
        internal bool IsReady() => Ready;
    }
    internal sealed class ZNet
    {
        internal static ZNet instance = new();
        internal bool Server = true;
        internal List<ZNetPeer> Peers = new();
        internal ZRpc ServerRpc = new();
        internal bool IsServer() => Server;
        internal List<ZNetPeer> GetPeers() => Peers;
        internal ZRpc GetServerRPC() => ServerRpc;
        internal double GetTimeSeconds() => Time.realtimeSinceStartup;
    }
    internal sealed class Game
    {
        internal static Game instance = new();
        internal HashSet<int> PortalPrefabHash = new() { 1, 2 };
    }
    internal class Character
    {
        internal bool Tamed;
        internal GameObject Components = new();
        internal bool IsTamed() => Tamed;
        internal T? GetComponent<T>() where T : class => Components.GetComponent<T>();
    }
    internal sealed class Player : Character
    {
        internal static Player? m_localPlayer;
        internal static bool m_debugMode;
        internal readonly Transform transform = new();
        internal bool Dead;
        internal bool IsDead() => Dead;
        internal void Message(MessageHud.MessageType type, string text) { }
    }
    internal sealed class MonsterAI { }
    internal sealed class HitData
    {
        internal enum HitType { Undefined, PlayerHit, EnemyHit, Structural, CinderFire }
        internal Character? Attacker;
        internal ZDOID m_attacker;
        internal HitType m_hitType;
        internal Character? GetAttacker() => Attacker;
        internal bool HaveAttacker() => !m_attacker.IsNone();
    }
    internal sealed class WearNTear
    {
        internal ZNetView View = null!;
        internal float m_health = 100;
        internal int Removals, Drops;
        internal long Sender;
        internal bool ThrowOnRemove, OtherModDenies;
        internal bool Supported;
        private bool HaveSupport() => Supported;
        internal void UpdateWear(float time) { }
        internal T? GetComponent<T>() where T : class => View as T;
        internal void Remove(bool blockDrop = false)
        {
            if (ProtectionChecks.Patch("RemoveRequestPatch", this, View)) RPC_Remove(100, blockDrop);
        }
        private void RPC_Remove(long sender, bool blockDrop)
        {
            if (!ProtectionChecks.Patch("RemoveAuthorityPatch", this, View) || OtherModDenies) return;
            if (ThrowOnRemove) throw new InvalidOperationException("simulated downstream failure");
            Sender = sender;
            Removals++;
            if (!blockDrop) Drops++;
            View.Zdo.Health = 0;
        }
    }
    internal static class MessageHud { internal enum MessageType { Center } }
    internal readonly struct PortalBuilder
    {
        internal readonly string AccountId;
        internal PortalBuilder(string account) { AccountId = account; }
        internal bool IsValid => !string.IsNullOrEmpty(AccountId);
    }
    internal readonly struct PortalRulesMessage
    {
        internal readonly string Token;
        internal PortalRulesMessage(string token) { Token = token; }
        internal static PortalRulesMessage Empty => default;
        internal bool IsEmpty => string.IsNullOrEmpty(Token);
        internal string Localize() => Token;
    }
    internal static class PortalRulesPlugin
    {
        internal const string StuWardSoftDependencyGuid = "sighsorry.STUWard";
        internal static bool IsAdmin;
        internal static readonly TestLogger PortalRulesLogger = new();
        internal enum Toggle { Off, On }
        internal static bool IsOn(this Toggle t) => t == Toggle.On;
    }
    internal sealed class TestLogger
    {
        internal int Warnings;
        internal void LogWarning(string message) { Warnings++; }
    }
    internal sealed class Entry { internal PortalRulesPlugin.Toggle Value = PortalRulesPlugin.Toggle.On; }
    internal static class PublicPortalConfig { internal static Entry EnablePortalProtection = new(); }
    internal static class PublicPortalKinds
    {
        internal static bool IsAdminPortalPrefab(int prefab) => prefab == 2;
        internal static bool IsRegisteredPortal(ZDO z) => z.Valid && Game.instance.PortalPrefabHash.Contains(z.Prefab);
    }
    internal static class PublicPortalData
    {
        internal static string LocalAccount = "builder";
        internal static PortalBuilder GetBuilder(ZDO z) => z.Builder;
        internal static bool TryGetPeerAccountId(ZNetPeer peer, out string account)
        { account = peer.Account; return peer.Authenticated; }
        internal static bool TryGetLocalAccountId(out string account)
        { account = LocalAccount; return account.Length > 0; }
        internal static bool IsPeerAdmin(ZNet znet, ZNetPeer peer) => peer.Admin;
        internal static ZNetPeer? FindPeer(ZNet znet, ZRpc rpc) => znet.Peers.Find(p => p.m_rpc == rpc);
        internal static bool TryGetAuthenticatedPeerCharacterPosition(ZNetPeer p, out Vector3 pos)
        { pos = p.Position; return p.Authenticated && p.Ready; }
    }
    internal static class PublicPortalCatalog
    {
        internal static bool IsServerReady = true;
        internal static bool TryGetAuthoritativeBuilder(ZDOID id, out PortalBuilder builder)
        { builder = ZDOMan.instance.GetZDO(id)?.Builder ?? default; return builder.IsValid; }
    }
    internal static class ClanPortalAccess
    {
        internal static int Queries;
        internal static bool LocalClan;
        internal static bool IsRequesterInBuilderPrimaryClan(PortalBuilder builder, ZNetPeer? peer)
        { Queries++; return peer?.Clan ?? LocalClan; }
    }
    internal static class PublicPortalInteraction
    {
        internal static bool Near = true;
        internal static bool TryValidatePortalInteraction(ZDO portal, ZNetPeer? peer, out PortalRulesMessage message)
        { message = default; return Near && (peer == null || peer.Authenticated); }
    }
    internal static class PublicPortalServerPolicy
    {
        internal static void SendPolicyMessage(ZNetPeer peer, PortalRulesMessage message) { }
    }

    public static class ProtectionChecks
    {
        private static int _checks;
        private static ZDO _portal = null!;
        private static WearNTear _wear = null!;
        private static ZNetPeer _peer = null!;
        private static readonly Type Service = typeof(PublicPortalProtection);
        private static object? Call(string method, params object?[] args) =>
            Service.GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args);
        internal static bool Patch(string type, params object[] args) => (bool)Service.
            GetNestedType(type, BindingFlags.NonPublic)!.GetMethod("Prefix", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)!;
        private static void Check(bool result, string description)
        {
            _checks++;
            if (!result) throw new Exception(description);
        }
        private static void Reset()
        {
            PublicPortalProtection.Shutdown();
            BepInEx.Bootstrap.Chainloader.PluginInfos.Clear();
            PublicPortalProtection.InitializeStuWard();
#if STUWARD_API
            STUWard.Plugin.IsReady = true; STUWard.ZNet.instance = new();
            STUWard.WardAccess.Candidates.Clear(); STUWard.WardAccess.ThrowOnQuery = false;
            STUWard.WardAccess.Queries = 0;
#endif
            Time.realtimeSinceStartup = 100;
            PublicPortalConfig.EnablePortalProtection.Value = PortalRulesPlugin.Toggle.On;
            ZNet.instance = new(); ZDOMan.instance = new(); ZNetScene.instance = new();
            Player.m_localPlayer = new(); Player.m_debugMode = false;
            PublicPortalData.LocalAccount = "builder"; PublicPortalInteraction.Near = true;
            PublicPortalCatalog.IsServerReady = true; PortalRulesPlugin.IsAdmin = false;
            ClanPortalAccess.Queries = 0; ClanPortalAccess.LocalClan = false;
            _portal = new() { m_uid = new(1) };
            ZDOMan.instance.Objects[_portal.m_uid] = _portal;
            _wear = new() { View = new() { Zdo = _portal } };
            GameObject obj = new(); obj.Add(_wear); obj.Add(_wear.View);
            ZNetScene.instance.Objects[_portal.m_uid] = obj;
            _peer = new(); ZNet.instance.Peers.Add(_peer);
            ZDOMan.instance.Objects[_peer.m_characterID] = new();
        }
        private static bool Approve(ZNetPeer? peer = null, bool debug = false) =>
            (bool)Call("TryApproveRemoval", _portal.m_uid, peer, debug, null)!;
        private static bool Attended() => (bool)Call("IsAttended", _portal, true)!;
        private static bool SkipCollapse(bool supported = false) => (bool)Call("ShouldSkipSupportDamage", supported, _wear)!;
        private static bool Attack(HitData hit) => Patch("AttackProtectionPatch", hit, _wear.View);
        private static HitData Monster()
        {
            Character c = new(); c.Components.Add(new MonsterAI());
            return new() { Attacker = c, m_attacker = new(42), m_hitType = HitData.HitType.EnemyHit };
        }
        private static List<HarmonyLib.CodeInstruction> Transpile(List<HarmonyLib.CodeInstruction> code) =>
            new((IEnumerable<HarmonyLib.CodeInstruction>)Service.GetNestedType("SupportProtectionPatch", BindingFlags.NonPublic)!
                .GetMethod("Transpiler", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { code })!);
        private static float RunSupportBranch(bool supported)
        {
            _wear.Supported = supported;
            var method = new DynamicMethod("supportBranch", typeof(float), new[] { typeof(WearNTear) }, typeof(ProtectionChecks).Module, true);
            ILGenerator il = method.GetILGenerator();
            il.DeclareLocal(typeof(float));
            Label end = il.DefineLabel();
            MethodInfo haveSupport = typeof(WearNTear).GetMethod("HaveSupport", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var finalLoad = new HarmonyLib.CodeInstruction(OpCodes.Ldloc_0);
            finalLoad.labels.Add(end);
            var original = new List<HarmonyLib.CodeInstruction>
            {
                new(OpCodes.Ldc_R4, 7f), new(OpCodes.Stloc_0), // existing environmental wear
                new(OpCodes.Ldarg_0), new(OpCodes.Call, haveSupport),
                new(OpCodes.Brtrue, end), new(OpCodes.Ldc_R4, 100f), new(OpCodes.Stloc_0),
                finalLoad, new(OpCodes.Ret)
            };
            var changed = Transpile(original);
            Check(changed.Count == original.Count + 2 && changed.Contains(finalLoad) &&
                  changed.FindAll(c => c.Calls(haveSupport)).Count == 1,
                "Transpiler keeps original support calculation and labels, adding only the collapse guard");
            foreach (var instruction in changed)
            {
                foreach (Label label in instruction.labels) il.MarkLabel(label);
                switch (instruction.operand)
                {
                    case null: il.Emit(instruction.opcode); break;
                    case float value: il.Emit(instruction.opcode, value); break;
                    case Label label: il.Emit(instruction.opcode, label); break;
                    case MethodInfo target: il.Emit(instruction.opcode, target); break;
                    default: throw new Exception("Unexpected test IL operand");
                }
            }
            return ((Func<WearNTear, float>)method.CreateDelegate(typeof(Func<WearNTear, float>)))(_wear);
        }
        public static void Run()
        {
            Reset();
            Check(Approve(), "Builder can dismantle");
            Check(_wear.Drops == 1 && _wear.Removals == 1, "Normal resource path runs once");
            Check(!Approve() && _wear.Drops == 1, "Duplicate request cannot duplicate resources");
            Reset(); Check(!Approve(_peer), "Public traveler does not acquire removal trust");
            _peer.Account = "builder";
            Check(Approve(_peer) && _wear.Sender == _peer.m_uid, "Preserve actual remote Builder sender on server-owned portal");
            Reset(); _peer.Clan = true;
            Check(Approve(_peer), "Existing Clan policy grants dismantling");
            Reset(); _peer.Admin = true;
            Check(!Approve(_peer), "Admin without debug denied");
            Check(Approve(_peer, true), "Verified admin with debug allowed");
            Reset(); Check(!Approve(_peer, true), "Debug claim alone gives no rights");
            Reset(); PublicPortalInteraction.Near = false;
            Check(!Approve(), "Trusted actor still must be nearby");
            Reset(); PublicPortalCatalog.IsServerReady = false;
            Check(!Approve(), "Unknown server authority fails closed");
            Reset(); _peer.Account = "builder"; _peer.Authenticated = false;
            Check(!Approve(_peer), "Unauthenticated identity fails closed");

            Reset(); _portal.Owner = _peer.m_uid;
            Check(Approve() && _wear.Drops == 0 && _peer.m_rpc.Sent.Count == 1,
                "Server delegates to actual remote owner without dropping resources itself");
            Reset(); _portal.Owner = 999;
            Check(!Approve(), "Unavailable owner does not trigger fallback resource refund");
            Reset(); ZNetScene.instance.Objects.Clear();
            Check(!Approve() && _wear.Drops == 0, "Unloaded owner object must be retried");

            Reset(); ZNet.instance.Server = false;
            Call("OnRemoveApproved", new ZRpc(), _portal.m_uid, 200L, 103d);
            Check(_wear.Drops == 0, "Forged non-server approval rejected");
            Call("OnRemoveApproved", ZNet.instance.ServerRpc, _portal.m_uid, 200L, 99d);
            Check(_wear.Drops == 0, "Expired approval rejected");
            Call("OnRemoveApproved", ZNet.instance.ServerRpc, _portal.m_uid, 200L, double.NaN);
            Check(_wear.Drops == 0, "Invalid expiry rejected");
            Call("OnRemoveApproved", ZNet.instance.ServerRpc, _portal.m_uid, 200L, 103d);
            Check(_wear.Drops == 1 && _wear.Sender == 200, "Owner executes authenticated original sender");
            Call("OnRemoveApproved", ZNet.instance.ServerRpc, _portal.m_uid, 200L, 103d);
            Check(_wear.Drops == 1, "Repeated approval executes once");
            Reset(); ZNet.instance.Server = false; _portal.Owner = 999;
            Call("OnRemoveApproved", ZNet.instance.ServerRpc, _portal.m_uid, 200L, 103d);
            Check(_wear.Drops == 0, "Owner migration rejects stale approval");
            Reset(); _portal.Health = 0;
            Check(!Approve() && _wear.Drops == 0, "Already destroyed portal cannot drop again");

            Reset();
            Check(!Patch("RemoveAuthorityPatch", _wear, _wear.View), "Raw removal RPC cannot bypass server approval");
            _wear.Remove(true);
            Check(_wear.Removals == 0, "blockDrop alone is not a bypass");
            PublicPortalProtection.RemoveForServerPolicy(_wear);
            Check(_wear.Removals == 1 && _wear.Drops == 0, "Server installation cleanup preserves no-drop behavior");
            Check(!Patch("RemoveAuthorityPatch", _wear, _wear.View), "Cleanup scope restored");
            Reset(); _wear.ThrowOnRemove = true;
            try { PublicPortalProtection.RemoveForServerPolicy(_wear); } catch (InvalidOperationException) { }
            Check(!Patch("RemoveAuthorityPatch", _wear, _wear.View), "Cleanup exception restores scope");
            Reset(); _wear.ThrowOnRemove = true;
            try { Approve(); } catch (TargetInvocationException) { }
            Check(!Patch("RemoveAuthorityPatch", _wear, _wear.View), "Approved removal exception restores scope");
            Reset(); _wear.OtherModDenies = true; Approve();
            Check(_wear.Drops == 0, "Other protection mod can still veto approved removal");

            Reset();
            Check(Attack(new() { m_hitType = HitData.HitType.Structural }), "Structural path passes");
            Check(Attack(new() { m_hitType = HitData.HitType.CinderFire }), "Environmental hit passes");
            Check(!Attack(new() { Attacker = new Player() }), "Player attack blocked");
            Check(!Attack(new() { Attacker = new Character { Tamed = true } }), "Tamed attack blocked");
            Check(!Attack(new() { m_attacker = new(999) }), "Unresolved attacker is not mistaken for environment");
            Check(!Attack(new() { m_hitType = HitData.HitType.PlayerHit }), "Missing Player object still blocked");
            Check(!Attack(new() { m_hitType = HitData.HitType.EnemyHit }), "Unresolved EnemyHit fails closed");
            Check(Attack(Monster()), "Monster damage allowed near living Builder");
            Reset(); Player.m_localPlayer = null;
            Check(!Attack(Monster()), "Unattended portal blocks monster");
            _peer.Account = "builder"; Time.realtimeSinceStartup += 1;
            Check(Attack(Monster()), "Authenticated remote ZDO supplies attendance without Player GameObject");
            _peer.Position = new(41); Time.realtimeSinceStartup += 1;
            Check(Attack(Monster()), "Departure grace allows combat to continue");
            Time.realtimeSinceStartup += 10;
            Check(!Attack(Monster()), "Departure grace eventually ends");
            Reset(); Player.m_localPlayer!.Dead = true;
            Check(!Attended(), "Dead host Builder is not attended");
            Reset(); Player.m_localPlayer = null; _peer.Account = "builder";
            ZDOMan.instance.Objects[_peer.m_characterID].Dead = true;
            Check(!Attended(), "Dead remote Builder is not attended");
            Reset(); Player.m_localPlayer = null; _peer.Clan = true; _peer.Position = new(40);
            Check(Attended(), "Trusted Clan at radius boundary counts");
            int calls = ClanPortalAccess.Queries; Attended(); Attended();
            Check(ClanPortalAccess.Queries == calls, "Repeated hits reuse same-second trust decision");
            Reset(); Player.m_localPlayer = null; _peer.Position = new(41);
            Check(!Attended() && ClanPortalAccess.Queries == 0, "Distant peers do not invoke Clan API");

            Reset(); ZNet.instance.Server = false;
            Check(!Attended() && ZNet.instance.ServerRpc.Sent.Count == 1, "Unknown attendance requests server and blocks");
            Attended(); Attended();
            Check(ZNet.instance.ServerRpc.Sent.Count == 1, "Multiple hits do not spam presence requests");
            long seq = (long)ZNet.instance.ServerRpc.Sent[0].Args[1];
            Call("OnPresenceResult", new ZRpc(), _portal.m_uid, seq, 3);
            Check(!Attended(), "Spoofed attendance reply rejected");
            Call("OnPresenceResult", ZNet.instance.ServerRpc, _portal.m_uid, seq + 1, 3);
            Check(!Attended(), "Mismatched attendance reply rejected");
            Call("OnPresenceResult", ZNet.instance.ServerRpc, _portal.m_uid, seq, 3);
            Check(Attended(), "Valid server attendance reply permits monster damage");
            Time.realtimeSinceStartup += 4;
            Check(!Attended(), "Expired reply does not extend vulnerability indefinitely");
            PublicPortalProtection.InvalidatePresence();
            Call("OnPresenceResult", ZNet.instance.ServerRpc, _portal.m_uid, seq, 3);
            Check(!Attended(), "Invalidation rejects in-flight old reply");
            long newSeq = (long)ZNet.instance.ServerRpc.Sent[ZNet.instance.ServerRpc.Sent.Count - 1].Args[1];
            Check(newSeq != seq, "Request IDs not reused after invalidation");
            Call("OnPresenceResult", ZNet.instance.ServerRpc, _portal.m_uid, newSeq, 3);
            PublicPortalProtection.ForgetPortal(_portal.m_uid);
            Check(!Attended(), "Destroyed portal clears attendance state");
            Reset(); _portal.Owner = _peer.m_uid;
            Call("OnPresenceRequest", _peer.m_rpc, _portal.m_uid, 1L);
            Check(_peer.m_rpc.Sent.Count == 1, "Actual owner receives server decision");
            _portal.Owner = 999;
            Call("OnPresenceRequest", _peer.m_rpc, _portal.m_uid, 2L);
            Check(_peer.m_rpc.Sent.Count == 1, "Non-owner cannot query attendance");

            Reset(); PublicPortalConfig.EnablePortalProtection.Value = PortalRulesPlugin.Toggle.Off;
            Check(Attack(new() { Attacker = new Player() }), "Off preserves original attack behavior");
            Check(Patch("RemoveAuthorityPatch", _wear, _wear.View), "Off preserves original removal behavior");
            Reset(); _portal.Prefab = 2;
            Check(Attack(new() { Attacker = new Player() }), "Admin portal policy untouched");
            Reset(); _portal.Creator = 0; _portal.Builder = default;
            Check(Attack(new() { Attacker = new Player() }), "Creatorless system portal untouched");
            Reset(); _portal.Prefab = 77;
            Check(Attack(new() { Attacker = new Player() }), "Ordinary building untouched");

            Reset();
            Check(!SkipCollapse(), "Living nearby Builder permits unsupported collapse");
            Player.m_localPlayer!.transform.position = new(41); Time.realtimeSinceStartup += 1;
            Check(Attended() && SkipCollapse(), "Support protection resumes without the monster departure grace");
            Reset(); Player.m_localPlayer = null;
            Check(SkipCollapse(), "Unattended unsupported portal is protected");
            int beforeSupported = ClanPortalAccess.Queries;
            Check(SkipCollapse(true) && ClanPortalAccess.Queries == beforeSupported, "Supported portal never needs trust lookup");
            Reset(); _portal.Prefab = 77;
            Check(!SkipCollapse(), "Unsupported non-portal follows vanilla");
            Reset(); _portal.Prefab = 2;
            Check(!SkipCollapse(), "Admin portal support policy unchanged");
            Reset(); PublicPortalConfig.EnablePortalProtection.Value = PortalRulesPlugin.Toggle.Off;
            Check(!SkipCollapse(), "Off permits normal unsupported collapse");
            Reset(); Player.m_localPlayer = null; _peer.Clan = true;
            Check(!SkipCollapse(), "Remote trusted Clan presence permits collapse without loaded player");
            PublicPortalProtection.ForgetPeer(_peer.m_rpc); _peer.Ready = false;
            Check(SkipCollapse(), "Disconnect invalidation restores support protection");

            Reset(); ZNet.instance.Server = false;
            Check(SkipCollapse(), "Unknown server response protects support");
            long supportSeq = (long)ZNet.instance.ServerRpc.Sent[0].Args[1];
            Call("OnPresenceResult", ZNet.instance.ServerRpc, _portal.m_uid, supportSeq, 1);
            Check(Attended() && SkipCollapse(), "Remote combat grace alone cannot permit collapse");
            Call("OnPresenceResult", ZNet.instance.ServerRpc, _portal.m_uid, supportSeq, 3);
            Check(!SkipCollapse(), "Remote current presence permits collapse");
            Time.realtimeSinceStartup += 4;
            Check(SkipCollapse(), "Expired remote presence cannot permit collapse");

            Reset(); Player.m_localPlayer = null;
            Check(RunSupportBranch(false) == 7f, "Guard suppresses collapse but preserves environmental wear");
            Check(RunSupportBranch(true) == 7f, "Supported portal retains environmental wear");
            Reset(); Check(RunSupportBranch(false) == 100f, "Attended unsupported portal keeps vanilla collapse damage");
            Reset(); PublicPortalConfig.EnablePortalProtection.Value = PortalRulesPlugin.Toggle.Off;
            Check(RunSupportBranch(false) == 100f, "Off executes vanilla collapse branch");
            var unknown = new List<HarmonyLib.CodeInstruction> { new(OpCodes.Ret) };
            int warnings = PortalRulesPlugin.PortalRulesLogger.Warnings;
            var fallback = Transpile(unknown);
            Check(fallback.Count == 1 && ReferenceEquals(fallback[0], unknown[0]) &&
                  PortalRulesPlugin.PortalRulesLogger.Warnings == warnings + 1,
                "Unknown game IL warns and preserves original code without aborting plugin initialization");

            Reset();
            BepInEx.Bootstrap.Chainloader.PluginInfos[PortalRulesPlugin.StuWardSoftDependencyGuid] = new() { Instance = new object() };
            PublicPortalProtection.InitializeStuWard();
            Check(!Attack(new() { Attacker = new Player() }), "Missing coverage API preserves portal protection");
            Check(!Patch("RemoveAuthorityPatch", _wear, _wear.View), "Old STUWard cannot bypass removal authority");
#if STUWARD_API
            Reset(); Player.m_localPlayer = null; ZNet.instance.Server = false;
            BepInEx.Bootstrap.Chainloader.PluginInfos[PortalRulesPlugin.StuWardSoftDependencyGuid] = new() { Instance = new TestLogger() };
            PublicPortalProtection.InitializeStuWard();
            PrivateArea ward = new(); STUWard.WardAccess.Candidates.Add(ward);
            Check(Attack(new() { Attacker = new Player() }), "Active ward delegates player damage to other patches");
            Check(Attack(Monster()), "Active ward delegates hostile damage without portal attendance");
            Check(!SkipCollapse(), "Unsupported portal collapses normally inside active ward");
            Check(RunSupportBranch(false) == 100f, "Real support branch retains collapse damage inside ward");
            Check(ClanPortalAccess.Queries == 0 && ZNet.instance.ServerRpc.Sent.Count == 0,
                "Ward delegation avoids portal trust and presence RPC queries");
            Check(Patch("RemoveRequestPatch", _wear, _wear.View) && Patch("RemoveAuthorityPatch", _wear, _wear.View),
                "Ward-covered removal passes to vanilla and STUWard checks at both endpoints");
            _wear.OtherModDenies = true; _wear.Remove();
            Check(_wear.Drops == 0, "Delegation never bypasses another mod's removal denial");

            int wardQueries = STUWard.WardAccess.Queries;
            Check(SkipCollapse(true) && STUWard.WardAccess.Queries == wardQueries, "Supported portal skips even ward lookup");
            PublicPortalConfig.EnablePortalProtection.Value = PortalRulesPlugin.Toggle.Off;
            Check(Attack(Monster()) && !SkipCollapse() && STUWard.WardAccess.Queries == wardQueries,
                "Disabled portal protection does not query STUWard");
            PublicPortalConfig.EnablePortalProtection.Value = PortalRulesPlugin.Toggle.On;
            _portal.Prefab = 77; Attack(Monster()); SkipCollapse();
            Check(STUWard.WardAccess.Queries == wardQueries, "Non-portal paths never query wards");
            _portal.Prefab = 1;

            _portal.Position = new(10);
            Check(Attack(Monster()), "Exact ward boundary uses STUWard containment");
            _portal.Position = new(11);
            Check(!Attack(new() { Attacker = new Player() }), "Coarse index candidate outside actual radius keeps protection");
            ward.Radius = 12;
            Check(Attack(Monster()), "Radius expansion takes effect without portal cache invalidation");
            ward.Radius = 9;
            Check(!Attack(new() { Attacker = new Player() }), "Radius shrink restores portal protection");
            _portal.Position = new(0); ward.Enabled = false;
            Check(!Attack(new() { Attacker = new Player() }) && SkipCollapse(), "Disabled stale index entry cannot waive protection");
            Check(!Patch("RemoveAuthorityPatch", _wear, _wear.View), "Ward disabling before removal RPC restores approval requirement");
            ward.Enabled = true;
            Check(Attack(Monster()), "Ward reactivation restores delegation");
            ward.Valid = false;
            Check(!Attack(new() { Attacker = new Player() }), "Destroyed stale ward does not waive protection");
            ward.Valid = true; ward.Ghost = true;
            Check(!Attack(new() { Attacker = new Player() }), "Placement ghost does not waive protection");
            ward.Ghost = false; ward.Managed = false;
            Check(!Attack(new() { Attacker = new Player() }), "Vanilla or other mod ward does not waive protection");
            ward.Managed = true; STUWard.Plugin.IsReady = false;
            Check(!Attack(new() { Attacker = new Player() }), "Failed or stopped STUWard retains portal protection");
            STUWard.Plugin.IsReady = true; STUWard.ZNet.instance = null;
            Check(!Attack(new() { Attacker = new Player() }), "No world means no active ward coverage");
            STUWard.ZNet.instance = new(); STUWard.WardAccess.Candidates.Clear();
            Check(!Attack(new() { Attacker = new Player() }), "Unloaded or removed ward restores portal protection");

            STUWard.WardAccess.Candidates.Add(ward); STUWard.WardAccess.ThrowOnQuery = true;
            int failedQueryWarnings = PortalRulesPlugin.PortalRulesLogger.Warnings;
            Check(!Attack(new() { Attacker = new Player() }) && SkipCollapse(), "API exceptions preserve damage and collapse protection");
            Check(!Patch("RemoveAuthorityPatch", _wear, _wear.View), "API exception cannot authorize raw removal");
            Check(PortalRulesPlugin.PortalRulesLogger.Warnings == failedQueryWarnings + 1, "Repeated API failure logs once");
            STUWard.WardAccess.ThrowOnQuery = false;
            Check(Attack(Monster()), "Recovered API resumes live delegation");

            ZNet.instance.Server = true;
            Check(!Approve(_peer), "Active ward never mints a PortalRules grant for an untrusted Builder requester");
            _peer.Account = "builder"; _wear.OtherModDenies = false;
            Check(Approve(_peer) && _wear.Drops == 1 && _wear.Sender == _peer.m_uid,
                "Existing Builder approval still preserves original sender inside ward");
            Check(!Approve(_peer) && _wear.Drops == 1, "Delegation does not bypass duplicate approved-removal checks");
            Console.WriteLine("STUWard integration checks used both production services with controlled ward candidates and lifecycle state.");
#endif
            Console.WriteLine($"{_checks} portal protection checks passed (complete production service; game/transport/Clan doubles, no Unity or Harmony execution).");
        }
    }
}
