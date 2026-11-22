using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using StardewAI.Companion.Logging;

namespace StardewAI.Companion.Desktop.Plugins;

internal sealed record PluginView(InstalledPlugin Installation, PluginStatus Status, bool HasSettings);

internal sealed class PluginHost : IAsyncDisposable
{
    private readonly PluginRegistry _registry;
    private readonly FileLogger _logger;
    private readonly FileStream _lease;
    private readonly SemaphoreSlim _operation = new(1);
    private readonly ConcurrentDictionary<string, PluginInstance> _credentials = new();
    private readonly ConcurrentDictionary<string, PluginRun> _runs = new();
    private readonly ConcurrentDictionary<string, PluginStatus> _offline = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _restarts = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly PluginEvents _events = new();
    private readonly object _messagesLock = new();
    private readonly HashSet<(string, string)> _messageIds = [];
    private readonly Queue<(string, string)> _recentMessages = new();
    private readonly Func<string, object, Task<PluginCallResult>> _runtime;
    private readonly HttpClient _catalogClient;
    private readonly string _catalogSettings;
    private PluginServer? _server;

    internal PluginHost(string root, FileLogger logger, Func<string, object, Task<PluginCallResult>>? runtime = null,
        HttpMessageHandler? catalogTransport = null)
    {
        _logger = logger;
        _runtime = runtime ?? ((_, _) => Task.FromResult(PluginCallResult.Unavailable()));
        var data = Path.Combine(root, "data");
        _catalogSettings = Path.Combine(data, "plugin-catalog.json");
        Directory.CreateDirectory(data);
        try { _lease = File.Open(Path.Combine(data, "plugin-host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new InvalidOperationException("此绿色包的插件中心已在运行。", ex); }
        try { _registry = new PluginRegistry(root); }
        catch { _lease.Dispose(); throw; }
        _catalogClient = new(catalogTransport ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    }

    internal event Action? Changed;
    internal event Action<string, string>? Notice;

    internal void ApplyRuntimeEvent(string type, JsonElement payload) => _events.Apply(type, payload);
    internal void DisconnectRuntime() => _events.Disconnect();

    internal PluginView[] List() => _registry.List().Select(plugin =>
    {
        string id = plugin.Manifest.Id;
        var status = _runs.TryGetValue(id, out var run) ? run.Instance.Status
            : _offline.TryGetValue(id, out var previous) ? previous : new PluginStatus(plugin.Enabled ? "stopped" : "disabled");
        return new PluginView(plugin, status, plugin.Manifest.Settings is not null);
    }).ToArray();

    internal string LogDirectory(string id) => Path.Combine(_logger.DirectoryPath, "plugins", id);

    internal CatalogSource? CatalogSettings() => File.Exists(_catalogSettings)
        ? JsonSerializer.Deserialize<CatalogSource>(File.ReadAllText(_catalogSettings), PluginPackages.Json) : null;

    internal async Task<CatalogIndex> ReadCatalogAsync(string url, CancellationToken cancellation)
    {
        using var token = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token, cancellation);
        CatalogSource? saved;
        try { saved = CatalogSettings(); }
        catch (JsonException) { saved = null; }
        var source = saved?.Url == url ? saved : new CatalogSource(url);
        var index = await new PluginCatalog(_catalogClient).ReadAsync(source, token.Token).ConfigureAwait(false);
        if (saved?.Url != url) PluginRegistry.WriteAtomic(_catalogSettings, new { url });
        return index;
    }

    internal Task ApplyCatalogAsync(CatalogIndex index, CatalogEntry entry) => RunExclusiveAsync(async token =>
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        string download = Path.Combine(Path.GetTempPath(), $"companion-plugin-{Guid.NewGuid():N}.zip");
        try
        {
            await new PluginCatalog(_catalogClient).DownloadAsync(index, entry, download, timeout.Token).ConfigureAwait(false);
            string key = index.Publishers.Single(publisher => publisher.Name == entry.Publisher).PublicKey;
            if (!_registry.List().Any(plugin => plugin.Manifest.Id == entry.Id))
            {
                await _registry.InstallAsync(download, _ => Task.FromResult(true), token, entry, key).ConfigureAwait(false);
                Notify();
                return true;
            }
            var next = await _registry.StageUpdateAsync(download, entry, key, token).ConfigureAwait(false);
            await SwitchVersionAsync(next, backup: null, token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("插件包下载超时。");
        }
        finally { File.Delete(download); }
    });

    internal Task RollbackAsync(string id) => RunExclusiveAsync(async token =>
    {
        var current = _registry.Find(id);
        var backup = current.Backup ?? throw new InvalidOperationException("没有可回退的版本。");
        var next = current with { Manifest = backup.Manifest, Backup = null, DataVersion = backup.DataVersion };
        var manifest = await PluginPackages.ValidateAsync(_registry.ProgramDirectory(next), token).ConfigureAwait(false);
        if (manifest.Id != next.Manifest.Id || manifest.Version != next.Manifest.Version)
            throw new InvalidDataException("回退版本与登记记录不一致。");
        await SwitchVersionAsync(next, backup, token).ConfigureAwait(false);
        return true;
    });

    private async Task SwitchVersionAsync(InstalledPlugin next, PluginBackup? backup, CancellationToken token)
    {
        string id = next.Manifest.Id;
        CancelRestart(id);
        bool wasRunning = _runs.ContainsKey(id);
        await StopLockedAsync(id).ConfigureAwait(false);
        PluginUpdate? update = null;
        try
        {
            token.ThrowIfCancellationRequested();
            update = _registry.BeginUpdate(id);
            if (backup is not null) _registry.RestoreBackupData(id, backup);
            _registry.UseVersion(next);
            if (next.Enabled)
            {
                var run = await StartLockedAsync(id, 0, token).ConfigureAwait(false);
                await WaitForUsableAsync(run, token).ConfigureAwait(false);
                if (!wasRunning) await StopLockedAsync(id).ConfigureAwait(false);
            }
            _registry.CommitUpdate(next, update, savePrevious: backup is null);
            Notify();
        }
        catch (Exception error) when (IsPluginFailure(error) || error is OperationCanceledException)
        {
            if (update is not null)
            {
                await StopLockedAsync(id).ConfigureAwait(false);
                _registry.RecoverUpdate();
            }
            if (wasRunning && !token.IsCancellationRequested)
            {
                try { await StartLockedAsync(id, 0, token).ConfigureAwait(false); }
                catch (Exception restartError) when (IsPluginFailure(restartError))
                {
                    throw new InvalidOperationException("已恢复原插件版本和数据，原版本启动失败，请查看日志。", restartError);
                }
            }
            Notify();
            throw;
        }
    }

    private static async Task WaitForUsableAsync(PluginRun run, CancellationToken token)
    {
        long start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start) < PluginServer.HeartbeatTimeout)
        {
            var state = run.Instance.Status.State;
            if (run.Process.Exited || state is "degraded" or "stopping") break;
            if (state is "ready" or "needs_configuration") return;
            await Task.Delay(100, token).ConfigureAwait(false);
        }
        throw new InvalidOperationException("新版本未能启动，插件版本与数据将恢复。");
    }

    internal async Task StartAutomaticAsync()
    {
        foreach (var plugin in _registry.List().Where(item => item.Enabled && item.AutoStart))
        {
            try { await StartAsync(plugin.Manifest.Id).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_closing.IsCancellationRequested) { return; }
            catch (Exception error) when (IsPluginFailure(error))
            {
                _logger.Write("desktop", "warn", "plugin_auto_start_failed", "插件自动启动失败。",
                    new { pluginId = plugin.Manifest.Id, errorType = error.GetType().Name });
            }
        }
    }

    internal Task<InstalledPlugin?> InstallAsync(string zip, Func<PluginManifest, Task<bool>> confirm) => RunExclusiveAsync(async token =>
    {
        var plugin = await _registry.InstallAsync(zip, confirm, token).ConfigureAwait(false);
        Notify();
        return plugin;
    });

    internal Task SetAutoStartAsync(string id, bool value) => RunExclusiveAsync(_ =>
    {
        var installed = _registry.Find(id);
        _registry.SetPreferences(id, installed.Enabled, value);
        Notify();
        return Task.FromResult(true);
    });

    internal Task StartAsync(string id) => RunExclusiveAsync(async token =>
    {
        CancelRestart(id);
        await StartLockedAsync(id, 0, token).ConfigureAwait(false);
        return true;
    });

    internal Task OpenSettingsAsync(string id, Action<Uri> open) => RunExclusiveAsync(async token =>
    {
        if (_registry.Find(id).Manifest.Settings is null) throw new InvalidOperationException("此插件没有设置入口。");
        CancelRestart(id);
        var run = await StartLockedAsync(id, 0, token).ConfigureAwait(false);
        if (run.Process.Exited || run.Instance.Settings is not { } address)
            throw new InvalidOperationException("插件设置页不可用，请重新启动插件。");
        open(address);
        return true;
    });

    internal Task StopAsync(string id, bool disable = false) => RunExclusiveAsync(async _ =>
    {
        CancelRestart(id);
        if (disable) _registry.SetPreferences(id, false, false);
        await StopLockedAsync(id).ConfigureAwait(false);
        return true;
    });

    internal Task RestartAsync(string id) => RunExclusiveAsync(async token =>
    {
        CancelRestart(id);
        await StopLockedAsync(id).ConfigureAwait(false);
        await StartLockedAsync(id, 0, token).ConfigureAwait(false);
        return true;
    });

    internal Task UninstallAsync(string id, bool keepData) => RunExclusiveAsync(async _ =>
    {
        CancelRestart(id);
        await StopLockedAsync(id).ConfigureAwait(false);
        _registry.Uninstall(id, keepData);
        Notify();
        return true;
    });

    private async Task<T> RunExclusiveAsync<T>(Func<CancellationToken, Task<T>> action)
    {
        var token = _closing.Token;
        await _operation.WaitAsync(token).ConfigureAwait(false);
        try { return await action(token).ConfigureAwait(false); }
        finally { _operation.Release(); }
    }

    private async Task<PluginRun> StartLockedAsync(string id, int retry, CancellationToken token)
    {
        if (_runs.TryGetValue(id, out var previous))
        {
            if (!previous.Process.Exited && !previous.Instance.Expired) return previous;
            EndRun(id, previous);
        }

        var installed = _registry.Find(id);
        _offline[id] = new("starting");
        Notify();
        PluginRun? run = null;
        PluginInstance? instance = null;
        try
        {
            string program = _registry.ProgramDirectory(installed);
            var manifest = await PluginPackages.ValidateAsync(program, token).ConfigureAwait(false);
            if (manifest.Id != installed.Manifest.Id || manifest.Version != installed.Manifest.Version)
                throw new InvalidDataException("安装清单与登记版本不匹配。");
            if (!installed.Enabled)
            {
                _registry.SetPreferences(id, true, false);
                installed = _registry.Find(id);
            }

            _server ??= await PluginServer.StartAsync(
                credential => _credentials.GetValueOrDefault(credential), Notify, _events, Receive, _runtime).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            instance = new PluginInstance(installed);
            _credentials[instance.Token] = instance;
            string data = _registry.DataDirectory(id);
            string logs = LogDirectory(id);
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(logs);
            var process = PluginProcess.Start(manifest, program, data, logs, _server.Address, instance.Token);
            run = new PluginRun(instance, process, retry, Stopwatch.GetTimestamp());
            _runs[id] = run;
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
            handshake.CancelAfter(PluginServer.HeartbeatTimeout);
            var exited = process.WaitAsync(handshake.Token);
            try
            {
                if (await Task.WhenAny(instance.Hello.Task, exited).WaitAsync(handshake.Token).ConfigureAwait(false) == exited
                    && exited.IsCompletedSuccessfully)
                    throw new InvalidOperationException($"插件在完成握手前已退出（退出码 {process.ExitCode}）。");
                await instance.Hello.Task.WaitAsync(handshake.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new TimeoutException("插件握手超时，请检查插件日志。");
            }
            _offline.TryRemove(id, out _);
            _logger.Write("desktop", "info", "plugin_started", "插件已完成握手。", new { pluginId = id, instanceId = instance.Id });
            _ = MonitorAsync(id, run);
            Notify();
            return run;
        }
        catch (Exception error)
        {
            if (run is not null) EndRun(id, run);
            else if (instance is not null) _credentials.TryRemove(instance.Token, out _);
            _offline[id] = new("failed", error is Win32Exception ? "无法启动插件进程。" : error.Message);
            _logger.Write("desktop", "warn", "plugin_start_failed", "插件启动失败。", new { pluginId = id, errorType = error.GetType().Name });
            Notify();
            throw;
        }
    }

    private async Task StopLockedAsync(string id)
    {
        _offline.TryRemove(id, out _);
        if (!_runs.TryGetValue(id, out var run))
        {
            await _runtime("/v2/plugins/close", new { pluginId = id }).ConfigureAwait(false);
            Notify();
            return;
        }
        run.Instance.Stop();
        Notify();
        var close = _runtime("/v2/plugins/close", new { pluginId = id, instanceId = run.Instance.Id });
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await run.Process.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.Write("desktop", "warn", "plugin_stop_timeout", "插件未在宽限期内退出。", new { pluginId = id });
        }
        finally
        {
            EndRun(id, run);
            Notify();
        }
        await close.ConfigureAwait(false);
    }

    private void EndRun(string id, PluginRun run)
    {
        run.Instance.Stop();
        _events.Close(run.Instance.Id);
        _runs.TryRemove(id, out _);
        _credentials.TryRemove(run.Instance.Token, out _);
        run.Process.Dispose();
    }

    private async Task MonitorAsync(string id, PluginRun run)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _closing.Token).ConfigureAwait(false);
                await _operation.WaitAsync(_closing.Token).ConfigureAwait(false);
                bool failed;
                try
                {
                    if (_runs.GetValueOrDefault(id) != run) return;
                    failed = run.Process.Exited || run.Instance.Expired;
                    if (failed)
                    {
                        EndRun(id, run);
                        _offline[id] = new("waiting_retry", "插件进程退出或心跳中断。");
                        Notify();
                    }
                }
                finally { _operation.Release(); }
                if (failed) break;
            }

            int attempt = Stopwatch.GetElapsedTime(run.Started) >= TimeSpan.FromMinutes(1) ? 0 : run.Retry;
            await RetryAsync(id, attempt, _closing.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (IsPluginFailure(error))
        {
            _logger.Write("desktop", "warn", "plugin_monitor_failed", "插件状态监控失败。", new { pluginId = id, errorType = error.GetType().Name });
        }
    }

    private async Task RetryAsync(string id, int attempt, CancellationToken token)
    {
        using var restart = CancellationTokenSource.CreateLinkedTokenSource(token);
        await _operation.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!_offline.TryGetValue(id, out var status) || status.State != "waiting_retry" || !_restarts.TryAdd(id, restart))
                return;
        }
        finally { _operation.Release(); }

        try
        {
            for (; attempt < 3; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 << attempt), restart.Token).ConfigureAwait(false);
                await _operation.WaitAsync(restart.Token).ConfigureAwait(false);
                try
                {
                    await StartLockedAsync(id, attempt + 1, restart.Token).ConfigureAwait(false);
                    return;
                }
                catch (Exception error) when (IsPluginFailure(error))
                {
                    if (attempt < 2)
                    {
                        _offline[id] = new("waiting_retry", "等待重新启动插件。");
                        Notify();
                    }
                }
                finally { _operation.Release(); }
            }
            if (attempt >= 3 && _offline.TryUpdate(id, new("failed", "插件连续退出，请手动重新启动。"),
                new("waiting_retry", "插件进程退出或心跳中断。"))) Notify();
        }
        finally { _restarts.TryRemove(new KeyValuePair<string, CancellationTokenSource>(id, restart)); }
    }

    private void CancelRestart(string id)
    {
        if (!_restarts.TryRemove(id, out var restart)) return;
        try { restart.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _closing.Cancel();
        await _operation.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_runs.Keys.Select(StopLockedAsync)).ConfigureAwait(false);
            if (_server is not null) await _server.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _catalogClient.Dispose();
            _lease.Dispose();
            _operation.Release();
        }
    }

    internal static bool IsPluginFailure(Exception error) => error is IOException or InvalidDataException
        or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException or Win32Exception
        or TimeoutException or ArgumentException or HttpRequestException;

    private void Notify() => Changed?.Invoke();

    private void Receive(PluginInstance instance, PluginMessage message)
    {
        var id = instance.Plugin.Manifest.Id;
        lock (_messagesLock)
        {
            if (instance.BusinessStopped.IsCancellationRequested || !_messageIds.Add((id, message.Id))) return;
            _recentMessages.Enqueue((id, message.Id));
            if (_recentMessages.Count > 4096) _messageIds.Remove(_recentMessages.Dequeue());
        }
        _logger.Write("desktop", "info", "plugin_message", "收到插件消息。",
            new { pluginId = id, messageId = message.Id, messageType = message.Type, message.CorrelationId });
        if (message.Text is not null) Notice?.Invoke(instance.Plugin.Manifest.Name, message.Text);
    }

    private sealed record PluginRun(PluginInstance Instance, PluginProcess Process, int Retry, long Started);
}
