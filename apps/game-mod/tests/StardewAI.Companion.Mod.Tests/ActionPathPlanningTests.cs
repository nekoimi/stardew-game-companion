using StardewAI.Companion.Mod.Adapters;
using StardewAI.Companion.Mod.Domain;
using StardewAI.Companion.Mod.Execution;
using StardewAI.Companion.Mod.Navigation;
using StardewAI.Companion.Mod.Observation;
using StardewAI.Companion.Mod.Transport;
using Xunit;

namespace StardewAI.Companion.Mod.Tests;

public sealed class ActionPathPlanningTests
{
    [Theory]
    [InlineData("native")]
    [InlineData("hoe")]
    [InlineData("plant")]
    [InlineData("water")]
    [InlineData("harvest")]
    [InlineData("chest")]
    [InlineData("shipping")]
    public void WorkSearchYieldsAndStopsWorldReadsOnPauseAndTakeover(string action)
    {
        int thread = Environment.CurrentManagedThreadId;
        var observer = new SimulatedWorldObserver { OnCollisionRead = () => Assert.Equal(thread, Environment.CurrentManagedThreadId) };
        var actor = new MechanicsActor("work-search", initialPose: new AuthoritativePose("Farm", 10, 10, FacingDirection.Down));
        actor.Hoe = new StardewValley.Tools.Hoe();
        actor.TryAddItemToInventory(new InventoryItem("(O)472", "Seeds", 10));
        var navigator = new SameMapNavigator(observer);
        var target = new TileCoordinate(150, 150);
        ISkillExecutionMachine machine;
        switch (action)
        {
            case "native":
                var native = new NativeActionStateMachine(actor, observer, navigator, new TestNativeActionAdapter());
                Assert.True(native.Start(new("work", "task", "Farm", NativeActionKind.ChopTree, "chop-tree", new[] { new NativeActionTarget(target) }), out _));
                machine = native;
                break;
            case "hoe":
                var hoe = new HoeZoneStateMachine(actor, observer, navigator, new TestHoeAdapter(observer));
                Assert.True(hoe.Start(new("work", "task", "Farm", new[] { target }), out _));
                machine = hoe;
                break;
            case "plant":
                observer.SetDirt(target, TileDirtState.DryDirt());
                var plant = new PlantZoneStateMachine(actor, observer, navigator, new TestPlantAdapter(observer));
                Assert.True(plant.Start(new("work", "task", "Farm", "(O)472", new[] { target }), out _));
                machine = plant;
                break;
            case "water":
                var second = new TileCoordinate(151, 150);
                observer.SetDirt(target, TileDirtState.DryDirt(hasCrop: true));
                observer.SetDirt(second, TileDirtState.DryDirt(hasCrop: true));
                var water = new WaterZoneStateMachine(actor, observer, navigator, new TestWateringCanAdapter(observer));
                Assert.True(water.Start(new("work", "task", "Farm", new[] { target, second }, 50, 20), out _));
                machine = water;
                break;
            case "harvest":
                observer.SetDirt(target, TileDirtState.DryDirt(hasCrop: true) with { IsHarvestable = true });
                var harvest = new HarvestZoneStateMachine(actor, observer, navigator, new UnusedHarvestAdapter());
                Assert.True(harvest.Start(new("work", "task", "Farm", new[] { target }, 50, 20), out _));
                machine = harvest;
                break;
            case "chest":
                observer.SetChest(target, new ChestScanInfo(target, 36, 36, new List<ChestSlotInfo>()));
                var chest = new ChestActionStateMachine(actor, observer, navigator, new TestChestAdapter(observer));
                Assert.True(chest.Start(new("work", "task", ChestActionKind.Organize, "Farm", target), out _));
                machine = chest;
                break;
            default:
                var adapter = new TestShippingAdapter(observer);
                adapter.Footprint.Clear();
                adapter.Footprint.Add(target);
                var shipping = new ShippingStateMachine(actor, observer, navigator, adapter);
                Assert.True(shipping.Start(new("work", "task", "Farm", new[] { new ShippingItemRequest("(O)472", 1) }), out _));
                machine = shipping;
                break;
        }
        Assert.InRange(observer.TilePassabilityChecks, 0, 8); // Start doesn't synchronously search.
        var origin = actor.PixelPosition;
        for (int tick = 1; tick <= 3; tick++)
        {
            int before = observer.TilePassabilityChecks;
            machine.Update(null, tick);
            Assert.InRange(observer.TilePassabilityChecks - before, 0, 1030);
            Assert.Equal(origin, actor.PixelPosition);
        }
        Assert.True(observer.TilePassabilityChecks > 0);
        Assert.True(machine.IsExecuting);
        machine.RequestPause();
        int reads = observer.TilePassabilityChecks;
        machine.Update(null, 4);
        machine.Update(null, 5);
        Assert.True(machine.IsPaused);
        Assert.Equal(reads, observer.TilePassabilityChecks);
        observer.TimeOfDay = 800;
        machine.Resume();
        machine.Update(null, 6);
        Assert.True(machine.IsExecuting);
        machine.RequestCancel("F9 player takeover");
        reads = observer.TilePassabilityChecks;
        for (int tick = 7; tick < 20; tick++) machine.Update(null, tick);
        Assert.False(machine.IsExecuting);
        Assert.Equal(reads, observer.TilePassabilityChecks);
        Assert.Equal(origin, actor.PixelPosition);
        Assert.Null(actor.ActiveTaskId);
        machine.Resume();
        machine.Update(null, 20);
        Assert.False(machine.IsExecuting);
    }

    [Fact]
    public void PartialActionSearchRestartsAfterActorRelocation()
    {
        var observer = new SimulatedWorldObserver();
        var actor = new MechanicsActor(initialPose: new AuthoritativePose("Farm", 10, 10, FacingDirection.Down));
        var navigator = new SameMapNavigator(observer);
        using var planning = new ActionPathPlanning();
        PathResult? result = null;
        int receipts = 0;
        planning.Start(actor, () => navigator.BeginPathToInteract(new(actor.LocationName, actor.Tile, actor.Facing), "Farm", new(150, 150)),
            path => { result = path; receipts++; });
        planning.Advance();
        Assert.True(planning.IsPending);
        actor.SetLocation("Farm", new(150, 149));
        planning.Advance();
        Assert.False(planning.IsPending);
        Assert.True(result!.Success);
        Assert.Equal(actor.Tile, Assert.Single(result.Steps).Tile);
        planning.Advance();
        Assert.Equal(1, receipts);
    }

    private sealed class UnusedHarvestAdapter : IHarvestAdapter
    {
        public HarvestTileResult HarvestTile(IFarmerActor actor, string locationName, TileCoordinate targetTile) =>
            throw new InvalidOperationException("Search must not reach the harvest action.");
    }
}
