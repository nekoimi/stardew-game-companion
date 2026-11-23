using StardewValley;
using StardewAI.Companion.Mod.Domain;
using StardewAI.Companion.Mod.Observation;

namespace StardewAI.Companion.Mod.Navigation;

public sealed record RouteSearchResult(ReachableRoute? Route, RouteFailure? Failure);

public sealed record ReachableRoute(IReadOnlyList<MapEdge> Edges, int WalkingTiles, TileCoordinate TargetTile);

/// <summary>Structured cross-map route failure: a stable reason code, a human-readable
/// message and machine-readable details (blocked side, exit/landing coordinates,
/// clearable obstacle tiles) so the agent can clear obstacles or pick a waypoint.</summary>
public sealed record RouteFailure(
    string ReasonCode,
    string Message,
    Dictionary<string, object>? Details = null);

/// <summary>Routes through reachable entrance poses, including leaving and re-entering one map.</summary>
public sealed class ReachableRoutePlanner
{
    private const int MaxExploredEntrances = 256;
    private const int MaxBlockingTiles = 16;
    private const int BlockingFrontierSearchBudget = 900;

    private readonly IWorldObserver _observer;
    private readonly SameMapNavigator _navigator;
    private readonly IWorldMapGraph _graph;

    public ReachableRoutePlanner(IWorldObserver observer, SameMapNavigator navigator, IWorldMapGraph graph)
    {
        _observer = observer;
        _navigator = navigator;
        _graph = graph;
    }

    private readonly record struct Entrance(string Location, TileCoordinate Tile);
    private static Entrance Key(string location, TileCoordinate tile) => new(location.ToUpperInvariant(), tile);

    private sealed record EdgeAttempt(MapEdge Edge, string FromLocation, TileCoordinate FromTile);

    public ReachableRoute? Find(string location, TileCoordinate start, string targetLocation,
        TileCoordinate target, int time, Farmer? farmer, out RouteFailure? failure, bool requireExactDestination = false)
    {
        using var search = BeginFind(location, start, targetLocation, target, time, farmer, requireExactDestination);
        var result = search.Complete();
        failure = result.Failure;
        return result.Route;
    }

    public NavigationSearch<RouteSearchResult> BeginFind(string location, TileCoordinate start, string targetLocation,
        TileCoordinate target, int time, Farmer? farmer, bool requireExactDestination = false) =>
        new(search => Search(search, location, start, targetLocation, target, time, farmer, requireExactDestination));

