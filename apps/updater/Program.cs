using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using StardewAI.Companion.Updates;

namespace StardewAI.Companion.Updater;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 5 || args[0] is not ("install" or "recover") || !Path.IsPathFullyQualified(args[1])
            || !Guid.TryParseExact(args[2], "N", out var id) || !int.TryParse(args[3], out int parentId)
            || !long.TryParse(args[4], out long parentStart))
            return 2;

        var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new UpdateProgressWindow();
        application.Startup += async (_, _) =>
        {
            IProgress<UpdateProgress> progress = new Progress<UpdateProgress>(window.ShowProgress);
            try
            {
                await Task.Run(() => Run(args[1], args[0], id, parentId, parentStart, progress.Report));
                window.AllowClose();
                window.Close();
            }
            catch (Exception error)
            {
                window.ShowFailure(error.Message);
            }
        };
        application.Run(window);
        return window.Failed ? 1 : 0;
    }

    private static void Run(string root, string operation, Guid id, int parentId, long parentStart,
        Action<UpdateProgress> report)
    {
        var transaction = new UpdateTransaction(root);
        var launcher = UpdatePackage.SafePath(root, UpdatePackage.Launcher);
        WaitForOwner(parentId, parentStart);
        using (transaction.AcquireLease())
        {
            try
            {
                report(new("正在检查游戏是否已退出…"));
                File.Delete(transaction.ResultPath);
                UpdateProcess.RequireGameExit();
                if (operation == "recover") transaction.Recover(progress: report);
                else
                {
                    report(new("正在验证更新授权…"));
                    transaction.ConsumeAuthorization(id);
                    transaction.Apply(id, CheckStagedLauncher, CheckUpdatedProgram, report);
                }
            }
            catch (Exception error) when (IsUpdateFailure(error))
            {
                if (transaction.HasPendingUpdate) throw;
                if (!File.Exists(transaction.ResultPath))
                    UpdateTransaction.WriteJson(transaction.ResultPath,
                        new UpdateResult(false, $"更新未完成。{error.Message}"));
            }
        }
        report(new("正在打开伴侣…"));
        using var restarted = UpdateProcess.Start(launcher, root);

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
    }

    private static bool IsUpdateFailure(Exception error) =>
        error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException
            or JsonException or System.ComponentModel.Win32Exception or TimeoutException;

    private static void WaitForOwner(int parentId, long parentStart)
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

    private static void CheckProcess(string executable, string directory, string component, params string[] arguments)
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
}
