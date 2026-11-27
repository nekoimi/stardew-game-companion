using StardewValley;
using StardewAI.Companion.Mod.Domain;
using StardewAI.Companion.Mod.Observation;

namespace StardewAI.Companion.Mod.Navigation;

public sealed record RouteSearchResult(ReachableRoute? Route, RouteFailure? Failure);

public sealed record ReachableRoute(
    IReadOnlyList<MapEdge> Edges,
    int WalkingTiles,
    int WalkingCost,
    TileCoordinate TargetTile)
{
    public IReadOnlyList<PathResult> Paths { get; init; } = Array.Empty<PathResult>();
    public IReadOnlyList<TileCoordinate> Origins { get; init; } = Array.Empty<TileCoordinate>();
}

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
    public string SearchActivity { get; private set; } = "idle";
    public int ExploredEntrances { get; private set; }
    public int PathSearches { get; private set; }
    public List<object> ExitDiagnostics { get; } = new();
    public NavigationSearch<RouteSearchResult> BeginJourney(string location, TileCoordinate start, string targetLocation,
        TileCoordinate target, int time, Farmer? farmer, bool exact, NavigationGoal? goal, IReadOnlySet<MapEdge> avoid) =>
        new(search => SearchJourney(search, location, start, targetLocation, target, time, farmer, exact, goal, avoid));

    private IEnumerable<NavigationSearch?> SearchJourney(NavigationSearch<RouteSearchResult> search, string location,
        TileCoordinate start, string targetLocation, TileCoordinate target, int time, Farmer? farmer, bool exact,
        NavigationGoal? goal, IReadOnlySet<MapEdge> avoid)
    {
        // Same-map requests retain exact native geometry and leave/re-enter detours.
        if (string.Equals(location, targetLocation, StringComparison.OrdinalIgnoreCase))
        {
            var local = BeginFind(location, start, targetLocation, target, time, farmer, exact, goal, avoid);
            yield return local; search.Result = local.Result; yield break;
        }
        SearchActivity = "topology"; ExploredEntrances = 0; PathSearches = 0; ExitDiagnostics.Clear();
        var topology = new RouteTopology(targetLocation, target, exact, goal?.StandingTiles.Keys.ToArray());
        foreach (var step in topology.Capture(_graph, location)) yield return step;
        if (topology.Outgoing(location).Count == 0)
        {
            search.Result = new(null, new("NO_EXIT", $"No usable exit found from '{location}'.")); yield break;
        }
        var excluded = new HashSet<MapEdge>(avoid);
        RouteFailure? failure = null;
        // Repeated exits in a boundary strip may be unreachable together. Bound
        // candidate recovery; every search still yields and remains cancellable.
        for (int attempt = 0; attempt < 12; attempt++)
        {
            var origin = Key(location, start);
            var queue = new PriorityQueue<(Entrance State, string Name, int Cost), int>();
            var costs = new Dictionary<Entrance, int> { [origin] = 0 };
            var previous = new Dictionary<Entrance, (Entrance From, MapEdge Edge)>();
            queue.Enqueue((origin, location, 0), 0);
            List<MapEdge>? route = null;
            while (queue.TryDequeue(out var node, out _) && ExploredEntrances < 1024)
            {
                yield return null;
                if (node.Cost != costs[node.State]) continue;
                ExploredEntrances++;
                if (string.Equals(node.Name, targetLocation, StringComparison.OrdinalIgnoreCase))
                {
                    route = new(); var state = node.State;
                    while (previous.TryGetValue(state, out var back)) { route.Add(back.Edge); state = back.From; }
                    route.Reverse(); break;
                }
                foreach (var edge in topology.Outgoing(node.Name))
                {
                    yield return null;
                    if (excluded.Contains(edge)) continue;
                    if (!_graph.CheckEdgeTraversable(edge, time, farmer, out var reason, out var locks))
                    {
                        if (edge.TargetLocation.Equals(targetLocation, StringComparison.OrdinalIgnoreCase))
                            failure = new("TARGET_UNAVAILABLE", reason ?? "Native entrance is locked.", locks);
                        continue;
                    }
                    var next = Key(edge.TargetLocation, edge.TargetTile);
                    int cost = node.Cost + Math.Max(0, WalkingPath.Distance(node.State.Tile, edge.SourceTile) - WalkingPath.CardinalCost)
                        + WalkingPath.CardinalCost;
                    if (costs.TryGetValue(next, out int old) && old <= cost) continue;
                    int bound = topology.Bound(edge.TargetLocation, edge.TargetTile);
                    if (bound == int.MaxValue) continue;
                    costs[next] = cost; previous[next] = (node.State, edge);
                    queue.Enqueue((next, edge.TargetLocation, cost), cost + bound);
                }
            }
            if (route is not { Count: > 0 }) break;
            // A short, bounded lookahead can reject a known sealed destination
            // entrance without exhaustively planning all remote maps up front.
            var last = route[^1];
            using (var finalProbe = BeginDestinationPath(targetLocation, last.TargetTile, target, exact, goal))
            {
                for (int slice = 0; slice < 16 && !finalProbe.IsComplete; slice++)
                { finalProbe.Advance(32, 0.5); yield return null; }
                if (finalProbe.IsComplete && !finalProbe.Result.Success)
                { excluded.Add(last); failure = new("DESTINATION_PATH_BLOCKED", finalProbe.Result.ErrorMessage ?? "Destination entrance blocked."); continue; }
            }
            var first = route[0];
            SearchActivity = $"exit:{location}:{start}->{first.SourceTile}:{first.TargetLocation}"; PathSearches++;
            var path = BeginExitPath(location, start, first); yield return path;
            ExitDiagnostics.Add(new { location, start, exit = first.SourceTile, target = first.TargetLocation,
                success = path.Result.Success, error = path.Result.ErrorMessage,
                candidates = first.SourceTile.CardinalNeighbors().Select(tile => new { tile,
                    passable = _observer.IsTilePassable(location, tile), trigger = IsTrigger(location, tile) }).ToArray() });
            if (path.Result.Success)
            {
                int estimate = path.Result.WalkingCost;
                var position = first.TargetTile;
                foreach (var edge in route.Skip(1))
                { estimate += WalkingPath.Distance(position, edge.SourceTile) + WalkingPath.CardinalCost; position = edge.TargetTile; }
                estimate += WalkingPath.Distance(position, target);
                search.Result = new(new ReachableRoute(route, estimate / WalkingPath.CardinalCost, estimate, target)
                    { Paths = new[] { path.Result }, Origins = new[] { start } }, null);
                yield break;
            }
            excluded.Add(first);
            failure = new("EXIT_PATH_BLOCKED", path.Result.ErrorMessage ?? "The current exit is unreachable.",
                new() { ["side"] = "exit-side", ["edge"] = DescribeEdge(first) });
        }
        search.Result = new(null, failure ?? new("NO_ROUTE", "No available map entrance route leads to the requested destination."));
    }

    public ReachableRoutePlanner(IWorldObserver observer, SameMapNavigator navigator, IWorldMapGraph graph)
    {
        _observer = observer;
        _navigator = navigator;
        _graph = graph;
    }

    private readonly record struct Entrance(string Location, TileCoordinate Tile);
    private static Entrance Key(string location, TileCoordinate tile) => new(location.ToUpperInvariant(), tile);

    private sealed record EdgeAttempt(MapEdge Edge, string FromLocation, TileCoordinate FromTile);
    private sealed record SearchNode(Entrance State, string Name, int Cost, MapEdge? Pending = null);

    public ReachableRoute? Find(string location, TileCoordinate start, string targetLocation,
        TileCoordinate target, int time, Farmer? farmer, out RouteFailure? failure, bool requireExactDestination = false)
    {
        using var search = BeginFind(location, start, targetLocation, target, time, farmer, requireExactDestination);
        var result = search.Complete();
        failure = result.Failure;
        return result.Route;
    }

    public NavigationSearch<RouteSearchResult> BeginFind(string location, TileCoordinate start, string targetLocation,
        TileCoordinate target, int time, Farmer? farmer, bool requireExactDestination = false, NavigationGoal? goal = null,
        IReadOnlySet<MapEdge>? avoidEntrances = null) =>
        new(search => Search(search, location, start, targetLocation, target, time, farmer, requireExactDestination, goal, avoidEntrances));

    private IEnumerable<NavigationSearch?> Search(NavigationSearch<RouteSearchResult> search, string location,
        TileCoordinate start, string targetLocation, TileCoordinate target, int time, Farmer? farmer, bool requireExactDestination,
        NavigationGoal? goal, IReadOnlySet<MapEdge>? avoidEntrances)
    {
        if (goal is null && requireExactDestination && (IsTrigger(targetLocation, target) || !_observer.IsTilePassable(targetLocation, target)))
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
        SearchActivity = "topology"; ExploredEntrances = 0; PathSearches = 0;
        var origin = Key(location, start);
        var topology = new RouteTopology(targetLocation, target, requireExactDestination, goal?.StandingTiles.Keys.ToArray());
        foreach (var step in topology.Capture(_graph, location)) yield return step;
        var distances = new Dictionary<Entrance, (int Cost, int Tiles)> { [origin] = (0, 0) };
        var previous = new Dictionary<Entrance, (Entrance From, MapEdge Edge, PathResult Path)>();
        var queue = new PriorityQueue<SearchNode, int>();
        queue.Enqueue(new(origin, location, 0), 0);
        ReachableRoute? best = null;
        string? unavailable = null;
        int explored = 0;
        // Failure evidence gathered while searching: which side of which boundary broke.
        var exitFailures = new List<EdgeAttempt>();
        var arrivalFailures = new List<EdgeAttempt>();
        var destinationFailures = new List<(string Location, TileCoordinate Entrance)>();
        string? firstLockReason = null;
        while (queue.TryDequeue(out var node, out int estimate) && explored < MaxExploredEntrances)
        {
            yield return null;
            int cost = node.Cost;
            var reached = distances[node.State];
            if (cost != reached.Cost) continue;
            if (best is not null && estimate >= best.WalkingCost) break;
            if (node.Pending is { } pending)
            {
                if (!_graph.CheckEdgeTraversable(pending, time, farmer, out var blocked, out _))
                {
                    if (string.Equals(pending.TargetLocation, targetLocation, StringComparison.OrdinalIgnoreCase))
                    {
                        var lockedExit = BeginExitPath(node.Name, node.State.Tile, pending);
                        yield return lockedExit;
                        if (lockedExit.Result.Success) unavailable ??= blocked;
                    }
                    firstLockReason ??= blocked;
                    continue;
                }
                SearchActivity = $"exit:{node.Name}:{node.State.Tile}->{pending.SourceTile}:{pending.TargetLocation}";
                PathSearches++;
                var walkSearch = BeginExitPath(node.Name, node.State.Tile, pending);
                yield return walkSearch;
                var walk = walkSearch.Result;
                if (!walk.Success) { exitFailures.Add(new(pending, node.Name, node.State.Tile)); continue; }
                if (!TryArrival(pending, out var arrival)) { arrivalFailures.Add(new(pending, node.Name, node.State.Tile)); continue; }
                int nextCost = cost + walk.WalkingCost + WalkingPath.CardinalCost;
                var next = Key(pending.TargetLocation, arrival);
                if (distances.TryGetValue(next, out var old) && old.Cost <= nextCost) continue;
                distances[next] = (nextCost, reached.Tiles + CountWalkingTiles(walk, node.State.Tile) + 1);
                previous[next] = (node.State, pending, walk);
                int bound = topology.Bound(pending.TargetLocation, arrival);
                if (bound != int.MaxValue) queue.Enqueue(new(next, pending.TargetLocation, nextCost), nextCost + bound);
                continue;
            }
            explored++; ExploredEntrances = explored;
            if (string.Equals(node.Name, targetLocation, StringComparison.OrdinalIgnoreCase))
            {
                SearchActivity = $"destination:{node.Name}:{node.State.Tile}->{target}"; PathSearches++;
                var finalSearch = BeginDestinationPath(node.Name, node.State.Tile, target, requireExactDestination, goal);
                yield return finalSearch;
                var final = finalSearch.Result;
                if (final.Success)
                {
                    int total = cost + final.WalkingCost;
                    if (best is null || total < best.WalkingCost)
                    {
                        var edges = new List<MapEdge>();
                        var paths = new List<PathResult>();
                        var origins = new List<TileCoordinate>();
                        var state = node.State;
                        while (previous.TryGetValue(state, out var back))
                        {
                            edges.Add(back.Edge);
                            paths.Add(back.Path);
                            origins.Add(back.From.Tile);
                            state = back.From;
                        }
                        edges.Reverse();
                        paths.Reverse(); origins.Reverse(); paths.Add(final); origins.Add(node.State.Tile);
                        best = new ReachableRoute(edges, reached.Tiles + CountWalkingTiles(final, node.State.Tile),
                            total, final.Steps[^1].Tile) { Paths = paths, Origins = origins };
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
            foreach (var edge in topology.Outgoing(node.Name))
            {
                yield return null;
                if (avoidEntrances?.Contains(edge) == true) continue;
                int bound = topology.ViaBound(node.State.Tile, edge);
                if (bound == int.MaxValue || (best is not null && cost + bound >= best.WalkingCost)) continue;
                queue.Enqueue(node with { Pending = edge }, cost + bound);
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

    private static int CountWalkingTiles(PathResult path, TileCoordinate start) =>
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
        if (_observer.IsWarpOrDoorTile(location, edge.SourceTile))
        {
            var approaches = new Dictionary<TileCoordinate, FacingDirection>();
            foreach (var neighbor in edge.SourceTile.CardinalNeighbors())
            {
                int dx = neighbor.X - edge.SourceTile.X, dy = neighbor.Y - edge.SourceTile.Y;
                // Boundary warps are often outside the map while TouchAction
                // occupies the entire last column. Stop before that trigger band;
                // entering it belongs to native transition execution, not standing.
                for (int distance = 1; distance <= 3; distance++)
                {
                    yield return null;
                    var tile = new TileCoordinate(edge.SourceTile.X + dx * distance, edge.SourceTile.Y + dy * distance);
                    if (_observer.IsWarpOrDoorTile(location, tile)) continue;
                    if (_observer.IsTilePassable(location, tile) && !_observer.IsPlayerOnTile(location, tile))
                        approaches[tile] = dx == 1 ? FacingDirection.Left : dx == -1 ? FacingDirection.Right
                            : dy == 1 ? FacingDirection.Up : FacingDirection.Down;
                    break;
                }
            }
            var approach = _navigator.BeginPathToAny(location, start, approaches);
            yield return approach; search.Result = approach.Result; yield break;
        }
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
        bool requireExactDestination = false, NavigationGoal? goal = null) => goal is not null
        ? _navigator.BeginPathToAny(location, start, goal.StandingTiles)
        : new(search => SearchDestinationPath(search, location, start, target, requireExactDestination));

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
