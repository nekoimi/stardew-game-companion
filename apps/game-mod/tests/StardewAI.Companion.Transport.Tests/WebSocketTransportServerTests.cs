using System.Diagnostics;
using System.Collections.Concurrent;
using System.Reflection;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace StardewAI.Companion.Mod.Transport.Tests;

public class WebSocketTransportServerTests
{
    [Fact]
    public async Task AutomaticPortRetriesWhenAnotherListenerClaimsItsFirstCandidate()
    {
        using var server = new WebSocketTransportServer(0, "test-token", new TestTransportHandler(), "save", "session");
        var first = server.Port;
        using var competingListener = new HttpListener();
        competingListener.Prefixes.Add($"http://127.0.0.1:{first}/");
        competingListener.Start();
        server.Start();
        Assert.NotEqual(first, server.Port);
        using var client = new ClientWebSocket();
        client.Options.SetRequestHeader("Authorization", "Bearer test-token");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/chat"), timeout.Token);
        Assert.Equal(WebSocketState.Open, client.State);
    }

    [Fact]
    public async Task DesktopControlIsAcceptedOnlyForEnabledChatAndCurrentSaveSession()
    {
        using var server = new WebSocketTransportServer(0, "test-token", new TestTransportHandler(), "save", "session");
        var accepted = new ConcurrentQueue<string>();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.OnDesktopControl += payload =>
        {
            accepted.Enqueue(payload["requestId"]!.GetValue<string>());
            received.TrySetResult();
        };
        server.Start();
        using var client = new ClientWebSocket();
        client.Options.SetRequestHeader("Authorization", "Bearer test-token");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/chat"), timeout.Token);
        while (!server.IsChatConnected) await Task.Delay(15, timeout.Token);
        Task Send(string requestId, string saveId, string gameSessionId) => SendRawJsonAsync(client,
            JsonSerializer.Serialize(new {
                protocolVersion = AgentProtocol.Version, messageType = "desktop.control", saveId, gameSessionId, payload = new { requestId, action = "takeover" },
            }));
        server.DesktopClientEnabled = true;
        await Send("stale-session", "save", "old");
        await Send("wrong-save", "other", "session");
        await Send("current", "save", "session");
        await received.Task.WaitAsync(timeout.Token);
        Assert.Equal("current", Assert.Single(accepted));
    }

