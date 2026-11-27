using StardewAI.Companion.Mod.Domain;
using StardewAI.Companion.Mod.Execution;
using StardewAI.Companion.Mod.Navigation;
using StardewAI.Companion.Mod.Observation;
using Xunit;

namespace StardewAI.Companion.Mod.Tests;

public sealed class NavigationOptimizationTests
{
    [Fact]
    public void BoundaryWarpApproachesBeforeTheTouchActionStripInsteadOfStandingOnATrigger()
    {
        var observer = new SimulatedWorldObserver();
        for (int x = 0; x < 8; x++) observer.SetPassable("Farm", new(x, 1), true);
        observer.SetWarpOrDoor(new(7, 1), true); observer.SetWarpOrDoor(new(8, 1), true);
        var graph = new WorldMapGraph();
        var edge = new MapEdge("Farm", "BusStop", new(8, 1), new(1, 1), MapEdgeKind.Warp);
        graph.AddEdge(edge);
        var planner = new ReachableRoutePlanner(observer, new(observer), graph);
        var path = planner.FindExitPath("Farm", new(0, 1), edge);
        Assert.True(path.Success);
        Assert.Equal(new TileCoordinate(6, 1), path.Steps[^1].Tile);
        Assert.Equal(FacingDirection.Right, path.Steps[^1].Facing);
        Assert.DoesNotContain(path.Steps, step => observer.IsWarpOrDoorTile("Farm", step.Tile));
    }

    [Fact]
    public void TopologyCacheNoticesAWarpMovedWithoutChangingItsCount()
    {
        var location = new StardewValley.GameLocation(); location.name.Value = "Farm";
        location.warps.Add(new StardewValley.Warp(8, 1, "BusStop", 1, 1, false));
        var graph = new WorldMapGraph(() => new[] { location });
        Assert.Equal(new TileCoordinate(8, 1), Assert.Single(graph.GetOutgoingEdges("Farm")).SourceTile);
        location.warps.Clear(); location.warps.Add(new StardewValley.Warp(9, 2, "Town", 2, 2, false));
        var changed = Assert.Single(graph.GetOutgoingEdges("Farm"));
        Assert.Equal("Town", changed.TargetLocation); Assert.Equal(new TileCoordinate(9, 2), changed.SourceTile);
    }

    [Fact]
    public void CrossMapSearchPrunesUnrelatedExitsBeforeExpensiveCollisionSearch()
    {
        var observer = new SimulatedWorldObserver();
        observer.SetPassable("Town", new(1, 1), true);
        observer.SetPassable("Town", new(2, 1), true);
        var graph = new WorldMapGraph();
        for (int i = 0; i < 30; i++)
            graph.AddEdge(new("Farm", "DeadEnd" + i, new(1000, 1000 + i), new(1, 1), MapEdgeKind.Warp));
        graph.AddEdge(new("Farm", "Town", new(3, 1), new(1, 1), MapEdgeKind.Warp));
        var route = new ReachableRoutePlanner(observer, new(observer), graph)
            .Find("Farm", new(0, 1), "Town", new(2, 1), 1000, null, out var failure, true);
        Assert.Null(failure);
        Assert.Equal("Town", Assert.Single(route!.Edges).TargetLocation);
        Assert.Equal(2, route.Paths.Count);
        Assert.InRange(observer.TilePassabilityChecks, 1, 180);
    }

    [Fact]
    public void ReusedPathsRejectNewTileAndSegmentObstructionsWithoutRelyingOnRevision()
    {
        var observer = new SimulatedWorldObserver();
        var navigator = new SameMapNavigator(observer);
        var start = new TileCoordinate(0, 1); var goal = new TileCoordinate(10, 1);
        Assert.True(navigator.FindPath("Farm", start, goal).Success);
        Assert.True(navigator.FindPath("Farm", start, goal).Success);
        Assert.Equal(1, navigator.CacheHits);
        observer.SetPassable(new(5, 1), false);
        observer.BlockedWalkingSegments.Add((new(4, 2), new(5, 2)));
        var detour = navigator.FindPath("Farm", start, goal);
        Assert.True(detour.Success);
        Assert.DoesNotContain(detour.Steps, step => step.Tile == new TileCoordinate(5, 1));
        Assert.DoesNotContain(detour.Steps, step => step.From == new TileCoordinate(4, 2) && step.Tile == new TileCoordinate(5, 2));
        Assert.Equal(1, navigator.CacheInvalidations);
        observer.SetPassable(new(5, 1), true);
        observer.BlockedWalkingSegments.Clear(); navigator.InvalidatePaths();
        var direct = navigator.FindPath("Farm", start, goal);
        Assert.True(direct.WalkingCost < detour.WalkingCost);
    }

