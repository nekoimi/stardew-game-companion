using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using StardewAI.Companion.Logging;
using StardewAI.Companion.Updates;

namespace StardewAI.Companion.Desktop;

internal readonly record struct RuntimeEndpoint(int Port, string Credential);

internal sealed class DesktopProcesses(string root, FileLogger logger)
{
    private const string LogPrefix = "STARDEW_COMPANION_LOG ";
    private static readonly Regex DiagnosticToken = new(@"\A[A-Za-z0-9_.:-]{1,100}\z", RegexOptions.CultureInvariant);
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
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        gameStart.ArgumentList.Add("--mods-path");
        gameStart.ArgumentList.Add(Path.Combine(root, "Mods"));
        gameStart.Environment["STARDEW_DESKTOP_MODE"] = "1";
        gameStart.Environment["STARDEW_COMPANION_LOG_DIR"] = logger.DirectoryPath;
        var profile = PortableData.Open(root);
        if (profile.External is not null)
        {
            gameStart.Environment["STARDEW_COMPANION_MOD_DATA_DIR"] = profile.ModData;
            gameStart.Environment["STARDEW_COMPANION_MOD_CONFIG"] = profile.ModConfig;
        }
        else
        {
            gameStart.Environment.Remove("STARDEW_COMPANION_MOD_DATA_DIR");
            gameStart.Environment.Remove("STARDEW_COMPANION_MOD_CONFIG");
        }
        foreach (var name in new[] { "DEEPSEEK_API_KEY", "OPENAI_API_KEY", "OPENAI_COMPATIBLE_API_KEY", "OPENAI_COMPATIBLE_BASE_URL" })
            gameStart.Environment.Remove(name);
        _game = new Process { StartInfo = gameStart, EnableRaisingEvents = true };
        int gameErrorReported = 0;
        _game.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is { Length: > 0 } && Interlocked.Exchange(ref gameErrorReported, 1) == 0)
                logger.Write("mod", "warn", "smapi_stderr", "SMAPI 输出了错误信息，详情可查看 SMAPI 原生日志。");
        };
        _game.Exited += (_, _) =>
        {
            logger.Write("desktop", "info", "game_exited", "游戏进程已退出。");
            GameExited?.Invoke();
        };
        if (!_game.Start()) throw new InvalidOperationException("无法启动星露谷。");
        logger.Write("desktop", "info", "game_started", "已启动游戏进程。");
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
        runtimeStart.Environment["STARDEW_COMPANION_DATA_DIR"] = PortableData.Open(root).Data;
        foreach (var name in new[] { "DEEPSEEK_API_KEY", "OPENAI_API_KEY", "OPENAI_COMPATIBLE_API_KEY", "OPENAI_COMPATIBLE_BASE_URL" })
            runtimeStart.Environment.Remove(name);
        if (modelTestOnly) runtimeStart.Environment["STARDEW_MODEL_TEST_ONLY"] = "1";
        else runtimeStart.Environment.Remove("STARDEW_MODEL_TEST_ONLY");

        var ready = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        int runtimeErrorReported = 0;
        _runtime = new Process { StartInfo = runtimeStart, EnableRaisingEvents = true };
        _runtime.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not { } line) return;
            try
            {
                using var json = JsonDocument.Parse(line);
                if (json.RootElement.ValueKind != JsonValueKind.Object) return;
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
        };
        _runtime.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not { Length: > 0 } line) return;
            if (!LogRuntimeEvent(line) && Interlocked.Exchange(ref runtimeErrorReported, 1) == 0)
                logger.Write("runtime", "warn", "runtime_stderr", "运行时输出了额外的错误信息。");
        };
        _runtime.Exited += (sender, _) =>
        {
            ready.TrySetException(new InvalidOperationException("伴侣服务提前退出，请查看诊断。"));
            int? exitCode = null;
            try { exitCode = (sender as Process)?.ExitCode; }
            catch (InvalidOperationException) { }
            logger.Write("runtime", runtimeToken.IsCancellationRequested || (modelTestOnly && exitCode == 0) ? "info" : "warn",
                "runtime_exited", "运行时进程已退出。", new { generation, modelTestOnly, exitCode });
            if (!runtimeToken.IsCancellationRequested) RuntimeExited?.Invoke(generation);
        };
        if (!_runtime.Start()) throw new InvalidOperationException("无法启动伴侣服务。");
        logger.Write("runtime", "info", "runtime_started", "已启动运行时。", new { generation, modelTestOnly });
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
                    logger.Write("runtime", "warn", "runtime_stop_failed", "未能正常停止伙伴服务。", new { errorType = ex.GetType().Name });
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
                logger.Write("desktop", "warn", "stop_signal_cleanup_failed", "未能清理伙伴服务停止信号。", new { errorType = ex.GetType().Name });
            }
        }
    }

    private bool LogRuntimeEvent(string line)
    {
        int marker = line.IndexOf(LogPrefix, StringComparison.Ordinal);
        if (marker < 0 || line.Length > 4096) return false;
        int start = marker + LogPrefix.Length;
        int end = line.LastIndexOf('}');
        if (end < start) return false;
        try
        {
            using var json = JsonDocument.Parse(line.AsMemory(start, end - start + 1));
            var entry = json.RootElement;
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("event", out var kind)
                || kind.ValueKind != JsonValueKind.String) return false;
            var (level, message) = kind.GetString() switch
            {
                "game_connected" => ("info", "运行时已连接当前游戏。"),
                "game_disconnected" => ("warn", "运行时与游戏断开连接。"),
                "model_configured" => ("info", "模型配置已应用。"),
                "model_test_passed" => ("info", "模型与工具调用测试通过。"),
                "model_request_failed" => ("warn", "模型配置或连接测试失败。"),
                "model_request_started" => ("info", "开始调用模型。"),
                "model_request_retry" => ("warn", "模型调用暂时失败，等待重试。"),
                "tool_result" => ("info", "Agent 工具调用已返回。"),
                "work_stage_completed" => ("info", "农场工作阶段已结算。"),
                "turn_completed" => ("info", "Agent 回合已结算。"),
                "turn_failed" => ("warn", "Agent 回合未完成。"),
                "action_result" => ("info", "收到原生动作回执。"),
                "receipt_retention_failed" => ("warn", "历史回执整理未完成，将在下次检查时重试。"),
                _ => (null, null),
            };
            if (message is null) return false;
            var details = new Dictionary<string, JsonElement>();
            foreach (var property in entry.EnumerateObject())
            {
                if (property.Name is "completedCount" or "remainingCount" or "skippedCount" or "failedCount"
                    or "nativeActions" or "targetCount" or "modelRequestsAtDispatch" or "modelRequestsDuringStage"
                    or "modelRequests" or "elapsedMs" or "stages"
                    && property.Value.ValueKind == JsonValueKind.Number)
                    details[property.Name] = property.Value.Clone();
                if (property.Name == "needsReconciliation" && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    details[property.Name] = property.Value.Clone();
                if (property.Name is "gameSessionId" or "requestId" or "commandId" or "skillId" or "status" or "tool"
                    or "provider" or "protocol" or "code" or "turnId" or "goalId" or "operation" or "stopReason"
                    && property.Value.ValueKind == JsonValueKind.String
                    && DiagnosticToken.IsMatch(property.Value.GetString()!))
                    details[property.Name] = property.Value.Clone();
                if (property.Name == "stageId" && entry.TryGetProperty("turnId", out var turn)
                    && turn.ValueKind == JsonValueKind.String && Guid.TryParse(turn.GetString(), out var turnId)
                    && property.Value.ValueKind == JsonValueKind.String
                    && property.Value.GetString()!.StartsWith(turnId.ToString() + ":", StringComparison.Ordinal)) {
                    // Provider call IDs may be long; retain a stable bounded hash.
                    details[property.Name] = JsonSerializer.SerializeToElement(turnId + ":" +
                        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(property.Value.GetString()!)))[..16]);
                }
            }
            logger.Write("runtime", level!, kind.GetString()!, message, details);
            return true;
        }
        catch (JsonException) { return false; }
    }

    internal void ReleaseGameHandle()
    {
        _game?.Dispose();
        _game = null;
    }

    internal static bool AnyGameRunning() => UpdateProcess.AnyGameRunning();

    private static bool IsRunning(Process? process)
    {
        try { return process is { HasExited: false }; }
        catch (InvalidOperationException) { return false; }
    }
}
