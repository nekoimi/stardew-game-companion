using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace StardewAI.Companion.Desktop;

internal readonly record struct RuntimeEndpoint(int Port, string Credential);

internal sealed class DesktopProcesses(string root, Action<string> log)
{
    private readonly string _mod = DesktopInstallation.ModDirectory(root);
    private readonly string _stopFile = Path.Combine(Path.GetTempPath(), $"stardew-desktop-{Guid.NewGuid():N}.stop");
    private Process? _runtime;
    private Process? _game;

    internal bool GameRunning => IsRunning(_game);
    internal bool RuntimeRunning => IsRunning(_runtime);

    internal event Action<int>? RuntimeExited;
    internal event Action? GameExited;

    internal void StartGame(string gameDirectory)
    {
        if (AnyGameRunning())
            throw new InvalidOperationException("游戏已在运行，请先保存并退出。");
        var smapi = Path.Combine(gameDirectory, "StardewModdingAPI.exe");
        var gameStart = new ProcessStartInfo(smapi)
        {
            WorkingDirectory = gameDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        gameStart.ArgumentList.Add("--mods-path");
        gameStart.ArgumentList.Add(Path.Combine(root, "Mods"));
        gameStart.Environment["STARDEW_DESKTOP_MODE"] = "1";
        _game = new Process { StartInfo = gameStart, EnableRaisingEvents = true };
        _game.OutputDataReceived += (_, args) => { if (args.Data is { } line) log(line); };
        _game.ErrorDataReceived += (_, args) => { if (args.Data is { } line) log(line); };
        _game.Exited += (_, _) => GameExited?.Invoke();
        if (!_game.Start()) throw new InvalidOperationException("无法启动星露谷。");
        _game.BeginOutputReadLine();
        _game.BeginErrorReadLine();
    }

    internal async Task<RuntimeEndpoint> StartRuntimeAsync(string python, DesktopSettings settings,
        int generation, CancellationToken runtimeToken)
    {
        var credential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var runtimeStart = new ProcessStartInfo(python)
        {
            WorkingDirectory = _mod, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        var client = settings.Clients?.GetValueOrDefault(settings.Backend) ?? new(settings.Model, settings.PiPath);
        foreach (string argument in new[] { "-B", "-m", "stardew_ai_runtime.desktop_service", "--run-dir", _mod,
            "--owner-pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--backend", settings.Backend, "--model", client.Model, "--client", client.Path, "--effort", client.Effort })
            runtimeStart.ArgumentList.Add(argument);
        runtimeStart.Environment["STARDEW_DESKTOP_TOKEN"] = credential;
        runtimeStart.Environment["STARDEW_BRIDGE_STOP_FILE"] = _stopFile;
        runtimeStart.Environment["STARDEW_CONNECTION_SOURCE"] = settings.ConnectionSource;
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
            log(line);
        };
        _runtime.ErrorDataReceived += (_, args) => { if (args.Data is { } line) log(line); };
        _runtime.Exited += (_, _) =>
        {
            ready.TrySetException(new InvalidOperationException("伴侣服务提前退出，请查看诊断。"));
            if (!runtimeToken.IsCancellationRequested) RuntimeExited?.Invoke(generation);
        };
        if (!_runtime.Start()) throw new InvalidOperationException("无法启动伴侣服务。");
        _runtime.BeginOutputReadLine();
        _runtime.BeginErrorReadLine();
        int endpoint = await ready.Task.WaitAsync(TimeSpan.FromSeconds(35), runtimeToken);
        if (!IsRunning(_runtime)) throw new InvalidOperationException("伴侣服务提前退出，请查看诊断。");
        return new RuntimeEndpoint(endpoint, credential);
    }

    internal async Task StopRuntimeAsync()
    {
        var runtime = _runtime;
        _runtime = null;
        try
        {
            if (runtime is not null && IsRunning(runtime))
            {
                try
                {
                    File.WriteAllText(_stopFile, "stop");
                    await runtime.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(13));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
                {
                    log($"未能正常停止伙伴服务：{ex.Message}");
                    if (IsRunning(runtime)) runtime.Kill(entireProcessTree: true);
                    await runtime.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4));
                }
            }
        }
        catch (InvalidOperationException) { }
        finally
        {
            runtime?.Dispose();
            try { File.Delete(_stopFile); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                log($"未能清理伙伴服务停止信号：{ex.Message}");
            }
        }
    }

    internal void ReleaseGameHandle()
    {
        _game?.Dispose();
        _game = null;
    }

    internal static bool AnyGameRunning() => new[] { "StardewModdingAPI", "Stardew Valley" }.Any(name =>
    {
        var matches = Process.GetProcessesByName(name);
        foreach (var process in matches) process.Dispose();
        return matches.Length != 0;
    });

    private static bool IsRunning(Process? process)
    {
        try { return process is { HasExited: false }; }
        catch (InvalidOperationException) { return false; }
    }
}
