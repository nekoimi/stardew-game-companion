using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;
using StardewAI.Companion.Updates;

namespace StardewAI.Companion.Desktop;

public partial class MainWindow
{
    private UpdateTransaction? _updates;
    private CancellationTokenSource? _updateCancellation;
    private (Guid Id, ReleaseInfo Release, string ManifestHash)? _stagedUpdate;
    private ReleaseInfo? _installedRelease;
    private PublishedUpdate? _publishedUpdate;
    private readonly byte[]? _updatePublisherKey = UpdateSignature.TrustedKey();

    private void InitializeUpdates()
    {
        try
        {
            var info = _installedRelease = UpdatePackage.ReadInfo(_root);
            ShowDataLocation();
            PreviewUpdates.IsChecked = _savedSettings.CheckPreviewUpdates;
            InstalledReleaseText.Text = $"当前版本 {info?.Tag ?? System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";
            if (!File.Exists(Path.Combine(_root, UpdatePackage.Manifest))
                || !File.Exists(Path.Combine(_root, UpdatePackage.Helper)))
            {
                UpdateFeedback.Text = "请从完整绿色包打开伴侣以使用更新功能。";
                return;
            }
            _updates = new UpdateTransaction(_root);
            if (_updatePublisherKey is null) UpdateFeedback.Text = "这是本地开发构建，可通过 ZIP 验收更新。";
            if (File.Exists(_updates.ResultPath))
            {
                UpdateFeedback.Text = UpdatePackage.ReadJson<UpdateResult>(_updates.ResultPath).Detail;
                File.Delete(_updates.ResultPath);
            }
            _updates.CleanFinishedStages();
        }
        catch (Exception error) when (IsUpdateFailure(error))
        {
            UpdateFeedback.Text = error.Message;
            LogError("update_init_failed", "无法读取更新状态。", error);
        }
        finally { RefreshUpdateControls(); }
    }

    private void RefreshUpdateControls()
    {
        bool ready = _updates is not null && !_closing && _updateCancellation is null;
        CheckUpdatesButton.IsEnabled = PreviewUpdates.IsEnabled = ready && _installedRelease is not null && _updatePublisherKey is not null;
        DownloadUpdateButton.IsEnabled = ready && _publishedUpdate is not null;
        SelectUpdateButton.IsEnabled = ready;
        InstallUpdateButton.IsEnabled = ready && _stagedUpdate is not null;
    }

    private void PreviewUpdatesChanged(object sender, RoutedEventArgs e)
    {
        if (_updates is null || _closing || _updateCancellation is not null) return;
        bool preview = PreviewUpdates.IsChecked == true;
        if (_savedSettings.CheckPreviewUpdates == preview) return;
        try
        {
            SaveDesktopSettings(_savedSettings with { CheckPreviewUpdates = preview });
            ClearStagedUpdate();
            _publishedUpdate = null;
            UpdateNotes.Text = UpdateFeedback.Text = "";
            RefreshUpdateControls();
        }
        catch (Exception error) when (IsUpdateFailure(error))
        {
            PreviewUpdates.IsChecked = _savedSettings.CheckPreviewUpdates;
            UpdateFeedback.Text = error.Message;
            LogError("update_channel_save_failed", "无法保存更新通道。", error);
        }
    }

    private async void CheckUpdatesClicked(object sender, RoutedEventArgs e)
    {
        if (_installedRelease is not { } installed || _closing || _updateCancellation is not null) return;
        ClearStagedUpdate();
        _publishedUpdate = null;
        UpdateNotes.Text = "";
        bool preview = _savedSettings.CheckPreviewUpdates;
        string channel = preview ? "预览版" : "正式版";
        await RunUpdateOperationAsync($"正在检查{channel}…", async token =>
        {
            var release = await GitHubCompanionReleases.Official.FindAsync(preview, token);
            if (_closing) return;
            if (release is null)
            {
                UpdateFeedback.Text = $"目前没有可用的{channel}。";
                return;
            }
            if (!release.CanOfferTo(installed))
            {
                UpdateFeedback.Text = $"当前已是最新{channel}。";
                return;
            }
            _publishedUpdate = release;
            UpdateNotes.Text = release.Notes;
            UpdateFeedback.Text = $"可下载{channel} {release.Tag}。";
        });
    }

    private async void DownloadUpdateClicked(object sender, RoutedEventArgs e)
    {
        if (_updates is not { } updates || _publishedUpdate is not { } release || _updatePublisherKey is not { } key
            || _closing || _updateCancellation is not null) return;
        await StageUpdateAsync("正在下载更新…", (id, token) =>
        {
            var progress = new Progress<int?>(percent =>
            {
                if (_closing || token.IsCancellationRequested) return;
                UpdateFeedback.Text = percent.HasValue ? $"正在下载更新… {percent}%" : "正在校验下载的更新包…";
            });
            return GitHubCompanionReleases.Official.DownloadAsync(release, updates.Stage(id), updates.Payload(id), key, progress, token);
        });
    }

