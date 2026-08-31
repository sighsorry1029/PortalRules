using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx.Bootstrap;
using UnityEngine;

namespace PortalRules;

internal readonly struct PortalCoinPaymentReceipt
{
    internal readonly Player? Player;
    internal readonly Inventory? Inventory;
    internal readonly int InventoryCoins;
    internal readonly int PocketCoins;

    internal PortalCoinPaymentReceipt(
        Player player,
        Inventory inventory,
        int inventoryCoins,
        int pocketCoins)
    {
        Player = player;
        Inventory = inventory;
        InventoryCoins = Math.Max(0, inventoryCoins);
        PocketCoins = Math.Max(0, pocketCoins);
    }

    internal int TotalCoins => InventoryCoins + PocketCoins;
}

internal static class PortalCoinWallet
{
    private enum CurrencyPocketState
    {
        Absent = 0,
        Present = 1,
        Unavailable = 2
    }

    private const string CurrencyPocketCoinDataKey = "CoinPocket_CoinCount";
    private const string CoinPrefabName = "Coins";
    private const string CurrencyPocketTypeName =
        "CurrencyPocket.CurrencyPocket";
    private const string CurrencyPocketUpdateUiMethodName =
        "UpdatePocketUI";

    private static Assembly? CachedCurrencyPocketAssembly;
    private static MethodInfo? CachedCurrencyPocketUpdateUi;
    private static bool CurrencyPocketUpdateUiResolved;
    private static bool CurrencyPocketLookupFailureLogged;
    private static bool CurrencyPocketBalanceFailureLogged;
    private static bool CurrencyPocketUiFailureLogged;
    private static ObjectDB? CachedCoinObjectDb;
    private static ItemDrop.ItemData? CachedCoinItemData;

    internal static int GetLocalCoinCount()
    {
        if (!TryGetLocalInventoryAndCoinName(
                out Inventory inventory,
                out string coinName))
        {
            return 0;
        }

        int inventoryCoins = Math.Max(
            0,
            inventory.CountItems(coinName, -1, false));
        CurrencyPocketState currencyPocketState =
            GetCurrencyPocketState(out _);
        if (currencyPocketState == CurrencyPocketState.Absent)
        {
            return inventoryCoins;
        }

        Player? player = Player.m_localPlayer;
        if (currencyPocketState != CurrencyPocketState.Present ||
            player == null ||
            !TryGetCurrencyPocketBalance(player, out int pocketCoins))
        {
            return 0;
        }

        long total = (long)inventoryCoins + pocketCoins;
        return total >= int.MaxValue ? int.MaxValue : (int)total;
    }

    internal static bool HasLocalCoins(int amount)
    {
        return amount <= 0 || GetLocalCoinCount() >= amount;
    }

    internal static bool TryConsumeLocalCoins(
        int amount,
        out PortalCoinPaymentReceipt receipt)
    {
        receipt = default;
        if (amount <= 0)
        {
            return true;
        }

        Player? player = Player.m_localPlayer;
        if (player == null ||
            !TryGetLocalInventoryAndCoinName(
                out Inventory inventory,
                out string coinName))
        {
            return false;
        }

        return GetCurrencyPocketState(out _) switch
        {
            CurrencyPocketState.Present => TryConsumeCurrencyPocketCoins(
                player,
                inventory,
                coinName,
                amount,
                out receipt),
            CurrencyPocketState.Absent => TryConsumeVanillaCoins(
                player,
                inventory,
                coinName,
                amount,
                out receipt),
            _ => false
        };
    }

    internal static bool TryRefundLocalCoins(
        in PortalCoinPaymentReceipt receipt)
    {
        if (receipt.TotalCoins <= 0)
        {
            return true;
        }

        Player? player = receipt.Player;
        Inventory? inventory = receipt.Inventory;
        if (player == null ||
            inventory == null ||
            !ReferenceEquals(player, Player.m_localPlayer))
        {
            return false;
        }

        return receipt.PocketCoins > 0 ||
               GetCurrencyPocketState(out _) == CurrencyPocketState.Present
            ? TryRestoreCurrencyPocketPayment(
                player,
                inventory,
                receipt.InventoryCoins,
                receipt.PocketCoins)
            : TryRefundPhysicalCoins(
                inventory,
                receipt.InventoryCoins,
                out int restored) &&
              restored == receipt.InventoryCoins;
    }

