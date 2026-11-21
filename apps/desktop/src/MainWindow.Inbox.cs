using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace StardewAI.Companion.Desktop;

public sealed record DesktopMilestone(string Id, string Title, string Status, string Label, double UpdatedAt, JsonElement Data);
public sealed record DesktopCareMessage(string Id, string Label, string Text, bool Unread);

public partial class MainWindow
{
    private string? _milestonesSignature;
    private string? _closedDecisionsSignature;
    private bool _changingMilestone;
    private bool _changingNotice;
    private bool _readingMessages;
    private bool _hasMessages;
    private bool _messagesChanged;
    private int? _messageRevision;
    private int _unreadMessages;
    private int? _messagesPageBefore;
    private int? _olderMessagesBefore;

    private bool CarePageVisible => Pages.SelectedItem == MessagesPage && MessageViews.SelectedItem == CarePage;

    private void ResetInboxViews()
    {
        _milestonesSignature = _closedDecisionsSignature = null;
        _changingMilestone = _changingNotice = _readingMessages = _hasMessages = _messagesChanged = false;
        _messageRevision = _messagesPageBefore = _olderMessagesBefore = null;
        _unreadMessages = 0;
        MilestonePicker.ItemsSource = ClosedDecisionPicker.ItemsSource = MessagePicker.ItemsSource = null;
        MilestoneDetails.ItemsSource = null;
        MilestoneStatus.Text = MessagesStatus.Text = ClosedDecisionStatus.Text = "进入存档后可查看。";
        DecisionFeedback.Text = "";
        MessagesPage.Header = "伙伴消息";
    }

    private void ApplyInboxStatus(JsonElement status)
    {
        var milestoneRows = status.GetProperty("milestones");
        string milestoneSignature = milestoneRows.GetRawText();
        if (_milestonesSignature != milestoneSignature)
        {
            bool first = _milestonesSignature is null;
            _milestonesSignature = milestoneSignature;
            string? selected = (MilestonePicker.SelectedItem as DesktopMilestone)?.Id;
            var items = milestoneRows.EnumerateArray().Select(MilestoneEntry).ToArray();
            MilestonePicker.ItemsSource = items;
            MilestonePicker.SelectedItem = items.FirstOrDefault(item => item.Id == selected) ?? items.FirstOrDefault();
            if (first || items.Length == 0) MilestoneStatus.Text = items.Length == 0 ? "当前没有计划节点。" : "";
        }

        var closedRows = status.GetProperty("closedDecisions");
        string closedSignature = closedRows.GetRawText();
        if (_closedDecisionsSignature != closedSignature)
        {
            string? selected = (ClosedDecisionPicker.SelectedItem as DesktopDecision)?.Id;
            _closedDecisionsSignature = closedSignature;
            var items = closedRows.EnumerateArray().Select(item => new DesktopDecision(Text(item, "id"),
                Text(item, "message"), Array.Empty<string>())).ToArray();
            ClosedDecisionPicker.ItemsSource = items;
            ClosedDecisionPicker.SelectedItem = items.FirstOrDefault(item => item.Id == selected) ?? items.FirstOrDefault();
            if (!_changingNotice) ClosedDecisionStatus.Text = items.Length == 0 ? "没有已关闭的事项。" : "";
        }

        int? revision = Number(status, "inboxRevision");
        int unread = Number(status, "unreadMessages") ?? 0;
        if (revision != _messageRevision || unread != _unreadMessages) _messagesChanged = true;
        _messageRevision = revision;
        _unreadMessages = unread;
        MessagesPage.Header = unread > 0 ? $"伙伴消息（{unread}）" : "伙伴消息";
        if (CarePageVisible && _messagesChanged && !_readingMessages && _messagesPageBefore is null)
            _ = ReadMessagesAsync(null);
    }

    private static DesktopMilestone MilestoneEntry(JsonElement row)
    {
        var title = Text(row, "title");
        var status = Text(row, "status");
        var label = status switch
        {
            "suggested" => "建议", "adopted" => "已采纳", "deferred" => "已暂缓", "cancelled" => "已撤回",
            "completed" => Text(row, "verification") == "verified" ? "已核实完成" : "完成待核实",
            "missed" => "已过期", _ => status
        };
        return new DesktopMilestone(Text(row, "id"), title, status, $"{label} · {title}", row.GetProperty("updatedAt").GetDouble(), row);
    }

