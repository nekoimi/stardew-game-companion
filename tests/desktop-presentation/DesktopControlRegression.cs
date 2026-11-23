using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using StardewAI.Companion.Desktop;

// Run only from the isolated copy prepared by start-desktop-acceptance.ps1.
internal static class DesktopControlRegression
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static int Run(string evidence)
    {
        evidence = Path.GetFullPath(evidence);
        var expected = Path.GetFullPath(Path.Combine(evidence, "..", "desktop"));
        if (!string.Equals(expected, Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase)
            || !File.Exists(Path.Combine(evidence, "desktop-package.json"))
            || Environment.GetEnvironmentVariable("STARDEW_AI_SESSION_CONFIG") is not { } config
            || !File.Exists(config) || Environment.GetEnvironmentVariable("NODE_OPTIONS") is not { Length: > 0 })
            throw new InvalidOperationException("Controls regression requires the isolated simulated desktop fixture.");
        var package = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(evidence, "desktop-package.json")));
        if (!package.GetProperty("simulated").GetBoolean()) throw new InvalidOperationException("Mock API required.");
        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary {
            Source = new Uri("/星露谷游戏伴侣;component/FarmRoom.xaml", UriKind.Relative) });
        var window = new MainWindow();
        var results = new ConcurrentQueue<JsonElement>();
        var replies = new ConcurrentDictionary<string, bool>();
        string lastRequest = "";
        var passed = new List<string>();
        int exit = 0;
        int sequence = 0;
        var report = Path.Combine(evidence, "desktop-controls.json");
        DesktopSession Session() => Field<DesktopSession>(window, "_session");
        object Binding() => new { saveId = Field<string>(window, "_saveId"), gameSessionId = Field<string>(window, "_gameSessionId") };
        async Task<JsonElement> Control(string action)
        {
            var binding = JsonSerializer.SerializeToElement(Binding());
            return await Session().PostAsync("/v2/control", new { saveId = binding.GetProperty("saveId").GetString(),
                gameSessionId = binding.GetProperty("gameSessionId").GetString(), action });
        }
        async Task Send(string text, string mode = "send")
        {
            var binding = JsonSerializer.SerializeToElement(Binding());
            lastRequest = Guid.NewGuid().ToString();
            await Session().PostAsync("/v2/commands", new { saveId = binding.GetProperty("saveId").GetString(),
                gameSessionId = binding.GetProperty("gameSessionId").GetString(), requestId = lastRequest, text, mode });
        }
        async Task Driver(string channel, string action)
        {
            string label = "controls-" + ++sequence;
            string request = Path.Combine(evidence, channel == "lifecycle" ? "lifecycle-request.json" : "life-ui-request.json");
            string response = Path.Combine(evidence, $"{channel}-{label}.json");
            await Wait(() => !File.Exists(request), "Previous driver request did not finish");
            File.WriteAllText(request + ".tmp", JsonSerializer.Serialize(new { label, action }));
            File.Move(request + ".tmp", request);
            await Wait(() => File.Exists(response), $"Driver {action} did not return", 90);
            var result = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(response));
            if (result.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
        }
        async Task Idle() => await Wait(() => replies.ContainsKey(lastRequest) && !Field<bool>(window, "_modelBusy") && Field<string>(window, "_activeCommandId").Length == 0,
            "Native action or model did not stop", 60);
        async Task<string> Position() => (await Control("observe")).GetProperty("observation").GetProperty("companion") is var actor
            ? $"{actor.GetProperty("locationId")}:{actor.GetProperty("tileX")}:{actor.GetProperty("tileY")}" : "";
        async Task MovementFrom(string origin)
        {
            var watch = Stopwatch.StartNew();
            while (await Position() == origin)
            {
                if (watch.Elapsed.TotalSeconds > 20) throw new TimeoutException("Native actor did not actually move before takeover/reconnect");
                await Task.Delay(100);
            }
            if (Field<string>(window, "_activeCommandId").Length == 0) throw new InvalidOperationException("Navigation finished before the control boundary was exercised");
        }
        void Pass(string name) { passed.Add(name); File.WriteAllText(report, JsonSerializer.Serialize(new { passed, results = results.ToArray() })); }
        window.Loaded += async (_, _) =>
        {
            try
            {
                ((PasswordBox)window.FindName("ApiKey")).Password = "accept-usage";
                typeof(MainWindow).GetMethod("LaunchClicked", Private)!.Invoke(window, new object[] { window, new RoutedEventArgs() });
                await Wait(() => typeof(MainWindow).GetField("_session", Private)!.GetValue(window) is DesktopSession s &&
                    Field<DesktopProcesses>(s, "_processes").GameRunning, "Desktop did not launch the isolated game", 60);
                Session().EventReceived += (_, message) => {
                    if (message.GetProperty("type").GetString() == "result") results.Enqueue(message.GetProperty("payload").Clone());
                    if (message.GetProperty("type").GetString() == "life.chat.reply" && message.GetProperty("payload") is var reply
                        && reply.TryGetProperty("status", out var state) && state.GetString() is "completed" or "failed")
                        replies[reply.GetProperty("requestId").GetString()!] = true;
                };
                await Driver("lifecycle", "snapshot");
                await Driver("lifecycle", "load");
                await Wait(() => typeof(MainWindow).GetField("_gameSessionId", Private)!.GetValue(window) is string { Length: > 0 },
                    "Isolated save did not bind", 90);
                await Driver("life-ui", "close");

                await Control("handoff");
                await Send("等待接管");
                await Wait(() => Field<bool>(window, "_modelBusy"), "Model did not start");
                await Driver("life-ui", "takeover-key");
                await Wait(() => Field<string>(window, "_owner") == "human", "F9 did not take ownership");
                await Idle();
                await Task.Delay(1000);
                if (Field<string>(window, "_owner") != "human" || !results.IsEmpty) throw new InvalidOperationException("Thinking takeover allowed a delayed action");
                Pass("F9 during model thinking; no delayed action or automatic handoff");

                await Control("handoff");
                var origin = await Position();
                await Send("走到出口附近");
                await Wait(() => Field<string>(window, "_activeCommandId").Length > 0, "Native navigation did not start");
                await MovementFrom(origin);
                string walking = Field<string>(window, "_activeCommandId");
                await Driver("life-ui", "takeover-key");
                await Idle();
                if (Field<string>(window, "_owner") != "human" || !results.Any(row => row.GetProperty("commandId").GetString() == walking))
                    throw new InvalidOperationException("Walking takeover did not preserve the native receipt");
                Pass("F9 during native navigation; final receipt retained");

                await Control("handoff");
                await Send("等待接管");
                await Wait(() => Field<bool>(window, "_modelBusy"), "Model did not start for replacement");
                await Send("走一步", "interrupt");
                await Idle();
                if (results.Count < 2) throw new InvalidOperationException("Replacement did not execute a new native action");
                Pass("Interrupt replaces the old turn after settlement");

                origin = await Position();
                await Send("走到出口附近");
                await Wait(() => Field<string>(window, "_activeCommandId").Length > 0, "Reconnect navigation did not start");
                await MovementFrom(origin);
                string interrupted = Field<string>(window, "_activeCommandId");
                // Terminate only the runtime owned by this isolated desktop session.
                Field<Process>(Field<DesktopProcesses>(Session(), "_processes"), "_runtime").Kill();
                await Wait(() => Session().CanReconnect, "Runtime exit was not detected");
                await Session().ReconnectAsync();
                await Wait(() => Field<bool>(window, "_recoveryPending"), "Reconnect did not require reconciliation");
                await Control("recover");
                await Wait(() => !Field<bool>(window, "_recoveryPending"), "Old action remained unresolved");
                if (!results.Any(row => row.GetProperty("commandId").GetString() == interrupted))
                    throw new InvalidOperationException("Reconnection lost the interrupted action receipt");
                Pass("Owned runtime reconnect reconciles old action without re-execution");
                await Control("resume");
                await Control("handoff");
                var date = Field<string>(window, "_gameDate");
                await Send("等到明天再确认农场情况");
                await Idle();
                await Wait(() => ((ListBox)window.FindName("GoalPicker")).HasItems, "Waiting goal was not saved");
                await Send("现在回床睡觉");
                await Wait(() => Field<string>(window, "_gameDate") is { Length: > 0 } next && next != date,
                    "Native sleep did not advance to the next day", 180);
                await Wait(() => !Field<bool>(window, "_modelBusy") && !((ListBox)window.FindName("GoalPicker")).HasItems,
                    "Waiting goal did not resume from the next-day observation", 90);
                if (!results.Any(row => row.GetProperty("skillId").GetString() == "sleep" && row.GetProperty("terminalState").GetString() == "succeeded"))
                    throw new InvalidOperationException("Sleep has no successful native receipt");
                Pass("Native sleep and next-day automatic continuation of the same waiting goal");
                await Control("takeover");
                await Driver("lifecycle", "save-exit");
                Console.WriteLine("Isolated desktop control regression passed.");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error); exit = 1;
                File.WriteAllText(report, JsonSerializer.Serialize(new { passed, error = error.ToString(), results = results.ToArray() }));
            }
            finally
            {
                // Every handle belongs to the verified isolated fixture above.
                if (typeof(MainWindow).GetField("_session", Private)!.GetValue(window) is DesktopSession session)
                {
                    var owned = Field<DesktopProcesses>(session, "_processes");
                    if (owned.GameRunning) Field<Process>(owned, "_game").Kill();
                    await session.StopAsync();
                }
                typeof(MainWindow).GetField("_closing", Private)!.SetValue(window, true);
                if (typeof(MainWindow).GetField("_plugins", Private)!.GetValue(window) is IAsyncDisposable plugins) await plugins.DisposeAsync();
                var logger = typeof(MainWindow).GetField("_logger", Private)!.GetValue(window)!;
                await (Task)logger.GetType().GetMethod("StopAsync", Private)!.Invoke(logger, null)!;
                typeof(MainWindow).GetField("_exitReady", Private)!.SetValue(window, true);
                window.Close();
            }
        };
        app.Run(window);
        return exit;
    }

    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static async Task Wait(Func<bool> check, string failure, int seconds = 30)
    {
        var watch = Stopwatch.StartNew();
        while (!check())
        {
            if (watch.Elapsed.TotalSeconds > seconds) throw new TimeoutException(failure);
            await Task.Delay(100);
        }
    }
}
