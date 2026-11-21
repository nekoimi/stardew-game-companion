using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace StardewAI.Companion.Desktop;

public sealed record DesktopMemory(string Id, string Kind, string Label, string Text);

public partial class MainWindow
{
    private bool _loadingMemory;
    private int? _memoryRevision;

    private void ResetMemoryView()
    {
        _loadingMemory = false;
        _memoryRevision = null;
        MemoryPicker.ItemsSource = null;
        MemoryKind.SelectedValue = "preference";
        MemoryText.Clear();
        MemoryFeedback.Text = "进入存档后可读取伙伴记忆。";
    }

    private void RefreshMemoryControls(bool connected)
    {
        bool ready = connected && !_loadingMemory;
        bool editable = ready && _memoryRevision.HasValue;
        var selected = MemoryPicker.SelectedItem as DesktopMemory;
        ReadMemoryButton.IsEnabled = ready;
        NewMemoryButton.IsEnabled = MemoryPicker.IsEnabled = editable;
        MemoryKind.IsEnabled = editable && selected is null;
        MemoryText.IsEnabled = SaveMemoryButton.IsEnabled = editable && selected?.Kind != "event";
        DeleteMemoryButton.IsEnabled = editable && selected is not null;
    }

    private async void ReadMemoryClicked(object sender, RoutedEventArgs e) => await ChangeMemoryAsync(null);

    private void NewMemoryClicked(object sender, RoutedEventArgs e)
    {
        MemoryPicker.SelectedItem = null;
        MemoryKind.SelectedValue = "preference";
        MemoryText.Clear();
        MemoryFeedback.Text = "新增偏好或约定";
        MemoryText.Focus();
    }

    private void MemorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (MemoryKind is null || MemoryText is null) return;
        var entry = MemoryPicker.SelectedItem as DesktopMemory;
        MemoryKind.SelectedValue = entry?.Kind ?? "preference";
        MemoryText.Text = entry?.Text ?? "";
        RefreshMemoryControls(_gameSessionId is not null && !_closing);
    }

    private async void SaveMemoryClicked(object sender, RoutedEventArgs e)
    {
        var text = MemoryText.Text.Trim();
        if (text.Length == 0)
        {
            MemoryFeedback.Text = "请填写记忆内容。";
            return;
        }
        var selected = MemoryPicker.SelectedItem as DesktopMemory;
        await ChangeMemoryAsync(selected is null ? "add" : "correct", selected?.Id, text);
    }

    private async void DeleteMemoryClicked(object sender, RoutedEventArgs e)
    {
        if (MemoryPicker.SelectedItem is not DesktopMemory entry) return;
        int view = _saveViewGeneration;
        if (MessageBox.Show(this, "删除所选记忆？删除后无法撤销。", "删除记忆", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes && view == _saveViewGeneration)
            await ChangeMemoryAsync("delete", entry.Id);
    }

    private async Task ChangeMemoryAsync(string? action, string? entryId = null, string? text = null)
    {
        if (_loadingMemory || _session is not { } session || _gameSessionId is not { } game) return;
        int view = _saveViewGeneration;
        object body = action is null ? new { gameSessionId = game, saveId = _saveId }
            : new { gameSessionId = game, saveId = _saveId, op = action, id = entryId, kind = MemoryKind.SelectedValue, text, expectedRevision = _memoryRevision };
        var selectedId = action == "delete" ? null : (MemoryPicker.SelectedItem as DesktopMemory)?.Id;
        _loadingMemory = true;
        MemoryFeedback.Text = action is null ? "正在读取记忆…" : "正在保存…";
        RefreshMemoryControls(true);
        try
        {
            var result = await session.PostAsync("/v2/memory", body);
            if (view != _saveViewGeneration) return;
            _memoryRevision = Number(result, "memoryRevision");
            var entries = Rows(result, "entries").Reverse().Select(MemoryEntry).ToArray();
            MemoryPicker.ItemsSource = entries;
            MemoryPicker.SelectedItem = entries.FirstOrDefault(entry => entry.Id == selectedId);
            if (MemoryPicker.SelectedItem is null) MemoryText.Clear();
            MemoryFeedback.Text = action switch
            {
                "delete" => "记忆已删除。",
                "add" or "correct" => "记忆已保存。",
                _ => entries.Length == 0 ? "当前存档暂无记忆。" : $"当前存档 · {entries.Length} 条记忆"
            };
        }
        catch (Exception ex) { if (view == _saveViewGeneration) MemoryFeedback.Text = ex.Message; }
        finally
        {
            if (view == _saveViewGeneration)
            {
                _loadingMemory = false;
                RefreshMemoryControls(_gameSessionId is not null && !_closing);
            }
        }
    }

    private static DesktopMemory MemoryEntry(JsonElement entry)
    {
        var kind = Text(entry, "kind");
        var label = kind switch { "preference" => "偏好", "agreement" => "约定", "event" => "共同经历", _ => "记忆" };
        var source = Text(entry, "source") switch { "player" => "你", "companion" => "伙伴", "system" => "游戏记录", _ => "" };
        if (source.Length > 0) label += $" · {source}";
        var date = Text(entry, "gameDate");
        if (date.Length > 0 && date != "unknown") label += $" · {date}";
        return new DesktopMemory(Text(entry, "id"), kind, label, Text(entry, "text"));
    }
}