    [Fact]
    public void LateCancelAndSubsequentQueryReplayConfirmedResultThroughIncomingQueue()
    {
        var handler = new TestTransportHandler();
        using var server = new WebSocketTransportServer(0, "token", handler, "save", "session");
        typeof(WebSocketTransportServer).GetProperty(nameof(server.State))!.SetValue(server, TransportState.Ready);
        var completed = new SkillResultPayload("done", "task", "succeeded", 3, 0, 0, 9, new());
        server.Idempotency.MarkCompleted("idempotency", completed);
        var receive = typeof(WebSocketTransportServer).GetMethod("ProcessIncomingRawMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        foreach (var type in new[] { "skill.cancel", "skill.result.query" })
        {
            var envelope = new EnvelopeDto(AgentProtocol.Version, type, type, "test", 1, 1, DateTimeOffset.UtcNow,
                new JsonObject { ["commandId"] = "done" }, SaveId: "save", GameSessionId: "session");
            receive.Invoke(server, new object[] { JsonSerializer.Serialize(envelope), 0L });
            server.Update();
        }
        var outgoing = (System.Collections.IEnumerable)typeof(WebSocketTransportServer)
            .GetField("_outboundCriticalQueue", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        var results = outgoing.Cast<object>().Select(item => (EnvelopeDto)item.GetType().GetProperty("Envelope")!.GetValue(item)!).ToList();
        Assert.Equal(2, results.Count);
        Assert.All(results, result => {
            Assert.Equal("skill.result", result.MessageType);
            Assert.Equal("succeeded", result.Payload["terminalState"]!.ToString());
            Assert.Equal(3, result.Payload["completedCount"]!.GetValue<int>());
        });
        Assert.Equal(0, handler.CancelCount);
        Assert.Equal(0, handler.AcceptCount);
        Assert.True(server.Idempotency.TryGetCompletedResult("done", out var cached));
        Assert.Same(completed, cached);
    }

    [Fact]
    public async Task ChatCancelWaitsForGameThreadAndRejectsStaleConnectionOrSession()
    {
        var handler = new TestTransportHandler();
        using var server = new WebSocketTransportServer(0, "test-token", handler, saveId: "save-123", gameSessionId: "session-456");
        server.Start();
        using var client = new ClientWebSocket();
        client.Options.SetRequestHeader("Authorization", "Bearer test-token");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/chat"), timeout.Token);
        while (!server.IsChatConnected) await Task.Delay(10, timeout.Token);
        var queue = (BoundedMessageQueue)typeof(WebSocketTransportServer)
            .GetField("_pendingChatCommands", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(server)!;
        async Task SendCancel(string command)
        {
            await SendRawJsonAsync(client, JsonSerializer.Serialize(new {
                protocolVersion = AgentProtocol.Version, messageType = "skill.cancel", messageId = command,
                saveId = "save-123", gameSessionId = "session-456", senderInstanceId = "test",
                sequenceNumber = 1, worldRevision = 1, sentAt = DateTimeOffset.UtcNow,
                payload = new { commandId = command, reason = "player", requestedByPlayer = true, cancelPolicy = "safe-point" }
            }));
            while (queue.TotalCount == 0) await Task.Delay(10, timeout.Token);
        }
        await SendCancel("first");
        Assert.Equal(0, handler.CancelCount);
        int updateThread = Environment.CurrentManagedThreadId;
        server.Update();
        Assert.Equal(1, handler.CancelCount);
        Assert.Equal(updateThread, handler.LastCancelThreadId);
        await SendCancel("old-session");
        typeof(WebSocketTransportServer).GetField("_gameSessionId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(server, "new-session");
        server.Update();
        Assert.Equal(1, handler.CancelCount);
        typeof(WebSocketTransportServer).GetField("_gameSessionId", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(server, "session-456");
        await SendCancel("old-connection");
        typeof(WebSocketTransportServer).GetField("_chatSocketGeneration", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(server, server.ChatGeneration + 1);
        server.Update();
        Assert.Equal(1, handler.CancelCount);
        client.Abort();
    }

    [Fact]
    public async Task LifeRequestPublishesNativeObservationFirstAndRetainsItsCaptureTime()
    {
        var handler = new TestTransportHandler();
        using var server = new WebSocketTransportServer(0, "test-token", handler);
        using var socket = new ControlledChatSocket();
        SetChatSocket(server, socket, 1);
        var observation = handler.CaptureCurrentSnapshot(7) with { ObservedAt = 123.5 };
        Assert.True(await server.SendLifeChatSubmitAsync(new LifeChatSubmitPayload("r", "save", "chat", "work"), observation));
        Assert.Equal(2, socket.SentMessages.Count);
        using var first = JsonDocument.Parse(socket.SentMessages[0]);
        Assert.Equal("world.snapshot", first.RootElement.GetProperty("messageType").GetString());
        Assert.Equal(123.5, first.RootElement.GetProperty("payload").GetProperty("observedAt").GetDouble());
        using var second = JsonDocument.Parse(socket.SentMessages[1]);
        Assert.Equal("life.chat.submit", second.RootElement.GetProperty("messageType").GetString());
    }

    [Fact]
    public async Task LifeRequestDoesNotDispatchWhenObservationPublicationFails()
    {
        var handler = new TestTransportHandler();
        using var server = new WebSocketTransportServer(0, "test-token", handler);
        using var socket = new ControlledChatSocket(fail: true);
        SetChatSocket(server, socket, 1);
        Assert.False(await server.SendLifeChatSubmitAsync(new LifeChatSubmitPayload("r", "save", "chat", "work"),
            handler.CaptureCurrentSnapshot(7)));
        Assert.DoesNotContain(socket.SentMessages, text => text.Contains("life.chat.submit"));
    }

    [Fact]
    public async Task ChatSnapshotBackpressureReportsSafeTimeoutOnceOnGameThread()
    {
        var logs = new ConcurrentQueue<string>();
        var problems = new List<string>();
        int callbackThread = 0;
        var handler = new TestTransportHandler();
        using var server = new WebSocketTransportServer(0, "test-token", handler,
            logger: (message, _) => logs.Enqueue(message));
        server.OnChatChannelProblem += (_, _, reason) =>
        {
            callbackThread = Environment.CurrentManagedThreadId;
            problems.Add(reason);
        };
        using var blockedSocket = new ControlledChatSocket(block: true);
        SetChatSocket(server, blockedSocket, 1);
        var snapshot = handler.CaptureCurrentSnapshot(1);
        snapshot = snapshot with { Companion = snapshot.Companion with {
            Activity = "private-payload-do-not-log" } };

        // A transport whose write never completes exposes cancellation deterministically.
        await server.SendSnapshotAsync(snapshot, 1).WaitAsync(TimeSpan.FromSeconds(12));
        int updateThread = Environment.CurrentManagedThreadId;
        server.Update();
        Assert.Equal(updateThread, callbackThread);
        Assert.Contains("快照发送超时（5秒）", Assert.Single(problems));
        Assert.Contains(logs, l => l.Contains("Chat send timeout: world.snapshot, generation 1"));
        Assert.DoesNotContain(logs, l => l.Contains("private-payload-do-not-log"));
        Assert.DoesNotContain(problems, p => p.Contains("test-token"));
        await server.SendSnapshotAsync(handler.CaptureCurrentSnapshot(2), 2);
        server.Update();
        Assert.Single(problems);
    }

    [Fact]
    public async Task BlockedOldChatGenerationCannotReportFailureOrSendIntoReplacement()
    {
        var logs = new ConcurrentQueue<string>();
        var problems = new List<string>();
        var handler = new TestTransportHandler();
        using var server = new WebSocketTransportServer(0, "test-token", handler,
            logger: (message, _) => logs.Enqueue(message));
        server.OnChatChannelProblem += (_, _, reason) => problems.Add(reason);
        using var blockedSocket = new ControlledChatSocket(block: true);
        SetChatSocket(server, blockedSocket, 1);
        var small = handler.CaptureCurrentSnapshot(1);
        Task blocked = server.SendSnapshotAsync(small, 1);
        await blockedSocket.SendStarted.Task;
        // Cache a small current snapshot; its old-generation send waits behind the blocked send.
        Task oldQueued = server.SendSnapshotAsync(small, 2);
        using var replacement = new ControlledChatSocket();
        SetChatSocket(server, replacement, 2);

        await Task.WhenAll(blocked, oldQueued).WaitAsync(TimeSpan.FromSeconds(12));
        server.Update();
        Assert.Empty(problems);
        Assert.True(server.IsChatConnected);
        Assert.Empty(replacement.SentMessages);
        Assert.True(await server.SendChatSubmitAsync(new ChatSubmitPayload("new-request", "still connected", "text", "save")));
        Assert.Contains("chat.submit", Assert.Single(replacement.SentMessages));
    }

    [Fact]
    public async Task WebSocketFailureReturnsFalseAndDoesNotRepeatOrExposeExceptionPayload()
    {
        var logs = new ConcurrentQueue<string>();
        var problems = new List<string>();
        using var server = new WebSocketTransportServer(0, "test-token", new TestTransportHandler(),
            logger: (message, _) => logs.Enqueue(message));
        using var failing = new ControlledChatSocket(fail: true);
        SetChatSocket(server, failing, 1);
        server.OnChatChannelProblem += (_, _, reason) => problems.Add(reason);
        var message = new ChatSubmitPayload("request", "private-message", "text", "save");
        Assert.False(await server.SendChatSubmitAsync(message));
        Assert.False(await server.SendChatSubmitAsync(message));
        Assert.Empty(problems);
        server.Update();
        Assert.Contains("发送失败", Assert.Single(problems));
        Assert.Single(logs.Where(l => l.Contains("Chat send WebSocket failure")));
        Assert.DoesNotContain(logs, l => l.Contains("secret-from-exception") || l.Contains("private-message"));
        SetChatSocket(server, failing, 2);
        Assert.False(await server.SendChatSubmitAsync(message));
        using var recovered = new ControlledChatSocket();
        SetChatSocket(server, recovered, 3);
        server.Update(); // a failure queued before replacement must be discarded too
        Assert.Single(problems);
        Assert.True(server.IsChatConnected);
    }

    private static void SetChatSocket(WebSocketTransportServer server, WebSocket socket, long generation)
    {
        typeof(WebSocketTransportServer).GetField("_chatSocket", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(server, socket);
        typeof(WebSocketTransportServer).GetField("_chatSocketGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(server, generation);
    }

    private sealed class ControlledChatSocket : WebSocket
    {
        private readonly bool _block;
        private readonly bool _fail;
        private WebSocketState _state = WebSocketState.Open;
        public TaskCompletionSource<bool> SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> SentMessages { get; } = new();
        public ControlledChatSocket(bool block = false, bool fail = false) { _block = block; _fail = fail; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            SendStarted.TrySetResult(true);
            if (_fail) throw new WebSocketException("secret-from-exception");
            if (_block)
            {
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { _state = WebSocketState.Aborted; throw; }
            }
            SentMessages.Add(Encoding.UTF8.GetString(buffer));
        }
    }

    [Fact]
    public async Task SpatialResultAbove64KbIsDeliveredAndOversizeResultBecomesTerminalError()
    {
        using var server = new WebSocketTransportServer(0, "test-token", new TestTransportHandler(), saveId: "save-123", gameSessionId: "session-456");
        server.Start();
        using var client = await ConnectClientAsync(server.Port, "test-token");
        await SendRawJsonAsync(client, CreateHelloJson("test-token"));
        await ReceiveEnvelopeAsync(client);
        server.Update();
        await ReceiveEnvelopeAsync(client);
        var result = new SkillResultPayload("cmd-map", "task-map", "succeeded", 0, 0, 0, 1, new(),
            SkillId: "inspect-location", Details: new() { ["farmSpace"] = new string('.', 100000) });
        await server.SendSkillResultAsync(result, "map-correlation");
        var received = await ReceiveEnvelopeAsync(client);
        Assert.Equal("succeeded", received.Payload["terminalState"]!.ToString());
        Assert.Equal(100000, received.Payload["details"]!["farmSpace"]!.ToString().Length);
        await server.SendSkillResultAsync(result with { Details = new() { ["farmSpace"] = new string('.', 3 * 1024 * 1024) } }, "large-map-correlation");
        var rejected = await ReceiveEnvelopeAsync(client);
        Assert.Equal("failed", rejected.Payload["terminalState"]!.ToString());
        Assert.Equal("RESPONSE_TOO_LARGE", rejected.Payload["error"]!["code"]!.ToString());
        Assert.Null(server.ActiveCommandId);
    }

    private sealed class TestTransportHandler : ITransportHandler
    {
        public int AcceptCount { get; private set; }
        public int CancelCount { get; private set; }
        public int LastCancelThreadId { get; private set; }
        public int PauseCount { get; private set; }
        public int ResumeCount { get; private set; }
        public int DisconnectCount { get; private set; }

        public int LastAcceptThreadId { get; private set; }
        public int LastDisconnectThreadId { get; private set; }

        public List<SkillExecutePayload> AcceptedPayloads { get; } = new();
        public List<SkillCancelPayload> CancelPayloads { get; } = new();
        public string? DisconnectReason { get; private set; }

        public bool AcceptShouldSucceed { get; set; } = true;
        public SkillResultPayload? CustomRejectResult { get; set; }
        public string? ActiveCommand { get; set; }
        public bool WasActiveCommandCancelled { get; set; }

        public bool TryAcceptSkillExecute(SkillExecutePayload payload, EnvelopeDto envelope, out SkillResultPayload? rejectResult)
        {
            AcceptCount++;
            LastAcceptThreadId = Environment.CurrentManagedThreadId;
            AcceptedPayloads.Add(payload);

            if (AcceptShouldSucceed)
            {
                ActiveCommand = payload.CommandId;
                rejectResult = null;
                return true;
            }

            rejectResult = CustomRejectResult ?? new SkillResultPayload(
                CommandId: payload.CommandId,
                TaskId: payload.TaskId,
                TerminalState: "rejected",
                CompletedCount: 0,
                SkippedCount: 0,
                FailedCount: 0,
                FinalWorldRevision: envelope.WorldRevision,
                Effects: new List<Dictionary<string, object>>()
            );
            return false;
        }

        public void OnCancelSkill(SkillCancelPayload payload, EnvelopeDto envelope)
        {
            LastCancelThreadId = Environment.CurrentManagedThreadId;
            CancelCount++;
            CancelPayloads.Add(payload);
            if (ActiveCommand == payload.CommandId)
            {
                WasActiveCommandCancelled = true;
                ActiveCommand = null;
            }
        }

        public void OnPauseSkill(SkillPausePayload payload, EnvelopeDto envelope)
        {
            PauseCount++;
        }

        public void OnResumeSkill(SkillResumePayload payload, EnvelopeDto envelope)
        {
            ResumeCount++;
        }

        public WorldSnapshotPayload CaptureCurrentSnapshot(long worldRevision)
        {
            return new WorldSnapshotPayload(
                CapturedRevision: worldRevision,
                Companion: new CompanionSnapshot(
                    LocationId: "Farm",
                    TileX: 64,
                    TileY: 15,
                    FacingDirection: 2,
                    Stamina: 270.0f,
                    MaxStamina: 270,
                    WaterCanLevel: 40,
                    MaxWaterCanLevel: 40,
                    HasWateringCan: true,
                    Activity: "idle"
                ),
                World: new WorldStateSnapshot(
                    CurrentLocation: "Farm",
                    TimeOfDay: 600,
                    Season: "spring",
                    DayOfMonth: 1,
                    IsRaining: false
                )
            );
        }

        public void OnTransportDisconnected(string reason)
        {
            DisconnectCount++;
            LastDisconnectThreadId = Environment.CurrentManagedThreadId;
            DisconnectReason = reason;
            if (ActiveCommand != null)
            {
                WasActiveCommandCancelled = true;
                ActiveCommand = null;
            }
        }
    }

    private static async Task<ClientWebSocket> ConnectClientAsync(int port, string token, bool useHeader = true)
    {
        var client = new ClientWebSocket();
        if (useHeader)
        {
            client.Options.SetRequestHeader("Authorization", $"Bearer {token}");
        }

        string uri = useHeader
            ? $"ws://127.0.0.1:{port}/"
            : $"ws://127.0.0.1:{port}/?token={token}";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await client.ConnectAsync(new Uri(uri), cts.Token);
        return client;
    }

    private static async Task SendRawJsonAsync(ClientWebSocket ws, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);
    }

    private static async Task SendAndDrainAsync(ClientWebSocket ws, WebSocketTransportServer server, string json, int delayMs = 60)
    {
        await SendRawJsonAsync(ws, json);
        await Task.Delay(delayMs);
        server.Update();
    }

    private static async Task<EnvelopeDto> ReceiveEnvelopeAsync(ClientWebSocket ws)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        while (true)
        {
            var res = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            ms.Write(buffer, 0, res.Count);
            if (res.EndOfMessage)
            {
                break;
            }
        }

        var json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        var envelope = JsonSerializer.Deserialize<EnvelopeDto>(json, WebSocketTransportServer.JsonOptions);
        Assert.NotNull(envelope);
        return envelope;
    }

    private static string CreateHelloJson(string token)
    {
        return JsonSerializer.Serialize(new
        {
            protocolVersion = AgentProtocol.Version,
            messageType = "runtime.hello",
            messageId = "msg-hello-1",
            senderInstanceId = "runtime-test",
            sequenceNumber = 0,
            worldRevision = 0,
            sentAt = DateTimeOffset.UtcNow.ToString("O"),
            payload = new
            {
                supportedProtocolVersions = new[] { AgentProtocol.Version },
                runtimeVersion = "0.1.0",
                sessionToken = token,
                features = new Dictionary<string, bool>()
            }
        });
    }

    private static string CreateExecuteJson(
        string commandId,
        string taskId,
        string idempotencyKey,
        List<TileCoord>? tiles = null,
        DateTimeOffset? expiresAt = null,
        int maxGameMinutes = 60)
    {
        return JsonSerializer.Serialize(new
        {
            protocolVersion = AgentProtocol.Version,
            messageType = "skill.execute",
            messageId = $"msg-exec-{commandId}",
            saveId = "save-123",
            gameSessionId = "session-456",
            senderInstanceId = "runtime-test",
            sequenceNumber = 1,
            worldRevision = 1,
            sentAt = DateTimeOffset.UtcNow.ToString("O"),
            expiresAt = (expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(5)).ToString("O"),
            idempotencyKey = idempotencyKey,
            payload = new
            {
                commandId = commandId,
                taskId = taskId,
                skillId = "water-zone",
                skillVersion = "0.1",
                expectedWorldRevision = 1,
                parameters = new
                {
                    locationId = "Farm",
                    tiles = tiles ?? new List<TileCoord> { new(64, 15), new(64, 16) }
                },
                budgets = new
                {
                    maxGameMinutes = maxGameMinutes,
                    maxStamina = 50.0f,
                    maxWater = 20
                },
                cancelPolicy = "safe-point",
                policyDecisionId = "policy-allow"
            }
        });
    }

    [Fact]
    public async Task UnauthorizedConnection_RejectedWith401()
    {
        var handler = new TestTransportHandler();
        using var server = new WebSocketTransportServer(0, "valid-secret", handler);
        server.Start();

        var client = new ClientWebSocket();
        client.Options.SetRequestHeader("Authorization", "Bearer wrong-secret");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<WebSocketException>(async () =>
        {
            await client.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/"), cts.Token);
        });
    }

    [Fact]
    public async Task HandshakeAndSnapshot_FullFlow_PreservesSaveAndSessionIds()
    {
        var handler = new TestTransportHandler();
        using var server = new WebSocketTransportServer(0, "test-token", handler, saveId: "save-123", gameSessionId: "session-456");
        server.Start();

        using var client = await ConnectClientAsync(server.Port, "test-token");

        // Send hello
        await SendRawJsonAsync(client, CreateHelloJson("test-token"));

        // Receive mod.welcome
        var welcome = await ReceiveEnvelopeAsync(client);
        Assert.Equal("mod.welcome", welcome.MessageType);
        Assert.Equal("save-123", welcome.SaveId);
        Assert.Equal("session-456", welcome.GameSessionId);

        // Server update triggers snapshot
        server.Update();

        var snapshot = await ReceiveEnvelopeAsync(client);
        Assert.Equal("world.snapshot", snapshot.MessageType);
        Assert.Equal("save-123", snapshot.SaveId);
        Assert.Equal("session-456", snapshot.GameSessionId);
        Assert.Equal(TransportState.Ready, server.State);
    }

    [Fact]
    public async Task MaliciousMissingPayload_FailsClosedWithoutCrashing()
    {
        var handler = new TestTransportHandler();
        using var server = new WebSocketTransportServer(0, "test-token", handler, saveId: "save-123", gameSessionId: "session-456");
        server.Start();

        using var client = await ConnectClientAsync(server.Port, "test-token");
        await SendRawJsonAsync(client, CreateHelloJson("test-token"));
        await ReceiveEnvelopeAsync(client); // welcome
        server.Update();
        await ReceiveEnvelopeAsync(client); // snapshot

        // 1. Send malformed JSON
        await SendRawJsonAsync(client, "{ not valid json !!! }");
        var err1 = await ReceiveEnvelopeAsync(client);
        Assert.Equal("protocol.error", err1.MessageType);
        Assert.Equal("MALFORMED_JSON", err1.Payload["code"]?.ToString());

        // 2. Send skill.execute with missing idempotencyKey (must NOT be synthesized)
        var missingIdemJson = JsonSerializer.Serialize(new
        {
            protocolVersion = AgentProtocol.Version,
            messageType = "skill.execute",
            messageId = "msg-missing-idem",
            saveId = "save-123",
            gameSessionId = "session-456",
            senderInstanceId = "runtime-test",
            sequenceNumber = 2,
            worldRevision = 1,
            sentAt = DateTimeOffset.UtcNow.ToString("O"),
            payload = new
            {
                commandId = "cmd-1",
                taskId = "task-1"
            }
        });
        await SendAndDrainAsync(client, server, missingIdemJson);

        var reject1 = await ReceiveEnvelopeAsync(client);
        Assert.Equal("skill.result", reject1.MessageType);
        Assert.Equal("rejected", reject1.Payload["terminalState"]?.ToString());
        Assert.Equal("MISSING_IDEMPOTENCY_KEY", reject1.Payload["error"]?["code"]?.ToString());

        // 3. Send skill.execute with 101 tiles (exceeds 100 max)
        var manyTiles = Enumerable.Range(0, 101).Select(i => new TileCoord(i, i)).ToList();
        await SendAndDrainAsync(client, server, CreateExecuteJson("cmd-2", "task-2", "idem-2", tiles: manyTiles));

        var reject2 = await ReceiveEnvelopeAsync(client);
        Assert.Equal("skill.result", reject2.MessageType);
        Assert.Equal("rejected", reject2.Payload["terminalState"]?.ToString());
        Assert.Equal("BOUNDS_VIOLATION", reject2.Payload["error"]?["code"]?.ToString());
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, false)]
    public async Task UnlimitedGameTimeIsAcceptedButNegativeBudgetIsRejected(int minutes, bool accepted)
    {
        var handler = new TestTransportHandler();
        using var server = new WebSocketTransportServer(0, "test-token", handler, saveId: "save-123", gameSessionId: "session-456");
        server.Start();
        using var client = await ConnectClientAsync(server.Port, "test-token");
        await SendRawJsonAsync(client, CreateHelloJson("test-token"));
        await ReceiveEnvelopeAsync(client);
        server.Update();
        await ReceiveEnvelopeAsync(client);
        await SendAndDrainAsync(client, server,
            CreateExecuteJson("budget-command", "budget-task", "budget-key", maxGameMinutes: minutes));
        Assert.Equal(accepted ? 1 : 0, handler.AcceptCount);
        if (!accepted)
        {
            var result = await ReceiveEnvelopeAsync(client);
            Assert.Equal("rejected", result.Payload["terminalState"]?.ToString());
        }
    }

    [Fact]
    public async Task DuplicateAndReplay_PreservesOriginalResultWithoutReexecution()
    {
        var handler = new TestTransportHandler();
        using var server = new WebSocketTransportServer(0, "test-token", handler, saveId: "save-123", gameSessionId: "session-456");
        server.Start();

        using var client = await ConnectClientAsync(server.Port, "test-token");
        await SendRawJsonAsync(client, CreateHelloJson("test-token"));
        await ReceiveEnvelopeAsync(client); // welcome
        server.Update();
        await ReceiveEnvelopeAsync(client); // snapshot

        // 1. Submit execute
        await SendAndDrainAsync(client, server, CreateExecuteJson("cmd-water-1", "task-1", "idem-water-1"));

        Assert.Equal(1, handler.AcceptCount);
        Assert.Equal("cmd-water-1", server.ActiveCommandId);

        // 2. Complete execution
        var resultPayload = new SkillResultPayload(
            CommandId: "cmd-water-1",
            TaskId: "task-1",
            TerminalState: "succeeded",
            CompletedCount: 2,
            SkippedCount: 0,
            FailedCount: 0,
            FinalWorldRevision: 2,
            Effects: new List<Dictionary<string, object>>(),
            Resources: new SkillResultResources(4.0f, 2, 10)
        );
        await server.SendSkillResultAsync(resultPayload, correlationId: "msg-exec-cmd-water-1", idempotencyKey: "idem-water-1");

        var firstResult = await ReceiveEnvelopeAsync(client);
        Assert.Equal("skill.result", firstResult.MessageType);
        Assert.Equal("succeeded", firstResult.Payload["terminalState"]?.ToString());
        Assert.Null(server.ActiveCommandId);

        // 3. Resend EXACT DUPLICATE execute
        await SendAndDrainAsync(client, server, CreateExecuteJson("cmd-water-1", "task-1", "idem-water-1"));

        // Handler must NOT have been called again!
        Assert.Equal(1, handler.AcceptCount);

        // Replayed result received
        var replayedResult = await ReceiveEnvelopeAsync(client);
        Assert.Equal("skill.result", replayedResult.MessageType);
        Assert.Equal("succeeded", replayedResult.Payload["terminalState"]?.ToString());
        Assert.Equal("cmd-water-1", replayedResult.Payload["commandId"]?.ToString());
    }

    private static string CreateResultQueryJson(string commandId, string? saveId = "save-123", string? sessionId = "session-456") =>
        JsonSerializer.Serialize(new {
            protocolVersion = AgentProtocol.Version, messageType = "skill.result.query", messageId = $"query-{commandId}",
            senderInstanceId = "runtime-restarted", sequenceNumber = 2, worldRevision = 1,
            sentAt = DateTimeOffset.UtcNow.ToString("O"), saveId, gameSessionId = sessionId,
            payload = new { commandId }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DesktopCanIdentifyNativeActionAfterCommandSocketDisconnect(bool terminalBeforeDisconnect)
    {
        var handler = new TestTransportHandler();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new WebSocketTransportServer(0, "test-token", handler, saveId: "save-123", gameSessionId: "session-456",
            logger: (message, _) => { if (message.Contains("disconnect scheduled")) disconnected.TrySetResult(); });
        server.DesktopClientEnabled = true;
        server.Start();
        using var client = await ConnectClientAsync(server.Port, "test-token");
        await SendRawJsonAsync(client, CreateHelloJson("test-token"));
        await ReceiveEnvelopeAsync(client);
        server.Update();
        await ReceiveEnvelopeAsync(client);
        await SendAndDrainAsync(client, server, CreateExecuteJson("old-command", "old-task", "old-key"));

        var receipt = new SkillResultPayload("old-command", "old-task", "partially-succeeded", 2, 0, 1, 2,
            new List<Dictionary<string, object>>(), new SkillResultResources(4, 2, 10));
        if (terminalBeforeDisconnect) await server.SendSkillResultAsync(receipt, "original-execute", "old-key");
        await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "command socket closed first", CancellationToken.None);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        server.Update();

        Assert.Null(server.ActiveCommandId);
        Assert.Equal("old-command", server.LatestDesktopActionId);
        if (!terminalBeforeDisconnect) await server.SendSkillResultAsync(receipt, "original-execute", "old-key");
        Assert.True(server.Idempotency.TryGetCompletedResult(server.LatestDesktopActionId!, out var recovered));
        Assert.Equal(receipt, recovered);
        Assert.Equal(1, handler.AcceptCount);
    }

    [Theory]
    [InlineData("succeeded")]
    [InlineData("partially-succeeded")]
    [InlineData("cancelled")]
    [InlineData("failed")]
    public async Task CommandResultQueryAfterReconnectReplaysTerminalWithoutRedispatch(string terminal)
    {
        var handler = new TestTransportHandler();
        using var server = new WebSocketTransportServer(0, "test-token", handler, saveId: "save-123", gameSessionId: "session-456");
        server.Start();
        using (var first = await ConnectClientAsync(server.Port, "test-token"))
        {
            await SendRawJsonAsync(first, CreateHelloJson("test-token"));
            await ReceiveEnvelopeAsync(first);
            server.Update();
            await ReceiveEnvelopeAsync(first);
            await SendAndDrainAsync(first, server, CreateExecuteJson("old-command", "old-task", "old-key"));
            await server.SendSkillResultAsync(new SkillResultPayload("old-command", "old-task", terminal, 1, 0, 0, 2,
                new List<Dictionary<string, object>> { new() { ["tile"] = new TileCoord(64, 15), ["state"] = "watered" } },
                new SkillResultResources(2, 1, 10)), "original-execute", "old-key");
            var original = await ReceiveEnvelopeAsync(first);
            await first.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "runtime exits", CancellationToken.None);
            await Task.Delay(60);
            server.Update();

            using var restarted = await ConnectClientAsync(server.Port, "test-token");
            await SendRawJsonAsync(restarted, CreateHelloJson("test-token"));
            await ReceiveEnvelopeAsync(restarted);
            server.Update();
            await ReceiveEnvelopeAsync(restarted);
            await SendAndDrainAsync(restarted, server, CreateResultQueryJson("old-command"));
            var recovered = await ReceiveEnvelopeAsync(restarted);
            Assert.Equal("skill.result", recovered.MessageType);
            Assert.Equal("query-old-command", recovered.CorrelationId);
            Assert.Equal(original.Payload.ToJsonString(), recovered.Payload.ToJsonString());
            Assert.Equal("save-123", recovered.SaveId);
            Assert.Equal("session-456", recovered.GameSessionId);
            Assert.Equal(1, handler.AcceptCount);
            Assert.Null(server.ActiveCommandId);
        }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("in-flight")]
    public async Task CommandResultQueryWithoutTerminalDoesNotFabricateOrExecute(string status)
    {
        var handler = new TestTransportHandler();
        using var server = new WebSocketTransportServer(0, "test-token", handler, saveId: "save-123", gameSessionId: "session-456");
        server.Start();
        using var client = await ConnectClientAsync(server.Port, "test-token");
        await SendRawJsonAsync(client, CreateHelloJson("test-token"));
        await ReceiveEnvelopeAsync(client);
        server.Update();
        await ReceiveEnvelopeAsync(client);
        if (status == "in-flight")
            await SendAndDrainAsync(client, server, CreateExecuteJson("pending-command", "pending-task", "pending-key"));
        await SendAndDrainAsync(client, server, CreateResultQueryJson("pending-command"));
        var result = await ReceiveEnvelopeAsync(client);
        Assert.Equal("skill.result.status", result.MessageType);
        Assert.Equal(status, result.Payload["status"]?.ToString());
        Assert.Null(result.Payload["terminalState"]);
        Assert.Equal(status == "in-flight" ? 1 : 0, handler.AcceptCount);
        Assert.Equal(status == "in-flight" ? "pending-command" : null, server.ActiveCommandId);
    }

    [Theory]
    [InlineData(null, "session-456")]
    [InlineData("save-123", null)]
    [InlineData("wrong-save", "session-456")]
    [InlineData("save-123", "old-session")]
    public async Task CommandResultQueryRequiresExactSaveAndSession(string? saveId, string? sessionId)
    {
        var handler = new TestTransportHandler();
        using var server = new WebSocketTransportServer(0, "test-token", handler, saveId: "save-123", gameSessionId: "session-456");
        server.Start();
        using var client = await ConnectClientAsync(server.Port, "test-token");
        await SendRawJsonAsync(client, CreateHelloJson("test-token"));
        await ReceiveEnvelopeAsync(client);
        server.Update();
        await ReceiveEnvelopeAsync(client);
        await SendAndDrainAsync(client, server, CreateResultQueryJson("command", saveId, sessionId));
        var result = await ReceiveEnvelopeAsync(client);
        Assert.Equal("protocol.error", result.MessageType);
        Assert.Equal("RESULT_SESSION_MISMATCH", result.Payload["code"]?.ToString());
        Assert.Equal(0, handler.AcceptCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(123)]
    public async Task CommandResultQueryRejectsInvalidIdentityWithoutExecuting(object? commandId)
    {
        var handler = new TestTransportHandler();
        using var server = new WebSocketTransportServer(0, "test-token", handler, saveId: "save-123", gameSessionId: "session-456");
        server.Start();
        using var client = await ConnectClientAsync(server.Port, "test-token");
        await SendRawJsonAsync(client, CreateHelloJson("test-token"));
        await ReceiveEnvelopeAsync(client);
        server.Update();
        await ReceiveEnvelopeAsync(client);
        var query = JsonNode.Parse(CreateResultQueryJson("command"))!.AsObject();
        query["payload"]!["commandId"] = JsonSerializer.SerializeToNode(commandId);
        await SendAndDrainAsync(client, server, query.ToJsonString());
        var result = await ReceiveEnvelopeAsync(client);
        Assert.Equal("protocol.error", result.MessageType);
        Assert.Equal("INVALID_PARAMETERS", result.Payload["code"]?.ToString());
        Assert.Equal(0, handler.AcceptCount);
    }

    [Fact]
    public async Task QueryingOldTerminalCannotClearOrRepeatOtherActiveWork()
    {
        var handler = new TestTransportHandler();
        using var server = new WebSocketTransportServer(0, "test-token", handler, saveId: "save-123", gameSessionId: "session-456");
        server.Start();
        using var client = await ConnectClientAsync(server.Port, "test-token");
        await SendRawJsonAsync(client, CreateHelloJson("test-token"));
        var welcome = await ReceiveEnvelopeAsync(client);
        Assert.True(welcome.Payload["commandResultQuery"]!.GetValue<bool>());
        server.Update();
        await ReceiveEnvelopeAsync(client);
        await SendAndDrainAsync(client, server, CreateExecuteJson("old-command", "old-task", "old-key"));
        await server.SendSkillResultAsync(new SkillResultPayload("old-command", "old-task", "succeeded", 1, 0, 0, 2,
            new List<Dictionary<string, object>>()), "old-execute", "old-key");
        await ReceiveEnvelopeAsync(client);
        await SendAndDrainAsync(client, server, CreateExecuteJson("new-command", "new-task", "new-key"));
        for (int retry = 0; retry < 2; retry++)
        {
            await SendAndDrainAsync(client, server, CreateResultQueryJson("old-command"));
            var oldResult = await ReceiveEnvelopeAsync(client);
            Assert.Equal("succeeded", oldResult.Payload["terminalState"]?.ToString());
            Assert.Equal("old-command", oldResult.Payload["commandId"]?.ToString());
            Assert.Equal("new-command", server.ActiveCommandId);
            Assert.Equal(2, handler.AcceptCount);
        }
    }

    [Fact]
    public async Task ConflictRejection_DoesNotMutateOriginalRecordOrActiveExecution()
    {
        var handler = new TestTransportHandler();
        using var server = new WebSocketTransportServer(0, "test-token", handler, saveId: "save-123", gameSessionId: "session-456");
        server.Start();

        using var client = await ConnectClientAsync(server.Port, "test-token");
        await SendRawJsonAsync(client, CreateHelloJson("test-token"));
        await ReceiveEnvelopeAsync(client); // welcome
        server.Update();
        await ReceiveEnvelopeAsync(client); // snapshot

        // 1. Start execution A with key 'shared-key'
        await SendAndDrainAsync(client, server, CreateExecuteJson("cmd-A", "task-A", "shared-key", tiles: new List<TileCoord> { new(1, 1) }));

        Assert.Equal(1, handler.AcceptCount);
        Assert.Equal("cmd-A", server.ActiveCommandId);

        // 2. Submit conflicting execution B with SAME 'shared-key' but DIFFERENT commandId and tiles!
        await SendAndDrainAsync(client, server, CreateExecuteJson("cmd-B", "task-B", "shared-key", tiles: new List<TileCoord> { new(9, 9) }));

        // Must reject B with IDEMPOTENCY_CONFLICT
        var conflictReject = await ReceiveEnvelopeAsync(client);
        Assert.Equal("skill.result", conflictReject.MessageType);
        Assert.Equal("rejected", conflictReject.Payload["terminalState"]?.ToString());
        Assert.Equal("IDEMPOTENCY_CONFLICT", conflictReject.Payload["error"]?["code"]?.ToString());

        // CRITICAL INVARIANCE: Command A must STILL be active and untouched!
        Assert.Equal("cmd-A", server.ActiveCommandId);
        Assert.Equal(1, handler.AcceptCount);

        // 3. Complete Command A
        var resultA = new SkillResultPayload(
            CommandId: "cmd-A",
            TaskId: "task-A",
            TerminalState: "succeeded",
            CompletedCount: 1,
            SkippedCount: 0,
            FailedCount: 0,
            FinalWorldRevision: 3,
            Effects: new List<Dictionary<string, object>>()
        );
        await server.SendSkillResultAsync(resultA, correlationId: "msg-A", idempotencyKey: "shared-key");

        var resultEnvelope = await ReceiveEnvelopeAsync(client);
        Assert.Equal("cmd-A", resultEnvelope.Payload["commandId"]?.ToString());
        Assert.Equal("succeeded", resultEnvelope.Payload["terminalState"]?.ToString());
    }

    [Fact]
    public async Task CancelBeforeExecute_PurgesQueuedCommandAndEmitsCancelledResult()
    {
        var handler = new TestTransportHandler();
        using var server = new WebSocketTransportServer(0, "test-token", handler, saveId: "save-123", gameSessionId: "session-456");
        server.Start();

        using var client = await ConnectClientAsync(server.Port, "test-token");
        await SendRawJsonAsync(client, CreateHelloJson("test-token"));
        await ReceiveEnvelopeAsync(client); // welcome
        server.Update();
        await ReceiveEnvelopeAsync(client); // snapshot

        // 1. Send execute for cmd-cancel-target
        await SendRawJsonAsync(client, CreateExecuteJson("cmd-cancel-target", "task-ct", "idem-ct"));

        // 2. IMMEDIATELY send cancel before server.Update() is called!
        var cancelJson = JsonSerializer.Serialize(new
        {
            protocolVersion = AgentProtocol.Version,
            messageType = "skill.cancel",
            messageId = "msg-cancel-1",
            correlationId = "msg-exec-cmd-cancel-target",
            saveId = "save-123",
            gameSessionId = "session-456",
            senderInstanceId = "runtime-test",
            sequenceNumber = 2,
            worldRevision = 1,
            sentAt = DateTimeOffset.UtcNow.ToString("O"),
            payload = new
            {
                commandId = "cmd-cancel-target",
                reason = "User stopped before start",
                requestedByPlayer = true,
                cancelPolicy = "safe-point"
            }
        });
        await SendRawJsonAsync(client, cancelJson);

        // Give network thread a moment to enqueue both
        await Task.Delay(100);

        // 3. Now drain server on the main thread
        server.Update();

        // Handler must NEVER have received execute!
        Assert.Equal(0, handler.AcceptCount);

        // Client must receive cancelled terminal result
        var cancelResult = await ReceiveEnvelopeAsync(client);
        Assert.Equal("skill.result", cancelResult.MessageType);
        Assert.Equal("cancelled", cancelResult.Payload["terminalState"]?.ToString());
        Assert.Equal("cmd-cancel-target", cancelResult.Payload["commandId"]?.ToString());
    }

    [Fact]
    public async Task Disconnect_QueuedToMainThread_ExecutesOnUpdateThread()
    {
        var handler = new TestTransportHandler();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new WebSocketTransportServer(0, "test-token", handler, saveId: "save-123", gameSessionId: "session-456",
            logger: (message, _) => { if (message.Contains("disconnect scheduled")) disconnected.TrySetResult(); });
        server.Start();

        var client = await ConnectClientAsync(server.Port, "test-token");
        await SendRawJsonAsync(client, CreateHelloJson("test-token"));
        await ReceiveEnvelopeAsync(client); // welcome
        server.Update();
        await ReceiveEnvelopeAsync(client); // snapshot

        // Start an active task
        await SendAndDrainAsync(client, server, CreateExecuteJson("cmd-disconnect-task", "task-d", "idem-d"));
        Assert.Equal("cmd-disconnect-task", server.ActiveCommandId);

        // Abruptly close client from client side
        client.Abort();
        client.Dispose();

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Before Update() is called, OnTransportDisconnected must NOT have run on the network thread!
        Assert.Equal(0, handler.DisconnectCount);

        // Now invoke Update() on this specific test thread
        int mainThreadId = Environment.CurrentManagedThreadId;
        server.Update();

        // Handler callback MUST have executed on main thread during Update()!
        Assert.Equal(1, handler.DisconnectCount);
        Assert.Equal(mainThreadId, handler.LastDisconnectThreadId);
        Assert.Null(server.ActiveCommandId);
    }

    [Fact]
    public async Task Backpressure_NormalQueueCapacityEnforced_ShedsNormalMessages()
    {
        var handler = new TestTransportHandler();
        using var server = new WebSocketTransportServer(0, "test-token", handler);
        server.Start();

        using var client = await ConnectClientAsync(server.Port, "test-token");
        await SendRawJsonAsync(client, CreateHelloJson("test-token"));
        await ReceiveEnvelopeAsync(client); // welcome
        server.Update();
        await ReceiveEnvelopeAsync(client); // snapshot

        // Rapidly flood 70 pause messages without calling server.Update()
        for (int i = 0; i < 70; i++)
        {
            var pauseJson = JsonSerializer.Serialize(new
            {
                protocolVersion = AgentProtocol.Version,
                messageType = "skill.pause",
                messageId = $"msg-pause-{i}",
                senderInstanceId = "runtime-test",
                sequenceNumber = i + 2,
                worldRevision = 1,
                sentAt = DateTimeOffset.UtcNow.ToString("O"),
                payload = new
                {
                    commandId = "cmd-1",
                    reason = $"Flood pause {i}"
                }
            });
            await SendRawJsonAsync(client, pauseJson);
        }

        // Allow network thread to process
        await Task.Delay(100);

        // Must receive at least one QUEUE_FULL protocol.error
        var errorEnv = await ReceiveEnvelopeAsync(client);
        Assert.Equal("protocol.error", errorEnv.MessageType);
        Assert.Equal("QUEUE_FULL", errorEnv.Payload["code"]?.ToString());
    }

    [Fact]
    public async Task MaliciousExpiredCommand_RejectedBeforeExecution()
    {
        var handler = new TestTransportHandler();
        using var server = new WebSocketTransportServer(0, "test-token", handler, saveId: "save-123", gameSessionId: "session-456");
        server.Start();

        using var client = await ConnectClientAsync(server.Port, "test-token");
        await SendRawJsonAsync(client, CreateHelloJson("test-token"));
        await ReceiveEnvelopeAsync(client); // welcome
        server.Update();
        await ReceiveEnvelopeAsync(client); // snapshot

        // Command expired 1 hour ago
        var pastTime = DateTimeOffset.UtcNow.AddHours(-1);
        await SendAndDrainAsync(client, server, CreateExecuteJson("cmd-expired", "task-exp", "idem-exp", expiresAt: pastTime));

        // Handler must NEVER be called
        Assert.Equal(0, handler.AcceptCount);

        var reject = await ReceiveEnvelopeAsync(client);
        Assert.Equal("skill.result", reject.MessageType);
        Assert.Equal("rejected", reject.Payload["terminalState"]?.ToString());
        Assert.Equal("EXPIRED", reject.Payload["error"]?["code"]?.ToString());
    }

    [Fact]
    public async Task InvalidSaveId_RejectedWithSaveMismatch()
    {
        var handler = new TestTransportHandler();
        using var server = new WebSocketTransportServer(0, "test-token", handler, saveId: "active-save-A", gameSessionId: "session-456");
        server.Start();

        using var client = await ConnectClientAsync(server.Port, "test-token");
        await SendRawJsonAsync(client, CreateHelloJson("test-token"));
        await ReceiveEnvelopeAsync(client); // welcome
        server.Update();
        await ReceiveEnvelopeAsync(client); // snapshot

        // Command targets wrong save
        var wrongSaveJson = JsonSerializer.Serialize(new
        {
            protocolVersion = AgentProtocol.Version,
            messageType = "skill.execute",
            messageId = "msg-wrong-save",
            saveId = "different-save-B",
            gameSessionId = "session-456",
            senderInstanceId = "runtime-test",
            sequenceNumber = 2,
            worldRevision = 1,
            sentAt = DateTimeOffset.UtcNow.ToString("O"),
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"),
            idempotencyKey = "idem-wrong-save",
            payload = new
            {
                commandId = "cmd-ws",
                taskId = "task-ws",
                skillId = "water-zone",
                skillVersion = "0.1",
                expectedWorldRevision = 1,
                parameters = new { locationId = "Farm", tiles = new[] { new { x = 1, y = 1 } } },
                budgets = new { maxGameMinutes = 60, maxStamina = 50.0f, maxWater = 20 },
                cancelPolicy = "safe-point",
                policyDecisionId = "policy-allow"
            }
        });
        await SendAndDrainAsync(client, server, wrongSaveJson);

        Assert.Equal(0, handler.AcceptCount);

        var reject = await ReceiveEnvelopeAsync(client);
        Assert.Equal("skill.result", reject.MessageType);
        Assert.Equal("rejected", reject.Payload["terminalState"]?.ToString());
        Assert.Equal("SAVE_MISMATCH", reject.Payload["error"]?["code"]?.ToString());
    }

    [Fact]
    public async Task ReconnectBeforeUpdate_DeliversOldDisconnectAndCancelsActiveTask()
    {
        var handler = new TestTransportHandler();
        using var server = new WebSocketTransportServer(0, "test-token", handler, saveId: "save-123", gameSessionId: "session-456");
        server.Start();

        // 1. Client 1 connects, handshakes, and starts executing cmd-1
        var client1 = await ConnectClientAsync(server.Port, "test-token");
        await SendRawJsonAsync(client1, CreateHelloJson("test-token"));
        await ReceiveEnvelopeAsync(client1); // welcome
        server.Update();
        await ReceiveEnvelopeAsync(client1); // snapshot

        await SendAndDrainAsync(client1, server, CreateExecuteJson("cmd-1", "task-1", "idem-1"));
        Assert.Equal(1, handler.AcceptCount);
        Assert.Equal("cmd-1", handler.ActiveCommand);
        Assert.Equal("cmd-1", server.ActiveCommandId);

        // 2. Client 1 disconnects unexpectedly
        await client1.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Client 1 done", CancellationToken.None);
        client1.Dispose();

        // Wait briefly for network thread to detect disconnect and schedule it
        await Task.Delay(50);

        // 3. CRUCIAL: Client 2 connects BEFORE server.Update() has run!
        var client2 = await ConnectClientAsync(server.Port, "test-token");
        await SendRawJsonAsync(client2, CreateHelloJson("test-token"));
        await Task.Delay(50);

        // 4. NOW server.Update() runs!
        // It MUST deliver client 1's disconnect first, cancelling cmd-1, before client 2 can begin!
        server.Update();

        Assert.Equal(1, handler.DisconnectCount);
        Assert.True(handler.WasActiveCommandCancelled, "Active command from client 1 must be cancelled on disconnect");
        Assert.Null(server.ActiveCommandId);

        // Client 2's handshake welcome was sent, snapshot now ready
        var welcome2 = await ReceiveEnvelopeAsync(client2);
        Assert.Equal("mod.welcome", welcome2.MessageType);

        server.Update();
        var snap2 = await ReceiveEnvelopeAsync(client2);
        Assert.Equal("world.snapshot", snap2.MessageType);

        // Client 2 can now execute cmd-2 cleanly
        await SendAndDrainAsync(client2, server, CreateExecuteJson("cmd-2", "task-2", "idem-2"));
        Assert.Equal(2, handler.AcceptCount);
        Assert.Equal("cmd-2", handler.ActiveCommand);
        Assert.Equal("cmd-2", server.ActiveCommandId);

        await client2.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Client 2 done", CancellationToken.None);
        client2.Dispose();
    }

    [Fact]
    public async Task SaturatedCriticalChannel_TriggersExplicitDisconnect_AndPreservesCachedReplay()
    {
        var handler = new TestTransportHandler();
        // Construct with maxCriticalOutboundCapacity: 2 to deterministically test saturation
        using var server = new WebSocketTransportServer(
            0,
            "test-token",
            handler,
            saveId: "save-123",
            gameSessionId: "session-456",
            logger: (TransportLogger?)null,
            maxNormalOutboundCapacity: 128,
            maxCriticalOutboundCapacity: 2
        );
        server.Start();

        var client = await ConnectClientAsync(server.Port, "test-token");
        await SendRawJsonAsync(client, CreateHelloJson("test-token"));
        await ReceiveEnvelopeAsync(client); // welcome
        server.Update();
        await ReceiveEnvelopeAsync(client); // snapshot

        // Execute cmd-sat
        await SendAndDrainAsync(client, server, CreateExecuteJson("cmd-sat", "task-sat", "idem-sat"));
        Assert.Equal(1, handler.AcceptCount);

        // Create a result payload that was completed
        var successResult = new SkillResultPayload(
            CommandId: "cmd-sat",
            TaskId: "task-sat",
            TerminalState: "succeeded",
            CompletedCount: 5,
            SkippedCount: 0,
            FailedCount: 0,
            FinalWorldRevision: 2,
            Effects: new List<Dictionary<string, object>>(),
            Resources: new SkillResultResources(10f, 5, 15)
        );

        // Send rapid burst of results to saturate the critical outbound queue (capacity is 2)
        // A tight synchronous loop prevents background SendLoopAsync from draining before queue is filled
        for (int i = 0; i < 20; i++)
        {
            _ = server.SendSkillResultAsync(successResult, $"msg-exec-{i}", "idem-sat");
        }

        // Wait briefly for explicit disconnect triggered by critical overflow
        await Task.Delay(100);

        // Assertion 1: Server transitioned to Offline
        Assert.Equal(TransportState.Offline, server.State);

        // Assertion 2: IdempotencyManager preserved the completed result!
        var check = server.Idempotency.Check("idem-sat", "cmd-sat", IdempotencyManager.ComputeSemanticFingerprint(new SkillExecutePayload(
            CommandId: "cmd-sat",
            TaskId: "task-sat",
            SkillId: "water-zone",
            SkillVersion: "0.1",
            ExpectedWorldRevision: 1,
            Parameters: new WaterZoneParameters("Farm", new List<TileCoord> { new(64, 15), new(64, 16) }),
            Budgets: new ExecutionBudgets(60, 50.0f, 20),
            CancelPolicy: "safe-point",
            PolicyDecisionId: "policy-allow"
        )), out var cached);
        Assert.Equal(IdempotencyCheck.CompletedReplay, check);
        Assert.NotNull(cached);
        Assert.Equal("succeeded", cached.TerminalState);
        Assert.Equal(5, cached.CompletedCount);

        client.Dispose();
    }

    [Fact]
    public async Task OutboundSequenceNumbers_AssignedInActualSendOrder()
    {
        var handler = new TestTransportHandler();
        using var server = new WebSocketTransportServer(0, "test-token", handler, saveId: "save-123", gameSessionId: "session-456");
        server.Start();

        var client = await ConnectClientAsync(server.Port, "test-token");
        await SendRawJsonAsync(client, CreateHelloJson("test-token"));

        var welcome = await ReceiveEnvelopeAsync(client);
        Assert.Equal(0, welcome.SequenceNumber);

        server.Update();
        var snapshot = await ReceiveEnvelopeAsync(client);
        Assert.Equal(1, snapshot.SequenceNumber);

        var result = new SkillResultPayload(
            CommandId: "cmd-seq",
            TaskId: "task-seq",
            TerminalState: "succeeded",
            CompletedCount: 1,
            SkippedCount: 0,
            FailedCount: 0,
            FinalWorldRevision: 2,
            Effects: new List<Dictionary<string, object>>()
        );
        await server.SendSkillResultAsync(result, "corr-1", "idem-seq");

        var resultEnv = await ReceiveEnvelopeAsync(client);
        Assert.Equal(2, resultEnv.SequenceNumber);

        client.Dispose();
    }

    [Fact]
    public async Task Test_RawTcpHandshake_MatchesPythonClient()
    {
        var handler = new TestTransportHandler();
        var logs = new List<string>();
        using var server = new WebSocketTransportServer(0, "test-token", handler, logger: (msg, lvl) => logs.Add($"{lvl}: {msg}"));
        server.Start();

        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", server.Port);
        using var stream = tcp.GetStream();

        string key = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        string req =
            $"GET / HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{server.Port}\r\n" +
            $"Upgrade: websocket\r\n" +
            $"Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Key: {key}\r\n" +
            $"Sec-WebSocket-Version: 13\r\n" +
            $"Authorization: Bearer test-token\r\n\r\n";

        byte[] reqBytes = Encoding.Latin1.GetBytes(req);
        await stream.WriteAsync(reqBytes);
        await stream.FlushAsync();

        byte[] respBytes = new byte[1024];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int read = await stream.ReadAsync(respBytes, cts.Token);
        string resp = Encoding.Latin1.GetString(respBytes, 0, read);

        Assert.StartsWith("HTTP/1.1 101", resp);
    }

    [Fact]
    public async Task Test_RawTcpHandshake_WrongToken_Returns401()
    {
        var handler = new TestTransportHandler();
        using var server = new WebSocketTransportServer(0, "test-token", handler);
        server.Start();

        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", server.Port);
        using var stream = tcp.GetStream();

        string key = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        string req =
            $"GET / HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{server.Port}\r\n" +
            $"Upgrade: websocket\r\n" +
            $"Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Key: {key}\r\n" +
            $"Sec-WebSocket-Version: 13\r\n" +
            $"Authorization: Bearer wrong-token\r\n\r\n";

        byte[] reqBytes = Encoding.Latin1.GetBytes(req);
        await stream.WriteAsync(reqBytes);
        await stream.FlushAsync();

        byte[] respBytes = new byte[1024];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int read = await stream.ReadAsync(respBytes, cts.Token);
        string resp = Encoding.Latin1.GetString(respBytes, 0, read);

        Assert.StartsWith("HTTP/1.1 401", resp);
    }

    [Fact]
    public async Task Test_RawTcpHandshake_Conflict_Returns409()
    {
        var handler = new TestTransportHandler();
        using var server = new WebSocketTransportServer(0, "test-token", handler);
        server.Start();

        // Connect first client
        var client1 = await ConnectClientAsync(server.Port, "test-token");

        // Attempt second client via raw TCP
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", server.Port);
        using var stream = tcp.GetStream();

        string key = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        string req =
            $"GET / HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{server.Port}\r\n" +
            $"Upgrade: websocket\r\n" +
            $"Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Key: {key}\r\n" +
            $"Sec-WebSocket-Version: 13\r\n" +
            $"Authorization: Bearer test-token\r\n\r\n";

        byte[] reqBytes = Encoding.Latin1.GetBytes(req);
        await stream.WriteAsync(reqBytes);
        await stream.FlushAsync();

        byte[] respBytes = new byte[1024];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int read = await stream.ReadAsync(respBytes, cts.Token);
        string resp = Encoding.Latin1.GetString(respBytes, 0, read);

        Assert.StartsWith("HTTP/1.1 409", resp);

        client1.Dispose();
    }

    [Fact]
    public async Task TypeScriptGameConnectionBindsToTheVersionTwoServer()
    {
        using var server = new WebSocketTransportServer(0, "test-token", new TestTransportHandler(), "save-123", "session-456");
        server.Start();
        string directory = Path.Combine(Path.GetTempPath(), $"star-game-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(directory, "data"));
        try
        {
            string script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "tests", "contracts", "connect-game.mjs"));
            File.WriteAllText(Path.Combine(directory, "data", "transport-discovery.json"), JsonSerializer.Serialize(new
            {
                protocolVersion = AgentProtocol.Version, port = server.Port, sessionToken = server.SessionToken,
                saveId = "save-123", gameSessionId = "session-456"
            }));
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("STAR_NODE_PATH") ?? "node")
            {
                UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true
            };
            start.ArgumentList.Add(script);
            start.ArgumentList.Add(directory);
            using var child = Process.Start(start)!;
            var stderr = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            try
            {
                while (!child.HasExited)
                {
                    server.Update();
                    if (server.IsChatConnected)
                        await server.SendDesktopStateAsync("desktop.control.result", new JsonObject { ["recoveryPending"] = false });
                    await Task.Delay(30, timeout.Token);
                }
            }
            finally { if (!child.HasExited) child.Kill(); }
            Assert.True(child.ExitCode == 0, await stderr);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Test_RawTcpHandshake_NonWebSocket_Returns400()
    {
        var handler = new TestTransportHandler();
        var logs = new List<string>();
        using var server = new WebSocketTransportServer(0, "test-token", handler, logger: (msg, lvl) => logs.Add($"{lvl}: {msg}"));
        server.Start();

        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", server.Port);
        using var stream = tcp.GetStream();

        string req =
            $"GET / HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{server.Port}\r\n" +
            $"Authorization: Bearer test-token\r\n\r\n";

        byte[] reqBytes = Encoding.Latin1.GetBytes(req);
        await stream.WriteAsync(reqBytes);
        await stream.FlushAsync();

        byte[] respBytes = new byte[1024];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        int read = await stream.ReadAsync(respBytes, cts.Token);
        string resp = Encoding.Latin1.GetString(respBytes, 0, read);

        Assert.StartsWith("HTTP/1.1 400", resp);
        Assert.Contains(logs, l => l.Contains("400 BadRequest"));
    }

    [Fact]
    public async Task Test_ChatChannel_SubmitAndReply_Roundtrip()
    {
        const string saveId = "Farm_Save01";
        const string gameSessionId = "session-chat-01";
        var handler = new TestTransportHandler();
        var logs = new List<string>();
        using var server = new WebSocketTransportServer(
            0,
            "test-token",
            handler,
            saveId: saveId,
            gameSessionId: gameSessionId,
            logger: (msg, lvl) => logs.Add($"{lvl}: {msg}")
        );
        server.Start();

        ChatReplyPayload? receivedReply = null;
        server.OnChatReplyReceived += reply => { receivedReply = reply; };

        // Connect ClientWebSocket to /chat
        using var clientWs = new ClientWebSocket();
        clientWs.Options.SetRequestHeader("Authorization", "Bearer test-token");
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await clientWs.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/chat"), connectCts.Token);

        Assert.Equal(WebSocketState.Open, clientWs.State);
        await Task.Delay(100);
        Assert.True(server.IsChatConnected);

        // Server sends chat submit
        var submitPayload = new ChatSubmitPayload("req-chat-01", "帮我把箱子里的种子种下", "text", saveId);
        bool sent = await server.SendChatSubmitAsync(submitPayload);
        Assert.True(sent);

        // Client receives chat.submit
        var buffer = new byte[4096];
        var recvResult = await clientWs.ReceiveAsync(new ArraySegment<byte>(buffer), connectCts.Token);
        Assert.True(recvResult.EndOfMessage);
        string recvText = Encoding.UTF8.GetString(buffer, 0, recvResult.Count);
        using var doc = JsonDocument.Parse(recvText);
        Assert.Equal("chat.submit", doc.RootElement.GetProperty("messageType").GetString());
        var p = doc.RootElement.GetProperty("payload");
        Assert.Equal("req-chat-01", p.GetProperty("requestId").GetString());
        Assert.Equal("帮我把箱子里的种子种下", p.GetProperty("text").GetString());

        // Client replies with chat.reply
        var replyJson = JsonSerializer.Serialize(new
        {
            protocolVersion = AgentProtocol.Version,
            messageType = "chat.reply",
            saveId,
            gameSessionId,
            payload = new
            {
                requestId = "req-chat-01",
                status = "completed",
                replyText = "胡萝卜种子已全部播种完成！",
                tokensUsed = 12500,
                promptTokens = 11000,
                outputTokens = 1500,
                conversationId = "cid-abc-123"
            }
        });
        var replyBytes = Encoding.UTF8.GetBytes(replyJson);
        await clientWs.SendAsync(new ArraySegment<byte>(replyBytes), WebSocketMessageType.Text, true, connectCts.Token);

        // Give server loop time to enqueue and drain on Update()
        await Task.Delay(100);
        server.Update();

        Assert.NotNull(receivedReply);
        Assert.Equal("req-chat-01", receivedReply!.RequestId);
        Assert.Equal("completed", receivedReply.Status);
        Assert.Equal("胡萝卜种子已全部播种完成！", receivedReply.ReplyText);
        Assert.Equal(12500, receivedReply.TokensUsed);
        Assert.Equal("cid-abc-123", receivedReply.ConversationId);

        // Close client and verify state
        await clientWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", connectCts.Token);
        await Task.Delay(100);
        Assert.False(server.IsChatConnected);
    }
}

