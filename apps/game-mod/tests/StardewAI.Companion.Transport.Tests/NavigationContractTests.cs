using System.Reflection;
using System.Text.Json;
using StardewAI.Companion.Mod.Transport;
using Xunit;

namespace StardewAI.Companion.Mod.Transport.Tests;

public sealed class NavigationContractTests
{
    [Fact]
    public void SharedTargetsAreValidatedBeforeNativeNavigationOrPreviewDispatch()
    {
        using var samples = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "navigation-goals.json")));
        var validate = typeof(WebSocketTransportServer).GetMethod("ValidateSkillBounds", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach (var sample in samples.RootElement.EnumerateArray())
        foreach (string skill in new[] { "navigate-to", "inspect-route" })
        {
            var parameters = sample.GetProperty("parameters").Deserialize<WaterZoneParameters>()!;
            var payload = new SkillExecutePayload("command", "task", skill, "0.1", 1, parameters, new(0, 50, 0), "safe-point", "policy");
            object?[] arguments = { payload, null };
            Assert.True((bool)validate.Invoke(null, arguments)! == sample.GetProperty("valid").GetBoolean(),
                $"{sample.GetProperty("name").GetString()} ({skill}): {arguments[1]}");
        }
    }
}
