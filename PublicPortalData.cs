using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HarmonyLib;
using Splatform;
using UnityEngine;

namespace PortalRules;

internal enum PublicPortalAccessMode
{
    Personal = 0,
    Admin = 1,
    Public = 2,
    Clan = 3,
    Tagged = 4,
    Invite = 5
}

internal enum PublicPortalTravelCostScope
{
    Off = 0,
    All = 1,
    AdminPortalTrips = 2,
    PersonalAndClanRoutesFree = 3,
    AllItemsSourceTrips = 4
}

internal enum RequiredGlobalKeyValidationFailure
{
    None = 0,
    TooLong = 1,
    ControlCharacters = 2,
    NumericKey = 3,
    ReservedKey = 4
}

internal readonly struct PortalOwner
{
    public readonly string Id;
    public readonly string Name;

    public PortalOwner(string id, string name)
    {
        Id = PublicPortalData.NormalizeId(id);
        Name = name ?? "";
    }

    public bool IsValid => !string.IsNullOrWhiteSpace(Id);
}

internal readonly struct PortalBuilder
{
    public readonly string AccountId;
    public readonly string Name;
    public readonly long CharacterPlayerId;
    public readonly long BuildSequence;

    public PortalBuilder(string accountId, string name)
        : this(accountId, name, 0L, 0L)
    {
    }

    public PortalBuilder(
        string accountId,
        string name,
        long characterPlayerId,
        long buildSequence)
    {
        AccountId = PublicPortalData.NormalizeId(accountId);
        Name = name ?? "";
        CharacterPlayerId = characterPlayerId;
        BuildSequence = Math.Max(0L, buildSequence);
    }

    public bool IsValid => !string.IsNullOrWhiteSpace(AccountId);

    public PortalBuilder WithBuildSequence(long buildSequence)
    {
        return new PortalBuilder(
            AccountId,
            Name,
            CharacterPlayerId,
            buildSequence);
    }
}

internal static class PublicPortalData
{
    private static readonly AccessTools.FieldRef<ZNet, Platform> SteamPlatform =
        AccessTools.FieldRefAccess<ZNet, Platform>("m_steamPlatform");
    private static string _serverAssignedOwnerId = "";
    private static bool _serverAssignedIsAdmin;

    internal const int ClanAccessAuthorityVersion = 3;
    internal const int RequiredGlobalKeyAuthorityVersion = 4;
    internal const int TemporaryPublicAuthorityVersion = 5;
    internal const int InviteAccessAuthorityVersion = 6;
    internal const int CurrentAccessAuthorityVersion = 6;
    internal const int CurrentBuilderAuthorityVersion = 2;
    internal const int MaximumRequiredGlobalKeyLength = 128;
    internal const int MaximumPortalLimit = 10000;
    internal const int MaximumPortalCount = 100000;
    internal const long MaximumUtcSeconds = 253402300799L;
    internal const float MaximumTeleportSourceDistance = 15f;

    public const string AccessModeKey = "PortalRules AccessMode";
    public const string OwnerIdKey = "PortalRules OwnerId";
    public const string OwnerNameKey = "PortalRules OwnerName";
    public const string AuthorizedClanIdKey = "PortalRules AuthorizedClanId";
    public const string AuthorityVersionKey = "PortalRules AuthorityVersion";
    public const string BuilderAccountIdKey = "PortalRules BuilderAccountId";
    public const string BuilderNameKey = "PortalRules BuilderName";
    public const string BuilderCharacterPlayerIdKey = "PortalRules BuilderPlayerId";
    public const string PortalBuildSequenceKey = "PortalRules BuildSequence";
    public const string BuilderAuthorityVersionKey = "PortalRules BuilderAuthorityVersion";
    public const string AuthorizedPrefabHashKey = "PortalRules AuthorizedPrefabHash";
    public const string PublicExpiresAtUtcSecondsKey = "PortalRules PublicExpiresAtUtc";
    public const string FavoriteIdKey = "PortalRules FavoriteId";
    public const string RequiredGlobalKeyKey = "PortalRules RequiredGlobalKey";
    internal const int MaximumFavoriteCount = 10;
    public const string ChangeAccessModeRpc = "sighsorry.PortalRules.ChangeAccessMode.v4";
    public const string ChangeAccessModeResultRpc = "sighsorry.PortalRules.ChangeAccessModeResult.v5";
    public const string ChangeAdminPortalTagRpc = "sighsorry.PortalRules.ChangeAdminPortalTag.v1";
    public const string ChangeAdminPortalRequiredGlobalKeyRpc =
        "sighsorry.PortalRules.ChangeAdminPortalRequiredGlobalKey.v1";
    public const string RemoveAdminPortalRpc = "sighsorry.PortalRules.RemoveAdminPortal.v1";
    public const string FavoritesKey = "PortalRules Favorites v3";

