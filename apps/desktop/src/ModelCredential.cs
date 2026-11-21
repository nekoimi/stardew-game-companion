using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace StardewAI.Companion.Desktop;

internal static class ModelCredential
{
    private static string Target(string root, string provider) =>
        "StardewGameCompanion/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant()))) + "/" + provider;

    internal static string Read(string root, string provider)
    {
        if (!CredRead(Target(root, provider), 1, 0, out var address))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 1168) return "";
            throw new Win32Exception(error, "无法读取模型凭据。");
        }
        try
        {
            var entry = Marshal.PtrToStructure<Credential>(address);
            return Marshal.PtrToStringUni(entry.Blob, checked((int)entry.BlobSize / 2)) ?? "";
        }
        finally { CredFree(address); }
    }

    internal static void Write(string root, string provider, string secret)
    {
        if (secret.Length is 0 or > 1200) throw new ArgumentException("请填写有效的 API Key。");
        var blob = Marshal.StringToCoTaskMemUni(secret);
        try
        {
            var entry = new Credential { Type = 1, Target = Target(root, provider), Blob = blob,
                BlobSize = (uint)(secret.Length * 2), Persist = 2, User = provider };
            if (!CredWrite(ref entry, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法保存模型凭据。");
        }
        finally { Marshal.ZeroFreeCoTaskMemUnicode(blob); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string Target;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint BlobSize;
        public IntPtr Blob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? Alias;
        public string User;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}