    private async void SelectUpdateClicked(object sender, RoutedEventArgs e)
    {
        if (_updates is not { } updates || _closing || _updateCancellation is not null) return;
        var dialog = new OpenFileDialog { Title = "选择绿色包 ZIP", Filter = "绿色包 (*.zip)|*.zip" };
        if (dialog.ShowDialog(this) != true) return;
        _publishedUpdate = null;
        UpdateNotes.Text = "";
        await StageUpdateAsync("正在校验 ZIP 和发行文件…", (id, token) => Task.Run(() =>
            {
                if (!File.Exists(dialog.FileName + ".sha256"))
                    throw new FileNotFoundException("请把发行附带的 .sha256 文件放在所选 ZIP 旁边。");
                var hash = UpdatePackage.ReadChecksum(dialog.FileName + ".sha256", Path.GetFileName(dialog.FileName));
                if (_updatePublisherKey is { } key) UpdateSignature.VerifyFile(dialog.FileName + UpdateSignature.Suffix, hash, key);
                return UpdatePackage.Extract(dialog.FileName, hash, updates.Payload(id), token);
            }, token));
    }

    private async Task StageUpdateAsync(string message, Func<Guid, CancellationToken, Task<ReleaseInfo>> stage)
    {
        ClearStagedUpdate();
        var id = Guid.NewGuid();
        try
        {
            await RunUpdateOperationAsync(message, async token =>
            {
                var info = await stage(id, token);
                if (_closing) return;
                _stagedUpdate = (id, info, UpdatePackage.ManifestHash(_updates!.Payload(id)));
                UpdateFeedback.Text = $"已校验 {info.Tag}，可以安装。";
            });
        }
        finally
        {
            if (_stagedUpdate is null) _updates?.DiscardStage(id);
        }
    }

    private void ClearStagedUpdate()
    {
        if (_stagedUpdate is { } staged) _updates?.DiscardStage(staged.Id);
        _stagedUpdate = null;
    }

    private async Task RunUpdateOperationAsync(string message, Func<CancellationToken, Task> operation)
    {
        using var cancellation = new CancellationTokenSource();
        _updateCancellation = cancellation;
        RefreshUpdateControls();
        UpdateFeedback.Text = message;
        try { await operation(cancellation.Token); }
        catch (OperationCanceledException) when (_closing) { }
        catch (OperationCanceledException)
        {
            UpdateFeedback.Text = "更新请求超时，请重试。";
        }
        catch (Exception error) when (IsUpdateFailure(error))
        {
            UpdateFeedback.Text = error.Message;
            LogError("update_operation_failed", "更新操作失败。", error);
        }
        finally
        {
            _updateCancellation = null;
            RefreshUpdateControls();
        }
    }

    private async void InstallUpdateClicked(object sender, RoutedEventArgs e)
    {
        if (_updates is not { } updates || _stagedUpdate is not { } staged
            || _closing || _updateCancellation is not null) return;
        if (_testingModel || _pluginBusy)
        {
            UpdateFeedback.Text = "请等模型测试或插件操作结束后再安装。";
            return;
        }
        try
        {
            UpdateProcess.RequireGameExit();
            string switchNotice = _installedRelease is { } installed && installed.Channel != staged.Release.Channel
                ? "此操作会切换发行通道，目标版本可能早于当前版本。\n\n" : "";
            if (!new ConfirmationDialog("安装更新",
                $"{switchNotice}从 {_installedRelease?.Tag ?? "当前版本"} 切换到 {staged.Release.Tag} 并重新打开伴侣？更新器会备份程序和用户数据。", "安装并重启").ShowFor(this))
                return;
            UpdateProcess.RequireGameExit();
            updates.StartHelper("install", staged.Id);
        }
        catch (Exception error) when (IsUpdateFailure(error))
        {
            UpdateFeedback.Text = error.Message;
            LogError("update_handoff_failed", "无法交接更新。", error);
            return;
        }
        UpdateFeedback.Text = "正在关闭伴侣并交接更新…";
        IsEnabled = false;
        await ExitDesktopAsync(() =>
        {
            updates.AuthorizeInstall(staged.Id, staged.ManifestHash);
            return Task.CompletedTask;
        }, "无法完成更新交接");
    }

    private static bool IsUpdateFailure(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException
            or HttpRequestException or System.ComponentModel.Win32Exception;
}
