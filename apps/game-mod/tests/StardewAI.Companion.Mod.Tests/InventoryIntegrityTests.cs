using StardewAI.Companion.Mod.Adapters;
using StardewAI.Companion.Mod.Domain;
using StardewValley;
using StardewValley.Enchantments;
using StardewValley.Tools;
using Xunit;

namespace StardewAI.Companion.Mod.Tests;

public sealed class InventoryIntegrityTests
{
    private static StardewValley.Object Wine(string name, string fruit, int price, int stack) => new()
    {
        ItemId = "348", Name = name, Price = price, Stack = stack,
        preserve = { Value = StardewValley.Object.PreserveType.Wine },
        preservedParentSheetIndex = { Value = fruit }
    };

    [Fact]
    public void OrganizePreservesIncompatibleWineAndFillsEarlierCompatibleStacks()
    {
        var apple = Wine("Apple Wine", "613", 300, 998);
        var starfruit = Wine("Starfruit Wine", "268", 2250, 3);
        var appleRemainder = Wine("Apple Wine", "613", 300, 5);
        var appleLast = Wine("Apple Wine", "613", 300, 2);
        var items = new List<Item> { apple, starfruit, appleRemainder, appleLast };
        Assert.False(apple.canStackWith(starfruit));

        var merges = NormalChestAdapter.MergeCompatibleStacks(items);

        Assert.Equal(999, apple.Stack);
        Assert.Same(starfruit, items[1]);
        Assert.Equal(3, starfruit.Stack);
        Assert.Equal(2250, starfruit.Price);
        Assert.Equal("268", starfruit.preservedParentSheetIndex.Value);
        Assert.Equal(6, appleRemainder.Stack);
        Assert.Null(items[3]);
        Assert.Equal(3, merges.Sum(m => m.MergedStack));
    }

    [Fact]
    public void NativeInventorySurvivesJsonSaveWithWineEnchantmentAndEmptySlots()
    {
        var wine = Wine("Starfruit Wine", "268", 2250, 3);
        wine.Quality = 2;
        wine.modData["product/custom"] = "keep wine state";
        var can = new WateringCan { UpgradeLevel = 3, WaterLeft = 0 };
        can.enchantments.Add(new EfficientToolEnchantment());
        can.modData["product/tool"] = "keep tool state";
        var state = CompanionActorState.CreateDefault("roundtrip");
        state.NativeInventory = NativeInventoryPersistence.Capture(new List<Item> { null!, wine, null!, can });
        string directory = Path.Combine(Path.GetTempPath(), "star-inventory-" + Guid.NewGuid().ToString("N"));
        try
        {
            var repo = new JsonActorStateRepository(directory);
            repo.Save(state);
            var loaded = repo.LoadOrInitializeDefaults("roundtrip");
            var restored = NativeInventoryPersistence.Restore(loaded.NativeInventory!);
            var restoredWine = Assert.IsType<StardewValley.Object>(restored[1]);
            Assert.Equal(3, restoredWine.Stack);
            Assert.Equal(2, restoredWine.Quality);
            Assert.Equal(2250, restoredWine.Price);
            Assert.Equal("268", restoredWine.preservedParentSheetIndex.Value);
            Assert.Equal(StardewValley.Object.PreserveType.Wine, restoredWine.preserve.Value);
            Assert.Equal("keep wine state", restoredWine.modData["product/custom"]);
            var restoredCan = Assert.IsType<WateringCan>(restored[3]);
            Assert.Equal(3, restoredCan.UpgradeLevel);
            Assert.Equal(0, restoredCan.WaterLeft);
            Assert.IsType<EfficientToolEnchantment>(Assert.Single(restoredCan.enchantments));
            Assert.Equal("keep tool state", restoredCan.modData["product/tool"]);
            Assert.Null(restored[0]);
            Assert.Null(restored[2]);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void CorruptNativeItemRejectsWholeRestoreAndCannotFallBackToSummary()
    {
        var slots = NativeInventoryPersistence.Capture(new List<Item> { Wine("Apple Wine", "613", 300, 1) });
        slots.Add(new NativeInventorySlot(3, "broken xml"));
        var farmer = (Farmer)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Farmer));
        var actor = new FarmerMechanicsActor("invalid-save");
        typeof(FarmerMechanicsActor).GetField("_gameFarmer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(actor, farmer);
        // No live Farmer fields have been initialized. Restoration must reject
        // the bad XML before touching position, resources or the live inventory.
        var state = CompanionActorState.CreateDefault("invalid-save");
        state.NativeInventory = slots;
        Assert.Throws<InvalidOperationException>(() => actor.ApplyPersistentState(state));
        slots[1] = slots[0];
        Assert.Throws<InvalidOperationException>(() => NativeInventoryPersistence.Restore(state.NativeInventory));
    }
}
