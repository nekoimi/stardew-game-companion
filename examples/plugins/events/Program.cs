using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

var host = Environment.GetEnvironmentVariable("STARDEW_PLUGIN_HOST_URL") ?? throw new InvalidOperationException("请通过伴侣插件宿主启动。");
var token = Environment.GetEnvironmentVariable("STARDEW_PLUGIN_TOKEN") ?? throw new InvalidOperationException("缺少插件运行凭据。");
var directory = Environment.GetEnvironmentVariable("STARDEW_PLUGIN_LOG_DIR")
    ?? Environment.GetEnvironmentVariable("STARDEW_PLUGIN_DATA_DIR") ?? throw new InvalidOperationException("缺少输出目录。");
Directory.CreateDirectory(directory);
var path = Path.Combine(directory, "events.jsonl");
using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { BaseAddress = new Uri(host) };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
using var quit = new CancellationTokenSource();
try
{
    var hello = await PostAsync("/v1/hello", new { protocolMin = 1, protocolMax = 1 });
    var stream = ReadEventsAsync(quit.Token);
    var interval = TimeSpan.FromMilliseconds(Math.Max(1000, hello.GetProperty("heartbeatIntervalMs").GetInt32()));
    try
    {
        while ((await PostAsync("/v1/heartbeat", new { state = "ready" })).GetProperty("directive").GetString() != "stop")
            await Task.Delay(interval, quit.Token);
    }
    finally
    {
        quit.Cancel();
        try { await stream; }
        catch (OperationCanceledException) when (quit.IsCancellationRequested) { }
    }
}
catch (Exception error) when (error is HttpRequestException or IOException or JsonException or OperationCanceledException)
{
    await RecordAsync(new { type = "disconnected", reason = error.GetType().Name });
    Environment.ExitCode = 1;
}

async Task<JsonElement> PostAsync(string route, object body)
{
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(quit.Token);
    timeout.CancelAfter(TimeSpan.FromSeconds(5));
    using var response = await http.PostAsJsonAsync(route, body, timeout.Token);
    response.EnsureSuccessStatusCode();
    return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: timeout.Token);
}

async Task ReadEventsAsync(CancellationToken cancellation)
{
    string? session = null;
    string? cursor = null;
    var seen = new HashSet<string>(StringComparer.Ordinal);
    var order = new Queue<string>();
    while (!cancellation.IsCancellationRequested)
    {
        try
        {
            if (cursor is null)
            {
                var snapshot = await http.GetFromJsonAsync<JsonElement>("/v1/state", cancellation);
                var next = snapshot.GetProperty("sessionId").GetString();
                if (next != session) { seen.Clear(); order.Clear(); }
                session = next;
                cursor = snapshot.GetProperty("cursor").GetString()!;
                await RecordAsync(new { type = "snapshot", payload = snapshot });
            }
            var route = "/v1/events?sessionId=" + Uri.EscapeDataString(session ?? "") + "&cursor=" + Uri.EscapeDataString(cursor);
            using var request = new HttpRequestMessage(HttpMethod.Get, route);
            request.Headers.TryAddWithoutValidation("Last-Event-ID", cursor);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (response.StatusCode == HttpStatusCode.Conflict) { cursor = null; continue; }
            response.EnsureSuccessStatusCode();
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation));
            string? id = null, topic = null, data = null;
            while (await reader.ReadLineAsync(cancellation) is { } line)
            {
                if (line.StartsWith("id: ", StringComparison.Ordinal)) id = line[4..];
                else if (line.StartsWith("event: ", StringComparison.Ordinal)) topic = line[7..];
                else if (line.StartsWith("data: ", StringComparison.Ordinal)) data = line[6..];
                else if (line.Length == 0 && id is not null && topic is not null && data is not null)
                {
                    using var frame = JsonDocument.Parse(data);
                    var value = frame.RootElement;
                    if (topic is "game.state" or "agent.state" or "agent.action.completed" or "agent.reply.completed")
                    {
                        var payload = value.GetProperty("payload");
                        if (topic != "agent.reply.completed" || FirstReply(payload.GetProperty("messageId").GetString()!))
                            await RecordAsync(new { type = topic, id, payload });
                    }
                    cursor = id;
                    id = topic = data = null;
                }
            }
        }
        catch (Exception error) when (!cancellation.IsCancellationRequested && (error is HttpRequestException or IOException or JsonException)) { }
        await Task.Delay(500, cancellation);
    }

    bool FirstReply(string id)
    {
        if (!seen.Add(id)) return false;
        order.Enqueue(id);
        if (order.Count > 1024) seen.Remove(order.Dequeue());
        return true;
    }
}

async Task RecordAsync(object item)
{
    if (File.Exists(path) && new FileInfo(path).Length >= 2 * 1024 * 1024)
        File.Move(path, path + ".1", overwrite: true);
    await File.AppendAllTextAsync(path, JsonSerializer.Serialize(item) + "\n");
}