    private static bool TryConsumeVanillaCoins(
        Player player,
        Inventory inventory,
        string coinName,
        int amount,
        out PortalCoinPaymentReceipt receipt)
    {
        receipt = default;
        if (inventory.CountItems(coinName, -1, false) < amount)
        {
            return false;
        }

        if (TryRemovePhysicalCoinsExact(
                inventory,
                coinName,
                amount,
                out int removed))
        {
            receipt = new PortalCoinPaymentReceipt(
                player,
                inventory,
                removed,
                pocketCoins: 0);
            return true;
        }

        if (removed > 0 &&
            (!TryRefundPhysicalCoins(inventory, removed, out int restored) ||
             restored != removed))
        {
            PortalRulesPlugin.PortalRulesLogger.LogError(
                $"Failed to restore {removed} Coins after an incomplete portal fare reservation.");
        }

        return false;
    }

    private static bool TryConsumeCurrencyPocketCoins(
        Player player,
        Inventory inventory,
        string coinName,
        int amount,
        out PortalCoinPaymentReceipt receipt)
    {
        receipt = default;
        int inventoryCoins = Math.Max(
            0,
            inventory.CountItems(coinName, -1, false));
        if (!TryGetCurrencyPocketBalance(player, out int pocketCoins))
        {
            return false;
        }

        if ((long)inventoryCoins + pocketCoins < amount)
        {
            return false;
        }

        int pocketCoinsToUse = Math.Min(pocketCoins, amount);
        int inventoryCoinsToUse = amount - pocketCoinsToUse;
        if (pocketCoinsToUse > 0 &&
            !TryRemoveCurrencyPocketCoins(player, pocketCoinsToUse))
        {
            return false;
        }

        if (!TryRemovePhysicalCoinsExact(
                inventory,
                coinName,
                inventoryCoinsToUse,
                out int inventoryCoinsRemoved))
        {
            if (!TryRestoreCurrencyPocketPayment(
                    player,
                    inventory,
                    inventoryCoinsRemoved,
                    pocketCoinsToUse))
            {
                PortalRulesPlugin.PortalRulesLogger.LogError(
                    $"Failed to restore an incomplete CurrencyPocket fare reservation of {inventoryCoinsRemoved + pocketCoinsToUse} Coins.");
            }

            return false;
        }

        receipt = new PortalCoinPaymentReceipt(
            player,
            inventory,
            inventoryCoinsRemoved,
            pocketCoinsToUse);
        return true;
    }

    private static bool TryRemovePhysicalCoinsExact(
        Inventory inventory,
        string coinName,
        int amount,
        out int removed)
    {
        removed = 0;
        if (amount <= 0)
        {
            return true;
        }

        int before = Math.Max(
            0,
            inventory.CountItems(coinName, -1, false));
        bool completed = true;
        try
        {
            List<ItemDrop.ItemData> items = new(inventory.GetAllItems());
            int remaining = amount;
            foreach (ItemDrop.ItemData item in items)
            {
                if (remaining <= 0)
                {
                    break;
                }

                if (item?.m_shared?.m_name != coinName || item.m_stack <= 0)
                {
                    continue;
                }

                int take = Math.Min(item.m_stack, remaining);
                inventory.RemoveItem(item, take);
                remaining -= take;
            }

            completed = remaining == 0;
        }
        catch (Exception ex)
        {
            completed = false;
            PortalRulesPlugin.PortalRulesLogger.LogError(
                $"Failed while removing physical Coins for a portal fare: {ex.GetBaseException().Message}");
        }

        int after = Math.Max(
            0,
            inventory.CountItems(coinName, -1, false));
        removed = Math.Max(0, before - after);
        return completed && removed == amount;
    }

    private static bool TryRefundPhysicalCoins(
        Inventory inventory,
        int amount,
        out int restored)
    {
        restored = 0;
        if (amount <= 0)
        {
            return true;
        }

        if (!TryGetCoinItemData(out ItemDrop.ItemData coin) ||
            coin.m_shared == null ||
            string.IsNullOrWhiteSpace(coin.m_shared.m_name))
        {
            return false;
        }

        int before = Math.Max(
            0,
            inventory.CountItems(coin.m_shared.m_name, -1, false));
        try
        {
            inventory.AddItem(
                CoinPrefabName,
                amount,
                coin.m_quality,
                coin.m_variant,
                0L,
                "");
        }
        catch (Exception ex)
        {
            PortalRulesPlugin.PortalRulesLogger.LogError(
                $"Failed while restoring physical Coins after a portal fare rollback: {ex.GetBaseException().Message}");
        }

        int after = Math.Max(
            0,
            inventory.CountItems(coin.m_shared.m_name, -1, false));
        restored = Math.Max(0, after - before);
        return restored == amount;
    }

