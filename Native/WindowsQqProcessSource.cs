using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace QQListener.Native;

/// <summary>
/// 真实的进程枚举：QQ.exe 的完整命令行是判断"是不是 Electron 主进程"的唯一依据。
///
/// .NET 的 <see cref="System.Diagnostics.Process"/> 不暴露别的进程的启动参数，
/// 这里改用 <c>NtQueryInformationProcess(ProcessCommandLineInformation)</c>——
/// Windows 8.1 起就有的半公开接口，只需要 PROCESS_QUERY_LIMITED_INFORMATION，
/// 不用像读 PEB 那样自己走未文档化的结构体偏移量。避免为这一件事引入
/// WMI/System.Management 依赖（那会牵动锁定的 NuGet lock file，Windows 侧却验证不了）。
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsQqProcessSource
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    public static IEnumerable<QqProcessInfo> Enumerate()
    {
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("QQ"))
        {
            using (process)
                yield return new QqProcessInfo(process.Id, "QQ.exe", TryQueryCommandLine(process.Id));
        }
    }

    /// <summary>
    /// 紧邻实际 OpenProcess（用于注入/卸载）之前的最后一次复核：这个 pid 现在是不是
    /// 仍然是 QQ 主进程。
    ///
    /// <see cref="Core.CoreService.Reconcile"/> 枚举一次快照之后要经过一轮 hookActive
    /// 探测才轮到这里，足够旧进程退出、系统把同一个 pid 分给一个完全无关的进程——
    /// <see cref="System.Diagnostics.Process.GetProcessById"/> 对已经不存在的 pid 直接
    /// 抛异常，天然覆盖"进程已退出"分支；命中的话再用已有的命令行判定确认它仍然是
    /// Electron 主进程而不是子进程。<see cref="ManualMapInjector"/> 把读盘解析 PE 的
    /// 结果缓存住了，所以从这里到真正 OpenProcess 之间基本不再夹着耗时操作。
    ///
    /// 局限：如果 pid 恰好在这个极短窗口内被系统回收给了另一个新启动的 QQ.exe 主进程
    /// 实例，这里没法区分"同一个实例"与"同名的新实例"——要彻底杜绝这一点得在整个
    /// 生命周期里持续持有进程句柄，超出这次修复的范围。这里做的是把窗口收窄到
    /// "紧邻 OpenProcess 之前的几个方法调用"，把实际发生概率降到可以接受的程度。
    /// </summary>
    public static bool IsStillMainQqProcess(int pid)
    {
        string processName;
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            processName = process.ProcessName;
        }
        // GetProcessById 对不存在的 pid 抛 ArgumentException；但如果进程恰好在
        // GetProcessById 和读 ProcessName 之间退出，.NET 内部走的是
        // InvalidOperationException（EnsureState 拿不到进程信息）——这正是这个方法
        // 存在的理由本身（"进程可能刚刚消失"），漏掉这个类型就等于没做这层保护。
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }

        if (!string.Equals(processName, "QQ", StringComparison.OrdinalIgnoreCase)) return false;

        try
        {
            return QqProcessLocator.IsMainQqProcess("QQ.exe", TryQueryCommandLine(pid));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // 这个方法的合同是"拿不准就当它不是"——命令行查询本身已经自己吞了大部分
            // 失败，这里只是兜底，万一某个 Marshal/P-Invoke 调用在边界情况下还是抛了。
            return false;
        }
    }

    private static IReadOnlyList<string>? TryQueryCommandLine(int pid)
    {
        var handle = NativeMethods.OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var commandLine = QueryCommandLineString(handle);
            return commandLine is null ? null : SplitCommandLine(commandLine);
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    private static string? QueryCommandLineString(IntPtr handle)
    {
        var status = NativeMethods.NtQueryInformationProcess(
            handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var requiredLength);
        if (status != StatusInfoLengthMismatch || requiredLength <= 0) return null;

        var buffer = Marshal.AllocHGlobal(requiredLength);
        try
        {
            status = NativeMethods.NtQueryInformationProcess(
                handle, ProcessCommandLineInformation, buffer, requiredLength, out _);
            if (status != 0) return null; // STATUS_SUCCESS == 0

            // 返回的是一个 UNICODE_STRING，紧跟着字符串本体写在同一块缓冲区里；
            // x64 上 Length(u16)+MaximumLength(u16)+对齐填充(4)+Buffer(u64) 共 16 字节。
            var byteLength = (ushort)Marshal.ReadInt16(buffer);
            return byteLength == 0 ? "" : Marshal.PtrToStringUni(buffer + 16, byteLength / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>用系统自己的分词规则切开命令行——跟 shell/CRT 解析 argv 是同一套规则。</summary>
    private static IReadOnlyList<string> SplitCommandLine(string commandLine)
    {
        var argv = NativeMethods.CommandLineToArgvW(commandLine, out var count);
        if (argv == IntPtr.Zero) return [];
        try
        {
            var result = new string[count];
            for (var i = 0; i < count; i++)
                result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "";
            return result;
        }
        finally
        {
            NativeMethods.LocalFree(argv);
        }
    }
}
