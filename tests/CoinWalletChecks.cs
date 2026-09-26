// Linked by Run-CoinWalletChecks.ps1 after the complete production wallet source.
// These doubles exercise wallet accounting; they are not a game/runtime validation.
namespace PortalRulesCoinWalletChecks
{
    public static class Checks
    {
        private const string EndosKey = "buidel_items_hud_count";
        private const string PocketKey = "CoinPocket_CoinCount";
        private static int _checks;

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            _checks++;
        }

        private static Player Reset(int physical = 0, int? purse = null, int? pocket = null)
        {
            foreach (FieldInfo field in typeof(PortalCoinWallet).GetFields(
                         BindingFlags.Static | BindingFlags.NonPublic))
            {
                if (!field.IsInitOnly && !field.IsLiteral)
                    field.SetValue(null, field.FieldType.IsValueType
                        ? Activator.CreateInstance(field.FieldType) : null);
            }
            BepInEx.Bootstrap.Chainloader.PluginInfos.Clear();
            PortalRulesPlugin.PortalRulesLogger.Messages.Clear();
            ValheimMuntBuidelHUD.EndosCoinPurseMod.VirtueleBuidelInv = null!;
            ValheimMuntBuidelHUD.EndosCoinPurseMod.Refreshes = 0;
            ValheimMuntBuidelHUD.EndosCoinPurseMod.ThrowOnRefresh = false;
            CurrencyPocket.CurrencyPocket.Refreshes = 0;
            CurrencyPocket.CurrencyPocket.ThrowOnRefresh = false;
            var player = new Player();
            Player.m_localPlayer = player;
            ObjectDB.instance = new ObjectDB();
            if (physical > 0) player.Inventory.Items.Add(Coin(physical));
            if (purse.HasValue)
            {
                RegisterEndos();
                var inventory = new Inventory();
                if (purse.Value != 0) inventory.Items.Add(Coin(purse.Value));
                ValheimMuntBuidelHUD.EndosCoinPurseMod.VirtueleBuidelInv = inventory;
                player.m_customData[EndosKey] = purse.Value.ToString(CultureInfo.InvariantCulture);
            }
            if (pocket.HasValue)
            {
                BepInEx.Bootstrap.Chainloader.PluginInfos.Add(
                    PortalRulesPlugin.CurrencyPocketSoftDependencyGuid,
                    new BepInEx.Bootstrap.PluginInfo { Instance = new CurrencyPocket.CurrencyPocket() });
                player.m_customData[PocketKey] = pocket.Value.ToString(CultureInfo.InvariantCulture);
            }
            return player;
        }

        private static void RegisterEndos()
        {
            BepInEx.Bootstrap.Chainloader.PluginInfos["EndosCoinPurse"] =
                new BepInEx.Bootstrap.PluginInfo { Instance = new ValheimMuntBuidelHUD.EndosCoinPurseMod() };
        }

        internal static ItemDrop.ItemData Coin(int count) => new ItemDrop.ItemData { m_stack = count };
        private static int Count(Inventory inventory)
        {
            long total = 0;
            foreach (ItemDrop.ItemData item in inventory.Items)
                if (item.m_shared.m_name == "$item_coins") total += item.m_stack;
            return total >= int.MaxValue ? int.MaxValue : (int)total;
        }
        private static Inventory Purse => ValheimMuntBuidelHUD.EndosCoinPurseMod.VirtueleBuidelInv;
        private static int Pocket(Player player) => int.Parse(player.m_customData[PocketKey], CultureInfo.InvariantCulture);
        private static void CheckSavedPurse(Player player, int expected)
        {
            Check(player.m_customData.TryGetValue(EndosKey, out string? saved) &&
                  saved == expected.ToString(CultureInfo.InvariantCulture),
                "Purse persistence must match its live balance immediately.");
        }

