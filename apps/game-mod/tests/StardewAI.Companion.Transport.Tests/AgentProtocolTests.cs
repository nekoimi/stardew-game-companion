using System.IO;
using System.Text.Json;
using StardewAI.Companion.Mod.Transport;
using Xunit;

namespace StardewAI.Companion.Transport.Tests;

public sealed class AgentProtocolTests
{
    [Fact]
    public void ClassifiesSharedActionLifecycleCases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "action-lifecycle.json");
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        foreach (var sample in json.RootElement.EnumerateArray())
        {
            var message = sample.GetProperty("message");
            var payload = message.GetProperty("payload");
            var settled = sample.GetProperty("settledActionIds").EnumerateArray()
                .Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
            var actual = AgentProtocol.ClassifyReceipt(Read(message, "protocolVersion")!, Read(message, "messageType")!,
                Read(message, "saveId"), Read(message, "gameSessionId"), Read(payload, "commandId"),
                Read(payload, "terminalState"), "farm", Read(sample, "gameSessionId")!,
                Read(sample, "expectedActionId"), settled);
            Assert.True(actual == Read(sample, "expected"), $"{Read(sample, "name")}: {actual}");
        }
    }

    private static string? Read(JsonElement json, string field) =>
        json.TryGetProperty(field, out var value) ? value.GetString() : null;
}
