using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace StardewAI.Companion.Desktop;

public sealed record OriginalSessionOption(string Id, string Label);
public sealed record DesktopProvider(string Id, string Name, string DefaultModel, bool AllowCmd,
    string DefaultEffort, string[] Efforts);
public partial class MainWindow
{
    private readonly Dictionary<string, ClientPreferences> _clients = new(StringComparer.Ordinal);
    private string _backend = "pi";
    private string? _activeSource;
    private bool _partnerReady;
    private bool _partnerServiceReady;
    private bool _adapterOwned;
    private bool _partnerChangePending;
    private string SelectedSource => PartnerSource.SelectedValue as string ?? "builtin";

    private void InitializePartnerSettings(DesktopSettings settings)
    {
        if (settings.Clients is not null)
            foreach (var (backend, preferences) in settings.Clients) _clients[backend] = preferences;
        _clients.TryAdd("pi", new(settings.Model, settings.PiPath));
        try
        {
            string path = Path.Combine(DesktopInstallation.ModDirectory(_root), "desktop-providers.json");
            BackendPicker.ItemsSource = JsonSerializer.Deserialize<DesktopProvider[]>(File.ReadAllText(path), JsonSerializerOptions.Web);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log($"读取模型客户端列表失败：{ex.Message}");
            ModelHint.Text = "模型客户端列表不可用，请完整解压发行包。";
        }
        _backend = settings.Backend;
        BackendPicker.SelectedValue = settings.Backend;
        PartnerSource.SelectedValue = settings.ConnectionSource == "external" ? "external" : "builtin";
        OriginalWorkspace.Text = settings.OriginalWorkspace;
        OriginalData.Text = settings.OriginalDataDirectory;
        OriginalClient.Text = settings.OriginalClient;
        OriginalName.Text = settings.OriginalName;
    }

    private Dictionary<string, ClientPreferences> ReadClientPreferences()
    {
        if (BackendPicker.SelectedItem is not null)
            _clients[_backend] = new(Model.Text.Trim(), ClientPath.Text.Trim(), ReasoningEffort.SelectedValue as string ?? "");
        return new(_clients, StringComparer.Ordinal);
    }

    private void BackendSelected(object sender, SelectionChangedEventArgs e)
    {
        if (ClientPath is null || BackendPicker.SelectedItem is not DesktopProvider provider) return;
        BackendError.Text = "";
        if (e.RemovedItems.Count > 0) ReadClientPreferences();
        _backend = provider.Id;
        var settings = _clients.GetValueOrDefault(provider.Id) ?? new();
        Model.Text = settings.Model;
        ClientPath.Text = settings.Path;
        ReasoningEffort.ItemsSource = provider.Efforts
            .Select(value => new ComboBoxItem { Tag = value, Content = ReasoningLabel(value) }).ToArray();
        ReasoningEffort.SelectedValue = provider.Efforts.Contains(settings.Effort) ? settings.Effort : provider.DefaultEffort;
        ModelHint.Text = provider.DefaultModel.Length == 0 ? "留空使用客户端当前配置的模型。"
            : $"留空使用 {provider.DefaultModel}。";
    }

    private static string ReasoningLabel(string value) => value switch
    {
        "default" => "默认",
        "off" => "关闭",
        "minimal" => "最低",
        "low" => "低",
        "medium" => "中",
        "high" => "高",
        "xhigh" => "极高",
        "max" => "最高",
        _ => value
    };

