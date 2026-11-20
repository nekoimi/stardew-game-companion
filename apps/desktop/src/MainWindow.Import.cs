using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace StardewAI.Companion.Desktop;

public partial class MainWindow
{
    private bool _importing;

    private void RefreshImportControls()
    {
        bool pending = DesktopInstallation.ImportPending(_root);
        ImportButton.Content = pending ? "继续上次导入" : "从原安装导入…";
        ImportDataOnly.IsEnabled = !pending;
    }

    private async void ImportClicked(object sender, RoutedEventArgs e)
    {
        if (_session is not null || _closing || _importing) return;
        string? source = null;
        bool dataOnly = ImportDataOnly.IsChecked == true;
        if (!DesktopInstallation.ImportPending(_root))
        {
            var picker = new OpenFolderDialog { Title = "选择原伙伴 Mod、游戏目录或旧绿色包目录" };
            var defaultSource = DesktopInstallation.ModDirectory(GameFolder.Text);
            if (Directory.Exists(defaultSource)) picker.InitialDirectory = defaultSource;
            if (picker.ShowDialog(this) != true) return;
            source = picker.FolderName;
            var nested = DesktopInstallation.ModDirectory(source);
            if (File.Exists(Path.Combine(nested, "manifest.json"))) source = nested;
            string message = dataOnly
                ? "复制旧安装中的工作、历史和伙伴记忆？后续使用这个绿色包已选择的伙伴来源。"
                : "复制旧安装中的工作、历史、伙伴记忆和已有 Pi 会话？导入会保留原安装。";
            if (MessageBox.Show(this, message, "导入旧安装数据", MessageBoxButton.YesNo,
                    MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }

        _importing = true;
        ImportFeedback.Text = "正在检查并复制旧数据…";
        RefreshControls();
        try
        {
            var result = await DesktopInstallation.ImportAsync(_root, source, dataOnly);
            int sessions = Number(result, "sessions") ?? 0;
            ImportFeedback.Text = $"已导入 {Number(result, "dataFiles")} 份数据，复制 {sessions} 个 Pi 会话。"
                + (sessions > 0 ? "选择本机 Pi 并进入对应存档可接续原会话。" : "启动游戏后进入对应存档。");
        }
        catch (Exception ex) { ImportFeedback.Text = ex.Message; }
        finally
        {
            _importing = false;
            RefreshControls();
        }
    }
}
