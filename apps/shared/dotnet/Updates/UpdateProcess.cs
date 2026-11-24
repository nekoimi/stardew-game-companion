using System.Diagnostics;
using System.IO;
using System.Globalization;

namespace StardewAI.Companion.Updates;

internal static class UpdateProcess
{
    internal static bool AnyGameRunning() => new[] { "StardewModdingAPI", "Stardew Valley" }.Any(name =>
    {
        var matches = Process.GetProcessesByName(name);
        foreach (var process in matches) process.Dispose();
        return matches.Length != 0;
    });

    internal static void RequireGameExit()
    {
        if (AnyGameRunning()) throw new InvalidOperationException("请先在游戏中保存并退出，再安装更新。");
    }

    internal static void StartHelper(string root, string helper, string operation, Guid id)
    {
        using var owner = Process.GetCurrentProcess();
        using var process = Start(helper, root, operation, root, id.ToString("N"),
            owner.Id.ToString(CultureInfo.InvariantCulture),
            owner.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
    }

    internal static Process Start(string executable, string root, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return Process.Start(start) ?? throw new IOException("无法启动更新进程。");
    }
}
