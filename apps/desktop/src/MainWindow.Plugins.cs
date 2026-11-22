using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using StardewAI.Companion.Desktop.Plugins;

namespace StardewAI.Companion.Desktop;

public partial class MainWindow
{
    private PluginHost? _plugins;
    private bool _pluginBusy;
    private bool _refreshingPlugins;
    private bool _applyingPluginSwitch;

    private void InitializePlugins()
    {
        try
        {
            var host = new PluginHost(_root, _logger, ForwardPluginCallAsync);
            _plugins = host;
            host.Changed += () => Dispatcher.InvokeAsync(() =>
            {
                if (!_closing && _plugins == host) RefreshPlugins();
            });
            host.Notice += (name, message) => Dispatcher.InvokeAsync(() =>
            {
                if (!_closing && _plugins == host) AddEntry("插件 · " + name, message);
            });
            _ = Task.Run(host.StartAutomaticAsync);
        }
        catch (Exception error) when (PluginHost.IsPluginFailure(error))
        {
            PluginFeedback.Text = error.Message;
            LogError("plugin_host_unavailable", "插件中心无法启动。", error);
        }
        RefreshPlugins();
    }

    private async Task<PluginCallResult> ForwardPluginCallAsync(string route, object body)
    {
        if (Volatile.Read(ref _session) is not { } session)
            return route == "/v2/plugins/submit" ? new PluginCallResult(409, JsonSerializer.SerializeToElement(
                new PluginError("no_game", "请先进入游戏存档。"), PluginEvents.OutputJson)) : PluginCallResult.Unavailable();
        try { return await session.PostPluginAsync(route, body).ConfigureAwait(false); }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException
            or ObjectDisposedException or JsonException)
        {
            return PluginCallResult.Unavailable();
        }
    }

    private void RefreshPlugins(string? selected = null)
    {
        var previous = PluginList.SelectedItem as PluginRow;
        var rows = _plugins?.List().Select(view => new PluginRow(view)).ToArray() ?? [];
        _refreshingPlugins = true;
        try
        {
            if (PluginList.ItemsSource is not PluginRow[] current || !current.SequenceEqual(rows))
                PluginList.ItemsSource = rows;
            string? target = selected ?? previous?.Id;
            PluginList.SelectedItem = ((PluginRow[])PluginList.ItemsSource).FirstOrDefault(row => row.Id == target)
                ?? ((PluginRow[])PluginList.ItemsSource).FirstOrDefault();
            PluginCount.Text = $"{rows.Length} 个";
        }
        finally { _refreshingPlugins = false; }
        RefreshPluginDetail();
    }

    private void PluginSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_refreshingPlugins && ReferenceEquals(e.Source, PluginList)) RefreshPluginDetail();
    }

    private void RefreshPluginDetail()
    {
        InstallPlugin.IsEnabled = _plugins is not null && !_pluginBusy;
        BrowsePluginCatalog.IsEnabled = InstallPlugin.IsEnabled;
        PluginList.IsEnabled = !_pluginBusy;
        var row = PluginList.SelectedItem as PluginRow;
        PluginEmpty.Visibility = row is null ? Visibility.Visible : Visibility.Collapsed;
        PluginDetail.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        PluginDetail.IsEnabled = !_pluginBusy;
        if (row is null) return;
        var view = row.View;
        var state = view.Status.State;
        bool enabled = view.Installation.Enabled;
        bool active = state is "connected" or "starting" or "needs_configuration" or "ready" or "degraded";
        PluginName.Text = row.Name;
        PluginAttribution.Text = row.Attribution + " · 本地 ZIP";
        PluginStatusText.Text = row.Status;
        PluginStatusBadge.Background = state == "ready" && !view.Status.RestartRequired
            ? (Brush)FindResource("Leaf") : new SolidColorBrush(Color.FromRgb(244, 231, 203));
        PluginSummary.Text = view.Status.RestartRequired ? "设置已保存，重启插件后生效。"
            : view.Status.Summary ?? state switch
            {
                "needs_configuration" => "插件已连接，请先完成首次设置。",
                "ready" => "插件运行中，已连接到伴侣程序。",
                "connected" => "插件已握手，正在等待就绪状态。",
                "starting" => "正在启动插件。",
                _ => ""
            };
        OpenPluginSettings.Visibility = view.HasSettings ? Visibility.Visible : Visibility.Collapsed;
        OpenPluginSettings.Content = active ? "打开插件设置" : enabled ? "启动并打开设置" : "启用并打开设置";
        TogglePluginRun.Content = active || state == "waiting_retry" ? "停止插件" : enabled ? "启动插件" : "启用插件";
        RestartPlugin.Visibility = active && view.Status.RestartRequired ? Visibility.Visible : Visibility.Collapsed;
        PluginAutoStart.IsEnabled = enabled;
        _applyingPluginSwitch = true;
        try { PluginAutoStart.IsChecked = view.Installation.AutoStart; }
        finally { _applyingPluginSwitch = false; }
        DisablePlugin.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        RollbackPlugin.Visibility = view.Installation.Backup is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BrowsePluginCatalogClicked(object sender, RoutedEventArgs e)
    {
        if (_plugins is not { } host || _closing) return;
        new PluginCatalogWindow(host) { Owner = this }.ShowDialog();
        if (!_closing) RefreshPlugins();
    }

    private async void RollbackPluginClicked(object sender, RoutedEventArgs e)
    {
        if (PluginList.SelectedItem is not PluginRow row || row.View.Installation.Backup is not { } backup) return;
        if (new ConfirmationDialog("回退插件版本", $"回退到 v{backup.Manifest.Version} 并恢复更新前的插件数据？", "回退版本").ShowFor(this))
            await RunPluginOperationAsync(host => host.RollbackAsync(row.Id));
    }

    private async void InstallPluginClicked(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "ZIP 插件包 (*.zip)|*.zip", CheckFileExists = true };
        if (picker.ShowDialog(this) != true) return;
        string? installedId = null;
        await RunPluginOperationAsync(async host =>
        {
            var installed = await host.InstallAsync(picker.FileName, manifest =>
                Dispatcher.InvokeAsync(() => !_closing && new ConfirmationDialog("确认安装本地插件",
                    $"{manifest.Name} · v{manifest.Version}\n发布者：{manifest.Publisher}\n来源：{Path.GetFileName(picker.FileName)}",
                    "安装插件").ShowFor(this)).Task);
            installedId = installed?.Manifest.Id;
        });
        if (installedId is not null && !_closing) RefreshPlugins(installedId);
    }

    private async void TogglePluginRunClicked(object sender, RoutedEventArgs e)
    {
        if (PluginList.SelectedItem is not PluginRow row) return;
        await RunPluginOperationAsync(host => Equals(TogglePluginRun.Content, "停止插件") ? host.StopAsync(row.Id) : host.StartAsync(row.Id));
    }

    private async void OpenPluginSettingsClicked(object sender, RoutedEventArgs e)
    {
        if (PluginList.SelectedItem is not PluginRow row) return;
        await RunPluginOperationAsync(host => host.OpenSettingsAsync(row.Id, address =>
            Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true })?.Dispose()));
    }

    private async void RestartPluginClicked(object sender, RoutedEventArgs e)
    {
        if (PluginList.SelectedItem is PluginRow row) await RunPluginOperationAsync(host => host.RestartAsync(row.Id));
    }

    private async void PluginAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (!_applyingPluginSwitch && PluginList.SelectedItem is PluginRow row)
        {
            bool value = PluginAutoStart.IsChecked == true;
            await RunPluginOperationAsync(host => host.SetAutoStartAsync(row.Id, value));
        }
    }

    private async void DisablePluginClicked(object sender, RoutedEventArgs e)
    {
        if (PluginList.SelectedItem is PluginRow row) await RunPluginOperationAsync(host => host.StopAsync(row.Id, disable: true));
    }

    private async void UninstallPluginClicked(object sender, RoutedEventArgs e)
    {
        if (PluginList.SelectedItem is not PluginRow row) return;
        var dialog = new ConfirmationDialog("卸载插件", $"卸载「{row.Name}」时会停止插件。取消勾选将删除插件的数据与设置。",
            "卸载插件", "保留插件的数据与设置");
        if (dialog.ShowFor(this))
            await RunPluginOperationAsync(host => host.UninstallAsync(row.Id, dialog.OptionChecked));
    }

    private async void OpenPluginLogsClicked(object sender, RoutedEventArgs e)
    {
        if (PluginList.SelectedItem is not PluginRow row) return;
        await RunPluginOperationAsync(host =>
        {
            string path = host.LogDirectory(row.Id);
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            return Task.CompletedTask;
        });
    }

    private async Task RunPluginOperationAsync(Func<PluginHost, Task> operation)
    {
        if (_plugins is not { } host || _pluginBusy || _closing) return;
        _pluginBusy = true;
        PluginFeedback.Text = "";
        RefreshPluginDetail();
        try { await operation(host); }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception error) when (PluginHost.IsPluginFailure(error))
        {
            PluginFeedback.Text = error.Message;
            LogError("plugin_operation_failed", "插件操作失败。", error);
        }
        finally
        {
            _pluginBusy = false;
            if (!_closing) RefreshPlugins();
        }
    }

    private sealed record PluginRow(PluginView View)
    {
        public string Id => View.Installation.Manifest.Id;
        public string Name => View.Installation.Manifest.Name;
        public string Attribution => $"{View.Installation.Manifest.Publisher} · v{View.Installation.Manifest.Version}";
        public string Status => View.Status.RestartRequired ? "需重启" : View.Status.State switch
        {
            "disabled" => "未启用",
            "stopped" => "已停止",
            "starting" => "启动中",
            "connected" => "已连接",
            "needs_configuration" => "待配置",
            "ready" => "已就绪",
            "degraded" => "运行异常",
            "stopping" => "停止中",
            "waiting_retry" => "等待重试",
            "failed" => "启动失败",
            _ => "未知状态"
        };
    }
}
