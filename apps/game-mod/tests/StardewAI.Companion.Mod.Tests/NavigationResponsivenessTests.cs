using System.Diagnostics;
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
        graph.AddEdge(new MapEdge("Farm", "Town", new(1000, 1000), new(1, 1), MapEdgeKind.Warp));
        var machine = new NavigationStateMachine(actor, observer, new SameMapNavigator(observer), graph);
        int receipts = 0;
        machine.OnCompleted += _ => receipts++;
        Assert.True(machine.Start(new("large-route", "route-task", "Town", new(1, 1), 0), out _));
        var origin = actor.Tile;
        for (int tick = 1; tick <= 3; tick++)
        {
            int before = observer.TilePassabilityChecks;
            machine.Update(null, tick);
            // A slice may spend its budget entering a nested iterator/JIT before
            // reaching a collision read. It must remain bounded and yield.
            Assert.InRange(observer.TilePassabilityChecks - before, 0, 1030);
            Assert.Equal(ExecutionState.Preparing, machine.CurrentState);
            Assert.Equal(origin, actor.Tile);
        }
        Assert.True(observer.TilePassabilityChecks > 0);
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
        var target = new TileCoordinate(1000, 1000);
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
    public void RoutePlanningTimeoutSettlesAndReleasesTheActorForAnotherAction()
    {
        long now = 0;
        var actor = new MechanicsActor("timeout-test", initialPose: new AuthoritativePose("Farm", 0, 1, FacingDirection.Down));
        var observer = new SimulatedWorldObserver();
        var machine = new NavigationStateMachine(actor, observer, new SameMapNavigator(observer), new WorldMapGraph())
        {
            GetTimestamp = () => now,
        };
        var results = new List<NavigationResult>();
        machine.OnCompleted += results.Add;
        var origin = actor.Tile;
        Assert.True(machine.Start(new("stall", "task", "Farm", new(1000, 1000), 0), out _));
        machine.Update(null, 1);
        Assert.Equal(ExecutionState.Preparing, machine.CurrentState);

        now += 21 * Stopwatch.Frequency;
        for (int tick = 2; tick < 10; tick++) machine.Update(null, tick);
        var failed = Assert.Single(results);
        Assert.Equal("stall", failed.CommandId);
        Assert.Equal(ExecutionState.Failed, failed.FinalState);
        Assert.Equal("ROUTE_PLANNING_TIMEOUT", failed.ErrorCode);
        Assert.Equal(origin, actor.Tile);
        Assert.False(machine.IsExecuting);

        Assert.True(machine.Start(new("next", "task", "Farm", origin, 0), out _));
        for (int tick = 10; tick < 20; tick++) machine.Update(null, tick);
        Assert.Equal(2, results.Count);
        Assert.Equal("next", results[1].CommandId);
        Assert.Equal(ExecutionState.Succeeded, results[1].FinalState);
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
        Assert.Equal(30, preview.Result["walkingTiles"]);
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

    [Fact]
    public void DirectRouteAvoidsFloodingOpenMapAndSearchingUnrelatedExits()
    {
        var observer = new SimulatedWorldObserver();
        var graph = new WorldMapGraph();
        graph.AddEdge(new MapEdge("Farm", "Town", new(500, 500), new(1, 1), MapEdgeKind.Warp));
        var planner = new ReachableRoutePlanner(observer, new SameMapNavigator(observer), graph);
        var route = planner.Find("Farm", new(0, 0), "Farm", new(60, 60), 600, null, out var failure,
            requireExactDestination: true);
        Assert.Null(failure);
        Assert.NotNull(route);
        Assert.Empty(route!.Edges);
        Assert.Equal(new TileCoordinate(60, 60), route.TargetTile);
        Assert.Equal(85, route.WalkingTiles);
        Assert.InRange(observer.TilePassabilityChecks, 1, 600);
    }

    [Fact]
    public void PreviewPreservesBlockedDestinationEvidenceWithoutPretendingAdjacentArrival()
    {
        var observer = new SimulatedWorldObserver();
        observer.SetPassable(new(5, 5), false);
        var planner = new ReachableRoutePlanner(observer, new SameMapNavigator(observer), new WorldMapGraph());
        var route = planner.Find("Farm", new(0, 0), "Farm", new(5, 5), 600, null, out var failure,
            requireExactDestination: true);
        Assert.Null(route);
        Assert.Equal("DESTINATION_UNREACHABLE", failure!.ReasonCode);
        Assert.True((bool)failure.Details!["exactArrivalRequired"]);
        Assert.NotEmpty((TileCoordinate[])failure.Details["standingCandidates"]);
    }
}
