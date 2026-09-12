#nullable enable annotations
using System;
using System.Collections.Generic;

public static class PortalRulesBehaviorChecks
{
    private const string CoinPrefabName = "Coins";
    private static bool _coinAvailable = true;
    private static int _checks;
    private static bool TryGetCoinItemData(out ItemDrop.ItemData coin)
    {
        coin = new ItemDrop.ItemData();
        return _coinAvailable;
    }
    private static bool TryAddCurrencyPocketCoins(Player player, int amount)
    {
        if (player.PocketFails) return false;
        player.Pocket += amount;
        return true;
    }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        _checks++;
    }
    /* PRODUCTION_METHODS */

    public static string Run()
    {
        foreach (string tag in new[] { "", "Home", "A\" [CONNECTED]", "first\nsecond", "<color=red>x</color>", "집" })
        foreach (string newline in new[] { "\n", "\r\n" })
        {
            string header = "Portal Tag:\"" + tag + "\"";
            string tail = newline + "<color=orange>Censored tag</color>" + newline + "[E] Set tag";
            string connected = header + " [CONNECTED]" + tail;
            Check(SetConnectionStatusAfterPortalTag(connected, header, "CONNECTED", "UNCONNECTED", "") == header + tail,
                "Non-Tagged: remove only the status after the exact tag");
            Check(SetConnectionStatusAfterPortalTag(connected, header, "CONNECTED", "UNCONNECTED", "UNCONNECTED") ==
                header + " [UNCONNECTED]" + tail, "Tagged without reciprocal connection");
            Check(SetConnectionStatusAfterPortalTag(connected, header, "CONNECTED", "UNCONNECTED", "CONNECTED") == connected,
                "Tagged connected: unchanged");
            Check(SetConnectionStatusAfterPortalTag(header + " [UNCONNECTED]" + tail, header, "CONNECTED", "UNCONNECTED", "CONNECTED") == connected,
                "Tagged reciprocal connection replaces unconnected");
        }
        Check(SetConnectionStatusAfterPortalTag("other [CONNECTED]\n", "expected", "CONNECTED", "UNCONNECTED", "") ==
            "other [CONNECTED]\n", "Foreign hover text is preserved");
        Check(SetConnectionStatusAfterPortalTag("tag [CONNECTED]extra\n", "tag", "CONNECTED", "UNCONNECTED", "") ==
            "tag [CONNECTED]extra\n", "Do not rewrite a foreign suffix");
        Check(SetConnectionStatusAfterPortalTag("tag [연결됨]\n안내", "tag", "연결됨", "연결 안 됨", "") == "tag\n안내",
            "Localized status");

        foreach (int room in new[] { 0, 3, 10 })
        foreach (bool throwAfterPartial in new[] { false, true })
        {
            var player = new Player();
            var inventory = new Inventory { Room = room, ThrowAfterPartial = throwAfterPartial };
            bool result = TryRestoreCurrencyPocketPayment(player, inventory, 10, 7);
            Check(result, "Pocket covers partial physical refunds, including exceptions");
            Check(inventory.Count + player.Pocket + inventory.WorldDrops == 17, "Refund conserves total currency");
            Check(inventory.WorldDrops == 0 && !inventory.LastDropIfFull && !inventory.LastCheated && !inventory.LastPickedUp,
                "Refund explicitly disables drop and preserves creation flags");
            Check(inventory.LastPosition.x == -1 && inventory.LastPosition.y == -1, "Refund uses automatic placement");
        }
        var full = new Inventory { Room = 0 };
        Check(!TryRefundPhysicalCoins(full, 10, out int restored) && restored == 0 && full.WorldDrops == 0,
            "Vanilla full inventory reports failure without hidden world drops");
        var partial = new Inventory { Room = 4 };
        Check(!TryRefundPhysicalCoins(partial, 10, out restored) && restored == 4, "Report actual partial refund");
        Check(!TryRestoreCurrencyPocketPayment(new Player { PocketFails = true }, new Inventory(), 10, 5),
            "Failed optional-mod refund is not success");
        _coinAvailable = false;
        Check(!TryRefundPhysicalCoins(new Inventory(), 1, out restored) && restored == 0, "Missing coin prefab");
        _coinAvailable = true;
        var unused = new Inventory();
        Check(TryRefundPhysicalCoins(unused, 0, out restored) && unused.Calls == 0, "Zero refund performs no mutation");

        var travelPlayer = new Player { Teleportable = true };
        Player.m_localPlayer = travelPlayer;
        Check(CanTeleportWithItems(false) && !travelPlayer.LastAllowAll,
            "Restricted source delegates to Valheim item policy");
        Check(CanTeleportWithItems(true) && travelPlayer.LastAllowAll,
            "All-items source flag reaches Valheim item policy");
        travelPlayer.Teleportable = false;
        Check(!CanTeleportWithItems(false),
            "Valheim item denial is preserved");
        Player.m_localPlayer = null;
        Check(!CanTeleportWithItems(true),
            "Missing local player cannot teleport");
        return _checks + " behavior checks passed (production method bodies; game boundaries are test doubles).";
    }
}

public struct Vector2i { public int x, y; public Vector2i(int a, int b) { x = a; y = b; } }
public class Player
{
    public static Player m_localPlayer;
    public int Pocket, Calls;
    public bool PocketFails, LastAllowAll, Teleportable;
    public Inventory Inventory = new Inventory();
    public Inventory GetInventory() { return Inventory; }
    public bool IsTeleportable(bool allowAllItems) { Calls++; LastAllowAll = allowAllItems; return Teleportable; }
}
public class ItemDrop
{
    public class ItemData
    {
        public Shared m_shared = new Shared();
        public int m_quality = 1, m_variant, m_stack = 1;
        public float Weight;
        public float GetWeight() { return Weight; }
    }
}
public class Shared
{
    public string m_name = "$item_coins";
    public int m_toolTier;
    public bool m_teleportable = true;
}
public class Inventory
{
    public List<ItemDrop.ItemData> Items = new List<ItemDrop.ItemData>();
    public int Room, Count, WorldDrops, Calls;
    public bool ThrowAfterPartial, LastDropIfFull, LastCheated, LastPickedUp;
    public Vector2i LastPosition;
    public int CountItems(string name, int quality, bool matchWorldLevel) { return Count; }
    public List<ItemDrop.ItemData> GetAllItems() { return Items; }
    public ItemDrop.ItemData AddItem(string name, int stack, int quality, int variant, long crafterID,
        string crafterName, Vector2i position, bool cheated, bool pickedUp = false, bool dropIfFullInv = true)
    {
        Calls++;
        LastPosition = position;
        LastDropIfFull = dropIfFullInv;
        LastCheated = cheated;
        LastPickedUp = pickedUp;
        int added = Math.Min(Room, stack);
        Room -= added;
        Count += added;
        if (dropIfFullInv) WorldDrops += stack - added;
        if (ThrowAfterPartial) throw new InvalidOperationException("Injected partial failure");
        return added == stack ? new ItemDrop.ItemData() : null;
    }
}
public static class PortalRulesPlugin { public static Log PortalRulesLogger = new Log(); }
public class Log { public void LogError(string message) {} public void LogWarning(string message) {} }
public enum GlobalKeys { TeleportAll }
public class ZoneSystem
{
    public static ZoneSystem instance = new ZoneSystem();
    public bool TeleportAll;
    public bool GetGlobalKey(GlobalKeys key) { return TeleportAll; }
}
