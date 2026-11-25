using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace StardewAI.Companion.Desktop;

public sealed record DesktopGoal(string Id, string Title, string Details, string FullText);
public sealed record BedtimeOption(int Value, string Label);

public partial class MainWindow
{
    private int _saveViewGeneration;
    private string? _workSignature;
    private JsonElement _workState;
    private bool _withdrawingGoal;
    private bool _readingHistory;
    private bool _hasHistoryPage;
    private int? _historyBefore;
    private bool _savingPreferences;
    private int? _profileRevision;
    private bool _loadingUsage;

    private void InitializeSaveViews()
    {
        Bedtime.ItemsSource = (from hour in Enumerable.Range(18, 8)
                              from minute in Enumerable.Range(0, 6)
                              select new BedtimeOption(hour * 100 + minute * 10, $"{hour % 24:00}:{minute * 10:00}")).ToArray();
        Bedtime.SelectedValuePath = nameof(BedtimeOption.Value);
        Bedtime.DisplayMemberPath = nameof(BedtimeOption.Label);
        ResetSaveViews();
    }

    private void ResetSaveViews()
    {
        _saveViewGeneration++;
        _pendingChatRequests.Clear();
        _submittingRequestId = null;
        _activeCommandId = _chatActivity = "";
        _workSignature = null;
        _workState = default;
        _attentionNotices.Clear();
        _seenCareMessages.Clear();
        ActionFeedbackPanel.Visibility = Visibility.Collapsed;
        _feedbackAt = null;
        _feedbackId = _feedbackDate = _gameDate = "";
        _observedFeedbackId = "";
        _recentObservedAt = null;
        _observingRecent = false;
        _withdrawingGoal = _readingHistory = _hasHistoryPage = _savingPreferences = false;
        _historyBefore = _profileRevision = null;
        GoalPicker.ItemsSource = WorkDetails.ItemsSource = HistoryEntries.ItemsSource = null;
        WorkProcess.ItemsSource = OtherWorkDetails.ItemsSource = null;
        WorkGoalExpander.Visibility = OtherWorkExpander.Visibility = WorkProcessExpander.Visibility = Visibility.Collapsed;
        WorkOverviewText.Text = "";
        WorkStatus.Text = HistoryStatus.Text = "进入存档后可查看。";
        ProfileFeedback.Text = "进入存档后可编辑。";
        TodayGoalText.Text = WorkStatus.Text;
        AgentNextStep.Text = "";
        WorkFeedback.Text = "";
        CompanionName.Clear();
        PlayStyle.SelectedIndex = Personality.SelectedIndex = CareFrequency.SelectedIndex = Bedtime.SelectedIndex = IdlePreference.SelectedIndex = -1;
        Initiative.IsChecked = false;
        ResetMemoryView();
        ResetInboxViews();
        ResetUsageView();
    }

    private void RefreshSaveViewControls()
    {
        bool hasSave = _gameSessionId is not null && !_closing;
        bool connected = hasSave && !_statusStreamDisconnected;
        bool hasGoals = GoalPicker.HasItems;
        WorkGoalColumn.Width = hasGoals ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        WorkGoals.Visibility = hasGoals ? Visibility.Visible : Visibility.Collapsed;
        GoToChatButton.Visibility = hasSave && !hasGoals ? Visibility.Visible : Visibility.Collapsed;
        GoToChatButton.IsEnabled = connected && _partnerReady;
        WithdrawGoalButton.IsEnabled = connected && !_withdrawingGoal && !_interrupting && !_recoveryPending && GoalPicker.SelectedItem is DesktopGoal;
        HistoryRefreshButton.IsEnabled = connected && !_readingHistory;
        HistoryOlderButton.IsEnabled = HistoryRefreshButton.IsEnabled && _historyBefore.HasValue;
        ReadProfileButton.IsEnabled = connected && !_savingPreferences;
        ReadProfileButton.Visibility = ProfileFields.Visibility = hasSave ? Visibility.Visible : Visibility.Collapsed;
        ProfileFields.IsEnabled = ReadProfileButton.IsEnabled && _profileRevision.HasValue;
        RefreshMemoryControls(connected);
        RefreshInboxControls(connected);
        RefreshUsageButton.IsEnabled = _partnerServiceReady && !_closing && !_loadingUsage && !_statusStreamDisconnected;
    }