    private void MilestoneSelected(object sender, SelectionChangedEventArgs e)
    {
        if (MilestoneDetails is null) return;
        var details = new List<TimelineEntry>();
        if (MilestonePicker.SelectedItem is DesktopMilestone item)
        {
            var row = item.Data;
            var date = GameDateLabel(Text(row, "targetDate"));
            var days = Number(row, "daysUntil") switch
            {
                > 0 and int remaining => $" · 还有 {remaining} 天",
                0 => " · 今天", < 0 => " · 日期已过", _ => ""
            };
            details.Add(new TimelineEntry(date + days, Text(row, "summary")));
            foreach (var prep in Rows(row, "prepItems"))
            {
                var state = Text(prep, "status") switch
                {
                    "done" => "已核实", "pending" => "待准备", "unknown" => "待核对", _ => "待核对"
                };
                var support = Text(prep, "support") == "manual" ? " · 由玩家完成" : "";
                details.Add(new TimelineEntry(state + support, string.Join("\n", new[] { Text(prep, "label"), Text(prep, "note") }
                    .Where(text => text.Length > 0))));
            }
            var terms = new List<string>();
            if (Number(row, "reservedFunds") is int funds) terms.Add($"计划保留 {funds:N0} 金");
            if (Number(row, "plannedCount") is int count) terms.Add($"计划数量 {count:N0}");
            string note = Text(row, "termsNote");
            if (note.Length > 0) terms.Add(note);
            if (terms.Count > 0) details.Add(new TimelineEntry("计划约定", string.Join("\n", terms)));
            var verification = Text(row, "verification");
            if (verification.Length > 0) details.Add(new TimelineEntry("完成核对", verification == "verified" ? "已核实" : "待核实"));
        }
        MilestoneDetails.ItemsSource = details;
        RefreshInboxControls(_gameSessionId is not null && !_closing);
    }

    private static string GameDateLabel(string value)
    {
        string[] parts = value.Split(':');
        if (parts.Length != 3) return value;
        string season = parts[1] switch { "spring" => "春", "summer" => "夏", "fall" => "秋", "winter" => "冬", _ => parts[1] };
        return $"第 {parts[0]} 年 {season} {parts[2]} 日";
    }

