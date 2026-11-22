using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace StardewAI.Companion.Desktop.Plugins;

internal sealed record PluginCallResult(int Status, JsonElement Payload)
{
    internal static PluginCallResult Unavailable() => new(503,
        JsonSerializer.SerializeToElement(new PluginError("runtime_unavailable", "伴侣运行时暂未连接。"), PluginEvents.OutputJson));
}

internal sealed record PluginMessage(string Type, string Id, string? CorrelationId, string? Text);

internal sealed partial class PluginServer
{
    [GeneratedRegex(@"\A[A-Za-z0-9_.-]{1,120}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    private static async Task ReadAsync(HttpContext context, PluginInstance instance, string path, PluginEvents events,
        Func<string, object, Task<PluginCallResult>> runtime)
    {
        if (path == "/v1/state")
        {
            await context.Response.WriteAsJsonAsync(events.Snapshot(), PluginEvents.OutputJson, cancellationToken: context.RequestAborted);
            return;
        }
        if (path == "/v1/events")
        {
            await StreamAsync(context, instance, events);
            return;
        }
        const string prefix = "/v1/commands/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || !Identifier().IsMatch(path[prefix.Length..]))
        {
            await Error(context, 404, "not_found", "找不到插件接口或指令。");
            return;
        }
        await RelayAsync(context, runtime("/v2/plugins/query",
            new { pluginId = instance.Plugin.Manifest.Id, requestId = path[prefix.Length..] }));
    }

    private static async Task StreamAsync(HttpContext context, PluginInstance instance, PluginEvents events)
    {
        var query = context.Request.Query;
        var session = query["sessionId"].ToString();
        var cursorText = context.Request.Headers.TryGetValue("Last-Event-ID", out var header) ? header.ToString() : query["cursor"].ToString();
        var requested = query["topics"].ToString();
        var topics = requested.Length == 0 ? PluginEvents.Topics : requested.Split(',').ToHashSet(StringComparer.Ordinal);
        if (!long.TryParse(cursorText, NumberStyles.None, CultureInfo.InvariantCulture, out long cursor)
            || session.Length > 0 && !Identifier().IsMatch(session) || !topics.IsSubsetOf(PluginEvents.Topics))
        {
            await Error(context, 400, "invalid_request", "订阅主题、游标或会话标识无效。");
            return;
        }
        var (stream, error) = events.Subscribe(instance, session.Length == 0 ? null : session, cursor, topics);
        if (stream is null)
        {
            if (error == "invalid_instance") await Error(context, 401, error, "插件正在停止。");
            else if (error == "limit_exceeded") await Error(context, 429, error, "订阅连接已达上限。");
            else await Error(context, 409, error!, "请获取最新状态快照后重新订阅。");
            return;
        }
        using (stream)
        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, instance.BusinessStopped, stream.End.Token))
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-store";
            var token = cancel.Token;
            try
            {
                await context.Response.WriteAsync(": connected\n\n", token);
                while (!token.IsCancellationRequested)
                {
                    using var next = CancellationTokenSource.CreateLinkedTokenSource(token);
                    next.CancelAfter(TimeSpan.FromSeconds(8));
                    try
                    {
                        if (!await stream.Frames.Reader.WaitToReadAsync(next.Token)) break;
                        while (stream.Frames.Reader.TryRead(out var frame))
                        {
                            await WriteFrame($"id: {frame.Id}\nevent: {frame.Topic}\ndata: {frame.Data}\n\n");
                        }
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        await WriteFrame(": heartbeat\n\n");
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }

            async Task WriteFrame(string text)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try { await context.Response.WriteAsync(text, timeout.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    context.Abort();
                    cancel.Cancel();
                }
            }
        }
    }

    private static async Task MessageAsync(HttpContext context, PluginInstance instance, JsonElement payload,
        Action<PluginInstance, PluginMessage> receive)
    {
        string? type = ReadString(payload, "type");
        string? id = ReadString(payload, "messageId");
        string? at = ReadString(payload, "at");
        if (!TryInt(payload, "version", out var version) || version != 1 || type is not ("status" or "notification" or "event")
            || id is null || !Identifier().IsMatch(id) || at is null || at.Length > 40
            || !DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            || !payload.TryGetProperty("payload", out var data) || data.ValueKind != JsonValueKind.Object
            || !ValidOptionalIdentifier(payload, "correlationId"))
        {
            await Error(context, 400, "invalid_request", "插件消息字段无效。");
            return;
        }
        var text = type == "notification" ? ReadString(data, "text")?.Trim() : null;
        if (type == "notification" && text is not { Length: > 0 and <= 500 })
        {
            await Error(context, 400, "invalid_request", "通知需要提供 1 至 500 个字符的文本。");
            return;
        }
        if (instance.BusinessStopped.IsCancellationRequested)
        {
            await Error(context, 401, "invalid_instance", "插件正在停止。");
            return;
        }

        receive(instance, new PluginMessage(type, id, ReadString(payload, "correlationId"), text));
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private static async Task SubmitAsync(HttpContext context, PluginInstance instance, JsonElement payload,
        Func<string, object, Task<PluginCallResult>> runtime)
    {
        var requestId = ReadString(payload, "requestId");
        var sessionId = ReadString(payload, "sessionId");
        var text = ReadString(payload, "text")?.Trim();
        if (requestId is null || !Identifier().IsMatch(requestId) || sessionId is null || !Identifier().IsMatch(sessionId)
            || text is not { Length: > 0 and <= 500 } || !ValidOptionalIdentifier(payload, "correlationId"))
        {
            await Error(context, 400, "invalid_request", "插件指令字段无效。");
            return;
        }
        if (instance.BusinessStopped.IsCancellationRequested)
        {
            await Error(context, 401, "invalid_instance", "插件正在停止。");
            return;
        }
        if (instance.Status.State != "ready")
        {
            await Error(context, 409, "not_ready", "插件尚未就绪。");
            return;
        }
        await RelayAsync(context, runtime("/v2/plugins/submit", new
        {
            pluginId = instance.Plugin.Manifest.Id, instanceId = instance.Id,
            requestId, sessionId, text, correlationId = ReadString(payload, "correlationId")
        }));
    }

    private static bool ValidOptionalIdentifier(JsonElement payload, string name) =>
        !payload.TryGetProperty(name, out _) || ReadString(payload, name) is { } value && Identifier().IsMatch(value);

    private static async Task RelayAsync(HttpContext context, Task<PluginCallResult> call)
    {
        var result = await call;
        context.Response.StatusCode = result.Status;
        await context.Response.WriteAsJsonAsync(result.Payload, cancellationToken: context.RequestAborted);
    }
}