    public static PublicPortalAccessMode GetAccessMode(ZDO zdo)
    {
        int value = zdo.GetInt(
            AccessModeKey,
            (int)PublicPortalAccessMode.Personal);
        return Enum.IsDefined(typeof(PublicPortalAccessMode), value) &&
               (value != (int)PublicPortalAccessMode.Invite ||
                zdo.GetInt(AuthorityVersionKey, 0) >=
                InviteAccessAuthorityVersion)
            ? (PublicPortalAccessMode)value
            : PublicPortalAccessMode.Personal;
    }

    public static PortalOwner GetOwner(ZDO zdo)
    {
        return new PortalOwner(zdo.GetString(OwnerIdKey, ""), zdo.GetString(OwnerNameKey, ""));
    }

    public static PortalBuilder GetBuilder(ZDO zdo)
    {
        return new PortalBuilder(
            zdo.GetString(BuilderAccountIdKey, ""),
            zdo.GetString(BuilderNameKey, ""),
            zdo.GetLong(BuilderCharacterPlayerIdKey, 0L),
            zdo.GetLong(PortalBuildSequenceKey, 0L));
    }

    public static string GetAuthorizedClanId(ZDO zdo)
    {
        return (zdo.GetString(AuthorizedClanIdKey, "") ?? "").Trim();
    }

    public static string GetRequiredGlobalKey(ZDO zdo)
    {
        return (zdo.GetString(RequiredGlobalKeyKey, "") ?? "").Trim();
    }

    public static long GetPublicExpiresAtUtcSeconds(ZDO zdo)
    {
        return NormalizePublicExpiresAtUtcSeconds(
            zdo.GetLong(PublicExpiresAtUtcSecondsKey, 0L));
    }

    public static void SetAccessMode(ZDO zdo, PublicPortalAccessMode mode, PortalOwner owner)
    {
        zdo.Set(AccessModeKey, (int)mode);
        zdo.Set(OwnerIdKey, owner.Id);
        zdo.Set(OwnerNameKey, owner.Name);
    }

    public static void SetAuthorizedClanId(ZDO zdo, string authorizedClanId)
    {
        zdo.Set(AuthorizedClanIdKey, (authorizedClanId ?? "").Trim());
    }

    public static void SetRequiredGlobalKey(ZDO zdo, string requiredGlobalKey)
    {
        zdo.Set(RequiredGlobalKeyKey, (requiredGlobalKey ?? "").Trim());
    }

    public static void SetPublicExpiresAtUtcSeconds(
        ZDO zdo,
        long publicExpiresAtUtcSeconds)
    {
        zdo.Set(
            PublicExpiresAtUtcSecondsKey,
            NormalizePublicExpiresAtUtcSeconds(publicExpiresAtUtcSeconds));
    }

    internal static long NormalizePublicExpiresAtUtcSeconds(long value)
    {
        return value is > 0L and <= MaximumUtcSeconds
            ? value
            : 0L;
    }

    internal static long GetUtcNowSeconds()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    internal static bool TryNormalizeRequiredGlobalKey(
        string? value,
        out string normalized,
        out RequiredGlobalKeyValidationFailure failure)
    {
        normalized = (value ?? "").Trim();
        failure = RequiredGlobalKeyValidationFailure.None;
        if (normalized.Length == 0)
        {
            return true;
        }

        if (normalized.Length > MaximumRequiredGlobalKeyLength)
        {
            failure = RequiredGlobalKeyValidationFailure.TooLong;
            return false;
        }

        foreach (char character in normalized)
        {
            if (char.IsControl(character))
            {
                failure = RequiredGlobalKeyValidationFailure.ControlCharacters;
                return false;
            }
        }

        string booleanKey = ZoneSystem.GetKeyValue(normalized, out _, out _);
        if (long.TryParse(
                booleanKey,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out _))
        {
            failure = RequiredGlobalKeyValidationFailure.NumericKey;
            return false;
        }

        if (Enum.TryParse(booleanKey, true, out GlobalKeys globalKey) &&
            Enum.IsDefined(typeof(GlobalKeys), globalKey) &&
            globalKey is GlobalKeys.NonServerOption or GlobalKeys.Count)
        {
            failure = RequiredGlobalKeyValidationFailure.ReservedKey;
            return false;
        }

        return true;
    }

