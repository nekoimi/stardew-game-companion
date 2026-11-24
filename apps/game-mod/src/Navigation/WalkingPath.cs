using Microsoft.Xna.Framework;
using StardewAI.Companion.Mod.Domain;
using StardewAI.Companion.Mod.Observation;

namespace StardewAI.Companion.Mod.Navigation;

internal static class WalkingPath
{
    // Native diagonal input advances each axis at 0.707 of cardinal speed.
    internal const int CardinalCost = 100;
    private const int DiagonalCost = 141;

    public static int Distance(TileCoordinate from, TileCoordinate to)
    {
        int x = Math.Abs(to.X - from.X);
        int y = Math.Abs(to.Y - from.Y);
        return Math.Min(x, y) * DiagonalCost + Math.Abs(x - y) * CardinalCost;
    }

    public static (TileCoordinate Horizontal, TileCoordinate Vertical) DiagonalSides(TileCoordinate from, TileCoordinate to)
    {
        return (new(to.X, from.Y), new(from.X, to.Y));
    }

    public static bool IsStepOpen(IWorldObserver observer, IFarmerActor actor, string location,
        PathStep step, IReadOnlyCollection<TileCoordinate>? avoid = null)
    {
        if (!string.Equals(location, actor.LocationName, StringComparison.OrdinalIgnoreCase)) return false;
        bool Open(TileCoordinate tile) => tile == actor.Tile || (avoid?.Contains(tile) != true
            && !observer.IsPlayerOnTile(location, tile) && observer.IsTilePassable(location, tile));
        if (!Open(step.Tile)) return false;
        if (step.From is not { } from) return true;
        if (actor.Tile == from && !observer.CanWalkBetween(location, from, step.Tile)) return false;
        if (!step.IsDiagonal) return true;

        var (horizontal, vertical) = DiagonalSides(from, step.Tile);
        return Open(horizontal) && Open(vertical)
            && !observer.IsWarpOrDoorTile(location, horizontal)
            && !observer.IsWarpOrDoorTile(location, vertical);
    }

    public static bool Move(IFarmerActor actor, PathStep step, float pixelsPerTick)
    {
        bool onRun = step.From is not null && IsOnRun(actor, step);
        var target = onRun && step.RunEnd is { } end ? end : step.Tile;
        var delta = ActorGuards.WalkingTargetPixel(actor, target) - actor.PixelPosition;
        if (ActorGuards.IsPlayerBound(actor))
        {
            MovePlayer(actor, step, delta);
            return false;
        }

        if (step.From is { } entry && step.IsDiagonal && actor.Tile == entry && !onRun)
        {
            MoveCompanion(actor, ActorGuards.WalkingTargetPixel(actor, entry) - actor.PixelPosition, pixelsPerTick);
            return false;
        }

        bool arrived = MoveCompanion(actor, delta, pixelsPerTick);
        return arrived && target == step.Tile;
    }

    private static bool MoveCompanion(IFarmerActor actor, Vector2 delta, float pixelsPerTick)
    {
        float length = delta.Length();
        if (length <= pixelsPerTick)
            return ActorGuards.TryFinishWalkingStep(actor, actor.PixelPosition + delta);

        delta *= pixelsPerTick / length;
        actor.MovePixels(delta.X, delta.Y);
        return false;
    }

    private static void MovePlayer(IFarmerActor actor, PathStep step, Vector2 delta)
    {
        if (actor is not PlayerActor player || !step.IsDiagonal)
        {
            actor.MovePixels(delta.X, delta.Y);
            return;
        }
        var from = step.From!.Value;
        if (actor.Tile == from && Math.Abs(Math.Abs(delta.X) - Math.Abs(delta.Y)) > 8f)
        {
            var alignment = ActorGuards.WalkingTargetPixel(actor, from) - actor.PixelPosition;
            actor.MovePixels(alignment.X, alignment.Y);
            return;
        }
        player.MoveDiagonally(delta.X, delta.Y);
    }

    private static bool IsOnRun(IFarmerActor actor, PathStep step)
    {
        var from = step.From!.Value;
        var direction = new Vector2(step.Tile.X - from.X, step.Tile.Y - from.Y);
        var offset = actor.PixelPosition - ActorGuards.WalkingTargetPixel(actor, from);
        float tolerance = ActorGuards.IsPlayerBound(actor)
            ? ActorGuards.NativeArrivalTolerance * (step.IsDiagonal ? 2 : 1)
            : 0.1f;
        return Math.Abs(offset.X * direction.Y - offset.Y * direction.X) <= tolerance;
    }

    public static int AdvanceReachedWaypoint(IFarmerActor actor, IReadOnlyList<PathStep> path, int index)
    {
        if (index >= path.Count) return index;
        var step = path[index];
        if (ActorGuards.HasReachedWalkingTile(actor, step.Tile)) return index + 1;
        if (step.RunEnd is not { } end || !IsOnRun(actor, step)) return index;
        var direction = new Vector2(end.X - step.Tile.X, end.Y - step.Tile.Y);
        var offset = actor.PixelPosition - ActorGuards.WalkingTargetPixel(actor, step.Tile);
        if (Vector2.Dot(direction, offset) >= 0) return index + 1;
        return index;
    }
}