    [Fact]
    public void MultiGoalSearchChoosesReachableInteractionPoseInsteadOfTryingEveryBlockedSide()
    {
        var observer = new SimulatedWorldObserver();
        foreach (var tile in new[] { new TileCoordinate(0, 1), new(1, 1), new(2, 1), new(3, 1) })
            observer.SetPassable("Farm", tile, true);
        var navigator = new SameMapNavigator(observer);
        using var search = navigator.BeginPathToAny("Farm", new(0, 1), new Dictionary<TileCoordinate, FacingDirection>
        { [new(1000, 1000)] = FacingDirection.Down, [new(3, 1)] = FacingDirection.Up });
        var result = search.Complete();
        Assert.True(result.Success);
        Assert.Equal(new TileCoordinate(3, 1), result.Steps[^1].Tile);
        Assert.Equal(FacingDirection.Up, result.Steps[^1].Facing);
        Assert.InRange(observer.TilePassabilityChecks, 1, 80);
        using var cached = navigator.BeginPathToAny("Farm", new(0, 1), new Dictionary<TileCoordinate, FacingDirection>
        { [new(3, 1)] = FacingDirection.Down });
        Assert.Equal(FacingDirection.Down, cached.Complete().Steps[^1].Facing);
        Assert.Equal(1, navigator.CacheHits);
    }

    [Fact]
    public void ShopGoalUsesObservedCounterAndVerifiesFacingWithoutOpeningOrBuying()
    {
        var observer = new SimulatedWorldObserver();
        observer.SetShopCounterTiles("TestShop", new[] { new TileCoordinate(4, 1) });
        observer.SetPassable(new(4, 1), false);
        var actor = new MechanicsActor("shop-target", initialPose: new AuthoritativePose("Farm", 0, 1, FacingDirection.Down));
        var machine = new NavigationStateMachine(actor, observer, new(observer), new WorldMapGraph());
        Assert.True(machine.Start(new("shop", "task", "Farm", default, NavigationTarget: "shop", ShopId: "TestShop"), out _));
        for (int tick = 1; tick < 300 && machine.IsExecuting; tick++) machine.Update(null, tick);
        Assert.Equal(ExecutionState.Succeeded, machine.CurrentState);
        Assert.Equal(new TileCoordinate(3, 1), actor.Tile);
        Assert.Equal(FacingDirection.Right, actor.Facing);
        Assert.Equal("shop", machine.FinalResult!.NavigationDetails!["navigationTarget"]);
    }

    [Fact]
    public void NewlyBlockedExitRecoversThroughAnotherEntranceWithinOneNavigationAction()
    {
        var observer = new SimulatedWorldObserver();
        foreach (var tile in Enumerable.Range(0, 5).SelectMany(x => new[] { new TileCoordinate(x, 1), new(x, 3) })
            .Append(new(0, 2))) observer.SetPassable("Farm", tile, true);
        observer.SetPassable("Town", new(1, 1), true); observer.SetPassable("Town", new(2, 1), true);
        var graph = new WorldMapGraph();
        graph.AddEdge(new("Farm", "Town", new(4, 1), new(1, 1), MapEdgeKind.Warp));
        graph.AddEdge(new("Farm", "Town", new(4, 3), new(1, 1), MapEdgeKind.Warp));
        var actor = new MechanicsActor("alternative", initialPose: new AuthoritativePose("Farm", 0, 1, FacingDirection.Down));
        var machine = new NavigationStateMachine(actor, observer, new(observer), graph);
        Assert.True(machine.Start(new("travel", "task", "Town", new(2, 1)), out _));
        int tick = 1;
        while (machine.CurrentState == ExecutionState.Preparing && tick < 100) machine.Update(null, tick++);
        Assert.Equal(ExecutionState.Navigating, machine.CurrentState);
        observer.SetPassable("Farm", new(1, 1), false);
        for (; tick < 1000 && machine.IsExecuting; tick++) machine.Update(null, tick);
        Assert.Equal(ExecutionState.Succeeded, machine.CurrentState);
        Assert.Equal(new TileCoordinate(4, 3), machine.FinalResult!.HopRecords[0].ExitWarp!.SourceTile);
        Assert.Equal(1, machine.FinalResult.NavigationDetails!["alternateEntrances"]);
    }
}
