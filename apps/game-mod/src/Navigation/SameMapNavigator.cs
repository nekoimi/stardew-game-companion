using StardewAI.Companion.Mod.Domain;
using StardewAI.Companion.Mod.Observation;

namespace StardewAI.Companion.Mod.Navigation;

/// <summary>
/// Navigates the Mechanics Actor across passable tiles within the SAME map.
/// Enforces:
/// 1. Same-map movement only; cross-map movement is strictly forbidden.
/// 2. Collision-respecting pathfinding.
/// 3. Valid adjacent standing tile and facing direction for tool interaction.
/// 4. Stuck detection without teleport recovery.
/// </summary>
public sealed class SameMapNavigator
{
    private readonly IWorldObserver _observer;
    private const int MaxSearchDepth = 2500;
    private const int ShortcutLookahead = 8;

    public SameMapNavigator(IWorldObserver observer)
    {
        _observer = observer ?? throw new ArgumentNullException(nameof(observer));
    }

    public PathResult FindPathToInteract(
        IFarmerActor actor,
        string targetLocation,
        TileCoordinate targetTile,
        IReadOnlyCollection<TileCoordinate>? avoidTiles = null)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return FindPathToInteract(new AuthoritativePose(actor.LocationName, actor.Tile, actor.Facing), targetLocation, targetTile, avoidTiles);
    }

    /// <summary>
    /// Finds a valid path from the actor's current pose to an adjacent standing tile
    /// facing the target tile.
    ///
    /// The obstacle set is identical to the one enforced during physical walking:
    /// native tile passability, dynamic player occupancy, and any explicitly
    /// reported blocked tiles from a previous replan (`avoidTiles`). The tile the
    /// actor already stands on is always exempt: it was reached legally and
    /// rejecting it would make every replan fail identically.
    /// </summary>
    public PathResult FindPathToInteract(AuthoritativePose currentPose, string targetLocation,
        TileCoordinate targetTile, IReadOnlyCollection<TileCoordinate>? avoidTiles = null)
    {
        using var search = BeginPathToInteract(currentPose, targetLocation, targetTile, avoidTiles);
        return search.Complete();
    }

    public NavigationSearch<PathResult> BeginPathToInteract(AuthoritativePose currentPose, string targetLocation,
        TileCoordinate targetTile, IReadOnlyCollection<TileCoordinate>? avoidTiles = null) =>
        new(search => SearchInteraction(search, currentPose, targetLocation, targetTile, avoidTiles));

    private IEnumerable<NavigationSearch?> SearchInteraction(NavigationSearch<PathResult> search,
        AuthoritativePose pose, string location, TileCoordinate target, IReadOnlyCollection<TileCoordinate>? avoid)
    {
        if (!string.Equals(pose.LocationName, location, StringComparison.OrdinalIgnoreCase))
        {
            search.Result = PathResult.Failed($"Cross-map navigation forbidden: Actor is at '{pose.LocationName}' but target is at '{location}'.");
            yield break;
        }
        if (pose.Tile.IsAdjacentTo(target) && FacingDirectionExtensions.DirectionToAdjacent(pose.Tile, target) is { } facing)
        {
            search.Result = PathResult.Completed(new[] { new PathStep(pose.Tile, facing) });
            yield break;
        }
        var candidates = GetCandidateStandingPositions(location, target, avoid).ToList();
        if (candidates.Count == 0)
        {
            search.Result = PathResult.Failed($"Target tile {target} has no passable adjacent standing positions.");
            yield break;
        }
        PathResult? best = null;
        foreach (var candidate in candidates)
        {
            var path = BeginPath(location, pose.Tile, candidate.Tile, candidate.Facing, avoid);
            yield return path;
            if (path.Result.Success && (best is null || path.Result.WalkingCost < best.WalkingCost))
                best = path.Result;
        }
        search.Result = best ?? PathResult.Failed($"No passable path found from {pose.Tile} to target adjacent positions for {target}.");
    }

    public PathResult FindPath(string locationName, TileCoordinate startTile, TileCoordinate destinationTile,
        FacingDirection? finalFacing = null, IReadOnlyCollection<TileCoordinate>? avoidTiles = null)
    {
        using var search = BeginPath(locationName, startTile, destinationTile, finalFacing, avoidTiles);
        return search.Complete();
    }

    public NavigationSearch<PathResult> BeginPath(string locationName, TileCoordinate startTile, TileCoordinate destinationTile,
        FacingDirection? finalFacing = null, IReadOnlyCollection<TileCoordinate>? avoidTiles = null) =>
        new(search => SearchPath(search, locationName, startTile, destinationTile, finalFacing, avoidTiles));

    private IEnumerable<NavigationSearch?> SearchPath(NavigationSearch<PathResult> search, string location,
        TileCoordinate start, TileCoordinate goal, FacingDirection? finalFacing, IReadOnlyCollection<TileCoordinate>? avoid)
    {
        if (start == goal)
        {
            search.Result = PathResult.Completed(new[] { new PathStep(start, finalFacing ?? FacingDirection.Down) });
            yield break;
        }
        search.Result = PathResult.Failed($"No passable path from {start} to {goal} on map '{location}'.");
        if (!IsTileNavigable(location, goal, avoid)) yield break;
        // A* keeps native collision checks close to the requested route. Favor
        // deeper nodes at equal f to avoid flooding an open map's entire rectangle.
        var queue = new PriorityQueue<(TileCoordinate Tile, int Cost), (int Estimate, int Tie)>();
        var parents = new Dictionary<TileCoordinate, TileCoordinate>();
        var costs = new Dictionary<TileCoordinate, int> { [start] = 0 };
        // Only memoize within this search. Physical walking still checks the live world.
        var passable = new Dictionary<TileCoordinate, bool> { [goal] = true };
        var triggers = new Dictionary<TileCoordinate, bool>();
        Func<TileCoordinate, bool> isTrigger = tile =>
        {
            if (!triggers.TryGetValue(tile, out bool trigger))
                triggers[tile] = trigger = _observer.IsWarpOrDoorTile(location, tile);
            return trigger;
        };
        bool IsOpen(TileCoordinate tile)
        {
            if (!passable.TryGetValue(tile, out bool open))
                passable[tile] = open = IsTileNavigable(location, tile, avoid) && !isTrigger(tile);
            return open;
        }
        bool CanStep(TileCoordinate from, TileCoordinate to)
        {
            if (!IsOpen(to)) return false;
            bool diagonal = from.X != to.X && from.Y != to.Y;
            if (diagonal)
            {
                var (horizontal, vertical) = WalkingPath.DiagonalSides(from, to);
                if (isTrigger(from) || isTrigger(to) || !IsOpen(horizontal) || !IsOpen(vertical)
                    || !_observer.CanWalkBetween(location, from, horizontal)
                    || !_observer.CanWalkBetween(location, from, vertical))
                    return false;
            }
            return _observer.CanWalkBetween(location, from, to);
        }
        queue.Enqueue((start, 0), (WalkingPath.Distance(start, goal), 0));
        int explored = 0;
        bool found = false;
        while (queue.Count > 0 && explored++ < MaxSearchDepth * 10)
        {
            var (current, cost) = queue.Dequeue();
            if (costs[current] != cost) { yield return null; continue; }
            if (current == goal) { found = true; break; }
            foreach (var neighbor in WalkingNeighbors(current))
            {
                // A diagonal expansion may perform three native collision sweeps.
                yield return null;
                int nextCost = cost + WalkingPath.Distance(current, neighbor);
                if (costs.TryGetValue(neighbor, out int previousCost) && previousCost <= nextCost) continue;
                if (!CanStep(current, neighbor)) continue;
                costs[neighbor] = nextCost;
                parents[neighbor] = current;
                queue.Enqueue((neighbor, nextCost), (nextCost + WalkingPath.Distance(neighbor, goal), -nextCost));
            }
        }
        if (!found) yield break;
        var tiles = new List<TileCoordinate>();
        for (var tile = goal; tile != start; tile = parents[tile]) tiles.Add(tile);
        tiles.Add(start);
        tiles.Reverse();
        var waypoints = new List<TileCoordinate> { start };
        for (int index = 0; index < tiles.Count - 1;)
        {
            int limit = Math.Min(index + ShortcutLookahead, tiles.Count - 1);
            int turns = 0;
            var direction = Direction(tiles[index], tiles[index + 1]);
            int walkingCost = 0;
            for (int next = index + 1; next <= limit; next++)
            {
                var nextDirection = Direction(tiles[next - 1], tiles[next]);
                if (nextDirection != direction) turns++;
                direction = nextDirection;
                walkingCost += WalkingPath.Distance(tiles[next - 1], tiles[next]);
                if (next < tiles.Count - 1 && isTrigger(tiles[next])) { limit = next; break; }
            }
            var from = tiles[index];
            var to = tiles[limit];
            List<TileCoordinate>? shortcut = null;
            if (turns > 1 && WalkingPath.Distance(from, to) == walkingCost)
            {
                foreach (bool diagonalFirst in new[] { true, false })
                {
                    var candidate = new List<TileCoordinate>();
                    var current = from;
                    while (current != to)
                    {
                        yield return null;
                        var delta = Direction(current, to);
                        if (!diagonalFirst && Math.Abs(to.X - current.X) != Math.Abs(to.Y - current.Y))
                            delta = Math.Abs(to.X - current.X) > Math.Abs(to.Y - current.Y)
                                ? new(delta.X, 0) : new(0, delta.Y);
                        var next = new TileCoordinate(current.X + delta.X, current.Y + delta.Y);
                        if (!CanStep(current, next)) break;
                        candidate.Add(next);
                        current = next;
                    }
                    if (current != to) continue;
                    shortcut = candidate;
                    break;
                }
            }

            if (shortcut is null) waypoints.Add(tiles[++index]);
            else
            {
                waypoints.AddRange(shortcut);
                index = limit;
            }
            yield return null;
        }

        var steps = new PathStep[waypoints.Count - 1];
        TileCoordinate? runEnd = null;
        for (int index = steps.Length - 1; index >= 0; index--)
        {
            var previous = waypoints[index];
            var tile = waypoints[index + 1];
            var facing = FacingDirectionExtensions.DirectionToAdjacent(previous, tile)
                ?? (tile.Y < previous.Y ? FacingDirection.Up : FacingDirection.Down);
            if (index + 1 < steps.Length && !isTrigger(tile) && !isTrigger(steps[index + 1].Tile)
                && Direction(previous, tile) == Direction(tile, steps[index + 1].Tile))
                runEnd ??= steps[index + 1].Tile;
            else
                runEnd = null;
            steps[index] = new PathStep(tile, facing, previous, runEnd);
        }
        if (finalFacing.HasValue) steps[^1] = steps[^1] with { Facing = finalFacing.Value };
        search.Result = PathResult.Completed(steps, costs[goal]);
    }

    private static TileCoordinate Direction(TileCoordinate from, TileCoordinate to) =>
        new(Math.Sign(to.X - from.X), Math.Sign(to.Y - from.Y));

    private static IEnumerable<TileCoordinate> WalkingNeighbors(TileCoordinate tile)
    {
        foreach (var cardinal in tile.CardinalNeighbors()) yield return cardinal;
        yield return new(tile.X - 1, tile.Y - 1);
        yield return new(tile.X + 1, tile.Y - 1);
        yield return new(tile.X + 1, tile.Y + 1);
        yield return new(tile.X - 1, tile.Y + 1);
    }

    private IEnumerable<(TileCoordinate Tile, FacingDirection Facing)> GetCandidateStandingPositions(
        string locationName,
        TileCoordinate target,
        IReadOnlyCollection<TileCoordinate>? avoidTiles = null)
    {
        // North candidate: stand at (X, Y - 1), face South (Down)
        var north = new TileCoordinate(target.X, target.Y - 1);
        if (IsTileNavigable(locationName, north, avoidTiles))
            yield return (north, FacingDirection.Down);

        // East candidate: stand at (X + 1, Y), face West (Left)
        var east = new TileCoordinate(target.X + 1, target.Y);
        if (IsTileNavigable(locationName, east, avoidTiles))
            yield return (east, FacingDirection.Left);

        // South candidate: stand at (X, Y + 1), face North (Up)
        var south = new TileCoordinate(target.X, target.Y + 1);
        if (IsTileNavigable(locationName, south, avoidTiles))
            yield return (south, FacingDirection.Up);

        // West candidate: stand at (X - 1, Y), face East (Right)
        var west = new TileCoordinate(target.X - 1, target.Y);
        if (IsTileNavigable(locationName, west, avoidTiles))
            yield return (west, FacingDirection.Right);
    }

    /// <summary>
    /// Single source of truth for the navigation obstacle set. Planning and
    /// physical walking must agree: native passability, dynamic player occupancy,
    /// and any explicitly avoided (recently blocked) tiles.
    /// </summary>
    private bool IsTileNavigable(
        string locationName,
        TileCoordinate tile,
        IReadOnlyCollection<TileCoordinate>? avoidTiles)
    {
        if (avoidTiles is not null && avoidTiles.Contains(tile))
            return false;
        if (!_observer.IsTilePassable(locationName, tile))
            return false;
        if (_observer.IsPlayerOnTile(locationName, tile))
            return false;
        return true;
    }

    /// <summary>
    /// Searches for a passable and reachable standing tile within a small radius around a center tile.
    /// Used as fallback when the target destination tile itself is impassable (e.g. building footprint/door).
    /// </summary>
    public (TileCoordinate Tile, PathResult Path)? FindNearestPassableReachableTile(
        string locationName, TileCoordinate startTile, TileCoordinate centerTile, int maxRadius = 3,
        Func<TileCoordinate, bool>? isTileExcluded = null)
    {
        using var search = BeginNearest(locationName, startTile, centerTile, maxRadius, isTileExcluded);
        return search.Complete();
    }

    public NavigationSearch<(TileCoordinate Tile, PathResult Path)?> BeginNearest(
        string location, TileCoordinate start, TileCoordinate center, int radius = 3,
        Func<TileCoordinate, bool>? excluded = null) =>
        new(search => SearchNearest(search, location, start, center, radius, excluded));

    private IEnumerable<NavigationSearch?> SearchNearest(NavigationSearch<(TileCoordinate Tile, PathResult Path)?> search,
        string location, TileCoordinate start, TileCoordinate center, int radius, Func<TileCoordinate, bool>? excluded)
    {
        var candidates = new List<(TileCoordinate Tile, float Distance)>();
        for (int dx = -radius; dx <= radius; dx++)
        for (int dy = -radius; dy <= radius; dy++)
        {
            if (dx == 0 && dy == 0) continue;
            float distance = MathF.Sqrt(dx * dx + dy * dy);
            if (distance > radius + 0.5f) continue;
            var tile = new TileCoordinate(center.X + dx, center.Y + dy);
            if (tile.X >= 0 && tile.Y >= 0 && IsTileNavigable(location, tile, null) && excluded?.Invoke(tile) != true)
                candidates.Add((tile, distance));
            yield return null;
        }
        foreach (var candidate in candidates.OrderBy(c => c.Distance)
                     .ThenBy(c => Math.Abs(c.Tile.X - start.X) + Math.Abs(c.Tile.Y - start.Y)))
        {
            var path = BeginPath(location, start, candidate.Tile);
            yield return path;
            if (path.Result.Success && path.Result.Steps.Count > 0)
            {
                search.Result = (candidate.Tile, path.Result);
                yield break;
            }
        }
    }
}