    private IEnumerable<NavigationSearch?> Search(NavigationSearch<RouteSearchResult> search, string location,
        TileCoordinate start, string targetLocation, TileCoordinate target, int time, Farmer? farmer, bool requireExactDestination)
    {
        if (requireExactDestination && (IsTrigger(targetLocation, target) || !_observer.IsTilePassable(targetLocation, target)))
        {
            var failure = new RouteFailure("DESTINATION_UNREACHABLE", "Requested standing tile is blocked or a map transition trigger. Choose an observed walkable standing tile; an adjacent arrival is not exact arrival.",
                new Dictionary<string, object> { ["blockedSide"] = "destination", ["locationId"] = targetLocation,
                    ["requestedTile"] = target, ["exactArrivalRequired"] = true,
                    ["standingCandidates"] = target.CardinalNeighbors().Where(tile =>
                        !IsTrigger(targetLocation, tile) && _observer.IsTilePassable(targetLocation, tile)
                        && !_observer.IsPlayerOnTile(targetLocation, tile)).ToArray(),
                    ["candidateScope"] = "standable-only; inspect-route must confirm reachability before dispatch" });
            search.Result = new RouteSearchResult(null, failure);
            yield break;
        }
        var origin = Key(location, start);
        var costs = new Dictionary<Entrance, int> { [origin] = 0 };
        var previous = new Dictionary<Entrance, (Entrance From, MapEdge Edge)>();
        var queue = new PriorityQueue<(Entrance State, string Name), int>();
        queue.Enqueue((origin, location), 0);
        ReachableRoute? best = null;
        string? unavailable = null;
        int explored = 0;
        // Failure evidence gathered while searching: which side of which boundary broke.
        var exitFailures = new List<EdgeAttempt>();
        var arrivalFailures = new List<EdgeAttempt>();
        var destinationFailures = new List<(string Location, TileCoordinate Entrance)>();
        string? firstLockReason = null;
        while (queue.TryDequeue(out var node, out int cost) && explored++ < MaxExploredEntrances)
        {
            yield return null;
            if (cost != costs[node.State]) continue;
            if (best is not null && cost >= best.WalkingTiles) break;
            if (string.Equals(node.Name, targetLocation, StringComparison.OrdinalIgnoreCase))
            {
                var finalSearch = BeginDestinationPath(node.Name, node.State.Tile, target, requireExactDestination);
                yield return finalSearch;
                var final = finalSearch.Result;
                if (final.Success)
                {
                    int total = cost + WalkingTiles(final, node.State.Tile);
                    if (best is null || total < best.WalkingTiles)
                    {
                        var edges = new List<MapEdge>();
                        var state = node.State;
                        while (previous.TryGetValue(state, out var back))
                        {
                            edges.Add(back.Edge);
                            state = back.From;
                        }
                        edges.Reverse();
                        best = new ReachableRoute(edges, total, final.Steps[^1].Tile);
                        // A directly reachable same-map destination needs no tour
                        // of unrelated exits. Blocked direct paths still try detours.
                        if (edges.Count == 0)
                        {
                            search.Result = new RouteSearchResult(best, null);
                            yield break;
                        }
                    }
                }
                else destinationFailures.Add((node.Name, node.State.Tile));
            }
            foreach (var edge in _graph.GetOutgoingEdges(node.Name))
            {
                yield return null;
                int lowerBound = Math.Abs(node.State.Tile.X - edge.SourceTile.X)
                    + Math.Abs(node.State.Tile.Y - edge.SourceTile.Y);
                if (best is not null && cost + lowerBound >= best.WalkingTiles) continue;
                if (!_graph.CheckEdgeTraversable(edge, time, farmer, out var blocked, out _))
                {
                    // Only a reachable entrance into the requested destination can
                    // explain its failure; locked side branches are irrelevant.
                    if (string.Equals(edge.TargetLocation, targetLocation, StringComparison.OrdinalIgnoreCase))
                    {
                        var lockedExit = BeginExitPath(node.Name, node.State.Tile, edge);
                        yield return lockedExit;
                        if (lockedExit.Result.Success) unavailable ??= blocked;
                    }
                    firstLockReason ??= blocked;
                    continue;
                }
                var walkSearch = BeginExitPath(node.Name, node.State.Tile, edge);
                yield return walkSearch;
                var walk = walkSearch.Result;
                if (!walk.Success)
                {
                    exitFailures.Add(new EdgeAttempt(edge, node.Name, node.State.Tile));
                    continue;
                }
                if (!TryArrival(edge, out var arrival))
                {
                    arrivalFailures.Add(new EdgeAttempt(edge, node.Name, node.State.Tile));
                    continue;
                }
                int nextCost = cost + WalkingTiles(walk, node.State.Tile) + 1;
                var next = Key(edge.TargetLocation, arrival);
                if (costs.TryGetValue(next, out int old) && old <= nextCost) continue;
                costs[next] = nextCost;
                previous[next] = (node.State, edge);
                queue.Enqueue((next, edge.TargetLocation), nextCost);
            }
        }

        var blocking = new List<Dictionary<string, object>>();
        if (best is null)
        {
            NavigationSearch<List<Dictionary<string, object>>>? frontier = null;
            if (destinationFailures.Count > 0)
                frontier = BeginBlockingFrontier(destinationFailures[0].Location, destinationFailures[0].Entrance);
            else if (unavailable is null && queue.Count == 0 && arrivalFailures.Count == 0 && exitFailures.Count > 0)
                frontier = BeginBlockingFrontier(exitFailures[0].FromLocation, exitFailures[0].FromTile);
            if (frontier is not null) { yield return frontier; blocking = frontier.Result; }
        }
        search.Result = new RouteSearchResult(best, best is not null ? null : ClassifyFailure(location, start, targetLocation, queue.Count > 0,
            unavailable, exitFailures, arrivalFailures, firstLockReason, target, destinationFailures, blocking));
    }

    private static int WalkingTiles(PathResult path, TileCoordinate start) =>
        path.Steps.Count == 1 && path.Steps[0].Tile == start ? 0 : path.Steps.Count;

