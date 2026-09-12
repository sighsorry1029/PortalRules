using System;
using System.Collections.Generic;
using UnityEngine;

namespace PortalRules;

internal static class PublicPortalTravelCost
{
    internal const int CargoWeightScale = 1000;
    internal const int MaximumCargoWeightUnits = 1000000000;
    private const float DefaultCoinsPerWeightKilometer = 0.1f;
    private static int _cachedFrame = -1;
    private static Player? _cachedPlayer;
    private static bool _cachedSourceAllowsAllItems;
    private static bool _cachedCanTravel;
    private static int _cachedCargoWeightUnits;

    internal static bool IsEnabled =>
        PublicPortalConfig.PortalFareMode.Value == PublicPortalFareMode.Pay;

    internal static bool TryGetLocalCargoWeightUnits(
        bool sourceAllowsAllItems,
        out int cargoWeightUnits,
        bool forceRefresh = false)
    {
        Player? player = Player.m_localPlayer;
        if (player == null)
        {
            cargoWeightUnits = 0;
            return false;
        }

        int frame = Time.frameCount;
        if (!forceRefresh &&
            frame == _cachedFrame &&
            ReferenceEquals(player, _cachedPlayer) &&
            sourceAllowsAllItems == _cachedSourceAllowsAllItems)
        {
            cargoWeightUnits = _cachedCargoWeightUnits;
            return _cachedCanTravel;
        }

        bool canTravel = TryGetCargoWeightUnits(
            player,
            sourceAllowsAllItems,
            out cargoWeightUnits);
        _cachedFrame = frame;
        _cachedPlayer = player;
        _cachedSourceAllowsAllItems = sourceAllowsAllItems;
        _cachedCanTravel = canTravel;
        _cachedCargoWeightUnits = cargoWeightUnits;
        return canTravel;
    }

    internal static bool TryGetCargoWeightUnits(
        Player player,
        bool sourceAllowsAllItems,
        out int cargoWeightUnits)
    {
        cargoWeightUnits = 0;
        Inventory? inventory = player?.GetInventory();
        if (inventory == null)
        {
            return false;
        }

        bool bypassOrdinaryRestrictions =
            sourceAllowsAllItems ||
            ZoneSystem.instance != null &&
            ZoneSystem.instance.GetGlobalKey(GlobalKeys.TeleportAll);
        double restrictedWeight = 0d;
        List<ItemDrop.ItemData> items = inventory.GetAllItems();
        foreach (ItemDrop.ItemData item in items)
        {
            if (item?.m_shared == null || item.m_stack <= 0)
            {
                continue;
            }

            // Valheim 1.0 reserves tier 1000+ for restrictions which also
            // apply to all-items portals and the TeleportAll world modifier.
            if (item.m_shared.m_toolTier >= 1000)
            {
                return false;
            }

            if (bypassOrdinaryRestrictions || item.m_shared.m_teleportable)
            {
                continue;
            }

            float itemWeight = item.GetWeight();
            if (float.IsNaN(itemWeight) ||
                float.IsInfinity(itemWeight) ||
                itemWeight <= 0f)
            {
                continue;
            }

            restrictedWeight += itemWeight;
            if (restrictedWeight >=
                (double)MaximumCargoWeightUnits / CargoWeightScale)
            {
                cargoWeightUnits = MaximumCargoWeightUnits;
                return IsEnabled;
            }
        }

        cargoWeightUnits = restrictedWeight <= 0d
            ? 0
            : (int)Math.Ceiling(restrictedWeight * CargoWeightScale);
        return cargoWeightUnits == 0 || IsEnabled;
    }

    internal static int CalculateCost(
        PublicPortalCatalogEntry source,
        PublicPortalCatalogEntry target)
    {
        if (!TryGetLocalCargoWeightUnits(
                source.AllowsAllItems,
                out int cargoWeightUnits))
        {
            return 0;
        }

        return CalculateCost(
            source.AllowsAllItems,
            source.Position,
            target.Position,
            cargoWeightUnits);
    }

    internal static int CalculateCost(
        bool sourceAllowsAllItems,
        Vector3 sourcePosition,
        Vector3 targetPosition,
        int cargoWeightUnits)
    {
        if (!IsEnabled ||
            sourceAllowsAllItems ||
            cargoWeightUnits <= 0 ||
            cargoWeightUnits > MaximumCargoWeightUnits ||
            !TryGetDistanceXZ(
                sourcePosition,
                targetPosition,
                out double distanceMeters))
        {
            return 0;
        }

        double rate = NonNegativeFiniteOrDefault(
            PublicPortalConfig.CoinsPerWeightKilometer.Value,
            DefaultCoinsPerWeightKilometer);
        return CalculateFare(cargoWeightUnits, distanceMeters, (float)rate);
    }

    internal static int CalculateFare(
        int cargoWeightUnits,
        double distanceMeters,
        float coinsPerWeightKilometer)
    {
        if (cargoWeightUnits <= 0 ||
            cargoWeightUnits > MaximumCargoWeightUnits ||
            double.IsNaN(distanceMeters) ||
            double.IsInfinity(distanceMeters) ||
            distanceMeters < 0d ||
            float.IsNaN(coinsPerWeightKilometer) ||
            float.IsInfinity(coinsPerWeightKilometer) ||
            coinsPerWeightKilometer < 0f)
        {
            return 0;
        }

        double total = Math.Ceiling(
            cargoWeightUnits /
            (double)CargoWeightScale *
            (distanceMeters / 1000d) *
            coinsPerWeightKilometer);
        if (double.IsNaN(total) ||
            double.IsInfinity(total) ||
            total >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return total <= 0d ? 0 : (int)total;
    }

    internal static Sprite? GetCoinIcon()
    {
        if (!PortalCoinWallet.TryGetCoinItemData(
                out ItemDrop.ItemData coin) ||
            coin.m_shared?.m_icons == null ||
            coin.m_variant < 0 ||
            coin.m_variant >= coin.m_shared.m_icons.Length)
        {
            return null;
        }

        return coin.GetIcon();
    }

    private static bool TryGetDistanceXZ(
        Vector3 sourcePosition,
        Vector3 targetPosition,
        out double distanceMeters)
    {
        distanceMeters = 0d;
        if (!IsFinite(sourcePosition.x) ||
            !IsFinite(sourcePosition.z) ||
            !IsFinite(targetPosition.x) ||
            !IsFinite(targetPosition.z))
        {
            return false;
        }

        double deltaX = (double)targetPosition.x - sourcePosition.x;
        double deltaZ = (double)targetPosition.z - sourcePosition.z;
        double squaredDistance = deltaX * deltaX + deltaZ * deltaZ;
        if (!IsFinite(squaredDistance) || squaredDistance < 0d)
        {
            return false;
        }

        distanceMeters = Math.Sqrt(squaredDistance);
        return IsFinite(distanceMeters);
    }

    private static double NonNegativeFiniteOrDefault(
        float value,
        float fallback)
    {
        return IsFinite(value) && value >= 0f ? value : fallback;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool IsFinite(double value)
    {
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
