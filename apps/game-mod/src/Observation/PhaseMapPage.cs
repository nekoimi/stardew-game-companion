using StardewAI.Companion.Mod.Domain;

namespace StardewAI.Companion.Mod.Observation;

/// <summary>Bounded row-major observation. No fixed radius or implicit empty success.</summary>
public static class PhaseMapPage
{
    public static Dictionary<string, object> Capture(string location, int width, int height, int offset, int limit,
        string fingerprint, int revision, Func<TileCoordinate, Dictionary<string, object>?> tree,
        Func<TileCoordinate, bool> water, Func<TileCoordinate, Dictionary<string, object>?> fixture)
    {
        long size = (long)width * height;
        if (width < 1 || height < 1 || size > int.MaxValue || offset < 0 || offset > size || limit < 0 || limit > 512)
            throw new ArgumentException("Invalid phase map page range.");
        var trees = new List<object>(); var waters = new List<object>(); var fixtures = new List<object>();
        int end = (int)Math.Min(size, (long)offset + limit);
        for (int index = offset; index < end; index++)
        {
            var tile = new TileCoordinate(index % width, index / width);
            if (tree(tile) is { } t) trees.Add(t);
            if (water(tile)) waters.Add(new { x = tile.X, y = tile.Y });
            if (fixture(tile) is { } f) fixtures.Add(f);
        }
        return new() { ["locationId"] = location, ["width"] = width, ["height"] = height,
            ["offset"] = offset, ["end"] = end, ["nextOffset"] = end < size ? end : -1,
            ["complete"] = end == size, ["fingerprint"] = fingerprint, ["capturedRevision"] = revision,
            ["trees"] = trees, ["waterTiles"] = waters, ["fixtures"] = fixtures, ["scannedTiles"] = end - offset };
    }
}
