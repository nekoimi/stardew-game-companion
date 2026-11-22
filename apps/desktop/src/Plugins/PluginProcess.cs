using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace StardewAI.Companion.Desktop.Plugins;

internal sealed class PluginProcess(Process process, SafeFileHandle job) : IDisposable
{
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint KillOnJobClose = 0x00002000;
    private static readonly string[] WindowsEnvironment =
    [
        "SystemRoot", "SystemDrive", "ComSpec", "PATH", "PATHEXT", "TEMP", "TMP", "USERPROFILE",
        "APPDATA", "LOCALAPPDATA", "PROGRAMDATA", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
        "CommonProgramFiles", "CommonProgramFiles(x86)", "HOMEDRIVE", "HOMEPATH", "USERNAME",
        "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "OS", "windir"
    ];

    internal bool Exited => process.HasExited;
    internal int ExitCode => process.ExitCode;
    internal Task WaitAsync(CancellationToken token = default) => process.WaitForExitAsync(token);

    internal static PluginProcess Start(PluginManifest manifest, string programDirectory, string dataDirectory,
        string logDirectory, string hostUrl, string token)
    {
        var executable = PluginPackages.Resolve(programDirectory, manifest.Entry.Executable);
        var workDirectory = PluginPackages.Resolve(programDirectory, manifest.Entry.WorkingDirectory);
        var command = new StringBuilder(string.Join(' ', new[] { executable }.Concat(manifest.Entry.Args).Select(Quote)));
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["STARDEW_PLUGIN_HOST_URL"] = hostUrl, ["STARDEW_PLUGIN_TOKEN"] = token,
            ["STARDEW_PLUGIN_DATA_DIR"] = dataDirectory, ["STARDEW_PLUGIN_LOG_DIR"] = logDirectory
        };
        foreach (var name in WindowsEnvironment)
            if (Environment.GetEnvironmentVariable(name) is { } value) variables[name] = value;
        var environment = Marshal.StringToHGlobalUni(string.Join('\0', variables.Select(item => item.Key + "=" + item.Value)) + "\0");

        var job = CreateJobObject(IntPtr.Zero, null);
        Process? process = null;
        try
        {
            if (job.IsInvalid) throw new Win32Exception();
            var limits = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = KillOnJobClose } };
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimit>()))
                throw new Win32Exception();
            var startup = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfo>() };
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false,
                CreateSuspended | CreateUnicodeEnvironment | CreateNoWindow, environment, workDirectory, ref startup, out var native))
                throw new Win32Exception();
            using var inheritedProcess = new SafeFileHandle(native.Process, ownsHandle: true);
            using var thread = new SafeFileHandle(native.Thread, ownsHandle: true);
            if (!AssignProcessToJobObject(job, inheritedProcess))
            {
                int error = Marshal.GetLastWin32Error();
                TerminateProcess(inheritedProcess, 1);
                throw new Win32Exception(error);
            }
            process = Process.GetProcessById(checked((int)native.Id));
            if (ResumeThread(thread) == uint.MaxValue) throw new Win32Exception();
            return new PluginProcess(process, job);
        }
        catch
        {
            job.Dispose();
            process?.Dispose();
            throw;
        }
        finally { Marshal.FreeHGlobal(environment); }
    }

    public void Dispose()
    {
        job.Dispose();
        process.Dispose();
    }

    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit
    {
        public long PerProcessUserTime, PerJobUserTime;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimit
    {
        public BasicLimit Basic;
        public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public IntPtr Reserved, Desktop, Title;
        public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedSize;
        public IntPtr ReservedBuffer, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo
    {
        public IntPtr Process, Thread;
        public uint Id, ThreadId;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int type, ref ExtendedLimit value, uint size);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string path, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint flags, IntPtr environment, string directory,
        ref StartupInfo startup, out ProcessInfo result);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(SafeFileHandle thread);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeFileHandle process, uint code);
}
