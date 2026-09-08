using System;
using UnityEngine;

namespace PortalRules;

internal static class PublicPortalTravelCost
{
    private const float DefaultIncludedDistanceMeters = 1000f;
    private const float DefaultCoinsPerKilometer = 5f;

    internal static bool IsEnabled =>
        PublicPortalConfig.TravelCostScope.Value is
            PublicPortalTravelCostScope.All or
            PublicPortalTravelCostScope.AdminPortalTrips or
            PublicPortalTravelCostScope.PersonalAndClanRoutesFree or
            PublicPortalTravelCostScope.AllItemsSourceTrips;

    internal static bool ShouldCharge(
        int sourcePrefabHash,
        PublicPortalAccessMode sourceAccessMode,
        bool sourceAllowsAllItems,
        int targetPrefabHash,
        PublicPortalAccessMode targetAccessMode)
    {
        return PublicPortalConfig.TravelCostScope.Value switch
        {
            PublicPortalTravelCostScope.All => true,
            PublicPortalTravelCostScope.AdminPortalTrips =>
                PublicPortalKinds.IsAdminPortalPrefab(sourcePrefabHash) ||
                PublicPortalKinds.IsAdminPortalPrefab(targetPrefabHash),
            PublicPortalTravelCostScope.PersonalAndClanRoutesFree =>
                !IsPersonalOrClan(sourceAccessMode) ||
                !IsPersonalOrClan(targetAccessMode),
            PublicPortalTravelCostScope.AllItemsSourceTrips =>
                sourceAllowsAllItems,
            _ => false
        };
    }

    internal static int CalculateCost(
        PublicPortalCatalogEntry source,
        PublicPortalCatalogEntry target)
    {
        return CalculateCost(
            source.PrefabHash,
            source.AccessMode,
            source.AllowsAllItems,
            source.Position,
            target.PrefabHash,
            target.AccessMode,
            target.Position);
    }

    internal static int CalculateCost(
        int sourcePrefabHash,
        PublicPortalAccessMode sourceAccessMode,
        bool sourceAllowsAllItems,
        Vector3 sourcePosition,
        int targetPrefabHash,
        PublicPortalAccessMode targetAccessMode,
        Vector3 targetPosition)
    {
        if (!ShouldCharge(
                sourcePrefabHash,
                sourceAccessMode,
                sourceAllowsAllItems,
                targetPrefabHash,
                targetAccessMode))
        {
            return 0;
        }

        if (!TryGetDistanceXZ(
                sourcePosition,
                targetPosition,
                out double distanceMeters))
        {
            return 0;
        }

        int baseCost = Math.Max(0, PublicPortalConfig.BaseCoinCost.Value);
        double includedDistance = NonNegativeFiniteOrDefault(
            PublicPortalConfig.BaseFareIncludedDistanceMeters.Value,
            DefaultIncludedDistanceMeters);
        double coinsPerKilometer = NonNegativeFiniteOrDefault(
            PublicPortalConfig.CoinsPerKilometer.Value,
            DefaultCoinsPerKilometer);
        double surchargeDistanceKilometers =
            Math.Max(0d, distanceMeters - includedDistance) / 1000d;
        double surcharge = Math.Ceiling(
            coinsPerKilometer * surchargeDistanceKilometers);
        double total = baseCost + surcharge;

        if (!IsFinite(total) || total >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return total <= 0d ? 0 : (int)total;
    }

    private static bool IsPersonalOrClan(
        PublicPortalAccessMode accessMode)
    {
        return accessMode is
            PublicPortalAccessMode.Personal or
            PublicPortalAccessMode.Clan;
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
