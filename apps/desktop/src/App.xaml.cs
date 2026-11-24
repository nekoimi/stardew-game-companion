using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using StardewAI.Companion.Updates;

namespace StardewAI.Companion.Desktop;

public partial class App : Application
{
    private FileStream? _packageLease;
    private FileStream? _dataLease;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string root = AppContext.BaseDirectory;
        try
        {
            if (e.Args is ["--check-launcher"])
            {
                _ = new MainWindow(healthCheck: true);
                Shutdown(0);
                return;
            }
            var transaction = new UpdateTransaction(root);
            if (e.Args is ["--check-update", var id] && Guid.TryParseExact(id, "N", out var probe))
            {
                if (!transaction.HasPendingUpdate || transaction.PendingId != probe)
                {
                    Shutdown(1);
                    return;
                }
                await CheckReleaseAsync(root, transaction, probe);
                Shutdown(0);
                return;
            }
            if (File.Exists(Path.Combine(root, UpdatePackage.Manifest)))
            {
                try { _packageLease = transaction.AcquireLease(); }
                catch (IOException)
                {
                    ShowStartupMessage("这个绿色包已打开或正在更新。");
                    Shutdown(1);
                    return;
                }
                if (transaction.HasPendingUpdate)
                {
                    UpdateProcess.RequireGameExit();
                    transaction.StartHelper("recover", transaction.PendingId);
                    Shutdown();
                    return;
                }
            }
            _dataLease = PortableData.Open(root).AcquireExternalLease();
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
            or JsonException or System.ComponentModel.Win32Exception or TimeoutException or System.Windows.Markup.XamlParseException)
        {
            if (e.Args is not (["--check-launcher"] or ["--check-update", _])) ShowStartupMessage(error.Message);
            Shutdown(1);
        }
    }

    private static void ShowStartupMessage(string message) =>
        new ConfirmationDialog("星露谷游戏伴侣", message, "关闭").ShowDialog();

    private static async Task CheckReleaseAsync(string root, UpdateTransaction transaction, Guid expected)
    {
        var installed = UpdatePackage.ReadInfo(root);
        var candidate = UpdatePackage.ReadInfo(transaction.Payload(expected));
        if (installed is null || installed != candidate) throw new InvalidDataException("安装后的版本信息不匹配。");
        _ = DesktopInstallation.LoadSettings(root);
        _ = new MainWindow(healthCheck: true);
        using var process = UpdateProcess.Start(DesktopInstallation.ResolveRuntime(root), root,
            "--disable-warning=ExperimentalWarning", Path.Combine(root, "runtime", "dist", "bootstrap.js"), "--check");
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25)); }
        catch (TimeoutException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw;
        }
        if (process.ExitCode != 0) throw new InvalidOperationException("新版运行时自检失败。");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _dataLease?.Dispose();
        _packageLease?.Dispose();
        base.OnExit(e);
    }
}
