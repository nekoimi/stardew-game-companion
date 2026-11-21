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
        foreach (var name in new[] { "DEEPSEEK_API_KEY", "OPENAI_API_KEY", "OPENAI_COMPATIBLE_API_KEY", "OPENAI_COMPATIBLE_BASE_URL" })
            gameStart.Environment.Remove(name);
        _game = new Process { StartInfo = gameStart, EnableRaisingEvents = true };
        _game.OutputDataReceived += (_, args) => { if (args.Data is { } line) log(line); };
        _game.ErrorDataReceived += (_, args) => { if (args.Data is { } line) log(line); };
        _game.Exited += (_, _) => GameExited?.Invoke();
        if (!_game.Start()) throw new InvalidOperationException("无法启动星露谷。");
        _game.BeginOutputReadLine();
        _game.BeginErrorReadLine();
    }

    internal async Task<RuntimeEndpoint> StartRuntimeAsync(string node,
        int generation, CancellationToken runtimeToken, bool modelTestOnly = false)
    {
        var credential = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var runtimeStart = new ProcessStartInfo(node)
        {
            WorkingDirectory = _mod, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        var arguments = new[] { "--disable-warning=ExperimentalWarning", Path.Combine(root, "runtime", "dist", "bootstrap.js"),
            "--desktop", _mod, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        foreach (string argument in arguments)
            runtimeStart.ArgumentList.Add(argument);
        runtimeStart.Environment["STARDEW_DESKTOP_TOKEN"] = credential;
        runtimeStart.Environment["STARDEW_BRIDGE_STOP_FILE"] = _stopFile;
        foreach (var name in new[] { "DEEPSEEK_API_KEY", "OPENAI_API_KEY", "OPENAI_COMPATIBLE_API_KEY", "OPENAI_COMPATIBLE_BASE_URL" })
            runtimeStart.Environment.Remove(name);
        if (modelTestOnly) runtimeStart.Environment["STARDEW_MODEL_TEST_ONLY"] = "1";
        else runtimeStart.Environment.Remove("STARDEW_MODEL_TEST_ONLY");

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
                    if (!json.RootElement.TryGetProperty("protocolVersion", out var version)
                        || !version.TryGetInt32(out int actual) || actual != 2)
                        ready.TrySetException(new InvalidOperationException("桌面与运行时的协议版本不匹配。"));
                    else ready.TrySetResult(number);
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