    private RouteFailure ClassifyFailure(string location, TileCoordinate start, string targetLocation,
        bool searchLimitExceeded, string? unavailable,
        List<EdgeAttempt> exitFailures, List<EdgeAttempt> arrivalFailures, string? firstLockReason,
        TileCoordinate target, List<(string Location, TileCoordinate Entrance)> destinationFailures,
        List<Dictionary<string, object>> blocking)
    {
        if (destinationFailures.Count > 0)
        {
            // Reaching the requested map disproves a failure to leave the source.
            // Unrelated blocked side exits must not overwrite this evidence.
            var reached = destinationFailures[0];
            return new RouteFailure("DESTINATION_PATH_BLOCKED",
                $"The entrance to '{reached.Location}' at {reached.Entrance} is reachable, but the requested destination {target} is not. Choose that observed entry waypoint to enter the map, then inspect/clear the blocked path or choose another destination.",
                new Dictionary<string, object>
                {
                    ["side"] = "destination-path",
                    ["targetLocation"] = reached.Location,
                    ["targetTile"] = new Dictionary<string, object> { ["x"] = target.X, ["y"] = target.Y },
                    ["reachableEntryTile"] = new Dictionary<string, object> { ["x"] = reached.Entrance.X, ["y"] = reached.Entrance.Y },
                    ["blockingTiles"] = blocking,
                });
        }
        if (unavailable is not null)
        {
            // A reachable entrance into the destination exists but the destination side
            // is locked (event/cutscene/hours): this is the most specific explanation.
            return new RouteFailure("TARGET_UNAVAILABLE", unavailable, new Dictionary<string, object>
            {
                ["side"] = "destination",
                ["targetLocation"] = targetLocation,
                ["lockReason"] = unavailable,
            });
        }

        if (searchLimitExceeded)
        {
            return new RouteFailure("SEARCH_LIMIT_EXCEEDED",
                $"Route search from '{location}' to '{targetLocation}' exceeded the exploration limit ({MaxExploredEntrances} entrances); retry later or route through a nearer intermediate map.",
                new Dictionary<string, object>
                {
                    ["side"] = "search",
                    ["exploredEntrances"] = MaxExploredEntrances,
                });
        }

        var originOutgoing = _graph.GetOutgoingEdges(location);
        if (originOutgoing.Count == 0)
        {
            return new RouteFailure("NO_EXIT",
                $"No usable exit found from '{location}': the starting map has no warp or door leading toward '{targetLocation}'.",
                new Dictionary<string, object>
                {
                    ["side"] = "exit-side",
                    ["sourceLocation"] = location,
                    ["sourceTile"] = new Dictionary<string, object> { ["x"] = start.X, ["y"] = start.Y },
                });
        }

        // A walkable exit whose landing pad is unstandable points at the arrival side;
        // an exit whose path cannot be walked at all points at the exit side.
        if (arrivalFailures.Count > 0)
        {
            var attempt = arrivalFailures[0];
            var candidates = new List<Dictionary<string, object>>();
            foreach (var candidate in ArrivalCandidates(attempt.Edge.TargetTile))
                candidates.Add(new Dictionary<string, object> { ["x"] = candidate.X, ["y"] = candidate.Y });
            return new RouteFailure("ARRIVAL_BLOCKED",
                $"Arrival blocked into '{attempt.Edge.TargetLocation}': the landing tile {attempt.Edge.TargetTile} and its neighbors are not standable.",
                new Dictionary<string, object>
                {
                    ["side"] = "arrival-side",
                    ["edge"] = DescribeEdge(attempt.Edge),
                    ["arrivalTile"] = new Dictionary<string, object> { ["x"] = attempt.Edge.TargetTile.X, ["y"] = attempt.Edge.TargetTile.Y },
                    ["candidatesChecked"] = candidates,
                });
        }

        if (exitFailures.Count > 0)
        {
            var attempt = exitFailures[0];
            var details = new Dictionary<string, object>
            {
                ["side"] = "exit-side",
                ["edge"] = DescribeEdge(attempt.Edge),
                ["exitTile"] = new Dictionary<string, object> { ["x"] = attempt.Edge.SourceTile.X, ["y"] = attempt.Edge.SourceTile.Y },
            };
            if (blocking.Count > 0) details["blockingTiles"] = blocking;
            return new RouteFailure("EXIT_PATH_BLOCKED",
                $"Exit path blocked on '{attempt.FromLocation}': no passable path to the '{attempt.Edge.TargetLocation}' exit at tile {attempt.Edge.SourceTile}.",
                details);
        }

        var detailsNoRoute = new Dictionary<string, object>
        {
            ["side"] = "exit-side",
            ["sourceLocation"] = location,
        };
        if (firstLockReason is not null) detailsNoRoute["lockReason"] = firstLockReason;
        return new RouteFailure("NO_ROUTE",
            $"No route found with passable entrance paths from '{location}' to '{targetLocation}'.",
            detailsNoRoute);
    }

    private static Dictionary<string, object> DescribeEdge(MapEdge edge) => new()
    {
        ["sourceLocation"] = edge.SourceLocation,
        ["targetLocation"] = edge.TargetLocation,
        ["sourceTile"] = new Dictionary<string, object> { ["x"] = edge.SourceTile.X, ["y"] = edge.SourceTile.Y },
        ["targetTile"] = new Dictionary<string, object> { ["x"] = edge.TargetTile.X, ["y"] = edge.TargetTile.Y },
    };

    private static IEnumerable<TileCoordinate> ArrivalCandidates(TileCoordinate tile) =>
        new[] { tile }.Concat(tile.CardinalNeighbors());