    private async void SaveViewSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, sender)) return;
        if (DiagnosticsPage is not null && Pages.SelectedItem == DiagnosticsPage && LogDirectoryText is not null)
            RefreshLogDirectory();
        if (CommandPage is not null && Pages.SelectedItem == CommandPage) TimelineScroll?.ScrollToBottom();
        if (UsagePage is not null && Pages.SelectedItem == UsagePage) await ReadUsageAsync();
        else if (_gameSessionId is not null) await LoadSelectedSaveViewAsync();
    }

    private void ResetUsageView()
    {
        _loadingUsage = false;
        UsageEntries.ItemsSource = null;
        UsageStatus.Text = _partnerServiceReady ? "等待刷新用量。" : "启动游戏后可读取本安装的模型用量。";
    }

    private async void RefreshUsageClicked(object sender, RoutedEventArgs e) => await ReadUsageAsync();

    private async Task ReadUsageAsync()
    {
        if (_loadingUsage || !_partnerServiceReady || _closing || _session is not { } session) return;
        int view = _saveViewGeneration;
        string? game = _gameSessionId;
        _loadingUsage = true;
        UsageStatus.Text = "正在读取用量…";
        RefreshUsageButton.IsEnabled = false;
        try
        {
            var summary = await session.PostAsync("/v2/usage", new { gameSessionId = game, saveId = _saveId });
            if (_session != session || view != _saveViewGeneration) return;
            var entries = new List<TimelineEntry> { new("今日 · 全部存档", Text(summary, "usageTodayText")) };
            if (game is not null)
            {
                entries.Add(new TimelineEntry("今日 · 当前存档", Text(summary, "usageSaveTodayText")));
                entries.Add(new TimelineEntry("累计 · 当前存档", Text(summary, "usageSaveTotalText")));
                foreach (var task in Rows(summary, "recentTasks"))
                    entries.Add(new TimelineEntry($"{Text(task, "title")} · {Text(task, "model")}", Text(task, "text")));
            }
            UsageEntries.ItemsSource = entries;
            UsageStatus.Text = game is null ? "进入存档后可查看该存档的今日和累计用量。" : "";
        }
        catch (Exception ex)
        {
            if (_session != session || view != _saveViewGeneration) return;
            UsageEntries.ItemsSource = null;
            UsageStatus.Text = ex.Message;
        }
        finally
        {
            if (_session == session && view == _saveViewGeneration)
            {
                _loadingUsage = false;
                RefreshUsageButton.IsEnabled = _partnerServiceReady && !_closing;
            }
        }
    }

    private async Task LoadSelectedSaveViewAsync()
    {
        if (Pages.SelectedItem == SettingsPage && SettingsTabs.SelectedItem == ProfileSettingsTab && !_profileRevision.HasValue)
            await ReadPreferencesAsync();
        if (Pages.SelectedItem == MemoryPage && !_memoryRevision.HasValue)
            await ChangeMemoryAsync(null);
        if (Pages.SelectedItem == HistoryPage && !_hasHistoryPage)
            await RefreshHistoryAsync(null);
        if (CarePageVisible && (!_hasMessages || _messagesChanged) && _messagesPageBefore is null)
            await ReadMessagesAsync(null);
    }

    private static IEnumerable<JsonElement> Rows(JsonElement data, string key) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static int? Number(JsonElement data, string key) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : null;

    private void ApplyWork(JsonElement work)
    {
        var signature = work.GetRawText();
        if (signature == _workSignature) return;
        _workSignature = signature;
        _workState = work.Clone();
        var current = Text(work, "currentGoalId");
        var selected = (GoalPicker.SelectedItem as DesktopGoal)?.Id;
        var goals = Rows(work, "activeGoals").Select(goal =>
        {
            var id = Text(goal, "id");
            var details = id == current ? "当前目标" : "进行中";
            var full = ActionPresentation.DisplayText(Text(goal, "text"));
            return new DesktopGoal(id, ActionPresentation.GoalTitle(full), details, full);
        }).ToArray();
        GoalPicker.ItemsSource = goals;
        GoalPicker.SelectedItem = goals.FirstOrDefault(goal => goal.Id == selected) ?? goals.FirstOrDefault();
        WorkStatus.Text = goals.Length == 0 ? "暂无进行中的目标。" : "";
        var currentGoal = goals.FirstOrDefault(goal => goal.Id == current) ?? goals.FirstOrDefault();
        TodayGoalText.Text = currentGoal?.Title ?? WorkStatus.Text;
        RenderWork();
    }

    private void GoalSelected(object sender, SelectionChangedEventArgs e)
    {
        if (WithdrawGoalButton is not null)
        {
            RefreshSaveViewControls();
            RenderWork();
        }
    }

    private void RenderWork(bool activityOnly = false)
    {
        if (WorkOverviewText is null || _workState.ValueKind != JsonValueKind.Object) return;
        var selected = GoalPicker.SelectedItem as DesktopGoal;
        bool isCurrent = selected is null || selected.Id == Text(_workState, "currentGoalId");
        if (isCurrent && !string.IsNullOrEmpty(_chatActivity)) WorkOverviewText.Text = _chatActivity;
        else if (!isCurrent) WorkOverviewText.Text = "所选目标的近期进展";
        if (activityOnly) return;
        var results = Rows(_workState, "recentExecutions").Select(ActionPresentation.Read).ToArray();
        var relevant = selected is null ? results : results.Where(result => result.GoalId == selected.Id).ToArray();
        var other = selected is null ? Array.Empty<WorkResult>() : results.Where(result => result.GoalId != selected.Id).ToArray();
        WorkGoalText.Text = selected?.FullText ?? "";
        var goal = Rows(_workState, "activeGoals").FirstOrDefault(row => Text(row, "id") == selected?.Id);
        if (goal.ValueKind == JsonValueKind.Object) {
            if (goal.TryGetProperty("phaseProgress", out var stage) && stage.ValueKind == JsonValueKind.Object) {
                string step = Text(stage, "step") switch {
                    "tend-crops" => "照料作物", "harvest-crops" => "收获作物", "chop-trees" => "采集木材",
                    "collect-products" => "收集产物", "store-products" => "收纳入库", _ => "核对成果"
                };
                WorkGoalText.Text += "\n\n本阶段：" + ActionPresentation.DisplayText(Text(stage, "objective"))
                    + "\n" + (Text(stage, "status") == "executing" ? "当前步骤：" + step
                        : Text(stage, "status") == "completed" ? "阶段成果已确认" : "阶段已停止：" + (Text(stage, "stopReason") switch {
                            "stamina-reserve" => "已达到保留体力边界", "inventory-full" => "背包空间不足",
                            "destination-replaced" or "destination-unavailable" => "原定箱子已变化，需要重新安排",
                            "phase-map-changed" => "地图发生变化，需要重新规划", "player-update" => "收到你的新要求",
                            "interrupted" or "phase-context-changed" => "控制或游戏状态已变化",
                            "recovery-pending" => "原生动作结果待核对", "product-collection-unconfirmed" => "产物去向尚未确认",
                            "product-source-unconfirmed" => "产物与其他物品混在一起，需要确认收集范围",
                            "existing-material-quantity-exceeded" => "同一位置的现有材料超出指定数量，需要重新安排",
                            "phase-objective-unmet" => "已完成部分工作，剩余目标需要重新安排", _ => "执行条件已变化，需要重新核对"
                        }));
                var products = Rows(stage, "storedItems").Select(row =>
                    ActionPresentation.DisplayText(Text(row, "displayName") is { Length: > 0 } label ? label : "材料") + " × "
                    + (row.TryGetProperty("count", out var count) ? count.ToString() : "待核对")).ToArray();
                if (products.Length > 0) WorkGoalText.Text += "\n已入库：" + string.Join("；", products);
            }
            if (goal.TryGetProperty("dayPlan", out var plan) && plan.ValueKind == JsonValueKind.Object) {
                string next = Text(plan, "nextStep");
                WorkGoalText.Text += "\n\n当天重点：" + ActionPresentation.DisplayText(Text(plan, "focus"))
                    + "\n下一步：" + ActionPresentation.DisplayText(next);
                var blockers = Rows(plan, "blockedBranches").Select(row =>
                    ActionPresentation.DisplayText(Text(row, "branch") + "：" + Text(row, "reason"))).ToArray();
                if (blockers.Length > 0) WorkGoalText.Text += "\n受阻事项：" + string.Join("；", blockers);
            }
            if (Text(goal, "planningIssue") == "missing-next-action")
                WorkGoalText.Text += "\n\n当前规划未给出可执行下一步，请调整要求或模型后继续。";
        }
        WorkGoalExpander.Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
        WorkDetails.ItemsSource = ActionPresentation.Stages(relevant);
        WorkProcess.ItemsSource = ActionPresentation.Process(relevant);
        OtherWorkDetails.ItemsSource = ActionPresentation.Summaries(other);
        WorkProcessExpander.Visibility = relevant.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        OtherWorkExpander.Visibility = other.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        WorkOverviewText.Text = isCurrent ? _chatActivity : "所选目标的近期进展";
        if (string.IsNullOrWhiteSpace(WorkOverviewText.Text)) WorkOverviewText.Text = relevant.Length == 0
            ? "暂时没有工作记录，交代农活后可在这里查看进展。" : "近期工作摘要";
        if (selected is not null && relevant.Length == 0)
            WorkDetails.ItemsSource = new[] { new WorkRecord("暂无关联记录", "所选目标尚无近期动作记录。旧版未关联目标的回执可在下方历史记录中查看。") };
        AgentNextStep.Text = "";
    }

    private async void WithdrawGoalClicked(object sender, RoutedEventArgs e)
    {
        if (_session is not { } session || _gameSessionId is not { } game || GoalPicker.SelectedItem is not DesktopGoal goal) return;
        int view = _saveViewGeneration;
        if (!new ConfirmationDialog("撤回目标", $"撤回目标“{goal.Title}”？已完成的动作会保留。", "撤回目标").ShowFor(this)
            || view != _saveViewGeneration) return;
        _withdrawingGoal = true;
        WorkFeedback.Text = "正在撤回目标…";
        RefreshSaveViewControls();
        try
        {
            await session.PostAsync("/v2/control", new { gameSessionId = game, saveId = _saveId, action = "cancel", goalId = goal.Id });
            if (view == _saveViewGeneration) WorkFeedback.Text = "所选目标已撤回。";
        }
        catch (Exception ex) { if (view == _saveViewGeneration) WorkFeedback.Text = ex.Message; }
        finally
        {
            if (view == _saveViewGeneration)
            {
                _withdrawingGoal = false;
                RefreshSaveViewControls();
            }
        }
    }

    private async void RefreshHistoryClicked(object sender, RoutedEventArgs e) => await RefreshHistoryAsync(null);

    private async void OlderHistoryClicked(object sender, RoutedEventArgs e)
    {
        if (_historyBefore.HasValue) await RefreshHistoryAsync(_historyBefore);
    }

    private async Task RefreshHistoryAsync(int? before)
    {
        if (_readingHistory || _session is not { } session || _gameSessionId is not { } game) return;
        int view = _saveViewGeneration;
        _readingHistory = true;
        HistoryStatus.Text = "正在读取对话记录…";
        RefreshSaveViewControls();
        try
        {
            var page = await session.PostAsync("/v2/history", new { gameSessionId = game, saveId = _saveId, before });
            if (view != _saveViewGeneration) return;
            // Each page arrives newest first. Preserve its cursor and show the entries in conversation order.
            var rows = Rows(page, "entries").Reverse().Select(HistoryEntry).ToArray();
            _historyBefore = Number(page, "nextBefore");
            _hasHistoryPage = true;
            HistoryEntries.ItemsSource = rows;
            HistoryStatus.Text = rows.Length == 0 ? "当前存档暂无对话记录。"
                : before.HasValue ? "较早的对话记录" : "当前存档的最新对话";
            HistoryScroll.ScrollToBottom();
        }
        catch (Exception ex) { if (view == _saveViewGeneration) HistoryStatus.Text = ex.Message; }
        finally
        {
            if (view == _saveViewGeneration)
            {
                _readingHistory = false;
                RefreshSaveViewControls();
            }
        }
    }

    private static TimelineEntry HistoryEntry(JsonElement row)
    {
        var label = Text(row, "speaker");
        var date = Text(row, "gameDate");
        if (date.Length > 0) label += $" · {GameDateLabel(date)}";
        var state = Flag(row, "rolledBack") ? "已回滚" : Text(row, "decisionStatus") switch
        {
            "pending" => "待确认",
            "answered" => "已回复",
            "dismissed" => "已关闭",
            _ => ""
        };
        if (state.Length > 0) label += $" · {state}";
        return new TimelineEntry(label, Text(row, "text"));
    }

    private async void ReadProfileClicked(object sender, RoutedEventArgs e) => await ReadPreferencesAsync();

    private Task ReadPreferencesAsync() => UpdatePreferencesAsync("/v2/profile", game => new { gameSessionId = game, saveId = _saveId },
        result => { ApplyProfile(result); ApplyInitiative(result); }, "已读取当前存档的偏好。");

    private async void SaveProfileClicked(object sender, RoutedEventArgs e)
    {
        if (_profileRevision is not int revision || Bedtime.SelectedValue is not int bedtime) return;
        var name = CompanionName.Text.Trim();
        if (name.Length == 0)
        {
            ProfileFeedback.Text = "请填写伙伴名字。";
            return;
        }
        var patch = new Dictionary<string, object?>
        {
            ["companionName"] = name, ["playStyle"] = PlayStyle.SelectedValue,
            ["personality"] = Personality.SelectedValue, ["careFrequency"] = CareFrequency.SelectedValue,
            ["bedtime"] = bedtime,
        };
        if (revision == 0) patch["onboarded"] = true;
        await UpdatePreferencesAsync("/v2/profile", game => new { gameSessionId = game, saveId = _saveId, expectedRevision = revision, patch },
            ApplyProfile, "存档偏好已保存。");
    }

    private async void SaveInitiativeClicked(object sender, RoutedEventArgs e) => await UpdatePreferencesAsync("/v2/initiative",
        game => new { gameSessionId = game, saveId = _saveId, mode = Initiative.IsChecked == true ? "free" : "command", preference = IdlePreference.SelectedValue },
        ApplyInitiative, "主动帮忙设置已保存。");

    private async Task UpdatePreferencesAsync(string route, Func<string, object> body, Action<JsonElement> apply, string success)
    {
        if (_savingPreferences || _session is not { } session || _gameSessionId is not { } game) return;
        int view = _saveViewGeneration;
        _savingPreferences = true;
        ProfileFeedback.Text = "正在等待确认…";
        RefreshSaveViewControls();
        try
        {
            var result = await session.PostAsync(route, body(game));
            if (view != _saveViewGeneration) return;
            apply(result);
            ProfileFeedback.Text = success;
        }
        catch (Exception ex) { if (view == _saveViewGeneration) ProfileFeedback.Text = ex.Message; }
        finally
        {
            if (view == _saveViewGeneration)
            {
                _savingPreferences = false;
                RefreshSaveViewControls();
            }
        }
    }

    private void ApplyProfile(JsonElement result)
    {
        _profileRevision = Number(result, "profileRevision");
        var profile = result.GetProperty("profile");
        CompanionName.Text = ProfileText("companionName", "阿星");
        PlayStyle.SelectedValue = ProfileText("playStyle", "earn");
        Personality.SelectedValue = ProfileText("personality", "gentle");
        CareFrequency.SelectedValue = ProfileText("careFrequency", "moderate");
        Bedtime.SelectedValue = Number(profile, "bedtime") ?? 2400;

        string ProfileText(string name, string fallback) => Text(profile, name) is { Length: > 0 } value ? value : fallback;
    }

    private void ApplyInitiative(JsonElement result)
    {
        Initiative.IsChecked = Text(result, "initiativeMode") == "free";
        IdlePreference.SelectedValue = Text(result, "idlePreference");
    }
}
