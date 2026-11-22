using System.ComponentModel;
using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace StardewAI.Companion.Desktop;

public partial class MainWindow
{
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ContextMenuStrip? _trayMenu;
    private Icon? _trayImage;
    private CloseWindowDialog? _closeDialog;
    private bool _exitReady;

    private void InitializeTray()
    {
        var icon = Application.GetResourceStream(new Uri("Assets/companion.ico", UriKind.Relative))
            ?? throw new InvalidOperationException("找不到托盘图标资源。");
        using var stream = icon.Stream;
        _trayImage = new Icon(stream, Forms.SystemInformation.SmallIconSize);
        _trayMenu = new Forms.ContextMenuStrip();
        _trayMenu.Items.Add("打开星露谷游戏伴侣", null, (_, _) => Dispatcher.BeginInvoke(RestoreFromTray));
        _trayMenu.Items.Add("退出伴侣", null, (_, _) => Dispatcher.BeginInvoke(() => ConfirmClose(allowHide: false)));
        _trayIcon = new Forms.NotifyIcon { Icon = _trayImage, Text = Title, ContextMenuStrip = _trayMenu };
        _trayIcon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) Dispatcher.BeginInvoke(RestoreFromTray);
        };
        _trayIcon.Visible = true;
    }

    private void RestoreFromTray()
    {
        if (_closing) return;
        if (_closeDialog is { } dialog)
        {
            dialog.Activate();
            return;
        }
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) SystemCommands.RestoreWindow(this);
        Activate();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _exitReady) return;
        e.Cancel = true;
        Dispatcher.BeginInvoke(() => ConfirmClose(allowHide: true));
    }

    private async void ConfirmClose(bool allowHide)
    {
        if (_closing) return;
        if (_closeDialog is { } openDialog)
        {
            openDialog.Activate();
            return;
        }

        var dialog = new CloseWindowDialog(allowHide, _session is not null);
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            dialog.Owner = this;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            dialog.ShowInTaskbar = false;
        }
        _closeDialog = dialog;
        bool? confirmed;
        try { confirmed = dialog.ShowDialog(); }
        finally { _closeDialog = null; }
        if (confirmed != true) return;
        if (dialog.MinimizeToTray)
        {
            Hide();
            return;
        }

        _closing = true;
        if (_session is { } session)
        {
            RefreshControls();
            await EndSessionAsync(session, "伴侣服务已关闭。");
        }
        _logger.Write("desktop", "info", "desktop_shutdown", "伴侣桌面已退出。");
        await _logger.StopAsync();
        _exitReady = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _trayIcon?.Dispose();
        _trayMenu?.Dispose();
        _trayImage?.Dispose();
        base.OnClosed(e);
    }
}