    /// <summary>
    /// Bounded BFS over passable tiles from a pose; collects the non-passable tiles that
    /// stopped the expansion (weeds, stones, wood, debris — usually clearable), nearest
    /// first. Best-effort: an empty result just means no frontier detail is available.
    /// </summary>
    private NavigationSearch<List<Dictionary<string, object>>> BeginBlockingFrontier(string location, TileCoordinate start) =>
        new(search => SearchBlockingFrontier(search, location, start));

    private IEnumerable<NavigationSearch?> SearchBlockingFrontier(NavigationSearch<List<Dictionary<string, object>>> search,
        string location, TileCoordinate start)
    {
        search.Result = new();
        var blocked = new List<(TileCoordinate Tile, int Distance)>();
        var visited = new HashSet<TileCoordinate> { start };
        var frontier = new Queue<(TileCoordinate Tile, int Distance)>();
        frontier.Enqueue((start, 0));
        int explored = 0;
        while (frontier.Count > 0 && explored++ < BlockingFrontierSearchBudget)
        {
            var (current, distance) = frontier.Dequeue();
            bool failed = false;
            try
            {
                foreach (var neighbor in current.CardinalNeighbors())
                {
                    if (!visited.Add(neighbor)) continue;
                    if (_observer.IsTilePassable(location, neighbor)) frontier.Enqueue((neighbor, distance + 1));
                    else blocked.Add((neighbor, distance + 1));
                }
            }
            catch { failed = true; }
            if (failed) yield break;
            yield return null;
        }
        search.Result = blocked.OrderBy(b => b.Distance).ThenBy(b => b.Tile.Y).ThenBy(b => b.Tile.X)
            .Take(MaxBlockingTiles).Select(b => new Dictionary<string, object> { ["x"] = b.Tile.X, ["y"] = b.Tile.Y }).ToList();
    }

    public PathResult FindExitPath(string location, TileCoordinate start, MapEdge edge)
    {
        using var search = BeginExitPath(location, start, edge);
        return search.Complete();
    }

    public NavigationSearch<PathResult> BeginExitPath(string location, TileCoordinate start, MapEdge edge) =>
        new(search => SearchExitPath(search, location, start, edge));

    private IEnumerable<NavigationSearch?> SearchExitPath(NavigationSearch<PathResult> search, string location,
        TileCoordinate start, MapEdge edge)
    {
        var path = _observer.IsTilePassable(location, edge.SourceTile)
            ? _navigator.BeginPath(location, start, edge.SourceTile)
            : _navigator.BeginPathToInteract(new AuthoritativePose(location, start, FacingDirection.Down), location, edge.SourceTile);
        yield return path;
        search.Result = path.Result;
    }

    public bool TryArrival(MapEdge edge, out TileCoordinate tile)
    {
        tile = edge.TargetTile;
        foreach (var candidate in ArrivalCandidates(tile))
        {
            if (!_observer.IsTilePassable(edge.TargetLocation, candidate) || _observer.IsPlayerOnTile(edge.TargetLocation, candidate)) continue;
            tile = candidate;
            return true;
        }
        return false;
    }

    public bool IsTrigger(string location, TileCoordinate tile) =>
        _observer.IsWarpOrDoorTile(location, tile) || _graph.GetOutgoingEdges(location).Any(e => e.SourceTile == tile);

    public PathResult FindDestinationPath(string location, TileCoordinate start, TileCoordinate target, bool requireExactDestination = false)
    {
        using var search = BeginDestinationPath(location, start, target, requireExactDestination);
        return search.Complete();
    }

    public NavigationSearch<PathResult> BeginDestinationPath(string location, TileCoordinate start, TileCoordinate target,
        bool requireExactDestination = false) =>
        new(search => SearchDestinationPath(search, location, start, target, requireExactDestination));

    private IEnumerable<NavigationSearch?> SearchDestinationPath(NavigationSearch<PathResult> search, string location,
        TileCoordinate start, TileCoordinate target, bool exact)
    {
        if (!IsTrigger(location, target) && _observer.IsTilePassable(location, target))
        {
            var path = _navigator.BeginPath(location, start, target);
            yield return path;
            if (path.Result.Success) { search.Result = path.Result; yield break; }
        }
        if (exact)
        {
            search.Result = PathResult.Failed($"Requested exact standing tile {target} on '{location}' is unreachable; no adjacent substitute is authorized.");
            yield break;
        }
        if (start.IsAdjacentTo(target) && _observer.IsTilePassable(location, start) && !IsTrigger(location, start))
        {
            search.Result = PathResult.Completed(new[] { new PathStep(start, FacingDirection.Down) });
            yield break;
        }
        var nearby = _navigator.BeginNearest(location, start, target, 3, t => IsTrigger(location, t));
        yield return nearby;
        search.Result = nearby.Result?.Path ?? PathResult.Failed($"Destination {target} on '{location}' is unreachable.");
    }
}
