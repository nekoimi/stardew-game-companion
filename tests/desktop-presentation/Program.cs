using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StardewAI.Companion.Desktop;
using StardewAI.Companion.Logging;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--controls") return DesktopControlRegression.Run(args[1]);
        VerifyBatchDiagnostics();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "work-feedback.json")));
        var fixture = document.RootElement;
        var raw = fixture.GetProperty("receipts").EnumerateArray().ToArray();
        var receipts = raw.Select(ActionPresentation.Read).ToArray();
        var original = fixture.GetRawText();
        var process = ActionPresentation.Process(receipts);
        Check(process.Count == receipts.Length - 1, "Only matching consecutive failed attempts may be merged.");
        Check(process.Single(row => row.Text.Contains("连续")).Text.Contains("连续 2 次"), "Retry evidence was lost.");
        Check(process.Any(row => row.Label.Contains("部分完成") && row.Text.Contains("背包")), "Partial pickup was hidden.");
        Check(!process.Any(row => row.Text.Contains("2 件") || row.Text.Contains("完成 0") || row.Text.Contains("跳过 12")), "Internal counters became item quantities.");
        Check(process.Any(row => row.Text.Contains("背包增加 9 件")) && process.Any(row => row.Text.Contains("浇水 2 格")), "Observed quantities were lost or double counted.");
        Check(!process.Any(row => row.Text.Contains("Requested") || row.Text.Contains("Raw technical") || row.Label.Contains("future-")), "Raw technical text reached presentation.");
        Check(ActionPresentation.Summaries(receipts).Any(row => row.Label.Contains("移动 · 已完成") && row.Text.Contains("此前有受阻")), "A later success erased earlier failures.");
        Check(ActionPresentation.Read(raw.Single(row => row.GetProperty("commandId").GetString() == "unknown")).NeedsPlayer, "Unknown outcomes must remain visible.");
        Check(ActionPresentation.Stages(receipts).Any(row => row.Text.Contains("背包增加 9 件")), "Stage quantities did not retain actual inventory evidence.");
        Check(fixture.GetRawText() == original, "Presentation mutated its input.");

        var app = new Application();
        app.Resources.MergedDictionaries.Add(new ResourceDictionary {
            Source = new Uri("/星露谷游戏伴侣;component/FarmRoom.xaml", UriKind.Relative) });
        var window = new MainWindow();
        int exit = 0;
        window.Loaded += async (_, _) =>
        {
            try
            {
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var work = new { currentGoalId = "wood", activeGoals = fixture.GetProperty("goals"),
                    recentExecutions = raw, activity = new { phase = "executing", skillId = "navigate-to", goalId = "wood" } };
                var status = new { ready = true, saveId = "farm", gameSessionId = "game", actorMode = "player",
                    controlOwner = "agent", paused = false, modelBusy = true, thinking = true,
                    clockPauseOwner = "model-thinking", connection = new { ready = true, clientName = "模拟模型" },
                    work, decisions = Array.Empty<object>(), closedDecisions = Array.Empty<object>(), milestones = Array.Empty<object>() };
                Invoke(window, "ApplyStatus", JsonSerializer.SerializeToElement(status));
                await DesktopEventStreamChecks.RunAsync((FileLogger)typeof(MainWindow).GetField("_logger", Private)!.GetValue(window)!);
                var entries = (ObservableCollection<TimelineEntry>)typeof(MainWindow).GetField("_entries", Private)!.GetValue(window)!;
                foreach (var receipt in raw.Take(7)) Invoke(window, "ApplyResult", WithBinding(receipt));
                Check(entries.Count == 0, "Routine actions and recoverable errors flooded chat.");
                Check(Control<Border>(window, "ActionFeedbackPanel").Visibility == Visibility.Visible, "Recent result is missing.");
                Check(Control<TextBlock>(window, "ActionFeedbackText").Text.Contains("背包"), "Recent result does not show the latest outcome.");
                var important = raw.Single(row => row.GetProperty("commandId").GetString() == "needs-player");
                Invoke(window, "ApplyResult", WithBinding(important));
                Invoke(window, "ApplyResult", WithBinding(important));
                Check(entries.Count == 1 && entries[0].Label == "需要你确认", "Important action duplicated or disappeared.");
                var stale = JsonSerializer.SerializeToElement(new { commandId = "other-game", gameSessionId = "other", terminalState = "failed", playerActionRequired = true });
                Invoke(window, "ApplyResult", stale);
                Check(entries.Count == 1, "An old session polluted the current chat.");
                var care = JsonSerializer.SerializeToElement(new { type = "life.care", payload = new { eventKey = "evening", kind = "evening", text = "记得回家。" } });
                Invoke(window, "ApplyEvent", care);
                Invoke(window, "ApplyEvent", care);
                Invoke(window, "ApplyEvent", JsonSerializer.SerializeToElement(new { type = "life.care", payload = new { eventKey = "done", kind = "work-done", text = "重复完成提醒" } }));
                Check(entries.Count == 2, "Reminder deduplication failed.");
                Control<TabControl>(window, "Pages").SelectedItem = Control<TabItem>(window, "WorkPage");
                var picker = Control<ListBox>(window, "GoalPicker");
                picker.SelectedItem = picker.Items.Cast<DesktopGoal>().Single(goal => goal.Id == "care");
                var rows = Control<ItemsControl>(window, "WorkDetails").Items.Cast<WorkRecord>().ToArray();
                Check(rows.Any(row => row.Text.Contains("浇水")) && !rows.Any(row => row.Text.Contains("拾取")), "Work from another goal leaked into selection.");
                Check(Control<Button>(window, "RecentObserveButton").IsEnabled, "Observation shortcut is unavailable.");
                Check(Control<TextBlock>(window, "WorkGoalText").Text.Contains("就寝时间") && !Control<TextBlock>(window, "WorkGoalText").Text.Contains("bedtime"), "Technical schedule label leaked into the goal.");
                Check(Control<Expander>(window, "OtherWorkExpander").Visibility == Visibility.Visible, "Unlinked historical work was silently discarded.");
                Check(!Control<Expander>(window, "WorkProcessExpander").IsExpanded, "Detailed process should start collapsed.");
                var phaseStatus = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.SerializeToElement(status))!;
                phaseStatus["agentPhase"] = JsonSerializer.SerializeToElement("planning-route");
                phaseStatus["activeCommandId"] = JsonSerializer.SerializeToElement("in-flight-route");
                Invoke(window, "ApplyStatus", JsonSerializer.SerializeToElement(phaseStatus));
                Check(Control<TextBlock>(window, "AgentStateText").Text == "正在寻路", "Route planning was presented as model thinking or walking.");
                phaseStatus["recoveryPending"] = JsonSerializer.SerializeToElement(true);
                Invoke(window, "ApplyStatus", JsonSerializer.SerializeToElement(phaseStatus));
                Check(Control<Button>(window, "RecentRecoverButton").Visibility == Visibility.Visible, "Missing receipt has no recovery shortcut.");
                phaseStatus.Remove("recoveryPending");
                phaseStatus.Remove("activeCommandId");
                phaseStatus["thinking"] = JsonSerializer.SerializeToElement(false);
                phaseStatus["agentPhase"] = JsonSerializer.SerializeToElement("model-retry");
                phaseStatus["modelIssue"] = JsonSerializer.SerializeToElement(new { code = "rate-limited", message = "模型服务请求频率受限。", attempt = 2, maxAttempts = 3 });
                Invoke(window, "ApplyStatus", JsonSerializer.SerializeToElement(phaseStatus));
                Check(Control<TextBlock>(window, "AgentStateText").Text == "等待重试"
                    && Control<TextBlock>(window, "ActivityText").Text.Contains("第 2/3 次"), "Model backoff is missing from the desktop status.");
                phaseStatus["modelBusy"] = JsonSerializer.SerializeToElement(false);
                phaseStatus["agentPhase"] = JsonSerializer.SerializeToElement("model-error");
                phaseStatus["modelIssue"] = JsonSerializer.SerializeToElement(new { code = "quota-exhausted", message = "模型服务余额或额度不足。" });
                Invoke(window, "ApplyStatus", JsonSerializer.SerializeToElement(phaseStatus));
                Check(Control<TextBlock>(window, "AgentStateText").Text == "模型调用失败"
                    && Control<TextBlock>(window, "ActivityText").Text.Contains("余额或额度不足"), "A permanent model error was presented as idle.");
                Invoke(window, "ApplyStatus", JsonSerializer.SerializeToElement(status));
                var draft = Control<TextBox>(window, "Input");
                draft.Text = "还没发出的要求";
                var generation = typeof(MainWindow).GetField("_saveViewGeneration", Private)!.GetValue(window);
                var selectedGoal = picker.SelectedItem;
                int chatCount = entries.Count;
                var disconnected = JsonSerializer.SerializeToElement(new { type = "desktop.connection", payload = new { status = "reconnecting" } });
                Invoke(window, "ApplyEvent", disconnected);
                Check(Control<TextBlock>(window, "ConnectionText").Text.Contains("桌面连接中断")
                    && !Control<TextBlock>(window, "ActivityText").Text.Contains("选择单人存档"), "SSE drop was misreported as a game disconnect.");
                Check(draft.Text == "还没发出的要求" && draft.IsEnabled && !Control<Button>(window, "SendButton").IsEnabled
                    && !Control<Button>(window, "AuthorityButton").IsEnabled, "Reconnect erased the draft or permitted actions from stale state.");
                Check(Equals(generation, typeof(MainWindow).GetField("_saveViewGeneration", Private)!.GetValue(window))
                    && ReferenceEquals(selectedGoal, picker.SelectedItem) && entries.Count == chatCount,
                    "SSE interruption reset save views, selection or conversation.");
                Invoke(window, "ApplyStatus", JsonSerializer.SerializeToElement(status));
                Check(draft.Text == "还没发出的要求" && Control<Button>(window, "SendButton").IsEnabled
                    && Equals(generation, typeof(MainWindow).GetField("_saveViewGeneration", Private)!.GetValue(window)),
                    "Same-session reconnect reset the draft or failed to restore controls.");
                var oldReceipt = JsonSerializer.SerializeToElement(new { saveId = "farm", gameSessionId = "game", commandId = "old-date-result",
                    skillId = "water-zone", terminalState = "succeeded", gameDate = "1:spring:17", recordedAt = "2020-01-01T00:00:00Z" });
                Invoke(window, "ApplyResult", oldReceipt);
                Check(Control<TextBlock>(window, "ActionFeedbackCaption").Text.Contains("超过 2 分钟"), "Old receipt looks like a live result.");
                var datedStatus = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.SerializeToElement(status))!;
                datedStatus["gameDate"] = JsonSerializer.SerializeToElement("1:spring:18");
                Invoke(window, "ApplyStatus", JsonSerializer.SerializeToElement(datedStatus));
                Check(Control<TextBlock>(window, "ActionFeedbackCaption").Text.Contains("之前的游戏日期"), "Yesterday's result appears current.");
                Invoke(window, "ApplyResult", WithBinding(JsonSerializer.SerializeToElement(new { commandId = "recent-player", skillId = "native-advance",
                    terminalState = "failed", playerActionRequired = true, error = new { code = "MENU_BLOCKED" } })));
                picker.SelectedItem = picker.Items.Cast<DesktopGoal>().Single(goal => goal.Id == "wood");
                window.UpdateLayout();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (args.Length > 0)
                {
                    Directory.CreateDirectory(args[0]);
                    Capture(window, Path.Combine(args[0], "work.png"));
                    Control<TabControl>(window, "Pages").SelectedItem = Control<TabItem>(window, "CommandPage");
                    window.UpdateLayout();
                    Capture(window, Path.Combine(args[0], "today.png"));
                }
                Invoke(window, "ApplyStatus", JsonSerializer.SerializeToElement(new { ready = false, connection = new { ready = false } }));
                Check(draft.Text.Length == 0 && Control<TextBlock>(window, "ConnectionText").Text == "等待游戏重新连接",
                    "A confirmed game disconnect was not distinguished from a desktop connection drop.");
                Console.WriteLine("Desktop feedback and SSE reconnect passed: drafts, views, controls, complete-frame cursor and actual game disconnect.");
            }
            catch (Exception error) { Console.Error.WriteLine(error); exit = 1; }
            finally
            {
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

    private static void VerifyBatchDiagnostics()
    {
        string root = Directory.CreateTempSubdirectory("companion-batch-logs-").FullName;
        var logger = new FileLogger(root);
        try
        {
            var processes = new DesktopProcesses(AppContext.BaseDirectory, logger);
            var write = typeof(DesktopProcesses).GetMethod("LogRuntimeEvent", Private)!;
            string turnId = Guid.NewGuid().ToString();
            string entry = "STARDEW_COMPANION_LOG " + JsonSerializer.Serialize(new {
                @event = "work_stage_completed", turnId, stageId = turnId + ":provider-call-" + new string('x', 160),
                goalId = "farm-goal", requestId = "farm-request", operation = "tend_crops", status = "completed",
                nativeActions = 3, targetCount = 12, modelRequestsDuringStage = 0, elapsedMs = 5000,
                needsReconciliation = false, apiKey = "secret-must-not-persist", text = "private-message",
            });
            Check((bool)write.Invoke(processes, new object[] { entry })!, "Stage metric was rejected by the desktop logger.");
            logger.StopAsync().GetAwaiter().GetResult();
            var line = File.ReadAllLines(Directory.GetFiles(root, "runtime-*.log").Single()).Single();
            using var parsed = JsonDocument.Parse(line);
            var details = parsed.RootElement.GetProperty("details");
            Check(details.GetProperty("nativeActions").GetInt32() == 3 &&
                details.GetProperty("modelRequestsDuringStage").GetInt32() == 0 &&
                details.GetProperty("turnId").GetString() == turnId &&
                details.GetProperty("stageId").GetString()!.StartsWith(turnId + ":"),
                "File logging lost the batch efficiency or correlation fields.");
            Check(!line.Contains("secret-must-not-persist") && !line.Contains("private-message"), "Private model data reached a diagnostic log.");
        }
        finally { logger.StopAsync().GetAwaiter().GetResult(); Directory.Delete(root, true); }
    }

    private static JsonElement WithBinding(JsonElement receipt)
    {
        var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(receipt)!;
        fields["gameSessionId"] = JsonSerializer.SerializeToElement("game");
        fields["saveId"] = JsonSerializer.SerializeToElement("farm");
        return JsonSerializer.SerializeToElement(fields);
    }

    private static T Control<T>(MainWindow window, string name) => (T)window.FindName(name);
    private static void Invoke(MainWindow window, string method, JsonElement payload) => typeof(MainWindow).GetMethod(method, Private)!.Invoke(window, new object[] { payload });
    private static void Check(bool condition, string failure) { if (!condition) throw new InvalidOperationException(failure); }
    private static void Capture(MainWindow window, string file)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(file);
        encoder.Save(stream);
    }
}