    public static void SetBuilder(ZDO zdo, PortalBuilder builder, int prefabHash)
    {
        zdo.Set(BuilderAccountIdKey, builder.AccountId);
        zdo.Set(BuilderNameKey, builder.Name);
        zdo.Set(BuilderCharacterPlayerIdKey, builder.CharacterPlayerId);
        zdo.Set(PortalBuildSequenceKey, builder.BuildSequence);
        zdo.Set(AuthorizedPrefabHashKey, prefabHash);
    }

    public static PortalOwner LocalOwner()
    {
        string id = _serverAssignedOwnerId;
        if (string.IsNullOrWhiteSpace(id) &&
            TryGetLocalSteamId64(out string steamId))
        {
            id = steamId;
        }

        string name = Player.m_localPlayer != null
            ? ((Character)Player.m_localPlayer).GetHoverName()
            : "";
        return new PortalOwner(id, name);
    }

    public static void SetServerAssignedOwnerId(string ownerId)
    {
        _serverAssignedOwnerId = NormalizeId(ownerId);
    }

    public static string ServerAssignedOwnerId => _serverAssignedOwnerId;

    public static bool ServerAssignedIsAdmin => _serverAssignedIsAdmin;

    public static void SetServerAssignedIsAdmin(bool isAdmin)
    {
        _serverAssignedIsAdmin = isAdmin;
    }

    public static bool TryGetPeerOwner(ZNetPeer? peer, out PortalOwner owner)
    {
        owner = new PortalOwner("", "");
        if (!TryGetPeerSteamId64(peer, out string steamId))
        {
            return false;
        }

        owner = new PortalOwner(steamId, peer?.m_playerName ?? "");
        return owner.IsValid;
    }

    internal static bool TryGetPeerSteamId64(ZNetPeer? peer, out string steamId)
    {
        steamId = "";
        if (ZNet.m_onlineBackend != OnlineBackendType.Steamworks ||
            ZNet.instance == null ||
            peer?.m_socket == null)
        {
            return false;
        }

        string hostName = peer.m_socket.GetHostName();
        if (string.IsNullOrWhiteSpace(hostName))
        {
            return false;
        }

        PlatformUserID platformUserId = new(
            SteamPlatform(ZNet.instance),
            hostName);
        return TryNormalizeSteamId64(platformUserId.ToString(), out steamId);
    }

    internal static bool TryGetLocalSteamId64(out string steamId)
    {
        steamId = "";
        if (ZNet.m_onlineBackend != OnlineBackendType.Steamworks)
        {
            return false;
        }

        try
        {
            if (TryNormalizeSteamId64(
                    UserInfo.GetLocalUser().UserId.ToString(),
                    out steamId))
            {
                return true;
            }
        }
        catch (Exception)
        {
            // Fall through to the distribution platform identity.
        }

        IDistributionPlatform? platform = PlatformManager.DistributionPlatform;
        return platform?.LocalUser != null &&
               TryNormalizeSteamId64(
                   platform.LocalUser.PlatformUserID.ToString(),
                   out steamId);
    }

    internal static bool TryGetAuthenticatedPeerPlayerId(
        ZNetPeer? peer,
        out long playerId)
    {
        return TryGetAuthenticatedPeerCharacter(
            peer,
            out _,
            out playerId);
    }

    public static bool IsPeerAdmin(ZNet znet, ZNetPeer peer)
    {
        return ZNet.m_onlineBackend == OnlineBackendType.Steamworks &&
               znet != null &&
               peer?.m_socket != null &&
               znet.IsAdmin(peer.m_socket.GetHostName());
    }

    internal static ZNetPeer? FindPeer(ZNet? znet, ZRpc? rpc)
    {
        if (znet == null || rpc == null)
        {
            return null;
        }

        foreach (ZNetPeer peer in znet.GetPeers())
        {
            if (peer != null && ReferenceEquals(peer.m_rpc, rpc))
            {
                return peer;
            }
        }

        return null;
    }

    internal static bool TryGetAuthenticatedPeerCharacterPosition(
        ZNetPeer? peer,
        out Vector3 position)
    {
        position = Vector3.zero;
        if (ZNetScene.instance == null ||
            !TryGetAuthenticatedPeerCharacter(
                peer,
                out ZDO character,
                out _))
        {
            return false;
        }

        // A dedicated server can retain the authenticated character ZDO while
        // its remote Player GameObject is outside the server's instantiated
        // scene. The ZDO has already been bound to this ready peer, its owner,
        // the Player prefab, and a non-zero playerID above, so requiring a live
        // Unity instance here creates false denials without strengthening the
        // identity or proximity checks.
        position = character.GetPosition();
        return IsFinite(position);
    }

