using System.Text.Json;
using System.Text.Json.Nodes;
using StardewAI.Companion.Mod.Domain;
using StardewAI.Companion.Mod.Execution;
using StardewAI.Companion.Mod.Navigation;
using StardewAI.Companion.Mod.Observation;
using StardewAI.Companion.Mod.Transport;
using Xunit;

namespace StardewAI.Companion.Mod.Tests;

public sealed class BatchedFarmContractTests
{
    [Fact]
    public void RegionInspectionPublishesTheSharedRuntimeContractAndRejectsFailedScans()
    {
        string? repo = AppContext.BaseDirectory;
        while (repo is not null && !File.Exists(Path.Combine(repo, "Directory.Build.props")))
            repo = Path.GetDirectoryName(repo.TrimEnd(Path.DirectorySeparatorChar));
        var sample = JsonNode.Parse(File.ReadAllText(Path.Combine(repo!, "contracts", "game-bridge", "examples", "batched-farm-work.json")))!;
        var parameters = sample["parameters"]!.Deserialize<WaterZoneParameters>()!;
        var observer = new SimulatedWorldObserver();
        observer.SetDirt(new(2, 3), TileDirtState.DryDirt(true, "(O)472"));
        observer.SetDirt(new(3, 3), TileDirtState.WateredDirt(true, "(O)24") with { IsHarvestable = true });
        observer.SetDirt(new(20, 20), TileDirtState.DryDirt(true));
        var actor = new MechanicsActor("farm-contract");
        var coordinator = new CompanionMechanicsCoordinator(actor, observer,
            new SameMapNavigator(observer), new TestWateringCanAdapter(observer));
        var request = new SkillExecutePayload("inspect", "batch", "inspect-farm-work", "0.1", observer.WorldRevision,
            parameters, new ExecutionBudgets(60, 50, 20), "safe-point", "test");
        var envelope = new EnvelopeDto("0.1", "skill.execute", "inspect-message", "runtime-test", 1,
            observer.WorldRevision, DateTimeOffset.UtcNow, new JsonObject(), IdempotencyKey: "inspect-once");
        Assert.True(coordinator.TryAcceptSkillExecute(request, envelope, out _));
        var farm = JsonSerializer.SerializeToNode(coordinator.LastSkillResultPayload!.Details!["farmWork"])!;
        // One immutable example is read by both languages; native observation must
        // produce it, including the requested region and the explicit completeness.
        Assert.Equal(sample["farmWork"]!.ToJsonString(), farm.ToJsonString());
        observer.FarmWorkScanFailure = new InvalidOperationException("Scan unavailable");
        Assert.False(coordinator.TryAcceptSkillExecute(request with { CommandId = "failed-scan" }, envelope, out var rejected));
        Assert.Equal("FARM_WORK_OBSERVATION_UNAVAILABLE", rejected!.Error!.Code);
        Assert.Null(coordinator.ActiveMachine);
    }
}