    private static bool TryRestoreCurrencyPocketPayment(
        Player player,
        Inventory inventory,
        int inventoryCoins,
        int pocketCoins)
    {
        inventoryCoins = Math.Max(0, inventoryCoins);
        pocketCoins = Math.Max(0, pocketCoins);
        bool inventoryRestored = TryRefundPhysicalCoins(
            inventory,
            inventoryCoins,
            out int restoredToInventory);
        int inventoryShortfall = Math.Max(
            0,
            inventoryCoins - restoredToInventory);
        long pocketRefundLong = (long)pocketCoins + inventoryShortfall;
        if (pocketRefundLong > int.MaxValue)
        {
            return false;
        }

        int pocketRefund = (int)pocketRefundLong;
        bool pocketRestored =
            pocketRefund <= 0 ||
            TryAddCurrencyPocketCoins(player, pocketRefund);
        if (inventoryShortfall > 0 && pocketRestored)
        {
            PortalRulesPlugin.PortalRulesLogger.LogWarning(
                $"A portal fare rollback could not return {inventoryShortfall} physical Coins to the inventory, so they were restored to CurrencyPocket instead.");
        }

        return pocketRestored &&
               (inventoryRestored ||
                restoredToInventory + inventoryShortfall == inventoryCoins);
    }

