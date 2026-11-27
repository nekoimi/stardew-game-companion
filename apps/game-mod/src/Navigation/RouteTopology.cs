using StardewAI.Companion.Mod.Domain;

namespace StardewAI.Companion.Mod.Navigation;

/// <summary>Collision-free lower bounds, never evidence that a native route is open.
/// Captured once per search so graph discovery is not repeated for every entrance.</summary>
internal sealed class RouteTopology
{
    private readonly Dictionary<string, IReadOnlyList<MapEdge>> _outgoing = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<MapEdge, int> _remaining = new();
    private readonly string _targetLocation;
    private readonly TileCoordinate _target;
    private readonly bool _exact;
    private readonly IReadOnlyCollection<TileCoordinate>? _goals;
    private bool _complete = true;

    public RouteTopology(string targetLocation, TileCoordinate target, bool exact, IReadOnlyCollection<TileCoordinate>? goals = null)
    { _targetLocation = targetLocation; _target = target; _exact = exact; _goals = goals; }

    public IEnumerable<NavigationSearch?> Capture(IWorldMapGraph graph, string origin)
    {
        var locations = new Queue<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { origin };
        locations.Enqueue(origin);
        while (locations.TryDequeue(out var location))
        {
            yield return null;
            var edges = graph.GetOutgoingEdges(location);
            _outgoing[location] = edges;
            foreach (var edge in edges)
            {
                if (!seen.Add(edge.TargetLocation)) continue;
                if (seen.Count > 512) { _complete = false; continue; }
                locations.Enqueue(edge.TargetLocation);
            }
        }
        var incoming = _outgoing.Values.SelectMany(edges => edges).Distinct()
            .GroupBy(edge => edge.TargetLocation, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var queue = new PriorityQueue<MapEdge, int>();
        if (incoming.TryGetValue(_targetLocation, out var finalEdges))
            foreach (var edge in finalEdges)
            {
                int cost = FinalBound(edge.TargetTile, landingSlack: true);
                _remaining[edge] = cost;
                queue.Enqueue(edge, cost);
            }
        while (queue.TryDequeue(out var next, out int cost))
        {
            yield return null;
            if (_remaining[next] != cost || !incoming.TryGetValue(next.SourceLocation, out var previous)) continue;
            foreach (var edge in previous)
            {
                yield return null;
                // A landing may settle one cardinal tile from its declared pose;
                // an exit can be activated from a cardinal approach tile.
                int candidate = cost + Math.Max(0, WalkingPath.Distance(edge.TargetTile, next.SourceTile)
                    - 4 * WalkingPath.CardinalCost) + WalkingPath.CardinalCost;
                if (_remaining.TryGetValue(edge, out int old) && old <= candidate) continue;
                _remaining[edge] = candidate;
                queue.Enqueue(edge, candidate);
            }
        }
    }

    public IReadOnlyList<MapEdge> Outgoing(string location) =>
        _outgoing.TryGetValue(location, out var edges) ? edges : Array.Empty<MapEdge>();

    public int Bound(string location, TileCoordinate tile)
    {
        if (!_complete) return 0;
        int best = string.Equals(location, _targetLocation, StringComparison.OrdinalIgnoreCase)
            ? FinalBound(tile, landingSlack: false) : int.MaxValue;
        foreach (var edge in Outgoing(location))
        {
            if (!_remaining.TryGetValue(edge, out int remaining)) continue;
            int cost = Math.Max(0, WalkingPath.Distance(tile, edge.SourceTile) - 3 * WalkingPath.CardinalCost)
                + WalkingPath.CardinalCost + remaining;
            best = Math.Min(best, cost);
        }
        return best;
    }

    public int ViaBound(TileCoordinate from, MapEdge edge)
    {
        if (!_complete) return 0;
        return _remaining.TryGetValue(edge, out int remaining)
            ? Math.Max(0, WalkingPath.Distance(from, edge.SourceTile) - 3 * WalkingPath.CardinalCost)
                + WalkingPath.CardinalCost + remaining : int.MaxValue;
    }

    private int FinalBound(TileCoordinate from, bool landingSlack) => Math.Max(0,
        (_goals is { Count: > 0 } ? _goals.Min(goal => WalkingPath.Distance(from, goal)) : WalkingPath.Distance(from, _target)) - (landingSlack ? WalkingPath.CardinalCost : 0)
        - (_exact ? 0 : 5 * WalkingPath.CardinalCost));
}
