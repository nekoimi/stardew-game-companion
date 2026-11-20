using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace StardewAI.Companion.Desktop;

public sealed record DesktopGoal(string Id, string Title, string Details);
public sealed record BedtimeOption(int Value, string Label);

public partial class MainWindow
{
    private int _saveViewGeneration;
    private string? _workSignature;
    private bool _withdrawingGoal;
    private bool _readingHistory;
    private bool _hasHistoryPage;
    private int? _historyBefore;
    private bool _savingPreferences;
    private int? _profileRevision;

    private void InitializeSaveViews()
    {
        Bedtime.ItemsSource = (from hour in Enumerable.Range(18, 8)
                              from minute in Enumerable.Range(0, hour == 25 ? 1 : 6)
                              select new BedtimeOption(hour * 100 + minute * 10, $"{hour % 24:00}:{minute * 10:00}")).ToArray();
        Bedtime.SelectedValuePath = nameof(BedtimeOption.Value);
        Bedtime.DisplayMemberPath = nameof(BedtimeOption.Label);
        ResetSaveViews();
    }

    private void ResetSaveViews()
    {
        _saveViewGeneration++;
        _workSignature = null;
        _withdrawingGoal = _readingHistory = _hasHistoryPage = _savingPreferences = false;
        _historyBefore = _profileRevision = null;
        GoalPicker.ItemsSource = WorkDetails.ItemsSource = HistoryEntries.ItemsSource = null;
        WorkStatus.Text = HistoryStatus.Text = ProfileFeedback.Text = "进入存档后可查看。";
        WorkFeedback.Text = "";
        CompanionName.Clear();
        PlayStyle.SelectedIndex = Personality.SelectedIndex = CareFrequency.SelectedIndex = Bedtime.SelectedIndex = IdlePreference.SelectedIndex = -1;
        Initiative.IsChecked = false;
        ResetMemoryView();
        ResetInboxViews();
    }

    private void RefreshSaveViewControls()
    {
        bool connected = _gameSessionId is not null && !_closing;
        WithdrawGoalButton.IsEnabled = connected && !_withdrawingGoal && !_interrupting && GoalPicker.SelectedItem is DesktopGoal;
        HistoryRefreshButton.IsEnabled = connected && !_readingHistory;
        HistoryOlderButton.IsEnabled = HistoryRefreshButton.IsEnabled && _historyBefore.HasValue;
        ReadProfileButton.IsEnabled = connected && !_savingPreferences;
        ProfileFields.IsEnabled = ReadProfileButton.IsEnabled && _profileRevision.HasValue;
        CompanionIdentityFields.Visibility = _activeSource == "external" ? Visibility.Collapsed : Visibility.Visible;
        RefreshMemoryControls(connected);
        RefreshInboxControls(connected);
    }

    private async void SaveViewSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_gameSessionId is not null && ReferenceEquals(e.Source, sender))
            await LoadSelectedSaveViewAsync();
    }

    private async Task LoadSelectedSaveViewAsync()
    {
        if (Pages.SelectedItem == SettingsPage && !_profileRevision.HasValue)
            await ReadPreferencesAsync();
        if (Pages.SelectedItem == MemoryPage && !_memoryRevision.HasValue)
            await ChangeMemoryAsync(null);
        if (Pages.SelectedItem == CommandPage && CommandViews.SelectedItem == HistoryPage && !_hasHistoryPage)
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
        var current = Text(work, "currentGoalId");
        var selected = (GoalPicker.SelectedItem as DesktopGoal)?.Id;
        var goals = Rows(work, "activeGoals").Select(goal =>
        {
            var id = Text(goal, "id");
            var details = Text(goal, "summary");
            if (id == current) details = details.Length > 0 ? $"当前目标 · {details}" : "当前目标";
            return new DesktopGoal(id, Text(goal, "text"), details);
        }).ToArray();
        GoalPicker.ItemsSource = goals;
        GoalPicker.SelectedItem = goals.FirstOrDefault(goal => goal.Id == selected) ?? goals.FirstOrDefault();
        WorkStatus.Text = goals.Length == 0 ? "暂无进行中的目标。" : "";

        var details = new List<TimelineEntry>();
        if (work.TryGetProperty("activity", out var activity))
        {
            Add("当前进度", Text(activity, "summary"));
            Add("下一步", Text(activity, "nextStep"));
        }
        Add("暂停或等待", Text(work, "pauseReason"));
        foreach (var condition in Rows(work, "waitingConditions")) Add("等待条件", condition.GetString() ?? "");
        foreach (var todo in Rows(work, "recentTodos")) Add("待办", Text(todo, "intent"));
        foreach (var execution in Rows(work, "recentExecutions").Reverse())
        {
            var date = Text(execution, "gameDate");
            var label = date.Length > 0 ? $"工作记录 · {date}" : "工作记录 · 日期未记录";
            Add(label, $"{Text(execution, "taskTitle")}\n{Text(execution, "summary")}");
        }
        WorkDetails.ItemsSource = details;

        void Add(string label, string text)
        {
            if (!string.IsNullOrWhiteSpace(text)) details.Add(new TimelineEntry(label, text));
        }
    }

    private void GoalSelected(object sender, SelectionChangedEventArgs e)
    {
        if (WithdrawGoalButton is not null) RefreshSaveViewControls();
    }

    private async void WithdrawGoalClicked(object sender, RoutedEventArgs e)
    {
        if (_session is not { } session || _gameSessionId is not { } game || GoalPicker.SelectedItem is not DesktopGoal goal) return;
        int view = _saveViewGeneration;
        if (MessageBox.Show(this, $"撤回目标“{goal.Title}”？已完成的动作会保留。", "撤回目标",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes
            || view != _saveViewGeneration) return;
        _withdrawingGoal = true;
        WorkFeedback.Text = "正在撤回目标…";
        RefreshSaveViewControls();
        try
        {
            await session.PostAsync("/v1/control", new { gameSessionId = game, action = "cancel", goalId = goal.Id });
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
            var page = await session.PostAsync("/v1/history", new { gameSessionId = game, before });
            if (view != _saveViewGeneration) return;
            var rows = Rows(page, "entries").Select(HistoryEntry).ToArray();
            _historyBefore = Number(page, "nextBefore");
            _hasHistoryPage = true;
            HistoryEntries.ItemsSource = rows;
            HistoryStatus.Text = rows.Length == 0 ? "当前存档暂无对话记录。"
                : before.HasValue ? "较早的对话记录" : "当前存档的最新对话";
            HistoryScroll.ScrollToTop();
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
        if (date.Length > 0) label += $" · {date}";
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

    private Task ReadPreferencesAsync() => UpdatePreferencesAsync("/v1/profile", game => new { gameSessionId = game },
        result => { ApplyProfile(result); ApplyInitiative(result); }, _activeSource == "external"
            ? "原伙伴的人格由原客户端管理。" : "已读取当前存档的伙伴设置。");

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
        await UpdatePreferencesAsync("/v1/profile", game => new { gameSessionId = game, expectedRevision = revision, patch },
            ApplyProfile, "伙伴设置已保存。");
    }

    private async void SaveInitiativeClicked(object sender, RoutedEventArgs e) => await UpdatePreferencesAsync("/v1/initiative",
        game => new { gameSessionId = game, mode = Initiative.IsChecked == true ? "free" : "command", preference = IdlePreference.SelectedValue },
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