    private static bool TryGetAuthenticatedPeerCharacter(
        ZNetPeer? peer,
        out ZDO character,
        out long playerId)
    {
        character = null!;
        playerId = 0L;
        if (peer == null ||
            !peer.IsReady() ||
            ZNet.instance == null ||
            !ZNet.instance.IsServer() ||
            ZDOMan.instance == null ||
            peer.m_characterID.IsNone() ||
            peer.m_characterID.UserID != peer.m_uid)
        {
            return false;
        }

        ZDO? authenticatedCharacter = ZDOMan.instance.GetZDO(peer.m_characterID);
        if (authenticatedCharacter == null ||
            !authenticatedCharacter.IsValid() ||
            authenticatedCharacter.GetOwner() != peer.m_uid)
        {
            return false;
        }

        if (ZNetScene.instance != null)
        {
            GameObject? characterPrefab =
                ZNetScene.instance.GetPrefab(authenticatedCharacter.GetPrefab());
            if (characterPrefab == null ||
                characterPrefab.GetComponent<Player>() == null)
            {
                return false;
            }
        }

        long authenticatedPlayerId = authenticatedCharacter.GetLong(
            ZDOVars.s_playerID,
            0L);
        if (authenticatedPlayerId == 0L)
        {
            return false;
        }

        character = authenticatedCharacter;
        playerId = authenticatedPlayerId;
        return true;
    }

    public static bool IsLocalOwner(PortalOwner owner)
    {
        return owner.IsValid && string.Equals(owner.Id, LocalOwner().Id, StringComparison.Ordinal);
    }

    public static string NormalizeId(string id)
    {
        id = (id ?? "").Trim();
        return id.StartsWith("Steam_", StringComparison.Ordinal) ? id.Substring("Steam_".Length) : id;
    }

    internal static bool TryNormalizeSteamId64(string? value, out string steamId)
    {
        steamId = "";
        string candidate = (value ?? "").Trim();
        if (candidate.StartsWith("Steam_", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate.Substring("Steam_".Length);
        }

        if (candidate.Length != 17 ||
            !ulong.TryParse(
                candidate,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out ulong parsed) ||
            parsed == 0UL)
        {
            return false;
        }

        steamId = parsed.ToString(CultureInfo.InvariantCulture);
        return string.Equals(candidate, steamId, StringComparison.Ordinal);
    }

    public static bool TryNormalizeFavoriteId(string? favoriteId, out string normalizedFavoriteId)
    {
        normalizedFavoriteId = "";
        if (!Guid.TryParseExact((favoriteId ?? "").Trim(), "N", out Guid parsedFavoriteId) ||
            parsedFavoriteId == Guid.Empty)
        {
            return false;
        }

        normalizedFavoriteId = parsedFavoriteId.ToString("N");
        return true;
    }

    public static void SetFavoriteId(ZDO zdo, string favoriteId)
    {
        if (zdo != null && TryNormalizeFavoriteId(favoriteId, out string normalizedFavoriteId))
        {
            zdo.Set(FavoriteIdKey, normalizedFavoriteId);
        }
    }

    private static bool IsFinite(Vector3 value)
    {
        return !float.IsNaN(value.x) &&
               !float.IsInfinity(value.x) &&
               !float.IsNaN(value.y) &&
               !float.IsInfinity(value.y) &&
               !float.IsNaN(value.z) &&
               !float.IsInfinity(value.z);
    }

    public static List<string> ReadFavorites()
    {
        if (Player.m_localPlayer == null ||
            !Player.m_localPlayer.m_customData.TryGetValue(FavoritesKey, out string value) ||
            string.IsNullOrWhiteSpace(value))
        {
            return new List<string>();
        }

        List<string> favorites = new(MaximumFavoriteCount);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string candidate in value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (TryNormalizeFavoriteId(candidate, out string favoriteId) &&
                seen.Add(favoriteId))
            {
                favorites.Add(favoriteId);
                if (favorites.Count >= MaximumFavoriteCount)
                {
                    break;
                }
            }
        }

        return favorites;
    }

    public static void WriteFavorites(IEnumerable<string> favorites)
    {
        if (Player.m_localPlayer == null)
        {
            return;
        }

        List<string> normalized = new(MaximumFavoriteCount);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string candidate in favorites)
        {
            if (!TryNormalizeFavoriteId(candidate, out string favoriteId) ||
                !seen.Add(favoriteId))
            {
                continue;
            }

            normalized.Add(favoriteId);
            if (normalized.Count >= MaximumFavoriteCount)
            {
                break;
            }
        }

        string value = string.Join(",", normalized);
        if (string.IsNullOrWhiteSpace(value))
        {
            Player.m_localPlayer.m_customData.Remove(FavoritesKey);
        }
        else
        {
            Player.m_localPlayer.m_customData[FavoritesKey] = value;
        }
    }

}
