using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using StardewAI.Companion.Logging;

namespace StardewAI.Companion.Desktop;

internal sealed class DesktopRuntimeApi : IDisposable
{
    private readonly HttpClient _client;
    private readonly CancellationToken _runtimeToken;

    internal DesktopRuntimeApi(RuntimeEndpoint endpoint, CancellationToken runtimeToken)
    {
        _runtimeToken = runtimeToken;
        _client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{endpoint.Port}"),
            Timeout = Timeout.InfiniteTimeSpan
        };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Credential);
    }

    internal void StartEvents(Action<JsonElement> publish, Func<bool> stillCurrent, FileLogger logger)
        => _ = ReceiveEventsAsync(publish, stillCurrent, logger);

    internal async Task<JsonElement> PostAsync(string route, object body, int timeoutSeconds = 25)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_runtimeToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var response = await _client.PostAsJsonAsync(route, body, timeout.Token);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: timeout.Token);
        if (response.IsSuccessStatusCode) return payload;
        throw new InvalidOperationException(payload.TryGetProperty("error", out var message) ? message.GetString() : "请求未获确认。");
    }

    private async Task ReceiveEventsAsync(Action<JsonElement> publish, Func<bool> stillCurrent, FileLogger logger)
    {
        var cancellationToken = _runtimeToken;
        string? cursor = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/v2/events");
                if (cursor is not null) request.Headers.TryAddWithoutValidation("Last-Event-ID", cursor);
                using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream);
                string? id = null;
                while (await reader.ReadLineAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(12), cancellationToken) is { } line)
                {
                    if (line.StartsWith("id: ", StringComparison.Ordinal)) id = line[4..];
                    if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                    using var json = JsonDocument.Parse(line.AsMemory(6));
                    if (!stillCurrent()) return;
                    publish(json.RootElement.Clone());
                    cursor = id ?? cursor;
                    id = null;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TimeoutException)
            {
                logger.Write("desktop", "warn", "event_stream_disconnected", "本地状态连接断开。", new { errorType = ex.GetType().Name });
            }
            using var disconnected = JsonDocument.Parse("""{"type":"status","payload":{"ready":false}}""");
            if (cancellationToken.IsCancellationRequested || !stillCurrent()) return;
            publish(disconnected.RootElement.Clone());
            try { await Task.Delay(1000, cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    public void Dispose() => _client.Dispose();
}
