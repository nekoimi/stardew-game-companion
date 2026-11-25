using System.Text.Json;
using System.Text.Json.Nodes;
using StardewAI.Companion.Mod.Observation;
using Xunit;

namespace StardewAI.Companion.Mod.Tests;

public sealed class PhaseMapObservationTests
{
    [Fact]
    public void PageUsesActualLargeMapBoundsAndPublishesTheSharedObservedContract()
    {
        string? repo = AppContext.BaseDirectory;
        while (repo is not null && !File.Exists(Path.Combine(repo, "Directory.Build.props"))) repo = Path.GetDirectoryName(repo);
        var sample = JsonNode.Parse(File.ReadAllText(Path.Combine(repo!, "contracts/game-bridge/examples/phase-map-page.json")))!;
        var visited = new HashSet<(int, int)>();
        var page = PhaseMapPage.Capture("Farm", 600, 2, 1024, 512, "fixture-map-v1", 7,
            tile => { visited.Add((tile.X, tile.Y)); return tile.X == 599 && tile.Y == 1
                ? new() { ["tile"] = new { x = 599, y = 1 }, ["kind"] = "tree", ["growthStage"] = 5, ["tapped"] = false } : null; },
            tile => tile.X == 590 && tile.Y == 1, _ => null);
        Assert.Equal(176, visited.Count);
        Assert.Contains((599, 1), visited);
        Assert.Equal(sample.ToJsonString(), JsonSerializer.SerializeToNode(page)!.ToJsonString());
        Assert.Throws<InvalidOperationException>(() => PhaseMapPage.Capture("Farm", 600, 2, 0, 512, "v1", 1,
            _ => throw new InvalidOperationException("Native read failed"), _ => false, _ => null));
        Assert.Throws<ArgumentException>(() => PhaseMapPage.Capture("Farm", 600, 2, 1201, 512, "v1", 1, _ => null, _ => false, _ => null));
    }
}
