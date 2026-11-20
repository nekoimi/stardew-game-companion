using StardewAI.Companion.Mod.Domain;
using StardewAI.Companion.Mod.Execution;
using StardewAI.Companion.Mod.Navigation;
using Xunit;

namespace StardewAI.Companion.Mod.Tests;

public class NavigationResponsivenessTests
{
    [Fact]
    public void LargeCrossMapSearchYieldsAndCancelStopsAllFurtherWorldReads()
    {
        int gameThread = Environment.CurrentManagedThreadId;
        var observer = new SimulatedWorldObserver { OnCollisionRead = () => Assert.Equal(gameThread, Environment.CurrentManagedThreadId) };
        observer.SetPassable("Town", new(1, 1), true);
        var actor = new MechanicsActor("responsive-test", initialPose: new AuthoritativePose("Farm", 0, 1, FacingDirection.Down));
        var graph = new WorldMapGraph();
        graph.AddEdge(new MapEdge("Farm", "Town", new(100, 100), new(1, 1), MapEdgeKind.Warp));
        var machine = new NavigationStateMachine(actor, observer, new SameMapNavigator(observer), graph);
        int receipts = 0;
        machine.OnCompleted += _ => receipts++;
        Assert.True(machine.Start(new("large-route", "route-task", "Town", new(1, 1), 0), out _));
        var origin = actor.Tile;
        for (int tick = 1; tick <= 3; tick++)
        {
            int before = observer.TilePassabilityChecks;
            machine.Update(null, tick);
            Assert.InRange(observer.TilePassabilityChecks - before, 1, 1030);
            Assert.Equal(ExecutionState.Preparing, machine.CurrentState);
            Assert.Equal(origin, actor.Tile);
        }
        machine.RequestCancel("player takeover");
        int reads = observer.TilePassabilityChecks;
        for (int tick = 4; tick < 20; tick++) machine.Update(null, tick);
        Assert.Equal(ExecutionState.Cancelled, machine.FinalResult!.FinalState);
        Assert.Equal(reads, observer.TilePassabilityChecks);
        Assert.Equal(origin, actor.Tile);
        Assert.Equal(1, receipts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SearchDiscardsOldOriginAfterRelocationOrPauseAndResume(bool pause)
    {
        var actor = new MechanicsActor("responsive-test", initialPose: new AuthoritativePose("Farm", 0, 1, FacingDirection.Down));
        var observer = new SimulatedWorldObserver();
        var machine = new NavigationStateMachine(actor, observer, new SameMapNavigator(observer), new WorldMapGraph());
        var target = new TileCoordinate(100, 100);
        Assert.True(machine.Start(new("moving-origin", "task", "Farm", target, 0), out _));
        machine.Update(null, 1);
        Assert.Equal(ExecutionState.Preparing, machine.CurrentState);
        if (pause)
        {
            machine.RequestPause();
            machine.Update(null, 2);
            int reads = observer.TilePassabilityChecks;
            machine.Update(null, 3);
            Assert.Equal(reads, observer.TilePassabilityChecks);
            Assert.True(machine.IsPaused);
        }
        actor.SetLocation("Farm", target);
        if (pause) machine.Resume();
        int beforeArrival = observer.TilePassabilityChecks;
        for (int tick = 4; tick < 12; tick++) machine.Update(null, tick);
        Assert.Equal(ExecutionState.Succeeded, machine.FinalResult!.FinalState);
        Assert.Equal(target, actor.Tile);
        Assert.Equal(beforeArrival, observer.TilePassabilityChecks);
    }

    [Fact]
    public void IncrementalPreviewKeepsCapturedOriginAndDoesNotMoveActor()
    {
        var actor = new MechanicsActor("responsive-test", initialPose: new AuthoritativePose("Farm", 0, 1, FacingDirection.Down));
        var observer = new SimulatedWorldObserver();
        for (int x = 0; x <= 30; x++) observer.SetPassable("Farm", new(x, 1), true);
        actor.SetLocation("Farm", new(0, 1));
        var machine = new NavigationStateMachine(actor, observer, new SameMapNavigator(observer), new WorldMapGraph());
        using var preview = machine.BeginPreviewRoute("Farm", new(30, 1));
        Assert.False(preview.Advance(maxSteps: 8, milliseconds: double.PositiveInfinity));
        actor.SetLocation("Farm", new(20, 1));
        for (int i = 0; i < 100 && !preview.IsComplete; i++) preview.Advance(8, double.PositiveInfinity);
        Assert.True(preview.IsComplete);
        Assert.Equal(true, preview.Result["reachable"]);
        Assert.Equal(29, preview.Result["walkingTiles"]);
        Assert.Equal(new TileCoordinate(20, 1), actor.Tile);
        var origin = System.Text.Json.JsonSerializer.SerializeToElement(preview.Result["origin"]);
        Assert.Equal(0, origin.GetProperty("tile").GetProperty("X").GetInt32());
    }

    [Fact]
    public void SlicedSearchStillRejectsNativeSegmentCollisionBetweenPassableTiles()
    {
        int gameThread = Environment.CurrentManagedThreadId;
        var observer = new SimulatedWorldObserver { OnCollisionRead = () => Assert.Equal(gameThread, Environment.CurrentManagedThreadId) };
        for (int x = 0; x <= 20; x++) observer.SetPassable("Farm", new(x, 1), true);
        observer.BlockedWalkingSegments.Add((new(10, 1), new(11, 1)));
        using var search = new SameMapNavigator(observer).BeginPath("Farm", new(0, 1), new(20, 1));
        Assert.False(search.Advance(4, double.PositiveInfinity));
        for (int i = 0; i < 100 && !search.IsComplete; i++) search.Advance(4, double.PositiveInfinity);
        Assert.True(search.IsComplete);
        Assert.False(search.Result.Success);
    }
}