        public static string Run()
        {
            _checks = 0;
            Player player = Reset(physical: 70);
            ItemDrop.ItemData physicalSource = player.Inventory.Items[0];
            Check(PortalCoinWallet.GetLocalCoinCount() == 70, "Absent optional mods: count physical coins.");
            Check(PortalCoinWallet.TryConsumeLocalCoins(25, out var receipt), "Physical-only payment.");
            Check(Count(player.Inventory) == 45 && receipt.TotalCoins == 25, "Exact physical debit and receipt.");
            Check(PortalCoinWallet.TryRefundLocalCoins(in receipt), "Physical-only refund.");
            Check(Count(player.Inventory) == 70, "Physical refund preserves the original balance.");

            player = Reset(purse: 120);
            player.m_customData[EndosKey] = "999999";
            Check(PortalCoinWallet.GetLocalCoinCount() == 120, "Live purse is authoritative, not stale save data.");
            Check(PortalCoinWallet.TryConsumeLocalCoins(40, out receipt), "Purse-only payment.");
            Check(Count(Purse) == 80 && Count(player.Inventory) == 0 && receipt.TotalCoins == 40,
                "Purse-only debit must not create physical coins.");
            CheckSavedPurse(player, 80);
            Check(ValheimMuntBuidelHUD.EndosCoinPurseMod.Refreshes > 0, "Purse debit refreshes its HUD.");
            Check(PortalCoinWallet.TryRefundLocalCoins(in receipt), "Purse-only refund.");
            Check(Count(Purse) == 120 && Count(player.Inventory) == 0, "Refund goes back to the purse.");
            CheckSavedPurse(player, 120);

            player = Reset(physical: 50, purse: 200);
            Check(player.Inventory.CountItems("$item_coins", -1, false) == 250,
                "Fixture models Endos' virtual CountItems augmentation.");
            Check(PortalCoinWallet.GetLocalCoinCount() == 250, "Purse must not be counted twice.");
            Check(PortalCoinWallet.TryConsumeLocalCoins(225, out receipt), "Mixed purse/physical payment.");
            Check(Count(Purse) == 0 && Count(player.Inventory) == 25, "Purse is used before physical stacks.");
            CheckSavedPurse(player, 0);
            Check(PortalCoinWallet.TryRefundLocalCoins(in receipt), "Mixed-source refund.");
            Check(Count(Purse) == 200 && Count(player.Inventory) == 50,
                "Refund restores each original source without AddItem redirection.");
            Check(player.Inventory.AddCalls == 0, "Physical rollback bypasses patched AddItem.");

            player = Reset(physical: 50, pocket: 80);
            Check(PortalCoinWallet.GetLocalCoinCount() == 130, "CurrencyPocket-only balance regression.");
            Check(PortalCoinWallet.TryConsumeLocalCoins(100, out receipt), "CurrencyPocket-only payment regression.");
            Check(Pocket(player) == 0 && Count(player.Inventory) == 30, "CurrencyPocket is used first.");
            Check(PortalCoinWallet.TryRefundLocalCoins(in receipt), "CurrencyPocket payment refund.");
            Check(Pocket(player) == 80 && Count(player.Inventory) == 50, "CurrencyPocket refund conserves its sources.");
            Check(CurrencyPocket.CurrencyPocket.Refreshes > 0, "CurrencyPocket UI reflection remains wired.");

            player = Reset(physical: 20, purse: 25);
            Check(!PortalCoinWallet.TryConsumeLocalCoins(46, out receipt) && receipt.TotalCoins == 0,
                "Insufficient funds do not produce a payment receipt.");
            Check(Count(Purse) == 25 && Count(player.Inventory) == 20,
                "Insufficient fare leaves every source untouched.");
            Check(PortalCoinWallet.TryConsumeLocalCoins(0, out receipt) && receipt.TotalCoins == 0,
                "Zero fare is free.");
            Check(PortalCoinWallet.TryConsumeLocalCoins(-1, out receipt) && receipt.TotalCoins == 0,
                "Nonpositive fare performs no mutation.");
            Check(PortalCoinWallet.TryRefundLocalCoins(in receipt), "Empty receipt needs no inventory mutation.");

            player = Reset(physical: 200);
            RegisterEndos();
            Check(PortalCoinWallet.GetLocalCoinCount() == 0 &&
                  !PortalCoinWallet.TryConsumeLocalCoins(1, out receipt),
                "Registered, uninitialized Endos fails closed even with physical coins.");
            Check(Count(player.Inventory) == 200, "Unavailable purse must not debit physical coins.");
            Check(PortalCoinWallet.TryConsumeLocalCoins(0, out receipt), "Free fare works before purse initialization.");
            ValheimMuntBuidelHUD.EndosCoinPurseMod.VirtueleBuidelInv = new Inventory();
            Purse.Items.Add(Coin(30));
            Check(PortalCoinWallet.GetLocalCoinCount() == 230, "Purse initialization is observed without process restart.");
            var nextPurse = new Inventory();
            nextPurse.Items.Add(Coin(7));
            ValheimMuntBuidelHUD.EndosCoinPurseMod.VirtueleBuidelInv = nextPurse;
            Check(PortalCoinWallet.GetLocalCoinCount() == 207, "Cache stores metadata, not the old purse instance.");

            player = Reset(physical: 200, purse: 0);
            player.m_customData[EndosKey] = "80";
            Check(PortalCoinWallet.GetLocalCoinCount() == 200,
                "A stale saved balance after manual withdrawal does not override the live empty purse.");
            Check(PortalCoinWallet.TryConsumeLocalCoins(200, out receipt) && Count(player.Inventory) == 0,
                "Physical fare can be paid while the live purse is empty and its saved balance is stale.");
            Check(Count(Purse) == 0 && player.m_customData[EndosKey] == "80",
                "Physical-only debit does not erase potentially pending purse restoration.");
            Check(PortalCoinWallet.TryRefundLocalCoins(in receipt) && Count(player.Inventory) == 200,
                "Physical refund need not credit the ambiguous empty purse.");
            Purse.Items.Add(Coin(80));
            Check(PortalCoinWallet.GetLocalCoinCount() == 280, "Delayed purse restoration becomes spendable once live.");

            player = Reset(physical: 20, purse: 0);
            player.m_customData[EndosKey] = "80";
            Check(PortalCoinWallet.TryConsumeLocalCoins(20, out receipt), "Reserve physical funds with ambiguous empty purse.");
            player.Inventory.Capacity = 1;
            var blockingItem = new ItemDrop.ItemData { m_shared = new ItemDrop.SharedData { m_name = "$item_stone" } };
            player.Inventory.Items.Add(blockingItem);
            Check(!PortalCoinWallet.TryRefundLocalCoins(in receipt) && Count(Purse) == 0 &&
                  player.m_customData[EndosKey] == "80",
                "No-room refund cannot credit an empty purse that might still receive saved coins.");
            player.Inventory.Items.Remove(blockingItem);
            Check(PortalCoinWallet.TryRefundLocalCoins(in receipt) && Count(player.Inventory) == 20 && Count(Purse) == 0,
                "A blocked refund retains its physical shortfall and can be retried without duplication.");

            player = Reset(physical: 5);
            BepInEx.Bootstrap.Chainloader.PluginInfos["EndosCoinPurse"] = new BepInEx.Bootstrap.PluginInfo();
            Check(PortalCoinWallet.GetLocalCoinCount() == 0 && !PortalCoinWallet.TryConsumeLocalCoins(1, out receipt),
                "Registered mod with a null plugin instance fails closed.");

            player = Reset(purse: 5000);
            ItemDrop.ItemData purseSource = Purse.Items[0];
            Check(PortalCoinWallet.TryConsumeLocalCoins(5000, out receipt) && Count(Purse) == 0,
                "Exact overstack payment depletes the purse.");
            Check(PortalCoinWallet.TryRefundLocalCoins(in receipt), "Empty-purse overstack refund succeeds.");
            Check(Count(Purse) == 5000 && Purse.Items.Count == 1 &&
                  Purse.Items[0].m_quality == purseSource.m_quality,
                "Overstack refund restores one virtual stack without max-stack clamping.");
            CheckSavedPurse(player, 5000);

            player = Reset(physical: 40, purse: 15);
            physicalSource = player.Inventory.Items[0];
            Check(PortalCoinWallet.TryConsumeLocalCoins(55, out receipt), "Full physical and purse depletion.");
            player.Inventory.Capacity = 1;
            var unrelatedItem = new ItemDrop.ItemData { m_shared = new ItemDrop.SharedData { m_name = "$item_stone" } };
            player.Inventory.Items.Add(unrelatedItem);
            Check(PortalCoinWallet.TryRefundLocalCoins(in receipt), "Full inventory rollback still restores original coins.");
            Check(Count(player.Inventory) == 0 && Count(Purse) == 55 && player.Inventory.Items.Contains(unrelatedItem),
                "No-room refund safely restores the measured physical shortfall to Endos without discarding items.");
            Check(PortalCoinWallet.TryRefundLocalCoins(in receipt) && Count(Purse) == 55,
                "A completed Endos receipt cannot duplicate its refund.");

            player = Reset(physical: 100, purse: 0);
            player.Inventory.Capacity = 1;
            physicalSource = player.Inventory.Items[0];
            Check(PortalCoinWallet.TryConsumeLocalCoins(50, out receipt), "Partial physical payment with empty Endos.");
            Check(PortalCoinWallet.TryRefundLocalCoins(in receipt) && Count(player.Inventory) == 100 && Count(Purse) == 0,
                "Full inventory with a surviving source stack restores that stack instead of redirecting.");
            Check(ReferenceEquals(player.Inventory.Items[0], physicalSource), "Partial-stack refund retains the source stack.");

            player = Reset(physical: 10, purse: 90);
            Check(PortalCoinWallet.TryConsumeLocalCoins(80, out receipt), "Create receipt before character switch.");
            Player oldPlayer = player;
            Inventory oldPurse = Purse;
            Player.m_localPlayer = new Player();
            Check(!PortalCoinWallet.TryRefundLocalCoins(in receipt), "Receipt cannot credit a different player.");
            Check(Count(oldPurse) == 10 && Count(Player.m_localPlayer.Inventory) == 0,
                "Rejected character-switch refund does not mutate either character.");
            Player.m_localPlayer = oldPlayer;
            var replacementPurse = new Inventory();
            replacementPurse.Items.Add(Coin(44));
            ValheimMuntBuidelHUD.EndosCoinPurseMod.VirtueleBuidelInv = replacementPurse;
            Check(!PortalCoinWallet.TryRefundLocalCoins(in receipt), "Receipt cannot credit a replacement purse.");
            Check(Count(oldPurse) == 10 && Count(replacementPurse) == 44,
                "Rejected purse-switch refund does not redirect coins.");
            ValheimMuntBuidelHUD.EndosCoinPurseMod.VirtueleBuidelInv = oldPurse;
            Check(PortalCoinWallet.TryRefundLocalCoins(in receipt) && Count(oldPurse) == 90,
                "Original player and live purse can still restore the receipt.");

            player = Reset(physical: 5, purse: 60);
            ValheimMuntBuidelHUD.EndosCoinPurseMod.ThrowOnRefresh = true;
            Check(PortalCoinWallet.TryConsumeLocalCoins(45, out receipt) && Count(Purse) == 15,
                "A HUD exception does not undo a valid debit or lose its receipt.");
            CheckSavedPurse(player, 15);
            Check(PortalCoinWallet.TryRefundLocalCoins(in receipt) && Count(Purse) == 60,
                "A HUD exception cannot lose a refund.");
            CheckSavedPurse(player, 60);

            player = Reset(pocket: 40);
            CurrencyPocket.CurrencyPocket.ThrowOnRefresh = true;
            Check(PortalCoinWallet.TryConsumeLocalCoins(25, out receipt) && Pocket(player) == 15,
                "CurrencyPocket HUD failures retain a valid payment.");
            Check(PortalCoinWallet.TryRefundLocalCoins(in receipt) && Pocket(player) == 40,
                "CurrencyPocket HUD failures do not lose its refund.");

            player = Reset(physical: 20, purse: 30);
            player.Inventory.Items.Clear();
            player.Inventory.Items.Add(Coin(8));
            var secondStack = Coin(12);
            secondStack.m_gridPos = new Vector2i(1, 0);
            player.Inventory.Items.Add(secondStack);
            player.Inventory.ThrowOnRemoveCall = 2;
            bool partialResult = PortalCoinWallet.TryConsumeLocalCoins(50, out receipt);
            Check(!partialResult, "Injected second-stack removal failure rejects payment.");
            Check(Count(player.Inventory) == 20 && Count(Purse) == 30,
                "Partial removal failure restores every debited source exactly.");
            CheckSavedPurse(player, 30);

            player = Reset(physical: 20, purse: 30);
            player.Inventory.ThrowOnChangedCall = 1;
            Check(!PortalCoinWallet.TryConsumeLocalCoins(50, out receipt),
                "Physical Changed callback throwing after removal rejects payment.");
            Check(Count(player.Inventory) == 20 && Count(Purse) == 30,
                "Physical callback failure after actual removal restores both sources.");
            CheckSavedPurse(player, 30);

            player = Reset(physical: 20, purse: 30);
            Purse.ThrowOnChangedCall = 1;
            Check(!PortalCoinWallet.TryConsumeLocalCoins(50, out receipt),
                "Purse Changed callback throwing after removal rejects payment.");
            Check(Count(player.Inventory) == 20 && Count(Purse) == 30,
                "Purse callback failure after exact depletion restores all sources.");
            CheckSavedPurse(player, 30);

            player = Reset(physical: 40, purse: 30);
            Check(PortalCoinWallet.TryConsumeLocalCoins(70, out receipt), "Create refund callback failure receipt.");
            player.Inventory.ThrowOnChangedCall = player.Inventory.ChangedCalls + 1;
            Purse.ThrowOnChangedCall = Purse.ChangedCalls + 1;
            Check(PortalCoinWallet.TryRefundLocalCoins(in receipt),
                "Refund callbacks throwing after successful physical/purse insertion still complete by measured deltas.");
            Check(Count(player.Inventory) == 40 && Count(Purse) == 30, "Refund callback failures preserve exact total.");
            CheckSavedPurse(player, 30);
            Check(PortalCoinWallet.TryRefundLocalCoins(in receipt) && Count(player.Inventory) == 40 && Count(Purse) == 30,
                "Retrying a callback-interrupted-but-complete refund cannot duplicate money.");

            player = Reset(physical: 40, purse: 0);
            ItemDrop.ItemData metadataSource = player.Inventory.Items[0];
            metadataSource.m_cheated = true;
            metadataSource.m_quality = 3;
            metadataSource.m_customData["source-marker"] = "original";
            Check(PortalCoinWallet.TryConsumeLocalCoins(40, out receipt), "Reserve full metadata-bearing physical stack.");
            var otherCoins = Coin(9);
            otherCoins.m_gridPos = new Vector2i(0, 0);
            player.Inventory.Items.Add(otherCoins);
            Check(PortalCoinWallet.TryRefundLocalCoins(in receipt), "Refund with original slot occupied uses another slot.");
            ItemDrop.ItemData? restoredMetadata = player.Inventory.GetItemAt(1, 0);
            Check(restoredMetadata != null && restoredMetadata.m_stack == 40 && restoredMetadata.m_cheated &&
                  restoredMetadata.m_quality == 3 && restoredMetadata.m_customData["source-marker"] == "original",
                "Physical refund preserves saved item metadata and avoids merging into unrelated Coins.");
            Check(otherCoins.m_stack == 9 && !otherCoins.m_cheated && Count(Purse) == 0,
                "An unrelated coin stack is not mutated by targeted restoration.");

            player = Reset(purse: 20);
            Check(PortalCoinWallet.TryConsumeLocalCoins(10, out receipt), "Create overflow refund receipt.");
            Purse.Items[0].m_stack = int.MaxValue;
            Check(!PortalCoinWallet.TryRefundLocalCoins(in receipt) && Count(Purse) == int.MaxValue,
                "A refund cannot overflow a subsequently increased purse balance.");
            Purse.Items[0].m_stack = 10;
            Check(PortalCoinWallet.TryRefundLocalCoins(in receipt) && Count(Purse) == 20,
                "A rejected overflow refund retains its unrefunded receipt balance for retry.");

            player = Reset(physical: int.MaxValue, purse: 100);
            Check(PortalCoinWallet.GetLocalCoinCount() == int.MaxValue, "Combined balance saturates without integer overflow.");
            player = Reset(physical: 10, purse: -1);
            Check(PortalCoinWallet.GetLocalCoinCount() == 0 && !PortalCoinWallet.TryConsumeLocalCoins(1, out receipt),
                "Negative virtual stack fails closed.");
            player = Reset(physical: 10, purse: 10);
            Purse.Items.Add(Coin(1));
            Check(PortalCoinWallet.GetLocalCoinCount() == 0 && !PortalCoinWallet.TryConsumeLocalCoins(1, out receipt),
                "Multiple purse coin stacks fail closed rather than changing Endos' single-stack invariant.");
            player = Reset(physical: 10, pocket: 10);
            player.m_customData[PocketKey] = "invalid";
            Check(PortalCoinWallet.GetLocalCoinCount() == 0 && !PortalCoinWallet.TryConsumeLocalCoins(1, out receipt),
                "Invalid CurrencyPocket data fails closed.");
            Player.m_localPlayer = null!;
            Check(PortalCoinWallet.GetLocalCoinCount() == 0 && !PortalCoinWallet.TryConsumeLocalCoins(1, out receipt),
                "Paid travel is unavailable without a local player.");
            Check(PortalCoinWallet.TryConsumeLocalCoins(0, out receipt), "Free travel does not require a local player wallet.");
            return _checks + " coin-wallet checks passed (complete production wallet; isolated doubles, not game runtime).";
        }
    }

    public static class PortalRulesPlugin
    {
        public const string CurrencyPocketSoftDependencyGuid = "CurrencyPocket";
        public const string EndosCoinPurseSoftDependencyGuid = "EndosCoinPurse";
        public static readonly Log PortalRulesLogger = new Log();
    }
    public class Log
    {
        public readonly List<string> Messages = new List<string>();
        public void LogError(string message) => Messages.Add(message);
        public void LogWarning(string message) => Messages.Add(message);
        public void LogDebug(string message) => Messages.Add(message);
    }
    public struct Vector2i
    {
        public int x, y;
        public Vector2i(int x, int y) { this.x = x; this.y = y; }
    }
    public class Player
    {
        public static Player m_localPlayer = null!;
        public Inventory Inventory = new Inventory();
        public Dictionary<string, string> m_customData = new Dictionary<string, string>();
        public Inventory GetInventory() => Inventory;
    }
    public class ItemDrop
    {
        public ItemData m_itemData = new ItemData();
        public class SharedData
        {
            public string m_name = "$item_coins";
            public int m_maxStackSize = 999;
        }
        public class ItemData
        {
            public SharedData m_shared = new SharedData();
            public int m_stack = 1, m_quality = 1, m_variant;
            public bool m_equipped, m_cheated;
            public byte m_worldLevel;
            public Vector2i m_gridPos;
            public UnityEngine.GameObject m_dropPrefab = null!;
            public Dictionary<string, string> m_customData = new Dictionary<string, string>();
            public ItemData Clone() => (ItemData)MemberwiseClone();
        }
    }
    public class ObjectDB
    {
        public static ObjectDB instance = null!;
        public UnityEngine.GameObject CoinPrefab = new UnityEngine.GameObject();
        public UnityEngine.GameObject? GetItemPrefab(string name) => name == "Coins" ? CoinPrefab : null;
    }
    public class Inventory
    {
        public readonly List<ItemDrop.ItemData> Items = new List<ItemDrop.ItemData>();
        public int Capacity = 100, RemoveCalls, AddCalls, ChangedCalls, ThrowOnRemoveCall, ThrowOnChangedCall;
        public Action? m_onChanged;
        public Inventory() { }
        public Inventory(bool temporary) { }
        public Inventory(string name, object? background, int width, int height) { Capacity = width * height; }
        public List<ItemDrop.ItemData> GetAllItems() => Items;
        public int GetWidth() => Capacity;
        public int GetHeight() => 1;
        public ItemDrop.ItemData? GetItemAt(int x, int y)
        {
            foreach (ItemDrop.ItemData item in Items)
                if (item.m_gridPos.x == x && item.m_gridPos.y == y) return item;
            return null;
        }
        public Vector2i FindEmptySlot(bool topFirst)
        {
            for (int x = 0; x < Capacity; x++) if (GetItemAt(x, 0) == null) return new Vector2i(x, 0);
            return new Vector2i(-1, -1);
        }
        public ItemDrop.ItemData? GetItem(string name)
        {
            foreach (ItemDrop.ItemData item in Items) if (item.m_shared.m_name == name) return item;
            return null;
        }
        public bool ContainsItem(ItemDrop.ItemData item) => Items.Contains(item);
        public int CountItems(string name, int quality = -1, bool matchWorldLevel = true)
        {
            long count = 0;
            foreach (ItemDrop.ItemData item in Items) if (item.m_shared.m_name == name) count += item.m_stack;
            Inventory? purse = ValheimMuntBuidelHUD.EndosCoinPurseMod.VirtueleBuidelInv;
            if (name == "$item_coins" && ReferenceEquals(this, Player.m_localPlayer?.Inventory) && purse != null)
                count += purse.GetItem(name)?.m_stack ?? 0;
            return count >= int.MaxValue ? int.MaxValue : (int)count;
        }
        public bool RemoveItem(ItemDrop.ItemData item, int amount)
        {
            RemoveCalls++;
            if (RemoveCalls == ThrowOnRemoveCall) throw new InvalidOperationException("Injected physical removal failure.");
            if (!Items.Contains(item)) return false;
            if (amount >= item.m_stack) Items.Remove(item);
            else item.m_stack -= amount;
            Changed();
            return true;
        }
        public bool RemoveItem(ItemDrop.ItemData item) => RemoveItem(item, item.m_stack);
        public void Changed()
        {
            ChangedCalls++;
            if (ChangedCalls == ThrowOnChangedCall) throw new InvalidOperationException("Injected callback failure after mutation.");
            m_onChanged?.Invoke();
        }
        public bool AddItem(ItemDrop.ItemData item)
        {
            AddCalls++;
            Inventory? purse = ValheimMuntBuidelHUD.EndosCoinPurseMod.VirtueleBuidelInv;
            if (ReferenceEquals(this, Player.m_localPlayer?.Inventory) && purse != null && item.m_shared.m_name == "$item_coins")
            {
                ItemDrop.ItemData? existing = purse.GetItem(item.m_shared.m_name);
                if (existing != null) existing.m_stack += item.m_stack;
                else { item.m_stack = Math.Min(item.m_stack, item.m_shared.m_maxStackSize); purse.Items.Add(item); }
                return true;
            }
            foreach (ItemDrop.ItemData existing in Items)
            {
                if (existing.m_shared.m_name != item.m_shared.m_name ||
                    existing.m_quality != item.m_quality || existing.m_worldLevel != item.m_worldLevel) continue;
                int amount = Math.Min(item.m_stack, Math.Max(0, existing.m_shared.m_maxStackSize - existing.m_stack));
                existing.m_stack += amount;
                existing.m_cheated |= item.m_cheated;
                item.m_stack -= amount;
                if (item.m_stack == 0) { Changed(); return true; }
            }
            Vector2i position = FindEmptySlot(true);
            bool success = position.x >= 0;
            if (success) { item.m_gridPos = position; Items.Add(item); }
            Changed();
            return success;
        }
        public bool MoveItemToThis(Inventory fromInventory, ItemDrop.ItemData item, int amount, int x, int y)
        {
            if (x < 0 || y != 0 || x >= Capacity) return false;
            amount = Math.Min(amount, item.m_stack);
            ItemDrop.ItemData? target = GetItemAt(x, y);
            int added;
            if (target != null)
            {
                if (target.m_shared.m_name != item.m_shared.m_name || target.m_quality != item.m_quality ||
                    target.m_worldLevel != item.m_worldLevel || target.m_cheated != item.m_cheated) return false;
                added = Math.Min(amount, Math.Max(0, target.m_shared.m_maxStackSize - target.m_stack));
                if (added == 0) return false;
                target.m_stack += added;
            }
            else
            {
                added = amount;
                ItemDrop.ItemData restored = item.Clone();
                restored.m_stack = added;
                restored.m_gridPos = new Vector2i(x, y);
                Items.Add(restored);
            }
            item.m_stack -= added;
            Changed();
            if (item.m_stack == 0) fromInventory.RemoveItem(item);
            else fromInventory.Changed();
            return added == amount;
        }
        public ItemDrop.ItemData? AddItem(string name, int stack, int quality, int variant, long crafterID,
            string crafterName, Vector2i position, bool cheated, bool pickedUp = false, bool dropIfFullInv = true)
        {
            var item = Checks.Coin(stack);
            return AddItem(item) ? item : null;
        }
    }
}

