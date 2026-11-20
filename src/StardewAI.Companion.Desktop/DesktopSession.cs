using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace StardewAI.Companion.Desktop;

internal sealed record DesktopSettings(string GameDirectory = "", string ActorMode = "companion", string Model = "", string PiPath = "");

internal sealed class DesktopSession
{
    private readonly string _root;
    private readonly string _mod;
    private readonly string _stopFile = Path.Combine(Path.GetTempPath(), $"stardew-desktop-{Guid.NewGuid():N}.stop");
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _lifetime = new();
    private Process? _runtime;
    private Process? _game;
    private HttpClient? _client;
    private FileStream? _packageLease;
    private bool _stopping;
    public event Action<JsonElement>? EventReceived;
    public event Action? GameExited;
    public string SettingsFile => Path.Combine(_root, "data", "desktop-settings.json");

    public DesktopSession(string root, Action<string> log)
    {
        _root = root;
        _mod = ModDirectory(root);
        _log = log;
    }

    public async Task StartAsync(DesktopSettings settings)
    {
        if (AnyGameRunning())
            throw new InvalidOperationException("游戏已在运行，请先在游戏中保存并退出，再从桌面启动。");
        var gameDirectory = Path.GetFullPath(settings.GameDirectory);
        var smapi = Path.Combine(gameDirectory, "StardewModdingAPI.exe");
        if (!IsGameDirectory(gameDirectory))
            throw new FileNotFoundException("请选择包含星露谷和 StardewModdingAPI.exe 的游戏目录。");
        var python = Path.Combine(_mod, "runtime", "python", "python.exe");
        if (!File.Exists(python) || !File.Exists(Path.Combine(_mod, "release-manifest.json")))
            throw new FileNotFoundException("发行包文件不完整，请完整解压 ZIP 后再打开程序。");

        _packageLease = AcquirePackageLease(_root);
        if (ImportPending(_root)) throw new InvalidOperationException("请在「设置」完成上次导入后再启动游戏。");
        var configPath = Path.Combine(_mod, "config.json");
        var config = File.Exists(configPath) ? JsonNode.Parse(File.ReadAllText(configPath))?.AsObject() : new JsonObject();
        if (config is null) throw new InvalidDataException("伙伴设置文件无效。");
        config["Mode"] = settings.ActorMode;
        config["ConnectionSource"] = "builtin";
        config["ConnectionSourceChosen"] = true;
        WriteJson(configPath, config);
        WriteJson(SettingsFile, settings);

        var credential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var runtimeStart = new ProcessStartInfo(python)
        {
            WorkingDirectory = _mod, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in new[] { "-B", "-m", "stardew_ai_runtime.desktop_service", "--run-dir", _mod,
            "--owner-pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--model", settings.Model, "--pi-cmd", settings.PiPath })
            runtimeStart.ArgumentList.Add(argument);
        runtimeStart.Environment["STARDEW_DESKTOP_TOKEN"] = credential;
        runtimeStart.Environment["STARDEW_BRIDGE_STOP_FILE"] = _stopFile;
        runtimeStart.Environment["STARDEW_CONNECTION_SOURCE"] = "builtin";
        runtimeStart.Environment["PYTHONUTF8"] = "1";
        // The desktop starts a portable runtime next to its own Mod.
        runtimeStart.Environment.Remove("STARDEW_CHAT_BACKEND_CONFIG");

        var ready = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _runtime = new Process { StartInfo = runtimeStart, EnableRaisingEvents = true };
        _runtime.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not { } line) return;
            try
            {
                using var json = JsonDocument.Parse(line);
                if (json.RootElement.TryGetProperty("port", out var port) && port.TryGetInt32(out int number) && number is > 0 and <= 65535)
                {
                    ready.TrySetResult(number);
                    return;
                }
                if (json.RootElement.TryGetProperty("error", out var error))
                    ready.TrySetException(new InvalidOperationException(error.GetString()));
            }
            catch (JsonException) { }
            _log(line);
        };
        _runtime.ErrorDataReceived += (_, args) => { if (args.Data is { } line) _log(line); };
        _runtime.Exited += (_, _) =>
        {
            if (!ready.TrySetException(new InvalidOperationException("伴侣服务提前退出，请查看诊断。")))
                _log("伴侣服务已退出。");
        };
        if (!_runtime.Start()) throw new InvalidOperationException("无法启动伴侣服务。");
        _runtime.BeginOutputReadLine();
        _runtime.BeginErrorReadLine();
        int endpoint = await ready.Task.WaitAsync(TimeSpan.FromSeconds(35), _lifetime.Token);
        _client = new HttpClient(new HttpClientHandler { UseProxy = false })
        {
            BaseAddress = new Uri($"http://127.0.0.1:{endpoint}"),
            Timeout = Timeout.InfiniteTimeSpan
        };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        _ = ReceiveEventsAsync(_client, _lifetime.Token);

        if (AnyGameRunning())
            throw new InvalidOperationException("游戏已在运行，请先保存并退出。");
        var gameStart = new ProcessStartInfo(smapi)
        {
            WorkingDirectory = gameDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        gameStart.ArgumentList.Add("--mods-path");
        gameStart.ArgumentList.Add(Path.Combine(_root, "Mods"));
        gameStart.Environment["STARDEW_DESKTOP_MODE"] = "1";
        _game = new Process { StartInfo = gameStart, EnableRaisingEvents = true };
        _game.OutputDataReceived += (_, args) => { if (args.Data is { } line) _log(line); };
        _game.ErrorDataReceived += (_, args) => { if (args.Data is { } line) _log(line); };
        _game.Exited += (_, _) => GameExited?.Invoke();
        if (!_game.Start()) throw new InvalidOperationException("无法启动星露谷。");
        _game.BeginOutputReadLine();
        _game.BeginErrorReadLine();
    }

    public async Task<JsonElement> PostAsync(string route, object body)
    {
        var client = _client ?? throw new InvalidOperationException("伴侣服务尚未启动。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        using var response = await client.PostAsJsonAsync(route, body, timeout.Token);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: timeout.Token);
        if (response.IsSuccessStatusCode) return payload;
        throw new InvalidOperationException(payload.TryGetProperty("error", out var message) ? message.GetString() : "请求未获确认。");
    }

    private async Task ReceiveEventsAsync(HttpClient client, CancellationToken cancellationToken)
    {
        string? cursor = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/events");
                if (cursor is not null) request.Headers.TryAddWithoutValidation("Last-Event-ID", cursor);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(stream);
                string? id = null;
                while (await reader.ReadLineAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(12), cancellationToken) is { } line)
                {
                    if (line.StartsWith("id: ", StringComparison.Ordinal)) id = line[4..];
                    if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                    using var json = JsonDocument.Parse(line.AsMemory(6));
                    EventReceived?.Invoke(json.RootElement.Clone());
                    cursor = id ?? cursor;
                    id = null;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TimeoutException)
            {
                _log($"本地状态连接断开：{ex.Message}");
            }
            using var disconnected = JsonDocument.Parse("""{"type":"status","payload":{"ready":false}}""");
            EventReceived?.Invoke(disconnected.RootElement.Clone());
            if (_runtime?.HasExited != false) return;
            try { await Task.Delay(1000, cancellationToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    public async Task StopAsync()
    {
        if (_stopping) return;
        _stopping = true;
        _lifetime.Cancel();
        _client?.Dispose();
        _game?.Dispose();
        var runtime = _runtime;
        try
        {
            if (runtime is not null && !runtime.HasExited)
            {
                File.WriteAllText(_stopFile, "stop");
                try { await runtime.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(13)); }
                catch (TimeoutException) when (!runtime.HasExited) { runtime.Kill(entireProcessTree: true); }
            }
        }
        catch (InvalidOperationException) { }
        finally
        {
            runtime?.Dispose();
            _packageLease?.Dispose();
            if (File.Exists(_stopFile)) File.Delete(_stopFile);
        }
    }

    internal static void WriteJson<T>(string path, T data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string pending = path + ".tmp";
        File.WriteAllText(pending, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(pending, path, overwrite: true);
    }

    internal static string ModDirectory(string root) => Path.Combine(root, "Mods", "StardewAI.Companion.Mod");

    internal static bool ImportPending(string root) => Directory.Exists(Path.Combine(ModDirectory(root), ".desktop-import-pending"));

    internal static async Task<JsonElement> ImportAsync(string root, string? source, bool dataOnly)
    {
        if (AnyGameRunning()) throw new InvalidOperationException("请先在游戏中保存并退出，再导入旧数据。");
        string target = ModDirectory(root);
        string python = Path.Combine(target, "runtime", "python", "python.exe");
        if (!File.Exists(python)) throw new FileNotFoundException("绿色包不完整，请完整解压后再导入。");
        using var lease = AcquirePackageLease(root);
        string? sourceRoot = source is null ? null : Directory.GetParent(source)?.Parent?.FullName;
        using var sourceLease = sourceRoot is not null && File.Exists(Path.Combine(sourceRoot, "星露谷游戏伴侣.exe"))
            ? AcquirePackageLease(sourceRoot) : null;
        if (AnyGameRunning()) throw new InvalidOperationException("游戏正在运行，请保存并退出后重试。");

        var start = new ProcessStartInfo(python)
        {
            WorkingDirectory = target, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "-B", "-m", "stardew_ai_runtime.desktop_import", "--target", target })
            start.ArgumentList.Add(argument);
        if (source is not null)
        {
            start.ArgumentList.Add("--source");
            start.ArgumentList.Add(source);
        }
        if (dataOnly) start.ArgumentList.Add("--data-only");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动数据导入。");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var json = await output;
        var stderr = await errors;
        if (string.IsNullOrWhiteSpace(json)) throw new InvalidOperationException($"数据导入进程未确认结果。{stderr.Trim()}");
        using var result = JsonDocument.Parse(json);
        if (result.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.GetString());
        if (process.ExitCode != 0) throw new InvalidOperationException("数据导入进程未确认结果。");
        return result.RootElement.Clone();
    }

    private static FileStream AcquirePackageLease(string root)
    {
        var directory = Path.Combine(root, "data");
        Directory.CreateDirectory(directory);
        try { return File.Open(Path.Combine(directory, "desktop.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new InvalidOperationException("所选绿色包已在运行，请退出后重试。", ex); }
    }

    private static bool AnyGameRunning() => new[] { "StardewModdingAPI", "Stardew Valley" }.Any(name =>
    {
        var matches = Process.GetProcessesByName(name);
        foreach (var process in matches) process.Dispose();
        return matches.Length != 0;
    });

    public static bool IsGameDirectory(string? path) => !string.IsNullOrWhiteSpace(path)
        && File.Exists(Path.Combine(path, "StardewModdingAPI.exe"))
        && File.Exists(Path.Combine(path, "Stardew Valley.dll"));

    public static string DetectGame()
    {
        var configured = Environment.GetEnvironmentVariable("STARDEW_GAME_PATH");
        if (IsGameDirectory(configured)) return configured!;
        const string defaultSteam = @"C:\Program Files (x86)\Steam";
        var steam = (Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam") is { } key
            ? ReadSteamPath(key) : null) ?? defaultSteam;
        var libraries = new List<string> { steam };
        try
        {
            var libraryFile = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (File.Exists(libraryFile))
                foreach (Match match in Regex.Matches(File.ReadAllText(libraryFile), "\"path\"\\s*\"([^\"]+)\""))
                    libraries.Add(match.Groups[1].Value.Replace(@"\\", @"\", StringComparison.Ordinal));
        }
        catch (IOException) { }
        return libraries.Select(root => Path.Combine(root, "steamapps", "common", "Stardew Valley"))
            .FirstOrDefault(IsGameDirectory) ?? "";
    }

    private static string? ReadSteamPath(RegistryKey key)
    {
        using (key) return key.GetValue("SteamPath") as string;
    }
}
