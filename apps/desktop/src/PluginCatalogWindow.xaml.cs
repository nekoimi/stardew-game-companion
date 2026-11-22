using System.ComponentModel;
using System.Windows;
using StardewAI.Companion.Desktop.Plugins;

namespace StardewAI.Companion.Desktop;

public partial class PluginCatalogWindow : Window
{
    private readonly PluginHost _host;
    private readonly CancellationTokenSource _closed = new();
    private CatalogIndex? _index;
    private bool _updating;

    internal PluginCatalogWindow(PluginHost host)
    {
        InitializeComponent();
        _host = host;
        try
        {
            if (host.CatalogSettings() is { } source)
            {
                CatalogUrl.Text = source.Url;
                CatalogPublicKey.Text = source.PublicKey;
            }
        }
        catch (Exception error) when (PluginHost.IsPluginFailure(error)) { CatalogFeedback.Text = error.Message; }
    }

    private async void LoadClicked(object sender, RoutedEventArgs e)
    {
        _index = null;
        CatalogRows.ItemsSource = null;
        CatalogFeedback.Text = "正在读取并验证目录…";
        LoadCatalog.IsEnabled = false;
        ApplyVersion.IsEnabled = false;
        try
        {
            _index = await _host.ReadCatalogAsync(new(CatalogUrl.Text.Trim(), CatalogPublicKey.Text.Trim()), _closed.Token);
            CatalogRows.ItemsSource = _index.Plugins;
            CatalogFeedback.Text = _index.Plugins.Length == 0 ? "目录暂无插件。" : "";
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (OperationCanceledException) { CatalogFeedback.Text = "加载目录超时。"; }
        catch (Exception error) when (PluginHost.IsPluginFailure(error)) { CatalogFeedback.Text = error.Message; }
        finally { LoadCatalog.IsEnabled = true; }
    }

    private void EntrySelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => ApplyVersion.IsEnabled = !_updating && CatalogRows.SelectedItem is CatalogEntry { Compatible: true };

    private async void ApplyClicked(object sender, RoutedEventArgs e)
    {
        if (_index is null || CatalogRows.SelectedItem is not CatalogEntry { Compatible: true } entry) return;
        if (MessageBox.Show(this, $"{entry.Name} · v{entry.Version}\n发布者：{entry.Publisher}\n来源：{new Uri(entry.DownloadUrl).Host}",
            "确认下载并安装或更新", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _updating = true;
        ApplyVersion.IsEnabled = LoadCatalog.IsEnabled = CatalogRows.IsEnabled = false;
        CatalogFeedback.Text = "正在下载和校验插件包…";
        try
        {
            await _host.ApplyCatalogAsync(_index, entry);
            CatalogFeedback.Text = "插件版本已就绪。";
        }
        catch (OperationCanceledException) when (_closed.IsCancellationRequested) { }
        catch (Exception error) when (PluginHost.IsPluginFailure(error)) { CatalogFeedback.Text = error.Message; }
        finally
        {
            _updating = false;
            ApplyVersion.IsEnabled = LoadCatalog.IsEnabled = CatalogRows.IsEnabled = true;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_updating) e.Cancel = true;
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed.Cancel();
        base.OnClosed(e);
    }
}
