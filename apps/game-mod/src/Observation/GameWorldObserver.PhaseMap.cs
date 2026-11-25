using System.Runtime.CompilerServices;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using StardewAI.Companion.Mod.Domain;

namespace StardewAI.Companion.Mod.Observation;

public sealed partial class GameWorldObserver
{
    private int _phaseMapGeneration;
    private readonly string _phaseMapSession = Guid.NewGuid().ToString("N");
    public void InvalidatePhaseMap() => _phaseMapGeneration++;

    public Dictionary<string, object> InspectPhaseMapPage(string locationName, int offset, int limit)
    {
        if (!IsMainThread) throw new InvalidOperationException("Phase map observation requires the game thread.");
        var location = Game1.getLocationFromName(locationName)
            ?? throw new InvalidOperationException("Phase map is not loaded.");
        var layer = location.Map?.Layers.FirstOrDefault() ?? throw new InvalidOperationException("Map dimensions are unavailable.");
        // Session salt forbids blindly trusting disk cache across a reload. Runtime
        // retains the disk entry as a hint, but must rebuild the observed world.
        string fingerprint = $"{_phaseMapSession}:{RuntimeHelpers.GetHashCode(location.Map)}:{_phaseMapGeneration}:{Game1.Date.TotalDays}:{layer.LayerWidth}:{layer.LayerHeight}";
        return PhaseMapPage.Capture(locationName, layer.LayerWidth, layer.LayerHeight, offset, limit, fingerprint, WorldRevision,
            tile => {
                if (!location.terrainFeatures.TryGetValue(new Vector2(tile.X, tile.Y), out var feature) || feature is not Tree tree) return null;
                return new() { ["tile"] = new { x = tile.X, y = tile.Y }, ["kind"] = tree.stump.Value ? "stump" : "tree",
                    ["growthStage"] = tree.growthStage.Value, ["tapped"] = tree.tapped.Value };
            }, tile => location.CanRefillWateringCanOnTile(tile.X, tile.Y)
                && tile.CardinalNeighbors().Any(standing => IsTilePassable(locationName, standing)),
            tile => {
                if (location.objects.TryGetValue(new Vector2(tile.X, tile.Y), out var item) && item is Chest chest && !chest.fridge.Value)
                    return new() { ["kind"] = "chest", ["tile"] = new { x = tile.X, y = tile.Y }, ["itemId"] = item.QualifiedItemId,
                        ["identity"] = RuntimeHelpers.GetHashCode(chest).ToString() };
                if (location.furniture.OfType<BedFurniture>().Any(bed => bed.TileLocation.X == tile.X && bed.TileLocation.Y == tile.Y))
                    return new() { ["kind"] = "bed", ["tile"] = new { x = tile.X, y = tile.Y } };
                if (location.warps.FirstOrDefault(warp => warp.X == tile.X && warp.Y == tile.Y) is { } exit)
                    return new() { ["kind"] = "exit", ["tile"] = new { x = tile.X, y = tile.Y }, ["targetLocation"] = exit.TargetName,
                        ["targetX"] = exit.TargetX, ["targetY"] = exit.TargetY };
                return null;
            });
    }
}
