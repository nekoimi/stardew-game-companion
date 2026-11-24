using System.IO;
using System.Windows;
using Microsoft.Win32;
using StardewAI.Companion.Updates;

namespace StardewAI.Companion.Desktop;

public partial class MainWindow
{
    private void ShowDataLocation()
    {
        var data = PortableData.Open(_root);
        DataLocationText.Text = data.External ?? $"随绿色包保存 · {data.Data}";
    }

    private async void MoveDataClicked(object sender, RoutedEventArgs e)
    {
        try { await ChangeDataAsync(PortableData.Open(_root)); }
        catch (Exception error) when (IsUpdateFailure(error)) { UpdateFeedback.Text = error.Message; }
    }

    private async void LinkDataClicked(object sender, RoutedEventArgs e) => await ChangeDataAsync(null);

    private async void ImportDataClicked(object sender, RoutedEventArgs e)
    {
        if (!CanChangeData()) return;
        var previous = new OpenFolderDialog { Title = "选择旧绿色包的根目录" };
        if (previous.ShowDialog(this) != true) return;
        try
        {
            if (!File.Exists(Path.Combine(previous.FolderName, UpdatePackage.Launcher)))
                throw new InvalidDataException("请选择包含「星露谷游戏伴侣.exe」的旧绿色包目录。");
            if (string.Equals(Path.TrimEndingDirectorySeparator(_root), previous.FolderName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请使用「迁移当前数据」移动这个绿色包的数据。");
            await ChangeDataAsync(PortableData.Open(previous.FolderName));
        }
        catch (Exception error) when (IsUpdateFailure(error)) { UpdateFeedback.Text = error.Message; }
    }

    private bool CanChangeData()
    {
        if (_closing) return false;
        if (_updateCancellation is not null || _pluginBusy || _testingModel)
        {
            UpdateFeedback.Text = "请等当前操作结束后再切换数据目录。";
            return false;
        }
        return true;
    }

    private async Task ChangeDataAsync(PortableData? source)
    {
        if (!CanChangeData()) return;
        try
        {
            UpdateProcess.RequireGameExit();
            var directory = new OpenFolderDialog
            {
                Title = source is null ? "选择已有的伴侣数据目录" : "选择保存伴侣数据目录的上一级位置"
            };
            if (directory.ShowDialog(this) != true) return;
            string target = source is null ? directory.FolderName : Path.Combine(directory.FolderName, "StardewGameCompanion");
            string operation = source is null ? "连接已有数据" : "复制数据并切换目录";
            if (!new ConfirmationDialog(operation,
                $"{target}\n\n下次打开伴侣时会使用这个目录中的设置、伙伴记忆和插件。请确认你信任其中的插件。原目录保留，完成后请重新打开伴侣。",
                "确认并退出").ShowFor(this)) return;
            UpdateProcess.RequireGameExit();
            IsEnabled = false;
            ClearStagedUpdate();
            await ExitDesktopAsync(() => Task.Run(() =>
            {
                UpdateProcess.RequireGameExit();
                if (source is not null)
                {
                    bool fromCurrent = string.Equals(_root, source.Root, StringComparison.OrdinalIgnoreCase);
                    using var package = fromCurrent ? null : new UpdateTransaction(source.Root).AcquireLease();
                    using var external = fromCurrent ? null : source.AcquireExternalLease();
                    using var session = source.AcquireSessionLease();
                    using var plugins = source.AcquirePluginLease();
                    PortableData.CreateFrom(source, target);
                }
                PortableData.Link(_root, target);
            }), "数据目录尚未切换");
        }
        catch (Exception error) when (IsUpdateFailure(error)) { UpdateFeedback.Text = error.Message; }
    }
}
