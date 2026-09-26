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
    internal readonly PortalCoinWallet.EndosPayment? Endos;

    internal PortalCoinPaymentReceipt(
        Player player,
        Inventory inventory,
        int inventoryCoins,
        int pocketCoins,
        PortalCoinWallet.EndosPayment? endos = null)
    {
        Player = player;
        Inventory = inventory;
        InventoryCoins = Math.Max(0, inventoryCoins);
        PocketCoins = Math.Max(0, pocketCoins);
        Endos = endos;
    }

    internal int TotalCoins => InventoryCoins + PocketCoins + (Endos?.PaidPurseCoins ?? 0);
}

internal static class PortalCoinWallet
{
    // Mutable refund progress is shared by receipt copies: a retried rollback must
    // not restore a successfully refunded source twice.
    internal sealed class EndosPayment
    {
        internal readonly Inventory Purse;
        internal readonly ItemDrop.ItemData? CoinTemplate;
        internal readonly List<(ItemDrop.ItemData Original, ItemDrop.ItemData Snapshot)> PhysicalRefunds = new();
        internal int PaidPurseCoins;
        internal int PurseRefund;

        internal EndosPayment(Inventory purse, ItemDrop.ItemData? coinTemplate)
        {
            Purse = purse;
            CoinTemplate = coinTemplate?.Clone();
        }
    }

    private enum CurrencyPocketState
    {
        Absent = 0,
        Present = 1,
        Unavailable = 2
    }

    private const string CurrencyPocketCoinDataKey = "CoinPocket_CoinCount";
    private const string CoinPrefabName = "Coins";
    private const string EndosCoinDataKey = "buidel_items_hud_count";
    private const string EndosTypeName = "ValheimMuntBuidelHUD.EndosCoinPurseMod";
    private static Type? CachedEndosType;
    private static FieldInfo? CachedEndosInventory;
    private static MethodInfo? CachedEndosUpdateUi;
    private static bool EndosFailureLogged;
    private static bool EndosUiFailureLogged;
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

        Player? player = Player.m_localPlayer;
        if (player == null || !TryGetEndosPurse(player, coinName,
                out Inventory? purse, out _, out int endosCoins))
        {
            return 0;
        }

        // CountItems can already include another mod's virtual wallet.
        long inventoryCoins = CountPhysicalCoins(inventory, coinName);
        if (purse != null)
        {
            return (int)Math.Min(int.MaxValue, inventoryCoins + endosCoins);
        }
        CurrencyPocketState currencyPocketState =
            GetCurrencyPocketState(out _);
        if (currencyPocketState == CurrencyPocketState.Absent)
        {
            return (int)Math.Min(int.MaxValue, inventoryCoins);
        }

        if (currencyPocketState != CurrencyPocketState.Present ||
            player == null ||
            !TryGetCurrencyPocketBalance(player, out int pocketCoins))
        {
            return 0;
        }

        long total = inventoryCoins + pocketCoins;
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

        if (!TryGetEndosPurse(player, coinName,
                out Inventory? purse, out ItemDrop.ItemData? purseCoin,
                out int purseCoins))
        {
            return false;
        }
        if (purse != null)
        {
            return TryConsumeEndosPayment(player, inventory, coinName,
                purse, purseCoin, purseCoins, amount, out receipt);
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
            !ReferenceEquals(player, Player.m_localPlayer) ||
            !ReferenceEquals(inventory, player.GetInventory()))
        {
            return false;
        }

        if (receipt.Endos != null)
        {
            return TryRestoreEndosPayment(player, inventory, receipt.Endos);
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
        out int removed,
        List<(ItemDrop.ItemData Original, ItemDrop.ItemData Snapshot)>? refunds = null)
    {
        removed = 0;
        if (amount <= 0)
        {
            return true;
        }

        long before = CountPhysicalCoins(inventory, coinName);
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
                ItemDrop.ItemData? snapshot = refunds != null ? item.Clone() : null;
                long stackBefore = CountPhysicalCoins(inventory, coinName);
                try
                {
                    inventory.RemoveItem(item, take);
                }
                finally
                {
                    int actual = (int)Math.Max(0, Math.Min(take,
                        stackBefore - CountPhysicalCoins(inventory, coinName)));
                    if (snapshot != null && actual > 0)
                    {
                        snapshot.m_stack = actual;
                        refunds!.Add((item, snapshot));
                    }
                    remaining -= actual;
                }
            }

