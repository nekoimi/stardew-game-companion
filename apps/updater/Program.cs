using System.Diagnostics;
using System.Text.Json;
using StardewAI.Companion.Updates;

if (args.Length != 5 || args[0] is not ("install" or "recover") || !Path.IsPathFullyQualified(args[1])
    || !Guid.TryParseExact(args[2], "N", out var id) || !int.TryParse(args[3], out int parentId)
    || !long.TryParse(args[4], out long parentStart))
    return 2;

string root = args[1];
var transaction = new UpdateTransaction(root);
var launcher = UpdatePackage.SafePath(root, UpdatePackage.Launcher);
try
{
    WaitForOwner();
    using (transaction.AcquireLease())
    {
        try
        {
            File.Delete(transaction.ResultPath);
            UpdateProcess.RequireGameExit();
            if (args[0] == "recover") transaction.Recover();
            else
            {
                transaction.ConsumeAuthorization(id);
                transaction.Apply(id, CheckStagedLauncher, CheckUpdatedProgram);
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
            or JsonException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            if (transaction.HasPendingUpdate) throw;
            if (!File.Exists(transaction.ResultPath))
                UpdateTransaction.WriteJson(transaction.ResultPath,
                    new UpdateResult(false, $"更新未完成。{error.Message}"));
        }
    }
    using var restarted = UpdateProcess.Start(launcher, root);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}

void WaitForOwner()
{
    Process? parent;
    try { parent = Process.GetProcessById(parentId); }
    catch (ArgumentException) { return; }
    using (parent)
    {
        try
        {
            if (parent.StartTime.ToUniversalTime().Ticks == parentStart && !parent.WaitForExit(45_000))
                throw new TimeoutException("伴侣未能及时退出，更新尚未应用。");
        }
        catch (InvalidOperationException) when (parent.HasExited) { }
    }
}

void CheckStagedLauncher(string payload)
{
    string candidate = UpdatePackage.SafePath(payload, UpdatePackage.Launcher);
    CheckProcess(candidate, payload, "新版启动程序", "--check-launcher");
    UpdateProcess.RequireGameExit();
}

void CheckUpdatedProgram()
{
    UpdateProcess.RequireGameExit();
    CheckProcess(launcher, root, "新版运行时", "--check-update", id.ToString("N"));
}

void CheckProcess(string executable, string directory, string component, params string[] arguments)
{
    using var process = StartCheckProcess();
    if (!process.WaitForExit(45_000))
    {
        process.Kill(entireProcessTree: true);
        process.WaitForExit();
        throw new InvalidOperationException($"{component}自检超时。");
    }
    if (process.ExitCode != 0) throw new InvalidOperationException($"{component}自检失败。");

    Process StartCheckProcess()
    {
        try { return UpdateProcess.Start(executable, directory, arguments); }
        catch (System.ComponentModel.Win32Exception error)
        {
            throw new InvalidOperationException($"{component}无法启动（Windows 错误 {error.NativeErrorCode}）。", error);
        }
    }
}
