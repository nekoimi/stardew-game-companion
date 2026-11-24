using System.ComponentModel;
using System.Windows;
using StardewAI.Companion.Updates;

namespace StardewAI.Companion.Updater;

public partial class UpdateProgressWindow : Window
{
    private bool _allowClose;
    internal bool Failed { get; private set; }

    public UpdateProgressWindow() => InitializeComponent();

    internal void ShowProgress(UpdateProgress progress)
    {
        Status.Text = progress.Label;
        StageProgress.IsIndeterminate = progress.Percent is null;
        StageProgress.Value = progress.Percent ?? 0;
    }

    internal void ShowFailure(string message)
    {
        Failed = true;
        Title = "更新未完成";
        Status.Text = $"更新未完成。{message}";
        StageProgress.Visibility = Visibility.Collapsed;
        CloseButton.Visibility = Visibility.Visible;
        AllowClose();
    }

    internal void AllowClose() => _allowClose = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (!_allowClose) e.Cancel = true;
    }

    private void CloseClicked(object sender, RoutedEventArgs e) => Close();
}