            completed = remaining == 0;
        }
        catch (Exception ex)
        {
            completed = false;
            PortalRulesPlugin.PortalRulesLogger.LogError(
                $"Failed while removing physical Coins for a portal fare: {ex.GetBaseException().Message}");
        }

        long after = CountPhysicalCoins(inventory, coinName);
        removed = (int)Math.Max(0, Math.Min(int.MaxValue, before - after));
        return completed && removed == amount;
    }

    private static long CountPhysicalCoins(Inventory inventory, string coinName)
    {
        long count = 0;
        foreach (ItemDrop.ItemData item in inventory.GetAllItems())
        {
            if (item?.m_shared?.m_name == coinName && item.m_stack > 0)
            {
                count += item.m_stack;
            }
        }
        return count;
    }

    private static bool TryGetEndosPurse(
        Player player, string coinName, out Inventory? purse,
        out ItemDrop.ItemData? coin, out int balance)
    {
        purse = null;
        coin = null;
        balance = 0;
        try
        {
            if (!Chainloader.PluginInfos.TryGetValue(
                    PortalRulesPlugin.EndosCoinPurseSoftDependencyGuid, out var plugin))
            {
                return true;
            }
            Type? type = plugin.Instance != null
                ? plugin.Instance.GetType().Assembly.GetType(EndosTypeName, false)
                : null;
            if (type == null)
            {
                throw new InvalidOperationException("plugin instance/type is unavailable");
            }
            if (CachedEndosType != type)
            {
                CachedEndosType = type;
                CachedEndosInventory = type.GetField("VirtueleBuidelInv",
                    BindingFlags.Public | BindingFlags.Static);
                CachedEndosUpdateUi = type.GetMethod("UpdateBuidelCount",
                    BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            }
            purse = CachedEndosInventory?.GetValue(null) as Inventory;
            if (purse == null || ReferenceEquals(purse, player.GetInventory()) ||
                !ReferenceEquals(player, Player.m_localPlayer) ||
                !TryReadEndosCoins(purse, coinName, out coin, out balance))
            {
                throw new InvalidOperationException("wallet inventory is unavailable or invalid");
            }
            return true;
        }
        catch (Exception ex)
        {
            LogEndosFailureOnce(ex.GetBaseException().Message);
            return false;
        }
    }

    private static bool TryReadEndosCoins(
        Inventory purse, string coinName, out ItemDrop.ItemData? coin, out int balance)
    {
        coin = null;
        balance = 0;
        foreach (ItemDrop.ItemData item in purse.GetAllItems())
        {
            // Endos persists and displays only the first Coins stack, not a sum.
            if (coin != null || item?.m_shared?.m_name != coinName || item.m_stack < 0)
            {
                return false;
            }
            coin = item;
            balance = item.m_stack;
        }
        return true;
    }

    private static bool IsSameEndosPurse(Player player, Inventory purse)
    {
        try
        {
            return ReferenceEquals(player, Player.m_localPlayer) &&
                   Chainloader.PluginInfos.TryGetValue(
                       PortalRulesPlugin.EndosCoinPurseSoftDependencyGuid, out var plugin) &&
                   plugin.Instance != null &&
                   ReferenceEquals(CachedEndosInventory?.GetValue(null), purse);
        }
        catch (Exception ex)
        {
            LogEndosFailureOnce(ex.GetBaseException().Message);
            return false;
        }
    }

    private static bool TryChangeEndosCoins(
        Player player, Inventory purse, ItemDrop.ItemData? template,
        string coinName, int delta, out int changed)
    {
        changed = 0;
        if (!IsSameEndosPurse(player, purse) ||
            !TryReadEndosCoins(purse, coinName, out ItemDrop.ItemData? coin, out int before) ||
            (long)before + delta < 0 || (long)before + delta > int.MaxValue)
        {
            return false;
        }
        // The saved cache can lag behind a manual wallet withdrawal. It is not
        // authoritative for spending, but crediting an empty wallet with a pending
        // saved balance could be overwritten by Endos' delayed load coroutine.
        if (delta > 0 && before == 0 &&
            player.m_customData.TryGetValue(EndosCoinDataKey, out string stored) &&
            (!int.TryParse(stored, out int savedCoins) || savedCoins != 0))
        {
            LogEndosFailureOnce("cannot credit an empty purse while its saved balance is unresolved");
            return false;
        }
        bool mutationCompleted = true;
        try
        {
            if (delta < 0 && coin != null)
            {
                purse.RemoveItem(coin, -delta);
            }
            else if (delta > 0 && coin != null)
            {
                coin.m_stack += delta;
            }
            else if (delta > 0 && template != null)
            {
                ItemDrop.ItemData restored = template.Clone();
                restored.m_stack = delta;
                // The prefab overload clamps oversized virtual purse balances.
                purse.AddItem(restored);
            }
        }
        catch (Exception ex)
        {
            mutationCompleted = false;
            LogEndosFailureOnce(ex.GetBaseException().Message);
        }
        if (!TryReadEndosCoins(purse, coinName, out _, out int after))
        {
            return false;
        }
        changed = after - before;
        // Even if an Inventory Changed subscriber throws after mutation, save the
        // observed balance and let the caller roll back the observed debit.
        bool saved = true;
        try
        {
            player.m_customData[EndosCoinDataKey] = after.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            // Keep 'changed' observable to the caller even if persistence fails
            // after an Inventory callback has already changed the live purse.
            saved = false;
            LogEndosFailureOnce(ex.GetBaseException().Message);
        }
        try
        {
            CachedEndosUpdateUi?.Invoke(null, null);
        }
        catch (Exception ex)
        {
            if (!EndosUiFailureLogged)
            {
                EndosUiFailureLogged = true;
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    $"Could not refresh EndosCoinPurse HUD; its balance is saved: {ex.GetBaseException().Message}");
            }
        }
        return mutationCompleted && saved && changed == delta;
    }

    private static bool TryConsumeEndosPayment(
        Player player, Inventory inventory, string coinName,
        Inventory purse, ItemDrop.ItemData? purseCoin, int purseCoins,
        int amount, out PortalCoinPaymentReceipt receipt)
    {
        receipt = default;
        if (CountPhysicalCoins(inventory, coinName) + purseCoins < amount)
        {
            return false;
        }
        EndosPayment payment = new(purse, purseCoin);
        int purseTake = Math.Min(purseCoins, amount);
        int physicalTake = amount - purseTake;
        bool committed = false;
        try
        {
            if (purseTake > 0)
            {
                bool success = TryChangeEndosCoins(player, purse, payment.CoinTemplate,
                    coinName, -purseTake, out int changed);
                payment.PaidPurseCoins = payment.PurseRefund = Math.Max(0, -changed);
                if (!success) return false;
            }
            if (!TryRemovePhysicalCoinsExact(inventory, coinName, physicalTake,
                    out int removed, payment.PhysicalRefunds)) return false;

            receipt = new PortalCoinPaymentReceipt(player, inventory, removed, 0, payment);
            committed = true;
            return true;
        }
        finally
        {
            if (!committed && !TryRestoreEndosPayment(player, inventory, payment))
            {
                PortalRulesPlugin.PortalRulesLogger.LogError(
                    "Failed to restore an incomplete EndosCoinPurse portal fare reservation.");
            }
        }
    }

    private static bool TryRestoreEndosPayment(Player player, Inventory inventory, EndosPayment payment)
    {
        if (!IsSameEndosPurse(player, payment.Purse) ||
            !ReferenceEquals(inventory, player.GetInventory())) return false;

        bool success = true;
        foreach (var physical in payment.PhysicalRefunds)
        {
            ItemDrop.ItemData snapshot = physical.Snapshot;
            if (snapshot.m_stack <= 0) continue;
            string name = snapshot.m_shared.m_name;
            long before = CountPhysicalCoins(inventory, name);
            try
            {
                Vector2i position = snapshot.m_gridPos;
                ItemDrop.ItemData? occupying = inventory.GetItemAt(position.x, position.y);
                if (position.x < 0 || position.y < 0 ||
                    position.x >= inventory.GetWidth() || position.y >= inventory.GetHeight() ||
                    (occupying != null && !ReferenceEquals(occupying, physical.Original)))
                {
                    position = new Vector2i(-1, -1);
                    for (int y = 0; y < inventory.GetHeight() && position.x < 0; ++y)
                    for (int x = 0; x < inventory.GetWidth(); ++x)
                    {
                        if (inventory.GetItemAt(x, y) == null)
                        {
                            position = new Vector2i(x, y);
                            break;
                        }
                    }
                }
                if (position.x >= 0 && position.y >= 0)
                {
                    // MoveItemToThis is the public targeted insertion API. Unlike
                    // AddItem(ItemData, Vector2i), it cannot merge into unrelated
                    // stacks elsewhere and lose the original coin metadata.
                    Inventory refundInventory = new(true);
                    ItemDrop.ItemData refund = snapshot.Clone();
                    if (refundInventory.AddItem(refund))
                    {
                        inventory.MoveItemToThis(refundInventory, refund,
                            snapshot.m_stack, position.x, position.y);
                    }
                }
            }
            catch (Exception ex)
            {
                PortalRulesPlugin.PortalRulesLogger.LogWarning(
                    $"Physical portal refund was interrupted: {ex.GetBaseException().Message}");
            }
            int restored = (int)Math.Max(0, Math.Min(snapshot.m_stack,
                CountPhysicalCoins(inventory, name) - before));
            snapshot.m_stack -= restored;
            if (snapshot.m_stack > 0)
            {
                TryChangeEndosCoins(player, payment.Purse, snapshot, name,
                    snapshot.m_stack, out int added);
                snapshot.m_stack -= Math.Max(0, added);
                if (added > 0)
                {
                    PortalRulesPlugin.PortalRulesLogger.LogWarning(
                        $"Restored {added} Coins to EndosCoinPurse because their inventory refund could not complete.");
                }
            }
            success &= snapshot.m_stack == 0;
        }
        if (payment.PurseRefund > 0 && payment.CoinTemplate != null)
        {
            TryChangeEndosCoins(player, payment.Purse, payment.CoinTemplate,
                payment.CoinTemplate.m_shared.m_name, payment.PurseRefund, out int added);
            payment.PurseRefund -= Math.Max(0, added);
        }
        return success && payment.PurseRefund == 0;
    }

    private static void LogEndosFailureOnce(string reason)
    {
        if (EndosFailureLogged) return;
        EndosFailureLogged = true;
        PortalRulesPlugin.PortalRulesLogger.LogWarning(
            $"EndosCoinPurse wallet is unavailable; paid portal travel will fail closed: {reason}");
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
                "",
                new Vector2i(-1, -1),
                cheated: false,
                pickedUp: false,
                dropIfFullInv: false);
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