    private async void MilestoneClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action } || MilestonePicker.SelectedItem is not DesktopMilestone item
            || _session is not { } session || _gameSessionId is not { } game) return;
        int view = _saveViewGeneration;
        _changingMilestone = true;
        MilestoneStatus.Text = "正在保存计划…";
        RefreshInboxControls(true);
        try
        {
            await session.PostAsync("/v1/milestones", new { gameSessionId = game, action, nodeId = item.Id, updatedAt = item.UpdatedAt });
            if (view == _saveViewGeneration) MilestoneStatus.Text = action == "adopt" ? "计划已采纳。" : "计划已暂缓。";
        }
        catch (Exception ex) { if (view == _saveViewGeneration) MilestoneStatus.Text = ex.Message; }
        finally
        {
            if (view == _saveViewGeneration)
            {
                _changingMilestone = false;
                RefreshInboxControls(!_closing);
            }
        }
    }

    private void OpenMilestonesClicked(object sender, RoutedEventArgs e)
    {
        Pages.SelectedItem = MilestonesPage;
    }

    private void RefreshInboxControls(bool connected)
    {
        var status = (MilestonePicker.SelectedItem as DesktopMilestone)?.Status;
        AdoptMilestoneButton.IsEnabled = connected && !_changingMilestone && status is "suggested" or "deferred";
        DeferMilestoneButton.IsEnabled = connected && !_changingMilestone && status is "suggested" or "adopted";
        DismissDecisionButton.IsEnabled = connected && !_changingNotice && DecisionPicker.SelectedItem is DesktopDecision;
        RestoreDecisionButton.IsEnabled = connected && !_changingNotice && ClosedDecisionPicker.SelectedItem is DesktopDecision;
        RefreshMessagesButton.IsEnabled = connected && !_readingMessages;
        OlderMessagesButton.IsEnabled = RefreshMessagesButton.IsEnabled && _olderMessagesBefore.HasValue;
        ReadMessageButton.IsEnabled = RefreshMessagesButton.IsEnabled && MessagePicker.SelectedItem is DesktopCareMessage { Unread: true };
    }

    private void MessageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ReadMessageButton is not null) RefreshInboxControls(_gameSessionId is not null && !_closing);
    }

    private void ClosedDecisionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (RestoreDecisionButton is not null) RefreshInboxControls(_gameSessionId is not null && !_closing);
    }

    private async void RefreshMessagesClicked(object sender, RoutedEventArgs e) => await ReadMessagesAsync(null);

    private async void OlderMessagesClicked(object sender, RoutedEventArgs e)
    {
        if (_olderMessagesBefore is int before) await ReadMessagesAsync(before);
    }

    private async void ReadMessageClicked(object sender, RoutedEventArgs e)
    {
        if (MessagePicker.SelectedItem is DesktopCareMessage { Unread: true } message)
            await ReadMessagesAsync(_messagesPageBefore, message.Id);
    }

    private async Task ReadMessagesAsync(int? before, string? readId = null)
    {
        if (_readingMessages || _session is not { } session || _gameSessionId is not { } game) return;
        int view = _saveViewGeneration;
        _readingMessages = true;
        _messagesChanged = false;
        MessagesStatus.Text = "正在读取提醒…";
        RefreshInboxControls(true);
        try
        {
            if (readId is not null) await session.PostAsync("/v1/messages", new { gameSessionId = game, readId });
            var page = await session.PostAsync("/v1/messages", new { gameSessionId = game, before });
            if (view != _saveViewGeneration) return;
            var entries = Rows(page, "entries").Select(row =>
            {
                var display = HistoryEntry(row);
                bool unread = Flag(row, "unread");
                return new DesktopCareMessage(Text(row, "id"), display.Label + (unread ? " · 未读" : ""), display.Text, unread);
            }).ToArray();
            string? selected = (MessagePicker.SelectedItem as DesktopCareMessage)?.Id;
            MessagePicker.ItemsSource = entries;
            MessagePicker.SelectedItem = entries.FirstOrDefault(item => item.Id == selected);
            _olderMessagesBefore = Number(page, "nextBefore");
            _messagesPageBefore = before;
            _hasMessages = true;
            MessagesStatus.Text = entries.Length == 0 ? "当前存档暂无伙伴提醒。" : "";
        }
        catch (Exception ex) { if (view == _saveViewGeneration) MessagesStatus.Text = ex.Message; }
        finally
        {
            if (view == _saveViewGeneration)
            {
                _readingMessages = false;
                RefreshInboxControls(!_closing);
                if (_messagesChanged && CarePageVisible && _messagesPageBefore is null) _ = ReadMessagesAsync(null);
            }
        }
    }

    private async void DismissDecisionClicked(object sender, RoutedEventArgs e) =>
        await ChangeNoticeAsync("dismiss", DecisionPicker.SelectedItem as DesktopDecision, DecisionFeedback);

    private async void RestoreDecisionClicked(object sender, RoutedEventArgs e) =>
        await ChangeNoticeAsync("restore", ClosedDecisionPicker.SelectedItem as DesktopDecision, ClosedDecisionStatus);

    private async Task ChangeNoticeAsync(string action, DesktopDecision? decision, TextBlock feedback)
    {
        if (_changingNotice || decision is null || _session is not { } session || _gameSessionId is not { } game) return;
        int view = _saveViewGeneration;
        _changingNotice = true;
        feedback.Text = "正在保存事项状态…";
        RefreshInboxControls(true);
        try
        {
            await session.PostAsync("/v1/notices", new { gameSessionId = game, action, noticeId = decision.Id });
            if (view == _saveViewGeneration) feedback.Text = action == "dismiss"
                ? "已关闭，可在「伙伴消息」恢复。" : "已恢复，可返回「指令」回复。";
        }
        catch (Exception ex) { if (view == _saveViewGeneration) feedback.Text = ex.Message; }
        finally
        {
            if (view == _saveViewGeneration)
            {
                _changingNotice = false;
                RefreshInboxControls(!_closing);
            }
        }
    }
}