    private static CurrencyPocketState GetCurrencyPocketState(
        out Assembly assembly)
    {
        if (CachedCurrencyPocketAssembly != null)
        {
            assembly = CachedCurrencyPocketAssembly;
            return CurrencyPocketState.Present;
        }

        try
        {
            if (!Chainloader.PluginInfos.TryGetValue(
                    PortalRulesPlugin.CurrencyPocketSoftDependencyGuid,
                    out var pluginInfo))
            {
                assembly = null!;
                return CurrencyPocketState.Absent;
            }

            if (pluginInfo.Instance != null)
            {
                assembly = pluginInfo.Instance.GetType().Assembly;
                CachedCurrencyPocketAssembly = assembly;
                return CurrencyPocketState.Present;
            }

            if (!CurrencyPocketLookupFailureLogged)
            {
                CurrencyPocketLookupFailureLogged = true;
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    "CurrencyPocket is registered but its plugin instance is unavailable; paid portal travel will fail closed.");
            }
        }
        catch (Exception ex)
        {
            if (!CurrencyPocketLookupFailureLogged)
            {
                CurrencyPocketLookupFailureLogged = true;
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    $"Could not inspect CurrencyPocket; paid portal travel will fail closed: {ex.GetBaseException().Message}");
            }
        }

        assembly = null!;
        return CurrencyPocketState.Unavailable;
    }

    private static bool TryGetCurrencyPocketBalance(
        Player player,
        out int balance)
    {
        balance = 0;
        if (!ReferenceEquals(player, Player.m_localPlayer) ||
            GetCurrencyPocketState(out _) != CurrencyPocketState.Present)
        {
            return false;
        }

        try
        {
            if (!player.m_customData.TryGetValue(
                    CurrencyPocketCoinDataKey,
                    out string storedBalance))
            {
                return true;
            }

            if (int.TryParse(
                    storedBalance,
                    NumberStyles.Integer,
                    CultureInfo.CurrentCulture,
                    out balance) &&
                balance >= 0)
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            LogCurrencyPocketBalanceFailureOnce(ex.GetBaseException().Message);
            balance = 0;
            return false;
        }

        LogCurrencyPocketBalanceFailureOnce(
            "the saved balance is not a non-negative 32-bit integer");
        balance = 0;
        return false;
    }

    private static bool TryRemoveCurrencyPocketCoins(
        Player player,
        int amount)
    {
        if (amount <= 0)
        {
            return true;
        }

        return TryGetCurrencyPocketBalance(player, out int balance) &&
               balance >= amount &&
               TrySetCurrencyPocketBalance(player, balance - amount);
    }

    private static bool TryAddCurrencyPocketCoins(
        Player player,
        int amount)
    {
        if (amount <= 0)
        {
            return true;
        }

        return TryGetCurrencyPocketBalance(player, out int balance) &&
               balance <= int.MaxValue - amount &&
               TrySetCurrencyPocketBalance(player, balance + amount);
    }

    private static bool TrySetCurrencyPocketBalance(
        Player player,
        int balance)
    {
        if (balance < 0 ||
            !ReferenceEquals(player, Player.m_localPlayer) ||
            GetCurrencyPocketState(out _) != CurrencyPocketState.Present)
        {
            return false;
        }

        try
        {
            player.m_customData[CurrencyPocketCoinDataKey] =
                balance.ToString(CultureInfo.InvariantCulture);
            RefreshCurrencyPocketUi();
            return true;
        }
        catch (Exception ex)
        {
            LogCurrencyPocketBalanceFailureOnce(ex.GetBaseException().Message);
            return false;
        }
    }

    private static void RefreshCurrencyPocketUi()
    {
        if (GetCurrencyPocketState(out Assembly assembly) !=
            CurrencyPocketState.Present)
        {
            return;
        }

        if (!CurrencyPocketUpdateUiResolved)
        {
            CurrencyPocketUpdateUiResolved = true;
            try
            {
                CachedCurrencyPocketUpdateUi = assembly
                    .GetType(
                        CurrencyPocketTypeName,
                        throwOnError: false,
                        ignoreCase: false)?
                    .GetMethod(
                        CurrencyPocketUpdateUiMethodName,
                        BindingFlags.Public |
                        BindingFlags.NonPublic |
                        BindingFlags.Static,
                        binder: null,
                        types: Type.EmptyTypes,
                        modifiers: null);
            }
            catch (Exception ex)
            {
                LogCurrencyPocketUiFailureOnce(ex.GetBaseException().Message);
            }
        }

        if (CachedCurrencyPocketUpdateUi == null)
        {
            LogCurrencyPocketUiFailureOnce(
                "CurrencyPocket.UpdatePocketUI was not found");
            return;
        }

        try
        {
            CachedCurrencyPocketUpdateUi.Invoke(null, null);
        }
        catch (Exception ex)
        {
            LogCurrencyPocketUiFailureOnce(ex.GetBaseException().Message);
        }
    }

    private static void LogCurrencyPocketBalanceFailureOnce(string reason)
    {
        if (CurrencyPocketBalanceFailureLogged)
        {
            return;
        }

        CurrencyPocketBalanceFailureLogged = true;
        PortalRulesPlugin.PortalRulesLogger.LogWarning(
            $"CurrencyPocket balance is unavailable; paid portal travel will remain disabled until it is repaired: {reason}");
    }

    private static void LogCurrencyPocketUiFailureOnce(string reason)
    {
        if (CurrencyPocketUiFailureLogged)
        {
            return;
        }

        CurrencyPocketUiFailureLogged = true;
        PortalRulesPlugin.PortalRulesLogger.LogWarning(
            $"Could not refresh the CurrencyPocket display immediately; its saved balance is still correct: {reason}");
    }

    private static bool TryGetLocalInventoryAndCoinName(
        out Inventory inventory,
        out string coinName)
    {
        inventory = Player.m_localPlayer != null
            ? Player.m_localPlayer.GetInventory()
            : null!;
        coinName = "";
        if (inventory == null ||
            !TryGetCoinItemData(out ItemDrop.ItemData coin) ||
            coin.m_shared == null ||
            string.IsNullOrWhiteSpace(coin.m_shared.m_name))
        {
            return false;
        }

        coinName = coin.m_shared.m_name;
        return true;
    }

    internal static bool TryGetCoinItemData(
        out ItemDrop.ItemData coin)
    {
        coin = null!;
        ObjectDB? objectDb = ObjectDB.instance;
        if (objectDb == null)
        {
            CachedCoinObjectDb = null;
            CachedCoinItemData = null;
            return false;
        }

        if (ReferenceEquals(CachedCoinObjectDb, objectDb) &&
            CachedCoinItemData != null)
        {
            coin = CachedCoinItemData;
            return true;
        }

        GameObject? coinPrefab = objectDb.GetItemPrefab(CoinPrefabName);
        ItemDrop? itemDrop = coinPrefab != null
            ? coinPrefab.GetComponent<ItemDrop>()
            : null;
        if (itemDrop?.m_itemData == null)
        {
            CachedCoinObjectDb = objectDb;
            CachedCoinItemData = null;
            return false;
        }

        coin = itemDrop.m_itemData;
        CachedCoinObjectDb = objectDb;
        CachedCoinItemData = coin;
        return true;
    }
}