namespace BepInEx.Bootstrap
{
    public class PluginInfo { public object? Instance; }
    public static class Chainloader
    {
        public static readonly Dictionary<string, PluginInfo> PluginInfos = new Dictionary<string, PluginInfo>();
    }
}
namespace UnityEngine
{
    public class GameObject
    {
        public readonly PortalRulesCoinWalletChecks.ItemDrop ItemDrop = new PortalRulesCoinWalletChecks.ItemDrop();
        public T? GetComponent<T>() where T : class => ItemDrop as T;
    }
}
namespace ValheimMuntBuidelHUD
{
    public class EndosCoinPurseMod
    {
        public static PortalRulesCoinWalletChecks.Inventory VirtueleBuidelInv = null!;
        public static int Refreshes;
        public static bool ThrowOnRefresh;
        public static void UpdateBuidelCount()
        {
            Refreshes++;
            if (ThrowOnRefresh) throw new InvalidOperationException("Injected Endos HUD failure.");
        }
    }
}
namespace CurrencyPocket
{
    public class CurrencyPocket
    {
        public static int Refreshes;
        public static bool ThrowOnRefresh;
        private static void UpdatePocketUI()
        {
            Refreshes++;
            if (ThrowOnRefresh) throw new InvalidOperationException("Injected CurrencyPocket HUD failure.");
        }
    }
}