    private void PartnerSourceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (BackendError is not null) BackendError.Text = "";
        if (OriginalFields is not null) RefreshPartnerControls();
    }

    private void BrowseClient(object sender, RoutedEventArgs e)
    {
        if (BackendPicker.SelectedItem is DesktopProvider provider) PickClient(ClientPath, provider.AllowCmd);
    }

    private void BrowseOriginalClient(object sender, RoutedEventArgs e) => PickClient(OriginalClient, true);

    private void PickClient(TextBox field, bool allowCmd)
    {
        var picker = new OpenFileDialog { Title = "选择模型客户端", Filter = allowCmd ? "客户端 (*.exe;*.cmd)|*.exe;*.cmd" : "客户端 (*.exe)|*.exe" };
        if (picker.ShowDialog(this) == true) field.Text = picker.FileName;
    }

    private void BrowseOriginalWorkspace(object sender, RoutedEventArgs e) => PickOriginalFolder(OriginalWorkspace, "选择原会话的工作目录");

    private void BrowseOriginalData(object sender, RoutedEventArgs e) => PickOriginalFolder(OriginalData, "选择原 MiniMax Code 数据目录");

    private void PickOriginalFolder(TextBox field, string title)
    {
        var picker = new OpenFolderDialog { Title = title };
        if (Directory.Exists(field.Text)) picker.InitialDirectory = field.Text;
        if (picker.ShowDialog(this) == true) field.Text = picker.FolderName;
    }

    private void OriginalLocationChanged(object sender, TextChangedEventArgs e)
    {
        if (OriginalSessions is null) return;
        OriginalSessions.ItemsSource = null;
        OriginalIdle.IsChecked = false;
        RefreshPartnerControls();
    }

    private void OriginalSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (AttachPartner is not null) RefreshPartnerControls();
    }

    private async void ListOriginalSessions(object sender, RoutedEventArgs e) => await ChangeOriginalPartnerAsync("list");

    private async void AttachOriginalPartner(object sender, RoutedEventArgs e) => await ChangeOriginalPartnerAsync("attach");

    private async void DisconnectOriginalPartner(object sender, RoutedEventArgs e) => await ChangeOriginalPartnerAsync("disconnect");

    private void SaveOriginalLocation(object sender, RoutedEventArgs e)
    {
        try
        {
            DesktopInstallation.SaveSettings(_root, Settings());
            OriginalFeedback.Text = "原会话目录已保存。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { OriginalFeedback.Text = ex.Message; }
    }

    private async Task ChangeOriginalPartnerAsync(string action)
    {
        if (_session is not { } desktop || _partnerChangePending || !_partnerServiceReady || _activeSource != "external") return;
        _partnerChangePending = true;
        OriginalFeedback.Text = action == "list" ? "正在读取原会话列表…" : "正在等待连接确认…";
        var args = new
        {
            action, cwd = OriginalWorkspace.Text.Trim(), dataDir = OriginalData.Text.Trim(), client = OriginalClient.Text.Trim(),
            name = OriginalName.Text.Trim(), originalClientIdle = OriginalIdle.IsChecked == true,
            sessionId = (OriginalSessions.SelectedItem as OriginalSessionOption)?.Id ?? ""
        };
        RefreshPartnerControls();
        try
        {
            var result = await desktop.PostAsync("/v1/partner", args, timeoutSeconds: 75);
            if (_session != desktop) return;
            if (action == "list")
            {
                if (OriginalWorkspace.Text.Trim() != args.cwd || OriginalData.Text.Trim() != args.dataDir || OriginalClient.Text.Trim() != args.client)
                {
                    OriginalFeedback.Text = "目录或客户端已变化，请重新列出会话。";
                    return;
                }
                var sessions = Rows(result, "sessions").Select(row =>
                {
                    var id = Text(row, "sessionId");
                    var title = Text(row, "title");
                    return new OriginalSessionOption(id, title.Length > 0 ? $"{title} · {id}" : id);
                }).ToArray();
                OriginalSessions.ItemsSource = sessions;
                OriginalFeedback.Text = sessions.Length == 0 ? "此工作目录暂无可接续的会话。" : "请选择要接续的原会话。";
            }
            else
            {
                _adapterOwned = action == "attach";
                if (action == "disconnect")
                {
                    _partnerReady = false;
                    OriginalIdle.IsChecked = false;
                }
                OriginalFeedback.Text = action == "attach" ? "已验证所选原会话。" : "桌面连接已断开，原会话已保留。";
            }
        }
        catch (Exception ex) { if (_session == desktop) OriginalFeedback.Text = ex.Message; }
        finally
        {
            if (_session == desktop)
            {
                _partnerChangePending = false;
                RefreshControls();
            }
        }
    }

    private void ApplyPartnerStatus(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("connection", out var state) || state.ValueKind != JsonValueKind.Object)
        {
            _partnerReady = _partnerServiceReady = _adapterOwned = false;
            if (_activeSource is not null) PartnerStatus.Text = "伴侣服务连接已中断";
            return;
        }
        _partnerServiceReady = true;
        _partnerReady = Flag(state, "ready");
        _adapterOwned = Flag(state, "ownedAdapter");
        var source = Text(state, "source");
        var name = Text(state, "conversationName");
        PartnerStatus.Visibility = Visibility.Visible;
        PartnerStatus.Text = source == "builtin" ? $"模型客户端 · {Text(state, "clientName")}"
            : Text(state, "status") switch
            {
                "connected" => $"原伙伴已连接{(name.Length > 0 ? $" · {name}" : "")}",
                "disconnected" => "原伙伴连接中断，可在设置中重连",
                "error" => "原伙伴连接需要修复，可在设置中断开后重连",
                _ => "等待接续原 MiniMax Code 会话"
            };
    }

    private void RefreshPartnerControls()
    {
        BuiltinFields.Visibility = SelectedSource == "builtin" ? Visibility.Visible : Visibility.Collapsed;
        OriginalFields.Visibility = (_activeSource ?? SelectedSource) == "external" ? Visibility.Visible : Visibility.Collapsed;
        bool service = _activeSource == "external" && _partnerServiceReady && !_partnerChangePending && !_closing;
        bool free = service && !_adapterOwned && !_partnerReady;
        ListSessionsButton.IsEnabled = free;
        OriginalSessions.IsEnabled = free;
        AttachPartner.IsEnabled = free && OriginalSessions.SelectedItem is not null && OriginalIdle.IsChecked == true;
        DisconnectPartner.IsEnabled = service && _adapterOwned;
        SavePartnerLocation.IsEnabled = !_closing && !_partnerChangePending;
    }

    private void ResetPartnerConnection()
    {
        _activeSource = null;
        _partnerReady = _partnerServiceReady = _adapterOwned = _partnerChangePending = false;
        OriginalIdle.IsChecked = false;
        OriginalSessions.ItemsSource = null;
        OriginalFeedback.Text = "启动游戏后，可在这里列出并接续原会话。";
        PartnerStatus.Visibility = Visibility.Collapsed;
    }
}
