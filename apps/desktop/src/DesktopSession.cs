using System.IO;
using System.Text.Json;
using StardewAI.Companion.Desktop.Plugins;
using StardewAI.Companion.Logging;

namespace StardewAI.Companion.Desktop;

internal sealed class DesktopSession
{
    private readonly string _root;
    private readonly FileLogger _logger;
    private readonly DesktopProcesses _processes;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private CancellationTokenSource? _runtimeLifetime;
    private DesktopSettings? _settings;
    private string _modelKey = "";
    private volatile int _runtimeGeneration;
    private DesktopRuntimeApi? _api;
    private FileStream? _packageLease;
    private bool _stopping;
    private Task? _stopTask;

    public event Action<int, JsonElement>? EventReceived;
    public event Action<int>? RuntimeExited;
    public event Action? GameExited;
    public int RuntimeGeneration => _runtimeGeneration;
    public bool CanReconnect => !_stopping && _processes.GameRunning && !_processes.RuntimeRunning;

    public DesktopSession(string root, FileLogger logger)
    {
        _root = root;
        _logger = logger;
        _processes = new DesktopProcesses(root, logger);
        _processes.RuntimeExited += generation => RuntimeExited?.Invoke(generation);
        _processes.GameExited += () => GameExited?.Invoke();
    }

    public async Task StartAsync(DesktopSettings settings, string key)
    {
        await _lifecycle.WaitAsync(_lifetime.Token);
        try
        {
            if (DesktopProcesses.AnyGameRunning())
                throw new InvalidOperationException("游戏已在运行，请先在游戏中保存并退出，再从桌面启动。");
            var gameDirectory = Path.GetFullPath(settings.GameDirectory);
            if (!DesktopInstallation.IsGameDirectory(gameDirectory))
                throw new FileNotFoundException("请选择包含星露谷和 StardewModdingAPI.exe 的游戏目录。");
            string node = DesktopInstallation.ResolveRuntime(_root);
            _packageLease = DesktopInstallation.AcquirePackageLease(_root);
            DesktopInstallation.ConfigureGame(_root, settings);

            _settings = settings;
            _modelKey = key;
            await StartRuntimeAsync(node, settings);
            _lifetime.Token.ThrowIfCancellationRequested();
            if (!_processes.RuntimeRunning)
                throw new InvalidOperationException("伴侣服务提前退出，请查看诊断。");
            _processes.StartGame(gameDirectory);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task ReconnectAsync()
    {
        await _lifecycle.WaitAsync(_lifetime.Token);
        try
        {
            if (!CanReconnect || _settings is not { } settings)
                throw new InvalidOperationException("当前无法重连伴侣服务。");
            await StopRuntimeAsync();
            try { await StartRuntimeAsync(DesktopInstallation.ResolveRuntime(_root), settings); }
            catch
            {
                await StopRuntimeAsync();
                throw;
            }
        }
        finally { _lifecycle.Release(); }
    }

    private async Task StartRuntimeAsync(string node, DesktopSettings settings)
    {
        var generation = ++_runtimeGeneration;
        _runtimeLifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _runtimeLifetime.Token;
        var endpoint = await _processes.StartRuntimeAsync(node, generation, token);
        token.ThrowIfCancellationRequested();
        _api = new DesktopRuntimeApi(endpoint, token);
        var selected = settings.ApiModel ?? new();
        await _api.PostAsync("/v2/model/configure", ModelRequest(selected, _modelKey));
        _api.StartEvents(message => EventReceived?.Invoke(generation, message),
            () => generation == _runtimeGeneration && _processes.RuntimeRunning, _logger);
    }

    public Task<JsonElement> PostAsync(string route, object body, int timeoutSeconds = 25)
        => (_api ?? throw new InvalidOperationException("伴侣服务尚未启动。")).PostAsync(route, body, timeoutSeconds);

    internal Task<PluginCallResult> PostPluginAsync(string route, object body)
        => Volatile.Read(ref _api)?.PostPluginAsync(route, body) ?? Task.FromResult(PluginCallResult.Unavailable());

    internal static async Task TestModelAsync(string root, ApiModelSettings selected, string key, FileLogger logger)
    {
        var processes = new DesktopProcesses(root, logger);
        int requestTimeout = selected.Stream ? 95 : 185;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(requestTimeout + 20));
        try
        {
            var endpoint = await processes.StartRuntimeAsync(DesktopInstallation.ResolveRuntime(root), 0, cancellation.Token, modelTestOnly: true);
            using var api = new DesktopRuntimeApi(endpoint, cancellation.Token);
            await api.PostAsync("/v2/model/test", ModelRequest(selected, key), timeoutSeconds: requestTimeout);
        }
        finally { await processes.StopRuntimeAsync(); }
    }

    private static object ModelRequest(ApiModelSettings selected, string key) => new {
        selection = selected,
        apiKey = key,
    };

    internal async Task ConfigureModelAsync(ApiModelSettings selected, string key)
    {
        if (_settings is not { } settings) throw new InvalidOperationException("伴侣服务尚未启动。");
        await PostAsync("/v2/model/configure", ModelRequest(selected, key));
        _settings = settings with { ApiModel = selected };
        _modelKey = key;
    }

    public Task StopAsync() => _stopTask ??= StopCoreAsync();

    private async Task StopCoreAsync()
    {
        _stopping = true;
        _lifetime.Cancel();
        await _lifecycle.WaitAsync();
        try { await StopRuntimeAsync(); }
        finally
        {
            _modelKey = "";
            _processes.ReleaseGameHandle();
            _packageLease?.Dispose();
            _lifecycle.Release();
        }
    }

    private async Task StopRuntimeAsync()
    {
        _runtimeLifetime?.Cancel();
        _runtimeLifetime?.Dispose();
        _runtimeLifetime = null;
        _api?.Dispose();
        _api = null;
        await _processes.StopRuntimeAsync();
    }
}
