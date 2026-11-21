using System.Windows;

namespace StardewAI.Companion.Desktop;

internal partial class CloseWindowDialog : Window
{
    internal bool MinimizeToTray { get; private set; }

    internal CloseWindowDialog(bool allowHide, bool sessionActive)
    {
        InitializeComponent();
        Heading.Text = allowHide ? "关闭窗口？" : "退出伴侣？";
        HideButton.Visibility = allowHide ? Visibility.Visible : Visibility.Collapsed;
        string background = allowHide ? "最小化后，伴侣继续运行。双击通知区图标可恢复窗口，图标折叠时可点上箭头查看。" : "";
        string exit = sessionActive ? "退出伴侣会停止 Agent。已打开的游戏保持运行，请在游戏中保存和退出。"
            : "";
        Summary.Text = background.Length > 0 && exit.Length > 0 ? $"{background}\n{exit}" : background + exit;
        Summary.Visibility = Summary.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HideClicked(object sender, RoutedEventArgs e)
    {
        MinimizeToTray = true;
        DialogResult = true;
    }

    private void ExitClicked(object sender, RoutedEventArgs e) => DialogResult = true;
}
