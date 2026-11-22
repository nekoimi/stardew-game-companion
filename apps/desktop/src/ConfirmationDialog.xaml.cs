using System.Windows;
using System.Windows.Automation;

namespace StardewAI.Companion.Desktop;

internal partial class ConfirmationDialog : Window
{
    internal bool ChoseAlternate { get; private set; }
    internal bool OptionChecked => Option.IsChecked == true;

    internal ConfirmationDialog(string heading, string summary, string confirm, string? option = null)
    {
        InitializeComponent();
        Title = Heading.Text = heading;
        Summary.Text = summary;
        Summary.Visibility = summary.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ConfirmButton.Content = confirm;
        ConfirmButton.Style = (Style)FindResource("PrimaryButton");
        if (option is not null)
        {
            Option.Content = option;
            Option.Visibility = Visibility.Visible;
        }
    }

    internal bool ShowFor(Window owner)
    {
        Owner = owner;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        return ShowDialog() == true;
    }

    internal static ConfirmationDialog CreateExit(bool allowHide, bool sessionActive)
    {
        string background = allowHide ? "最小化后，伴侣继续运行。双击通知区图标可恢复窗口，图标折叠时可点上箭头查看。" : "";
        string exit = sessionActive ? "退出伴侣会停止 Agent。已打开的游戏保持运行，请在游戏中保存和退出。"
            : "";
        string summary = background.Length > 0 && exit.Length > 0 ? $"{background}\n{exit}" : background + exit;
        var dialog = new ConfirmationDialog(allowHide ? "关闭窗口？" : "退出伴侣？", summary, "退出伴侣")
        {
            Title = "星露谷游戏伴侣",
            ShowInTaskbar = true
        };
        AutomationProperties.SetAutomationId(dialog.ConfirmButton, "ExitButton");
        if (allowHide)
        {
            dialog.AlternateButton.Content = "最小化到托盘";
            dialog.AlternateButton.Visibility = Visibility.Visible;
            AutomationProperties.SetAutomationId(dialog.AlternateButton, "HideButton");
            dialog.ConfirmButton.ClearValue(StyleProperty);
        }
        return dialog;
    }

    private void AlternateClicked(object sender, RoutedEventArgs e)
    {
        ChoseAlternate = true;
        DialogResult = true;
    }

    private void ConfirmClicked(object sender, RoutedEventArgs e) => DialogResult = true;
}
