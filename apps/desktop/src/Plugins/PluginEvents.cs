using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace StardewAI.Companion.Desktop.Plugins;

internal sealed record PluginGame(bool Connected, string? Date = null, int? TimeOfDay = null);
internal sealed record PluginAgent(string Phase, string Activity, string ControlOwner, string? Source = null);
internal sealed record PluginSnapshot(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? SessionId,
    string Cursor, PluginGame Game, PluginAgent Agent);
internal sealed record PluginFrame(long Id, string Topic, string Data);

internal sealed class PluginEvents
{
    private const int Capacity = 256;
    private readonly object _sync = new();
    private readonly Queue<PluginFrame> _replay = new();
    private readonly HashSet<Subscription> _subscriptions = [];
    private readonly HashSet<string> _seenMessages = new(StringComparer.Ordinal);
    private readonly Queue<string> _messageIds = new();
    private string? _session;
    private long _cursor;
    private long _floor;
    private PluginGame _game = new(false);
    private PluginAgent _agent = new("idle", "idle", "human");

    internal static readonly IReadOnlySet<string> Topics = new HashSet<string>(StringComparer.Ordinal)
    {
        "game.state", "agent.state", "agent.action.completed", "agent.reply.completed"
    };

    internal PluginSnapshot Snapshot()
    {
        lock (_sync) return new(_session, _cursor.ToString(CultureInfo.InvariantCulture), _game, _agent);
    }

    internal void Disconnect() => Apply("status", JsonSerializer.SerializeToElement(new { ready = false }));

    internal void Apply(string type, JsonElement payload)
    {
        lock (_sync)
        {
            var session = StringValue(payload, "gameSessionId");
            if (type == "status")
            {
                var nextSession = Flag(payload, "ready") ? session : null;
                if (nextSession != _session)
                {
                    _session = nextSession;
                    _floor = _cursor;
                    _replay.Clear();
                    _seenMessages.Clear();
                    _messageIds.Clear();
                    foreach (var stream in _subscriptions.ToArray()) Remove(stream);
                }
                var nextGame = new PluginGame(nextSession is not null, StringValue(payload, "gameDate"), IntValue(payload, "timeOfDay"));
                var nextAgent = new PluginAgent(Flag(payload, "paused") ? "paused"
                        : !string.IsNullOrEmpty(StringValue(payload, "activeCommandId")) ? "executing"
                        : Flag(payload, "thinking") ? "thinking" : "idle",
                    StringValue(payload, "activity") ?? "idle", StringValue(payload, "controlOwner") == "agent" ? "agent" : "human",
                    StringValue(payload, "pluginSource"));
                if (_game != nextGame) { _game = nextGame; Publish("game.state", nextGame); }
                if (_agent != nextAgent) { _agent = nextAgent; Publish("agent.state", nextAgent); }
                return;
            }
            if (session is null || session != _session) return;
            if (type == "result" && StringValue(payload, "commandId") is { Length: > 0 } actionId
                && StringValue(payload, "skillId") is { Length: > 0 } skillId
                && StringValue(payload, "terminalState") is { Length: > 0 } terminalState)
            {
                if (Remember("action:" + actionId))
                    Publish("agent.action.completed", new { actionId, skillId, terminalState, completedCount = IntValue(payload, "completedCount") });
            }
            if (type is "chat.reply" or "life.chat.reply" && !Flag(payload, "replyAlreadyDelivered")
                && StringValue(payload, "status") is "completed" or "public-message"
                && StringValue(payload, "requestId") is { Length: > 0 } messageId
                && StringValue(payload, "replyText") is { Length: > 0 } text && Remember("reply:" + messageId))
                Publish("agent.reply.completed", new { messageId, text, requestId = messageId });
        }
    }

    internal (Subscription? Stream, string? Error) Subscribe(PluginInstance instance, string? session, long cursor,
        IReadOnlySet<string> topics)
    {
        lock (_sync)
        {
            if (instance.BusinessStopped.IsCancellationRequested) return (null, "invalid_instance");
            if (session != _session) return (null, "session_changed");
            if (cursor < _floor || cursor > _cursor) return (null, "cursor_expired");
            if (_subscriptions.Count(stream => stream.Instance == instance.Id) >= 2) return (null, "limit_exceeded");
            var stream = new Subscription(this, instance.Id, topics);
            foreach (var frame in _replay)
                if (frame.Id > cursor && topics.Contains(frame.Topic)) stream.Frames.Writer.TryWrite(frame);
            _subscriptions.Add(stream);
            return (stream, null);
        }
    }

    internal void Close(string instance)
    {
        lock (_sync)
            foreach (var stream in _subscriptions.Where(stream => stream.Instance == instance).ToArray()) Remove(stream);
    }

    private void Publish<T>(string topic, T payload)
    {
        var frame = new PluginFrame(++_cursor, topic, JsonSerializer.Serialize(new EventData<T>(_session, payload), OutputJson));
        _replay.Enqueue(frame);
        if (_replay.Count > Capacity) _floor = _replay.Dequeue().Id;
        foreach (var stream in _subscriptions.ToArray())
            if (stream.Topics.Contains(topic) && !stream.Frames.Writer.TryWrite(frame)) Remove(stream);
    }

    private bool Remember(string id)
    {
        if (!_seenMessages.Add(id)) return false;
        _messageIds.Enqueue(id);
        if (_messageIds.Count > Capacity * 4) _seenMessages.Remove(_messageIds.Dequeue());
        return true;
    }

    private void Remove(Subscription stream)
    {
        if (!_subscriptions.Remove(stream)) return;
        stream.End.Cancel();
        stream.Frames.Writer.TryComplete();
    }

    private static string? StringValue(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int? IntValue(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    private static bool Flag(JsonElement obj, string name) => obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    internal static readonly JsonSerializerOptions OutputJson = new(PluginPackages.Json)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record EventData<T>(
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? SessionId, T Payload);

    internal sealed class Subscription(PluginEvents owner, string instance, IReadOnlySet<string> topics) : IDisposable
    {
        internal string Instance { get; } = instance;
        internal IReadOnlySet<string> Topics { get; } = topics;
        internal CancellationTokenSource End { get; } = new();
        internal Channel<PluginFrame> Frames { get; } = Channel.CreateBounded<PluginFrame>(Capacity);

        public void Dispose()
        {
            lock (owner._sync)
            {
                owner.Remove(this);
                End.Dispose();
            }
        }
    }
}
