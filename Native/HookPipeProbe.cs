using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace QQListener.Native;

internal static partial class HookPipeProbe
{
    [SupportedOSPlatform("windows")]
    public static bool Exists(string name) => WaitNamedPipeW(@"\\.\pipe\" + name, 1)
        || Marshal.GetLastWin32Error() is 5 or 121 or 231;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WaitNamedPipeW(string name, uint timeout);
}
