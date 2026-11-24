using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using StardewAI.Companion.Desktop;
using StardewAI.Companion.Logging;

internal static class DesktopEventStreamChecks
{
    internal static async Task RunAsync(FileLogger logger)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new ConcurrentQueue<JsonElement>();
        string reconnectHeaders = "";
        var server = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
                var headers = new StringBuilder();
                while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } line) headers.AppendLine(line);
                if (attempt == 1) reconnectHeaders = headers.ToString();
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\n\r\n"), timeout.Token);
                async Task Chunk(string text)
                {
                    var bytes = Encoding.UTF8.GetBytes(text);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"{bytes.Length:X}\r\n"), timeout.Token);
                    await stream.WriteAsync(bytes, timeout.Token);
                    await stream.WriteAsync("\r\n"u8.ToArray(), timeout.Token);
                    await stream.FlushAsync(timeout.Token);
                }
                if (attempt == 0)
                {
                    await Chunk("id: 41\ndata: {\"type\":\"status\",\"payload\":{\"ready\":true,\"gameSessionId\":\"game\"}}\n\n");
                    // A complete data line without the SSE delimiter is not delivered.
                    await Chunk("id: 42\ndata: {\"type\":\"life.chat.reply\",\"payload\":{\"replyText\":\"未确认的半帧\"}}\n");
                }
                else
                {
                    await Chunk("id: 42\ndata: {\"type\":\"life.chat.reply\",\"payload\":{\"replyText\":\"完整回复\"}}\n\n");
                    await Chunk("id: 43\ndata: {\"type\":\"status\",\"payload\":{\"ready\":true,\"gameSessionId\":\"game\",\"restored\":true}}\n\n");
                    await completed.Task.WaitAsync(timeout.Token);
                }
            }
        }, timeout.Token);
        try
        {
            using var api = new DesktopRuntimeApi(new RuntimeEndpoint(((IPEndPoint)listener.LocalEndpoint).Port, "isolated-test"), timeout.Token);
            api.StartEvents(message =>
            {
                received.Enqueue(message);
                if (message.GetProperty("payload").TryGetProperty("restored", out _)) completed.TrySetResult();
            }, () => !timeout.IsCancellationRequested, logger);
            await completed.Task.WaitAsync(timeout.Token);
            await server.WaitAsync(timeout.Token);
            var events = received.ToArray();
            if (!reconnectHeaders.Contains("Last-Event-ID: 41", StringComparison.OrdinalIgnoreCase)
                || events.Count(item => item.GetProperty("type").GetString() == "desktop.connection") != 1
                || events.Any(item => item.GetProperty("type").GetString() == "status" && !item.GetProperty("payload").GetProperty("ready").GetBoolean())
                || events.Count(item => item.GetProperty("type").GetString() == "life.chat.reply") != 1
                || events.Single(item => item.GetProperty("type").GetString() == "life.chat.reply").GetProperty("payload").GetProperty("replyText").GetString() != "完整回复")
                throw new InvalidOperationException("SSE reconnect lost its cursor, published an incomplete frame or falsely disconnected the game.");
        }
        finally
        {
            await timeout.CancelAsync();
            listener.Stop();
            try { await server; } catch (OperationCanceledException) { }
        }
    }
}
