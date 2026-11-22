using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace StardewAI.Companion.Desktop.Plugins;

internal sealed record PluginStatus(string State, string? Summary = null, bool RestartRequired = false);
internal sealed record PluginError(string Code, string Message, int? RetryAfterMs = null);

internal sealed class PluginInstance(InstalledPlugin plugin)
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _businessStopped = new();
    private long _lastHeartbeat;
    private long _rateWindow = Stopwatch.GetTimestamp();
    private int _requests;
    private bool _stopping;
    private Uri? _settings;
    private PluginStatus _status = new("starting");

    internal InstalledPlugin Plugin { get; } = plugin;
    internal string Id { get; } = Guid.NewGuid().ToString("N");
    internal string Token { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    internal TaskCompletionSource Hello { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal CancellationToken BusinessStopped => _businessStopped.Token;
    internal PluginStatus Status { get { lock (_sync) return _status; } }
    internal Uri? Settings { get { lock (_sync) return _stopping ? null : _settings; } }
    internal bool Expired { get { lock (_sync) return _lastHeartbeat != 0 && Stopwatch.GetElapsedTime(_lastHeartbeat) >= PluginServer.HeartbeatTimeout; } }

    internal void Stop()
    {
        lock (_sync)
        {
            _stopping = true;
            _status = new("stopping");
        }
        _businessStopped.Cancel();
    }

    internal int Authorize(bool lifecycle)
    {
        lock (_sync)
        {
            if (_stopping && !lifecycle) return 401;
            if (!lifecycle || !_stopping)
            {
                if (Stopwatch.GetElapsedTime(_rateWindow) >= TimeSpan.FromSeconds(10))
                {
                    _rateWindow = Stopwatch.GetTimestamp();
                    _requests = 0;
                }
                if (++_requests > 60) return 429;
            }
            return 200;
        }
    }

    internal bool TryHello(Uri? settings)
    {
        lock (_sync)
        {
            if (_stopping || Hello.Task.IsCompleted) return false;
            _settings = settings;
            _lastHeartbeat = Stopwatch.GetTimestamp();
            _status = new("connected");
            Hello.SetResult();
            return true;
        }
    }

    internal string? Heartbeat(PluginStatus status)
    {
        lock (_sync)
        {
            if (!Hello.Task.IsCompletedSuccessfully) return null;
            if (_stopping) return "stop";
            _lastHeartbeat = Stopwatch.GetTimestamp();
            _status = status;
            return "continue";
        }
    }
}

internal sealed partial class PluginServer : IAsyncDisposable
{
    internal static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(15);
    internal const int HeartbeatIntervalMs = 5000;
    private const int MaxBodyBytes = 64 * 1024;
    private readonly WebApplication _app;

    private PluginServer(WebApplication app) => _app = app;
    internal string Address => _app.Urls.Single();

    internal static async Task<PluginServer> StartAsync(Func<string, PluginInstance?> identify, Action changed,
        PluginEvents events, Action<PluginInstance, PluginMessage> message,
        Func<string, object, Task<PluginCallResult>> runtime)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Listen(IPAddress.Loopback, 0);
            options.Limits.MaxRequestBodySize = MaxBodyBytes;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(20);
            options.Limits.MaxConcurrentConnections = 64;
        });
        var app = builder.Build();
        app.Run(context => DispatchAsync(context, identify, changed, events, message, runtime));
        try { await app.StartAsync().ConfigureAwait(false); }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return new PluginServer(app);
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await _app.StopAsync(timeout.Token).ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task DispatchAsync(HttpContext context, Func<string, PluginInstance?> identify, Action changed,
        PluginEvents events, Action<PluginInstance, PluginMessage> message,
        Func<string, object, Task<PluginCallResult>> runtime)
    {
        if (!context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            || identify(context.Request.Headers.Authorization.ToString()[7..]) is not { } instance)
        {
            await Error(context, 401, "invalid_instance", "插件运行凭据无效。");
            return;
        }

        string path = context.Request.Path;
        var auth = instance.Authorize(lifecycle: path == "/v1/heartbeat");
        if (auth == 401) { await Error(context, auth, "invalid_instance", "插件正在停止。"); return; }
        if (auth == 429) { await Error(context, auth, "limit_exceeded", "请求过于频繁。", 10000); return; }
        bool post = HttpMethods.IsPost(context.Request.Method);
        if (!(post && path is "/v1/hello" or "/v1/heartbeat"))
        {
            if (!instance.Hello.Task.IsCompletedSuccessfully)
            {
                await Error(context, 409, "not_ready", "请先完成插件握手。");
                return;
            }
            if (HttpMethods.IsGet(context.Request.Method))
            {
                await ReadAsync(context, instance, path, events, runtime);
                return;
            }
            if (!post || path is not ("/v1/messages" or "/v1/commands"))
            {
                await Error(context, 404, "not_found", "找不到插件接口。");
                return;
            }
            await ReadBodyAsync(context, payload => path == "/v1/messages"
                ? MessageAsync(context, instance, payload, message) : SubmitAsync(context, instance, payload, runtime));
            return;
        }
        await ReadBodyAsync(context, payload => path == "/v1/hello"
            ? HelloAsync(context, instance, payload, changed, events)
            : HeartbeatAsync(context, instance, payload, changed, events));
    }

    private static async Task ReadBodyAsync(HttpContext context, Func<JsonElement, Task> route)
    {
        try
        {
            if (!context.Request.HasJsonContentType())
            {
                await Error(context, 400, "invalid_request", "请求应使用 JSON。");
                return;
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: timeout.Token);
            var payload = body.RootElement;
            if (payload.ValueKind != JsonValueKind.Object)
            {
                await Error(context, 400, "invalid_request", "请求应为 JSON 对象。");
                return;
            }
            await route(payload);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            await Error(context, 413, "payload_too_large", "请求大小超出限制。");
        }
        catch (JsonException) { await Error(context, 400, "invalid_request", "请求格式无效。"); }
        catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
        { await Error(context, 400, "invalid_request", "读取请求超时。"); }
    }

    private static async Task HelloAsync(HttpContext context, PluginInstance instance, JsonElement payload, Action changed, PluginEvents events)
    {
        if (!TryInt(payload, "protocolMin", out int min) || !TryInt(payload, "protocolMax", out int max) || min < 1 || max < min)
        {
            await Error(context, 400, "invalid_request", "插件协议范围无效。");
            return;
        }
        if (min > PluginPackages.ProtocolVersion || max < PluginPackages.ProtocolVersion)
        {
            instance.Hello.TrySetException(new InvalidOperationException("插件协议版本不兼容。"));
            await Error(context, 409, "protocol_incompatible", "插件协议版本不兼容。");
            return;
        }
        Uri? settings = null;
        bool hasSettings = payload.TryGetProperty("settingsUrl", out var url);
        if ((instance.Plugin.Manifest.Settings is not null) != hasSettings
            || hasSettings && (url.ValueKind != JsonValueKind.String || !TrySettingsUrl(url.GetString(), out settings)))
        {
            await Error(context, 400, "invalid_request", "插件设置地址须是本次运行的回环 HTTP(S) 地址。");
            return;
        }
        if (!instance.TryHello(settings))
        {
            await Error(context, 409, "not_ready", "插件实例已经握手或正在停止。");
            return;
        }
        changed();
        await context.Response.WriteAsJsonAsync(new { protocolVersion = PluginPackages.ProtocolVersion, instanceId = instance.Id,
            heartbeatIntervalMs = HeartbeatIntervalMs, sessionId = events.Snapshot().SessionId }, context.RequestAborted);
    }

    private static async Task HeartbeatAsync(HttpContext context, PluginInstance instance, JsonElement payload, Action changed, PluginEvents events)
    {
        var state = ReadString(payload, "state");
        string? summary = ReadString(payload, "summary");
        bool hasRestart = payload.TryGetProperty("restartRequired", out var restart);
        if (state is not ("starting" or "needs_configuration" or "ready" or "degraded" or "stopping")
            || payload.TryGetProperty("summary", out _) && (summary is null || summary.Length > 500)
            || hasRestart && restart.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            await Error(context, 400, "invalid_request", "插件心跳状态无效。");
            return;
        }
        var next = new PluginStatus(state, summary, hasRestart && restart.GetBoolean());
        var previous = instance.Status;
        string? directive = instance.Heartbeat(next);
        if (directive is null) { await Error(context, 409, "not_ready", "请先完成插件握手。"); return; }
        if (directive == "continue" && previous != next) changed();
        await context.Response.WriteAsJsonAsync(new { directive, sessionId = events.Snapshot().SessionId }, context.RequestAborted);
    }

    private static bool TrySettingsUrl(string? url, out Uri? result)
    {
        result = null;
        if (url is not { Length: > 0 and <= 2048 } || url.Any(char.IsControl)
            || !Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https")
            || parsed.Host is not ("127.0.0.1" or "[::1]") || parsed.UserInfo.Length != 0 || parsed.Port < 1)
            return false;
        result = parsed;
        return true;
    }

    private static bool TryInt(JsonElement value, string name, out int number)
    {
        number = 0;
        return value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out number);
    }

    private static string? ReadString(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static Task Error(HttpContext context, int status, string code, string message, int? retry = null)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new PluginError(code, message, retry),
            PluginPackages.Json, cancellationToken: context.RequestAborted);
    }
}
