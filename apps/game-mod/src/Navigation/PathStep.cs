using StardewAI.Companion.Mod.Domain;

namespace StardewAI.Companion.Mod.Navigation;

/// <summary>
/// A single discrete step in a path on the same map.
/// </summary>
public sealed record PathStep(TileCoordinate Tile, FacingDirection Facing, TileCoordinate? From = null, TileCoordinate? RunEnd = null)
{
    public bool IsDiagonal => From is { } from && from.X != Tile.X && from.Y != Tile.Y;
}
