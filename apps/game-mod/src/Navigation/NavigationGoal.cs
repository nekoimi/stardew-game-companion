using StardewAI.Companion.Mod.Domain;
using StardewAI.Companion.Mod.Observation;

namespace StardewAI.Companion.Mod.Navigation;

public sealed record NavigationGoal(string Location, TileCoordinate Reference,
    IReadOnlyDictionary<TileCoordinate, FacingDirection> StandingTiles, string Kind);

/// <summary>Read-only target binding. It never opens a shop, refills water, or changes maps.</summary>
internal static class NavigationGoals
{
    public static NavigationGoal? Resolve(string kind, string location, TileCoordinate tile, string? shop,
        IWorldObserver observer)
    {
        if (kind == "exact") return null;
        IReadOnlyList<TileCoordinate> targets;
        if (kind == "shop")
        {
            if (string.IsNullOrWhiteSpace(shop)) throw new ArgumentException("shopId is required for a shop destination.");
            var metadata = observer.GetShopActionMetadata(shop, location);
            if (!metadata.ActionFound || string.IsNullOrWhiteSpace(metadata.LocationName) || metadata.CounterTiles.Count == 0)
                throw new ArgumentException("The shop has no observed native counter or map.");
            location = metadata.LocationName!;
            targets = metadata.CounterTiles;
            tile = metadata.InteractionTile ?? targets[0];
        }
        else if (kind == "interact") targets = new[] { tile };
        else throw new ArgumentException("navigationTarget must be exact, interact or shop.");
        var standing = new Dictionary<TileCoordinate, FacingDirection>();
        foreach (var target in targets)
        foreach (var candidate in target.CardinalNeighbors())
            if (observer.IsTilePassable(location, candidate) && !observer.IsWarpOrDoorTile(location, candidate)
                && !observer.IsPlayerOnTile(location, candidate))
                standing.TryAdd(candidate, FacingDirectionExtensions.DirectionToAdjacent(candidate, target)!.Value);
        if (standing.Count == 0) throw new ArgumentException("The observed target has no available adjacent standing tile.");
        return new(location, tile, standing, kind);
    }
}
