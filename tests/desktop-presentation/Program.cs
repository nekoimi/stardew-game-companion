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

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--controls") return DesktopControlRegression.Run(args[1]);
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
                Invoke(window, "ApplyStatus", JsonSerializer.SerializeToElement(status));
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
                Console.WriteLine("Desktop work feedback passed chat noise, attention, deduplication, grouping, goal isolation, legacy and unknown-outcome checks.");
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
